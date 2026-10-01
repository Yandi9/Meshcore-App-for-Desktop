using Avalonia.Threading;
using MC1.Core.Services;
using MC1.Windows.ViewModels;
using Microsoft.Toolkit.Uwp.Notifications;

namespace MC1.Windows.Platform.Windows;

/// <summary>Windows toast notifications with inline quick reply.</summary>
public sealed class ToastNotifier : INotifier
{
    private readonly MeshApp _core;

    public ToastNotifier(MeshApp core)
    {
        _core = core;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    private bool Silent => !_core.Settings.Current.NotificationSound;

    public void ShowMessage(string title, string body, string conversationKey, bool allowReply)
    {
        try
        {
            var b = new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddArgument("key", conversationKey)
                .AddText(title, hintMaxLines: 1)
                .AddText(body)
                .AddAudio(new Uri("ms-winsoundevent:Notification.IM"), silent: Silent);
            if (allowReply)
            {
                b.AddInputTextBox("reply", placeHolderContent: L.T("Reply"))
                 .AddButton(new ToastButton()
                     .SetContent(L.T("Send"))
                     .AddArgument("action", "reply")
                     .AddArgument("key", conversationKey)
                     .SetTextBoxId("reply"));
            }
            b.Show(t =>
            {
                t.Group = "messages";
                t.Tag = Math.Abs(conversationKey.GetHashCode()).ToString();
            });
        }
        catch (Exception ex) { _core.Log.Warn("Toast", ex.Message); }
    }

    public void ShowInfo(string title, string body, string? actionKey = null)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("action", actionKey ?? "activate")
                .AddText(title)
                .AddText(body)
                .AddAudio(new Uri("ms-winsoundevent:Notification.Default"), silent: Silent)
                .Show(t => t.Group = "info");
        }
        catch (Exception ex) { _core.Log.Warn("Toast", ex.Message); }
    }

    public void UpdateBadge(int unread) { }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        args.TryGetValue("action", out var action);
        args.TryGetValue("key", out var key);
        Dispatcher.UIThread.Post(async () =>
        {
            var main = AppHost.Main;
            if (main is null) return;
            switch (action)
            {
                case "reply" when key is not null && e.UserInput.TryGetValue("reply", out var v) && v is string text && !string.IsNullOrWhiteSpace(text):
                    await QuickReply(key, text.Trim());
                    break;
                case "open" when key is not null:
                    main.OpenConversation(key);
                    break;
                case "discover":
                    AppHost.ActivateWindow?.Invoke();
                    main.Navigate(Page.Contacts);
                    main.Contacts.ShowDiscoverCommand.Execute(null);
                    break;
                default:
                    AppHost.ActivateWindow?.Invoke();
                    break;
            }
        });
    }

    private async Task QuickReply(string key, string text)
    {
        try
        {
            if (!_core.IsConnected) throw new InvalidOperationException(L.T("Not connected to a radio."));
            if (key.StartsWith("dm:") && _core.Contacts.Get(key[3..]) is { } contact)
                _core.Messages.QueueDirectMessage(contact, text);
            else if (key.StartsWith("ch:") && int.TryParse(key[3..], out var idx))
                _core.Messages.QueueChannelMessage(idx, text);
            else if (key.StartsWith("room:") && _core.Db.GetRemoteSession(key[5..]) is { } room)
                _core.Rooms.Post(room, text);
            _core.Messages.MarkRead(key);
        }
        catch (Exception ex)
        {
            _core.Log.Warn("Toast", "Quick reply failed: " + ex.Message);
            ShowInfo(L.T("Reply not sent"), ex.Message);
        }
        await Task.CompletedTask;
    }

    public static void Shutdown()
    {
        try { ToastNotificationManagerCompat.History.Clear(); } catch { /* ignore */ }
    }
}
