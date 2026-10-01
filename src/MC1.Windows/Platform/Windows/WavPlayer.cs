using System.Diagnostics;
using System.Runtime.InteropServices;
using MC1.Core.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace MC1.Windows.Platform.Windows;

/// <summary>
/// Plays the start-up animation's sound. It's prepared in Windows' media player in the background when MeshCore starts
/// (so it's ready when the animation begins) and started in step with the animation; if the media player can't play
/// it, Windows' simple PlaySound is the fallback. What happens is written to the log.
/// </summary>
internal static class WavPlayer
{
    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004;

    private static AppLog? _log;
    private static readonly object Gate = new();
    private static Task<MediaPlayer?>? _prepared;
    private static byte[]? _preparedWav;
    private static MediaPlayer? _playing;
    private static int _generation;

    // PlaySound reads the sound from memory while it plays, so it lives in unmanaged memory that is never moved.
    private static byte[]? _fallbackSource;
    private static IntPtr _fallbackBuffer;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    public static void Initialize(AppLog log) => _log = log;

    /// <summary>Gets the media player ready with this sound (in the background).</summary>
    public static void Prepare(byte[] wav)
    {
        lock (Gate)
        {
            if (_prepared is not null && ReferenceEquals(_preparedWav, wav)) return;
            _preparedWav = wav;
            _prepared = Task.Run(() => OpenAsync(wav));
        }
    }

    private static async Task<MediaPlayer?> OpenAsync(byte[] wav)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(wav);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var player = new MediaPlayer
            {
                AutoPlay = false,
                AudioCategory = MediaPlayerAudioCategory.SoundEffects,
                Volume = 1,
            };
            player.CommandManager.IsEnabled = false; // not a "now playing" item in Windows' media controls
            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaOpened += (_, _) => opened.TrySetResult(true);
            player.MediaFailed += (_, e) =>
            {
                _log?.Warn("Sound", $"Start-up sound: media player failed ({e.Error}): {e.ErrorMessage} {e.ExtendedErrorCode?.Message}");
                opened.TrySetResult(false);
            };
            player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
            var done = await Task.WhenAny(opened.Task, Task.Delay(4000));
            if (done == opened.Task && opened.Task.Result)
            {
                _log?.Info("Sound", $"Start-up sound ready ({sw.ElapsedMilliseconds} ms)");
                return player;
            }
            if (done != opened.Task) _log?.Warn("Sound", "Start-up sound: the media player didn't open it in time");
            player.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Warn("Sound", "Start-up sound: media player unavailable: " + ex.Message);
        }
        return null;
    }

    /// <summary>Plays the sound from the start (the animation started just now).</summary>
    public static void Play(byte[] wav)
    {
        var started = Stopwatch.GetTimestamp();
        var generation = Interlocked.Increment(ref _generation);
        Prepare(wav);
        _ = PlayAsync(wav, started, generation);
    }

    private static async Task PlayAsync(byte[] wav, long started, int generation)
    {
        MediaPlayer? player = null;
        try { player = _prepared is { } p ? await p.ConfigureAwait(false) : null; }
        catch { /* logged in OpenAsync */ }
        if (generation != Volatile.Read(ref _generation)) return; // skipped (or started again) meanwhile

        var late = Stopwatch.GetElapsedTime(started);
        if (player is not null)
        {
            try
            {
                // In step with the animation even when the player took a moment to get ready.
                player.Volume = 1;
                player.PlaybackSession.Position = late > TimeSpan.FromMilliseconds(120) ? late : TimeSpan.Zero;
                player.Play();
                _playing = player;
                _log?.Info("Sound", $"Start-up sound playing{(late.TotalMilliseconds > 120 ? $" (from {late.TotalMilliseconds:0} ms in)" : "")}");
                return;
            }
            catch (Exception ex)
            {
                _log?.Warn("Sound", "Start-up sound: media player couldn't play: " + ex.Message);
            }
        }
        if (late > TimeSpan.FromSeconds(1.5)) return; // too late to start it from the beginning
        try
        {
            if (!ReferenceEquals(wav, _fallbackSource))
            {
                if (_fallbackBuffer != IntPtr.Zero) PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                if (_fallbackBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_fallbackBuffer);
                _fallbackBuffer = Marshal.AllocHGlobal(wav.Length);
                Marshal.Copy(wav, 0, _fallbackBuffer, wav.Length);
                _fallbackSource = wav;
            }
            var ok = PlaySound(_fallbackBuffer, IntPtr.Zero, SndAsync | SndNoDefault | SndMemory);
            if (ok) _log?.Info("Sound", "Start-up sound playing (PlaySound)");
            else _log?.Warn("Sound", $"Start-up sound: PlaySound failed (error {Marshal.GetLastPInvokeError()}) — is an audio output device available?");
        }
        catch (Exception ex)
        {
            _log?.Warn("Sound", "Start-up sound: PlaySound unavailable: " + ex.Message);
        }
    }

    /// <summary>Stops it (the animation was skipped): a quick fade rather than a click.</summary>
    public static void Stop()
    {
        Interlocked.Increment(ref _generation);
        if (_fallbackBuffer != IntPtr.Zero) PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        if (_playing is not { } player) return;
        _playing = null;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var v = 0.8; v > 0; v -= 0.2)
                {
                    player.Volume = v;
                    await Task.Delay(30);
                }
                player.Pause();
                player.Volume = 1;
            }
            catch { /* best effort */ }
        });
    }
}
