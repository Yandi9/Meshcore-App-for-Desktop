using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Windows.ViewModels;

public enum ChatFilter { All, Unread, Direct, Channels, Rooms }

public sealed partial class ConversationListItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Preview { get; init; } = "";
    public string TimeText { get; init; } = "";
    public long SortKey { get; init; }
    public int Unread { get; init; }
    public int Mentions { get; init; }
    public bool IsMuted { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsRoomConnected { get; init; }
    public required string IconKey { get; init; }
    public required string AvatarText { get; init; }
    public string? Emoji { get; init; }
    public string? AvatarIcon => ShowIcon ? IconKey : null;
    public required string AvatarColor { get; init; }
    public ChatFilter Kind { get; init; }
    public bool HasUnread => Unread > 0;
    public bool HasUnreadMuted => Unread > 0 && IsMuted;
    public bool HasUnreadActive => Unread > 0 && !IsMuted;
    public bool HasMentions => Mentions > 0;
    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
    public bool ShowIcon => Kind != ChatFilter.Direct;
}

public sealed partial class ChatsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly Debouncer _reload;
    private List<ConversationListItem> _all = new();
    private bool _visible = true;

    public ChatsViewModel(MainWindowViewModel main)
    {
        _main = main;
        _reload = new Debouncer(TimeSpan.FromMilliseconds(150), Reload);
        OnData(c =>
        {
            if (c.Kind is DataKind.Conversations or DataKind.Contacts or DataKind.Channels or DataKind.Rooms or DataKind.Sessions or DataKind.Radio)
                _reload.Trigger();
        });
    }

    public ObservableCollection<ConversationListItem> Conversations { get; } = new();
    /// <summary>Every conversation regardless of the current search/filter.</summary>
    public IReadOnlyList<ConversationListItem> AllItems => _all;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ChatFilter _filter = ChatFilter.All;
    [ObservableProperty] private ConversationListItem? _selected;
    [ObservableProperty] private ConversationViewModel? _active;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int _totalUnread;
    [ObservableProperty] private string _unreadSummary = "";

    public IReadOnlyList<ChatFilter> Filters { get; } = Enum.GetValues<ChatFilter>();

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(ChatFilter value) => ApplyFilter();

    private bool _rebuilding;

    partial void OnSelectedChanged(ConversationListItem? oldValue, ConversationListItem? newValue)
    {
        if (_rebuilding) return;
        if (newValue is not null && Active is not null && Active.Key == newValue.Key) return;
        Active?.Deactivate();
        Active = newValue is null ? null : ConversationViewModel.Create(newValue.Key, _main);
        Active?.Activate(_visible);
        if (Active is null && Core.Settings.Current.LastConversation is not null) Core.Settings.Update(s => s.LastConversation = null);
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        Active?.SetOpen(visible);
    }

    public void OpenByKey(string key)
    {
        Reload();
        var item = _all.FirstOrDefault(c => c.Key == key);
        if (item is null) return;
        Filter = ChatFilter.All;
        Search = "";
        Selected = Conversations.FirstOrDefault(c => c.Key == key) ?? item;
    }

    public void Reload()
    {
        var radioId = Core.RadioId;
        if (radioId is null)
        {
            _all = new();
            ApplyFilter();
            return;
        }
        var db = Core.Db;
        var last = db.GetLastMessages(radioId);
        var list = new List<ConversationListItem>();
        foreach (var c in db.GetContacts(radioId))
        {
            if (c.IsBlocked) continue;
            last.TryGetValue(c.Id, out var m);
            if (m is null && c.UnreadCount == 0 && c.DraftText is null) continue;
            list.Add(new ConversationListItem
            {
                Key = MessageService.DmKey(c.Id),
                Title = c.DisplayName,
                Preview = m is null ? "" : m.IsOutgoing ? L.F("You: {0}", OneLine(m.Text)) : OneLine(m.Text),
                TimeText = m is null ? "" : Formatters.RelativeTime(m.Date),
                SortKey = Math.Max(c.LastMessageAt, m?.SortDate ?? 0),
                Unread = c.UnreadCount,
                Mentions = c.UnreadMentions,
                IsMuted = c.IsMuted,
                IsFavorite = c.IsFavorite,
                IconKey = Formatters.ContactIcon(c.ContactType),
                AvatarText = Formatters.Initials(c.DisplayName),
                Emoji = Formatters.AvatarEmoji(c.DisplayName),
                AvatarColor = Formatters.ColorFor(c.PublicKeyHex),
                Kind = ChatFilter.Direct,
            });
        }
        foreach (var ch in db.GetChannels(radioId))
        {
            last.TryGetValue("ch:" + ch.Idx, out var m);
            list.Add(new ConversationListItem
            {
                Key = MessageService.ChannelKey(ch.Idx),
                Title = ch.DisplayName,
                Preview = m is null ? (ch.IsPublic ? L.T("Public channel") : ch.IsHashtag ? L.T("Hashtag channel") : L.T("Private channel"))
                    : m.IsOutgoing ? L.F("You: {0}", OneLine(m.Text)) : (m.SenderName ?? "?") + ": " + OneLine(m.Text),
                TimeText = m is null ? "" : Formatters.RelativeTime(m.Date),
                SortKey = Math.Max(ch.LastMessageAt, m?.SortDate ?? 0),
                Unread = ch.UnreadCount,
                Mentions = ch.UnreadMentions,
                IsMuted = ch.IsMuted,
                IconKey = ch.IsPublic ? "Icon.Earth" : ch.IsHashtag ? "Icon.Pound" : "Icon.Lock",
                AvatarText = ch.IsHashtag ? "#" : Formatters.Initials(ch.DisplayName),
                AvatarColor = ch.IsPublic ? "#22A559" : Formatters.ColorFor(ch.Secret.ToHex()),
                Kind = ChatFilter.Channels,
            });
        }
        foreach (var r in db.GetRemoteSessions(radioId).Where(s => s.IsRoom))
        {
            var msgs = db.GetRoomMessages(r.Id, 1);
            var m = msgs.LastOrDefault();
            list.Add(new ConversationListItem
            {
                Key = MessageService.RoomKey(r.Id),
                Title = r.Name,
                Preview = m is null ? (r.IsConnected ? L.T("Room · signed in") : L.T("Room · not signed in"))
                    : m.IsFromSelf ? L.F("You: {0}", OneLine(m.Text)) : $"{m.AuthorName}: {OneLine(m.Text)}",
                TimeText = m is null ? "" : Formatters.RelativeTime(DateTimeOffset.FromUnixTimeSeconds(m.Timestamp)),
                SortKey = Math.Max(r.LastActivity, (m?.Timestamp ?? 0) * 1000),
                Unread = r.UnreadCount,
                IsMuted = r.NotificationLevel == (int)NotificationLevel.Muted,
                IsRoomConnected = r.IsConnected,
                IconKey = "Icon.ForumOutline",
                AvatarText = Formatters.Initials(r.Name),
                Emoji = Formatters.AvatarEmoji(r.Name),
                AvatarColor = "#F59E0B",
                Kind = ChatFilter.Rooms,
            });
        }
        _all = list.OrderByDescending(x => x.SortKey).ToList();
        TotalUnread = _all.Where(x => !x.IsMuted).Sum(x => x.Unread);
        var chats = _all.Count(x => x.Unread > 0);
        UnreadSummary = TotalUnread == 0 ? "" : L.Plural(chats, "{1} unread in {0} chat", "{1} unread in {0} chats", TotalUnread);
        ApplyFilter();
    }

    private static string OneLine(string s)
    {
        // Shared contacts show as "Contact: name" rather than their long key.
        if (s.Contains('<')) s = string.Concat(LinkDetector.Tokenize(s).Select(t => t.Text));
        var t = s.Replace('\n', ' ').Replace('\r', ' ');
        return t.Length > 90 ? t[..90] + "…" : t;
    }

    private void ApplyFilter()
    {
        var q = Search.Trim();
        var items = _all.Where(c => Filter switch
        {
            ChatFilter.Unread => c.Unread > 0,
            ChatFilter.Direct => c.Kind == ChatFilter.Direct,
            ChatFilter.Channels => c.Kind == ChatFilter.Channels,
            ChatFilter.Rooms => c.Kind == ChatFilter.Rooms,
            _ => true,
        }).Where(c => q.Length == 0 || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Preview.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var selectedKey = Active?.Key ?? Selected?.Key;
        _rebuilding = true;
        try
        {
            Conversations.Clear();
            foreach (var i in items) Conversations.Add(i);
            // Keep the active conversation without recreating it.
            Selected = selectedKey is null ? null : Conversations.FirstOrDefault(c => c.Key == selectedKey);
        }
        finally { _rebuilding = false; }
        IsEmpty = Conversations.Count == 0;
    }

    [RelayCommand]
    private async Task NewChat()
    {
        var vm = new NewChatViewModel(this);
        await AppHost.Dialogs.ShowDialog(vm, L.T("New conversation"), 460, 560);
    }

    [RelayCommand]
    private void MarkRead(ConversationListItem item)
    {
        Core.Messages.MarkRead(item.Key);
        Core.Notify(DataKind.Conversations);
    }

    [RelayCommand]
    private void ToggleMute(ConversationListItem item)
    {
        var level = item.IsMuted ? NotificationLevel.All : NotificationLevel.Muted;
        if (item.Key.StartsWith("dm:") && Core.Contacts.Get(item.Key[3..]) is { } c) Core.Contacts.SetNotificationLevel(c, level);
        else if (item.Key.StartsWith("ch:") && Core.RadioId is { } rid && Core.Db.GetChannel(rid, int.Parse(item.Key[3..])) is { } ch) Core.Channels.SetNotificationLevel(ch, level);
        else if (item.Key.StartsWith("room:") && Core.Db.GetRemoteSession(item.Key[5..]) is { } room)
        {
            room.NotificationLevel = (int)level;
            Core.Db.UpsertRemoteSession(room);
        }
        Core.Notify(DataKind.Conversations);
    }

    [RelayCommand]
    private async Task ClearConversation(ConversationListItem item)
    {
        if (!await AppHost.Dialogs.Confirm(L.F("Clear {0}?", item.Title), L.T("Deletes this conversation's messages from this PC. Your radio and contacts aren't changed."), L.T("Clear"), true)) return;
        if (item.Key.StartsWith("dm:")) Core.Db.ClearDirectMessages(item.Key[3..]);
        else if (item.Key.StartsWith("ch:") && Core.RadioId is { } rid && Core.Db.GetChannel(rid, int.Parse(item.Key[3..])) is { } ch) Core.Channels.ClearMessages(ch);
        else if (item.Key.StartsWith("room:")) Core.Db.ClearRoomMessages(item.Key[5..]);
        Core.Messages.MarkRead(item.Key);
        Core.Notify(DataKind.Messages, item.Key);
        Core.Notify(DataKind.Conversations);
    }

    [RelayCommand]
    private async Task CustomizeAppearance(ConversationListItem item) =>
        await AppHost.Dialogs.ShowDialog(new ChatAppearanceViewModel(item.Key, L.F("Appearance · {0}", item.Title)), L.T("Chat appearance"), 860, 700);

    [RelayCommand]
    private void MarkAllRead()
    {
        foreach (var c in _all.Where(c => c.Unread > 0)) Core.Messages.MarkRead(c.Key);
        Reload();
    }
}

/// <summary>Picker for starting a DM, joining a channel or opening a room.</summary>
public sealed partial class NewChatViewModel : ViewModelBase
{
    private readonly ChatsViewModel _chats;

    public NewChatViewModel(ChatsViewModel chats)
    {
        _chats = chats;
        _all = Core.Contacts.GetAll().Where(c => c.ContactType != ContactType.Repeater && !c.IsBlocked).OrderBy(c => c.DisplayName).ToList();
        ApplyFilter();
    }

    private readonly List<ContactRecord> _all;
    public ObservableCollection<ContactRecord> Contacts { get; } = new();
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _hashtag = "";
    [ObservableProperty] private string _privateName = "";
    [ObservableProperty] private string _channelLink = "";
    [ObservableProperty] private string? _error;
    public Action? Close { get; set; }

    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Contacts.Clear();
        foreach (var c in _all.Where(c => Search.Length == 0 || c.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase))) Contacts.Add(c);
    }

    [RelayCommand]
    private async Task OpenContact(ContactRecord c)
    {
        if (c.ContactType == ContactType.Room)
        {
            Close?.Invoke();
            await RoomLoginFlow.JoinAsync(c);
            return;
        }
        Close?.Invoke();
        _chats.OpenByKeyForce(MessageService.DmKey(c.Id));
    }

    [RelayCommand]
    private async Task JoinHashtag()
    {
        Error = null;
        try
        {
            var ch = await Core.Channels.JoinHashtagAsync(Hashtag);
            Close?.Invoke();
            _chats.OpenByKeyForce(MessageService.ChannelKey(ch.Idx));
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private async Task CreatePrivate()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(PrivateName)) { Error = L.T("Enter a channel name."); return; }
        try
        {
            var ch = await Core.Channels.CreatePrivateAsync(PrivateName.Trim());
            Close?.Invoke();
            _chats.OpenByKeyForce(MessageService.ChannelKey(ch.Idx));
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private async Task JoinPublic()
    {
        Error = null;
        try
        {
            var ch = await Core.Channels.AddPublicAsync();
            Close?.Invoke();
            _chats.OpenByKeyForce(MessageService.ChannelKey(ch.Idx));
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private async Task JoinFromLink()
    {
        Error = null;
        try
        {
            var ch = await Core.Channels.JoinFromUriAsync(ChannelLink);
            Close?.Invoke();
            _chats.OpenByKeyForce(MessageService.ChannelKey(ch.Idx));
        }
        catch (Exception ex) { Error = ex.Message; }
    }
}

public sealed partial class ChatsViewModel
{
    /// <summary>Opens a conversation even if it has no messages yet.</summary>
    public void OpenByKeyForce(string key)
    {
        Reload();
        var item = _all.FirstOrDefault(c => c.Key == key);
        if (item is null && key.StartsWith("dm:") && Core.Db.GetContact(key[3..]) is { } c)
        {
            item = new ConversationListItem
            {
                Key = key, Title = c.DisplayName, IconKey = Formatters.ContactIcon(c.ContactType),
                AvatarText = Formatters.Initials(c.DisplayName), Emoji = Formatters.AvatarEmoji(c.DisplayName), AvatarColor = Formatters.ColorFor(c.PublicKeyHex), Kind = ChatFilter.Direct,
                SortKey = long.MaxValue,
            };
            _all.Insert(0, item);
        }
        if (item is null) return;
        Filter = ChatFilter.All;
        Search = "";
        ApplyFilter();
        Selected = Conversations.FirstOrDefault(x => x.Key == key);
        _main.Navigate(Page.Chats);
    }
}
