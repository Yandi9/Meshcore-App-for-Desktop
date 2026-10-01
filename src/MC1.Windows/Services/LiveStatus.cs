using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MC1.Windows.Services;

/// <summary>What the lock screen shows: new messages and the radio's battery.</summary>
public sealed record LiveStatus(
    [property: JsonPropertyName("unread")] int Unread,
    [property: JsonPropertyName("mentions")] int Mentions,
    [property: JsonPropertyName("battery")] int? BatteryPercent,
    [property: JsonPropertyName("volts")] double? BatteryVolts,
    [property: JsonPropertyName("radio")] string RadioName,
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("state")] string State)
{
    /// <summary>Before the first status is published (a getter, so "state" is in the app's current language).</summary>
    public static LiveStatus Empty => new(0, 0, null, null, "", false, L.T("Not connected"));

    /// <summary>
    /// The lock screen widget's words in the app's language (it runs in its own process without the translations; it
    /// keeps the last ones it got for when MeshCore isn't running).
    /// </summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string> Labels => new()
    {
        ["lang"] = L.Code,
        ["unread"] = L.Plural(Unread, "new message", "new messages"),
        ["notConnected"] = L.T("Radio not connected"),
        ["notRunning"] = L.T("MeshCore isn't running"),
    };

    public string UnreadText => Unread == 0 ? L.T("No new messages") : L.Plural(Unread, "{0} new message", "{0} new messages");
    public string BatteryText => BatteryPercent is { } p ? L.F("Radio battery {0}%", p) : Connected ? L.T("Radio battery —") : L.T("Radio not connected");
}

/// <summary>
/// The latest <see cref="LiveStatus"/>, published by the main window and read by the lock-screen notification and
/// (over a named pipe) by the MeshCore lock screen widget.
/// </summary>
public static class LiveStatusHub
{
    private static LiveStatus? _current;
    private static readonly object Lock = new();

    public static LiveStatus Current { get { lock (Lock) return _current ?? LiveStatus.Empty; } }

    /// <summary>Raised (on the publishing thread) when something shown changed.</summary>
    public static event Action<LiveStatus>? Changed;

    public static void Publish(LiveStatus status)
    {
        lock (Lock)
        {
            if (status == _current) return;
            _current = status;
        }
        try { Changed?.Invoke(status); } catch { /* a listener failing mustn't break the UI */ }
    }

    /// <summary>Pipe the widget connects to; one per Windows user.</summary>
    public static string PipeName => "MeshCore.Status." + Environment.UserName;

    /// <summary>
    /// Serves the status pipe: each client (the widget) gets the current status as one line of JSON right away, then
    /// a new line whenever something changes (and every 30 s), until it disconnects.
    /// </summary>
    public static void StartPipeServer(Action<string>? log = null)
    {
        var thread = new Thread(() =>
        {
            var announced = false;
            var failures = 0;
            while (true)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.Out, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    if (!announced) { log?.Invoke("Lock screen widget connected"); announced = true; }
                    var client = server;
                    server = null;
                    new Thread(() => Serve(client)) { IsBackground = true, Name = "Live status client" }.Start();
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    if (++failures <= 3) log?.Invoke("Status pipe: " + ex.Message);
                    Thread.Sleep(2000);
                }
            }
        })
        { IsBackground = true, Name = "Live status pipe" };
        thread.Start();
    }

    private static void Serve(NamedPipeServerStream pipe)
    {
        using var changed = new AutoResetEvent(true);
        void OnChanged(LiveStatus _)
        {
            try { changed.Set(); } catch (ObjectDisposedException) { /* client gone */ }
        }
        Changed += OnChanged;
        try
        {
            using (pipe)
            {
                while (pipe.IsConnected)
                {
                    changed.WaitOne(TimeSpan.FromSeconds(30));
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Current) + "\n");
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* the widget went away */ }
        finally { Changed -= OnChanged; }
    }
}
