using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MC1.Core.Services;

namespace MC1.Windows.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    protected static MeshApp Core => AppHost.Core;

    private static readonly List<Action> s_detachers = [];

    /// <summary>
    /// Remembers how to unhook a view-model from a long-lived event (the core's, a static one). When the language
    /// changes the whole UI is rebuilt with new view-models, and <see cref="DetachAll"/> unhooks the old ones.
    /// </summary>
    protected static void Track(Action detach) { lock (s_detachers) s_detachers.Add(detach); }

    /// <summary>For view-models that unhook themselves earlier (a closed conversation).</summary>
    protected static void Untrack(Action detach) { lock (s_detachers) s_detachers.Remove(detach); }

    /// <summary>Unhooks every view-model created so far (before the UI is rebuilt).</summary>
    public static void DetachAll()
    {
        Action[] all;
        lock (s_detachers) { all = [.. s_detachers]; s_detachers.Clear(); }
        foreach (var d in all)
        {
            try { d(); } catch { /* best effort */ }
        }
    }

    /// <summary>Calls <paramref name="handler"/> on every data change until the UI is rebuilt.</summary>
    protected static void OnData(Action<DataChange> handler)
    {
        Core.DataChanged += handler;
        Track(() => Core.DataChanged -= handler);
    }

    /// <summary>Calls <paramref name="handler"/> on every connection status change until the UI is rebuilt.</summary>
    protected static void OnStatus(Action handler)
    {
        Core.StatusChanged += handler;
        Track(() => Core.StatusChanged -= handler);
    }

    protected static void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>Runs an async UI action, surfacing failures to the user instead of crashing.</summary>
    protected static async Task Guard(Func<Task> action, string? failureTitle = null)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Core.Log.Warn("UI", $"{failureTitle ?? "Action"} failed: {ex.Message}");
            await AppHost.Dialogs.ShowError(failureTitle ?? L.T("Something went wrong"), ex.Message);
        }
    }
}

/// <summary>Coalesces bursts of change notifications into a single UI refresh (runs at most once per delay, never starved).</summary>
public sealed class Debouncer(TimeSpan delay, Action action)
{
    private DispatcherTimer? _timer;

    private static bool s_held;
    private static readonly HashSet<Debouncer> s_waiting = [];

    /// <summary>
    /// Holds every refresh while the start-up animation plays (the radio syncs meanwhile), so the animation has the
    /// UI thread to itself; <see cref="Release"/> then runs each held refresh once.
    /// </summary>
    public static void Hold() => s_held = true;

    public static void Release()
    {
        if (!s_held) return;
        s_held = false;
        var waiting = s_waiting.ToArray();
        s_waiting.Clear();
        foreach (var d in waiting) d.Trigger();
    }

    public void Trigger()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (s_held) { s_waiting.Add(this); return; }
            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = delay };
                _timer.Tick += (_, _) =>
                {
                    _timer!.Stop();
                    if (s_held) s_waiting.Add(this);
                    else action();
                };
            }
            if (!_timer.IsEnabled) _timer.Start();
        });
    }

    public void Now() => Dispatcher.UIThread.Post(action);
}

/// <summary>Global access to the core app and UI services.</summary>
public static class AppHost
{
    public static MeshApp Core { get; set; } = null!;
    public static IDialogService Dialogs { get; set; } = null!;
    public static MainWindowViewModel? Main { get; set; }
    /// <summary>Brings the main window to the foreground (set by the view layer).</summary>
    public static Action? ActivateWindow { get; set; }
    /// <summary>Returns this PC's location (Windows location services), when available.</summary>
    public static Func<Task<(double Lat, double Lon)?>>? GetPcLocation { get; set; }
    /// <summary>Applies "System", "Light" or "Dark".</summary>
    public static Action<string>? ApplyTheme { get; set; }
    /// <summary>Bluetooth LE scanner (Windows only).</summary>
    public static MC1.Windows.Services.IBluetoothScanner? Bluetooth { get; set; }
    /// <summary>Raised on the UI thread with the total unread count (window title, tray and taskbar badges).</summary>
    public static Action<int>? UnreadChanged { get; set; }
    /// <summary>Reads / changes "start with Windows" (null where not supported).</summary>
    public static Func<bool>? GetAutoStart { get; set; }
    public static Action<bool>? SetAutoStart { get; set; }
    /// <summary>The lock-screen status setting changed (Windows).</summary>
    public static Action<bool>? LockScreenStatusChanged { get; set; }

    /// <summary>False when Windows' "Animation effects" is off (then the start-up animation is skipped).</summary>
    public static Func<bool>? SystemAnimationsEnabled { get; set; }

    /// <summary>Switches the app's language (a code, or null for Windows' language) and rebuilds the UI in it.</summary>
    public static Action<string?>? ChangeLanguage { get; set; }

    /// <summary>Plays the start-up animation now (the "Play" button in Settings).</summary>
    public static Action? PlayStartupAnimation { get; set; }

    /// <summary>Shows a view-model as a card over the main window until it closes itself.</summary>
    public static Func<object, double, Task>? ShowOverlay { get; set; }
    /// <summary>Installs/removes the MeshCore lock screen widget (Windows).</summary>
    public static MC1.Windows.Services.ILockScreenWidget? Widget { get; set; }
}

public interface IDialogService
{
    Task ShowError(string title, string message);
    Task ShowInfo(string title, string message);
    /// <param name="confirmText">The confirm button's text; null means "OK" (in the app's language).</param>
    Task<bool> Confirm(string title, string message, string? confirmText = null, bool destructive = false);
    Task<string?> Prompt(string title, string message, string initial = "", string? watermark = null, bool password = false);
    Task<string?> PickSaveFile(string title, string suggestedName, string extension, string description, string? startFolder = null);
    Task<string?> PickOpenFile(string title, string[] extensions, string description);
    Task CopyToClipboard(string text);
    Task<string?> ReadClipboard();
    Task ShowDialog(object viewModel, string title, double width = 520, double height = 600);
    void OpenUrl(string url);
}
