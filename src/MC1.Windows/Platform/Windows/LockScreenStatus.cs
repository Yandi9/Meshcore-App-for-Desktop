using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using MC1.Core.Services;
using MC1.Windows.Services;
using MC1.Windows.ViewModels;
using Windows.UI.Notifications;

namespace MC1.Windows.Platform.Windows;

/// <summary>
/// A "live" status card for the lock screen: while the PC is locked, one quiet notification shows the number of
/// new messages and the radio's battery, and is updated in place as they change. It's removed on unlock so it
/// never clutters the desktop. (Windows shows it when "Show notifications on the lock screen" is on.)
/// </summary>
public static class LockScreenStatus
{
    private const string Tag = "status";
    private const string Group = "lockscreen";
    private static MeshApp? _core;
    private static bool _locked;
    private static bool _shown;
    private static bool _dismissed;
    private static uint _sequence;

    public static void Initialize(MeshApp core)
    {
        _core = core;
        try { SystemEvents.SessionSwitch += OnSessionSwitch; }
        catch (Exception ex) { core.Log.Warn("LockScreen", "Lock/unlock events unavailable: " + ex.Message); }
        LiveStatusHub.Changed += OnStatusChanged;
    }

    private static bool Enabled => _core?.Settings.Current is { LockScreenStatus: true };

    private static void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _locked = true;
                _dismissed = false;
                if (_core is not null) _core.Messages.ScreenLocked = true;
                if (Enabled) Show(LiveStatusHub.Current);
                break;
            case SessionSwitchReason.SessionUnlock:
                _locked = false;
                if (_core is not null) _core.Messages.ScreenLocked = false;
                Remove();
                // Back at the desktop: the chat on screen counts as read again.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => AppHost.Main?.OnWindowActivatedIfActive());
                break;
        }
    }

    private static void OnStatusChanged(LiveStatus status)
    {
        if (!_locked || !Enabled || _dismissed) return;
        if (_shown && Update(status)) return;
        if (!_dismissed) Show(status);
    }

    private static Dictionary<string, string> Values(LiveStatus s) => new()
    {
        ["title"] = s.UnreadText,
        ["battery"] = s.BatteryText + (s.RadioName.Length > 0 ? " · " + s.RadioName : ""),
        ["state"] = s.Connected ? "" : s.State,
    };

    private static void Show(LiveStatus status)
    {
        try
        {
            var content = new ToastContentBuilder()
                .AddArgument("action", "activate")
                .AddVisualChild(new AdaptiveText { Text = new BindableString("title"), HintStyle = AdaptiveTextStyle.Title })
                .AddVisualChild(new AdaptiveText { Text = new BindableString("battery") })
                .AddVisualChild(new AdaptiveText { Text = new BindableString("state"), HintStyle = AdaptiveTextStyle.CaptionSubtle })
                .AddAudio(new ToastAudio { Silent = true })
                // A reminder stays on the lock screen until it's dismissed or the PC is unlocked.
                .SetToastScenario(ToastScenario.Reminder)
                .AddButton(new ToastButton().SetContent(L.T("Open MeshCore")).AddArgument("action", "activate"))
                .GetToastContent();
            var toast = new ToastNotification(content.GetXml())
            {
                Tag = Tag,
                Group = Group,
                Data = new NotificationData(Values(status), ++_sequence),
                ExpiresOnReboot = true,
            };
            ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
            _shown = true;
        }
        catch (Exception ex) { _core?.Log.Warn("LockScreen", "Status card not shown: " + ex.Message); }
    }

    /// <summary>Updates the card in place (no new pop-up); false if it's gone (e.g. dismissed).</summary>
    private static bool Update(LiveStatus status)
    {
        try
        {
            var result = ToastNotificationManagerCompat.CreateToastNotifier().Update(new NotificationData(Values(status), ++_sequence), Tag, Group);
            if (result == NotificationUpdateResult.Succeeded) return true;
            _shown = false;
            // Dismissed by the user: leave it gone until the next lock.
            if (result == NotificationUpdateResult.NotificationNotFound) _dismissed = true;
            return _dismissed;
        }
        catch { return false; }
    }

    private static void Remove()
    {
        if (!_shown) return;
        _shown = false;
        try { ToastNotificationManagerCompat.History.Remove(Tag, Group); } catch { /* already gone */ }
    }

    /// <summary>Called when the setting is switched off.</summary>
    public static void Hide() => Remove();
}
