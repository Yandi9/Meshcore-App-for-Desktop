using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MeshCore;

using MC1.Windows.Services;

namespace MC1.Windows.ViewModels;

public enum ConversationKind { Direct, Channel, Room }

public sealed partial class ConversationViewModel : ViewModelBase
{
    private const int PageSize = 200;
    private readonly MainWindowViewModel _main;
    private readonly Debouncer _reload;
    private int _limit = PageSize;
    private bool _active;
    private Action<DataChange>? _handler;

    private ConversationViewModel(string key, ConversationKind kind, MainWindowViewModel main)
    {
        Key = key;
        Kind = kind;
        _main = main;
        _reload = new Debouncer(TimeSpan.FromMilliseconds(60), LoadMessages);
    }

    public static ConversationViewModel? Create(string key, MainWindowViewModel main)
    {
        ConversationViewModel? vm = null;
        if (key.StartsWith("dm:") && Core.Db.GetContact(key[3..]) is { } c)
            vm = new ConversationViewModel(key, ConversationKind.Direct, main) { Contact = c };
        else if (key.StartsWith("ch:") && Core.RadioId is { } rid && Core.Db.GetChannel(rid, int.Parse(key[3..])) is { } ch)
            vm = new ConversationViewModel(key, ConversationKind.Channel, main) { Channel = ch };
        else if (key.StartsWith("room:") && Core.Db.GetRemoteSession(key[5..]) is { } r)
            vm = new ConversationViewModel(key, ConversationKind.Room, main) { Room = r };
        vm?.RefreshHeader();
        vm?.RefreshAppearance();
        vm?.LoadMessages();
        vm?.LoadDraft();
        return vm;
    }

    public string Key { get; }
    public ConversationKind Kind { get; }
    public ContactRecord? Contact { get; private set; }
    public ChannelRecord? Channel { get; private set; }
    public RemoteSessionRecord? Room { get; private set; }

    public ObservableCollection<MessageItemViewModel> Messages { get; } = new();
    public ObservableCollection<string> MentionSuggestions { get; } = new();
    public ObservableCollection<string> QuickEmoji { get; } = new();

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _avatarText = "";
    [ObservableProperty] private string _avatarColor = "#2463EB";
    [ObservableProperty] private string _iconKey = "Icon.Account";
    [ObservableProperty] private string? _emoji;
    [ObservableProperty] private string? _avatarIcon;
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private string _byteCounter = "";
    [ObservableProperty] private bool _overLimit;
    [ObservableProperty] private bool _canLoadOlder;
    [ObservableProperty] private MessageItemViewModel? _replyingTo;
    [ObservableProperty] private bool _showMentions;
    [ObservableProperty] private bool _canPost = true;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private bool _isRoomSignedIn;
    [ObservableProperty] private bool _isEmpty;

    public bool IsDirect => Kind == ConversationKind.Direct;
    public bool IsChannel => Kind == ConversationKind.Channel;
    public bool IsRoom => Kind == ConversationKind.Room;
    public int MaxBytes => Kind == ConversationKind.Channel ? Core.Messages.MaxChannelMessageBytes : MessageService.MaxDirectMessageBytes;

    /// <summary>Raised when new messages were appended (the view scrolls to the end).</summary>
    public event Action<bool>? MessagesAppended;

    /// <summary>Background and colours chosen for this chat (null = app theme).</summary>
    [ObservableProperty] private ChatAppearance? _appearance;

    private void RefreshAppearance() => Appearance = ChatAppearance.From(ChatThemes.Effective(Key));
    private void OnThemesChanged() => Ui(RefreshAppearance);

    [RelayCommand]
    private async Task CustomizeAppearance()
    {
        await AppHost.Dialogs.ShowDialog(new ChatAppearanceViewModel(Key, L.F("Appearance · {0}", Title)), L.T("Chat appearance"), 860, 700);
    }

    public void Activate(bool visible)
    {
        _active = true;
        Core.Settings.Update(st => st.LastConversation = Core.RadioId is { } r ? r + "|" + Key : null);
        _handler = OnDataChanged;
        Core.DataChanged += _handler;
        ChatThemes.Changed += OnThemesChanged;
        Track(_unhook ??= Unhook);
        SetOpen(visible);
        foreach (var e in Core.Settings.Current.RecentEmoji.Take(6)) QuickEmoji.Add(e);
    }

    public void Deactivate()
    {
        SaveDraft();
        _active = false;
        Unhook();
        if (_unhook is not null) Untrack(_unhook);
        Core.Messages.SetConversationOpen(Key, false);
    }

    private Action? _unhook;

    private void Unhook()
    {
        if (_handler is not null) Core.DataChanged -= _handler;
        ChatThemes.Changed -= OnThemesChanged;
    }

    public void SetOpen(bool open)
    {
        Core.Messages.SetConversationOpen(Key, open && _active);
        // Coming back to the chat (window restored, page shown): place the "New messages" line above what arrived
        // meanwhile, then count it as read.
        if (open && _active && Core.Messages.UnreadCount(Key) > 0) LoadMessages();
    }

    /// <summary>Id of the first message the user hadn't seen when the chat was opened / came back into view.</summary>
    private string? _dividerId;

    /// <summary>Raised when a new "New messages" line was placed (the view scrolls to it).</summary>
    public event Action? DividerPlaced;

    /// <summary>True while this chat is on screen with the window in front.</summary>
    private bool IsViewing => _active && Core.Messages.IsConversationOpen(Key);

    [ObservableProperty] private bool _hasNewDivider;

    /// <summary>Removes the "New messages" line (e.g. after the user sends a message).</summary>
    public void ClearDivider()
    {
        if (_dividerId is null) return;
        _dividerId = null;
        HasNewDivider = false;
        foreach (var m in Messages) m.ShowNewDivider = false;
    }

    private void OnDataChanged(DataChange c)
    {
        if (!_active) return;
        switch (c.Kind)
        {
            case DataKind.Messages when c.Key == Key || c.Key is null:
            case DataKind.RoomMessages when Room is not null && c.Key == Room.Id:
            case DataKind.Reactions:
            case DataKind.Repeats:
                _reload.Trigger();
                break;
            case DataKind.Contacts or DataKind.Channels or DataKind.Sessions or DataKind.Radio:
                Ui(RefreshHeader);
                break;
            case DataKind.Conversations:
                // E.g. the radio's message backlog finished syncing: unread messages here get their "New messages" line.
                Ui(() => { if (_active && Core.Messages.UnreadCount(Key) > 0) _reload.Trigger(); });
                break;
        }
    }

    private void RefreshHeader()
    {
        switch (Kind)
        {
            case ConversationKind.Direct:
                Contact = Core.Db.GetContact(Contact!.Id) ?? Contact;
                Title = Contact.DisplayName;
                var route = Contact.IsFloodRouted ? L.T("flood routing") : Contact.HopCount == 0 ? L.T("direct") : L.Plural(Contact.HopCount, "{0} hop", "{0} hops");
                Subtitle = $"{Formatters.TypeName(Contact.ContactType)} · {route} · {L.F("heard {0}", Formatters.AgoMs(Math.Max(Contact.LastHeard, Contact.LastAdvert * 1000)))}";
                AvatarText = Formatters.Initials(Contact.DisplayName);
                Emoji = Formatters.AvatarEmoji(Contact.DisplayName);
                AvatarIcon = null;
                AvatarColor = Formatters.ColorFor(Contact.PublicKeyHex);
                IconKey = Formatters.ContactIcon(Contact.ContactType);
                CanPost = Contact.ContactType != ContactType.Repeater;
                Banner = Contact.ContactType == ContactType.Repeater ? L.T("Repeaters don't accept messages. Use Manage to administer this node.") : null;
                break;
            case ConversationKind.Channel:
                Channel = Core.RadioId is { } rid ? Core.Db.GetChannel(rid, Channel!.Idx) ?? Channel : Channel;
                Title = Channel!.DisplayName;
                Subtitle = (Channel.IsPublic ? L.T("Public channel") : Channel.IsHashtag ? L.T("Hashtag channel") : L.T("Private channel")) + " · " + L.F("slot {0}", Channel.Idx)
                           + (Channel.FloodScope is { } fs ? " · " + L.F("scope {0}", fs == "all" ? L.T("all regions") : fs.Replace("region:", "")) : "");
                AvatarText = Channel.IsHashtag ? "#" : Formatters.Initials(Channel.DisplayName);
                AvatarColor = Channel.IsPublic ? "#22A559" : Formatters.ColorFor(Channel.Secret.ToHex());
                IconKey = Channel.IsPublic ? "Icon.Earth" : Channel.IsHashtag ? "Icon.Pound" : "Icon.Lock";
                Emoji = null;
                AvatarIcon = IconKey;
                CanPost = true;
                break;
            case ConversationKind.Room:
                Room = Core.Db.GetRemoteSession(Room!.Id) ?? Room;
                Title = Room.Name;
                IsRoomSignedIn = Room.IsConnected;
                Subtitle = !Room.IsConnected ? L.T("Room · not signed in") : Room.PermissionLevel switch
                {
                    RoomPermission.Admin => L.T("Room · signed in (admin)"),
                    RoomPermission.ReadWrite => L.T("Room · signed in (member)"),
                    _ => L.T("Room · signed in (read-only)"),
                };
                AvatarText = Formatters.Initials(Room.Name);
                AvatarColor = "#F59E0B";
                IconKey = "Icon.ForumOutline";
                Emoji = Formatters.AvatarEmoji(Room.Name);
                AvatarIcon = IconKey;
                CanPost = Room.IsConnected && Room.CanPost;
                Banner = !Room.IsConnected ? L.T("You're not signed in to this room. Sign in to receive new posts.") : !Room.CanPost ? L.T("You have read-only access.") : null;
                break;
        }
        UpdateCounter();
    }

    public void LoadMessages()
    {
        var unread = Core.Messages.UnreadCount(Key);
        // Make sure every unread message is loaded so the "New messages" line can sit above the first of them.
        if (unread > 0) _limit = Math.Min(Math.Max(_limit, unread + 30), 5000);
        var items = new List<MessageItemViewModel>();
        switch (Kind)
        {
            case ConversationKind.Direct:
                var dms = Core.Db.GetDirectMessages(Contact!.Id, _limit);
                CanLoadOlder = dms.Count >= _limit;
                items.AddRange(dms.Select(m => MessageItemViewModel.From(m, this)));
                break;
            case ConversationKind.Channel when Core.RadioId is { } rid:
                var cms = Core.Db.GetChannelMessages(rid, Channel!.Idx, _limit);
                CanLoadOlder = cms.Count >= _limit;
                items.AddRange(cms.Select(m => MessageItemViewModel.From(m, this)));
                break;
            case ConversationKind.Room:
                var rms = Core.Db.GetRoomMessages(Room!.Id, _limit);
                CanLoadOlder = rms.Count >= _limit;
                items.AddRange(rms.Select(m => MessageItemViewModel.From(m, this)));
                break;
        }
        // Day separators and sender grouping.
        DateTime? lastDay = null;
        string? lastSender = null;
        DateTimeOffset lastTime = DateTimeOffset.MinValue;
        foreach (var i in items)
        {
            var day = i.Time.LocalDateTime.Date;
            i.DayHeader = lastDay != day ? Formatters.DayHeader(i.Time) : null;
            i.ShowSender = i.CanShowSender && (i.DayHeader is not null || lastSender != i.SenderKey || (i.Time - lastTime).TotalMinutes > 10);
            lastDay = day;
            lastSender = i.SenderKey;
            lastTime = i.Time;
        }
        // "New messages" line: above the oldest of the last `unread` received messages (like the iPhone app).
        if (unread > 0 && _active)
        {
            // The unread ones are the most recently *received* messages; messages the radio kept while the app was
            // closed are sorted by when they were sent, so they may sit above older ones. The line goes above the
            // first of them in the chat.
            var newest = items.Where(i => !i.IsOutgoing).OrderByDescending(i => i.ReceivedAt).Take(unread).Select(i => i.Id).ToHashSet();
            var id = items.FirstOrDefault(i => newest.Contains(i.Id))?.Id;
            if (id is not null && id != _dividerId)
            {
                // While the old line is still unseen (more arrived while minimised), keep whichever is older.
                var oldIndex = _dividerId is null ? -1 : items.FindIndex(i => i.Id == _dividerId);
                var newIndex = items.FindIndex(i => i.Id == id);
                if (oldIndex < 0 || !_dividerStillUnseen || newIndex < oldIndex) _dividerId = id;
            }
            _dividerStillUnseen = true;
        }
        foreach (var i in items) i.ShowNewDivider = _dividerId is not null && i.Id == _dividerId;
        HasNewDivider = items.Any(i => i.ShowNewDivider);
        var appended = Merge(items);
        IsEmpty = Messages.Count == 0;
        // The backlog the radio kept while the app was closed stays unread until it has all arrived.
        if (unread > 0 && IsViewing && !Core.IsInitialSyncRunning)
        {
            Core.Messages.MarkRead(Key);
            _dividerStillUnseen = false;
            if (HasNewDivider) DividerPlaced?.Invoke();
            else if (appended) MessagesAppended?.Invoke(false);
        }
        else if (appended) MessagesAppended?.Invoke(items.LastOrDefault()?.IsOutgoing ?? false);
    }

    /// <summary>True while the messages below the current line haven't been looked at yet.</summary>
    private bool _dividerStillUnseen;

    private bool Merge(List<MessageItemViewModel> items)
    {
        // A busy chat (more messages than one page): each new message moves the page of newest messages on by one, so
        // the page starts further down the list on screen. Keep the bubbles already shown (the older ones stay
        // visible) and just add the new ones — rebuilding them all made new messages take a couple of seconds to appear.
        if (items.Count > 0 && Messages.Count > 0 && Messages[0].Id != items[0].Id)
        {
            var start = -1;
            for (var k = 1; k < Messages.Count; k++)
                if (Messages[k].Id == items[0].Id) { start = k; break; }
            var overlap = start > 0 ? Messages.Count - start : 0;
            if (start > 0 && overlap <= items.Count && Enumerable.Range(0, overlap).All(k => Messages[start + k].Id == items[k].Id))
            {
                for (var k = 0; k < overlap; k++) Messages[start + k].UpdateFrom(items[k]);
                var added = false;
                for (var k = overlap; k < items.Count; k++) { Messages.Add(items[k]); added = true; }
                // Load as many as are shown from now on, so the next change lines up from the first message.
                _limit = Math.Min(Math.Max(_limit, Messages.Count), 5000);
                return added;
            }
        }
        var sameIds = Messages.Count <= items.Count && Messages.Select(m => m.Id).SequenceEqual(items.Take(Messages.Count).Select(m => m.Id));
        if (!sameIds)
        {
            Messages.Clear();
            foreach (var i in items) Messages.Add(i);
            return true;
        }
        for (var k = 0; k < Messages.Count; k++) Messages[k].UpdateFrom(items[k]);
        var appended = false;
        for (var k = Messages.Count; k < items.Count; k++) { Messages.Add(items[k]); appended = true; }
        return appended;
    }

    [RelayCommand]
    private void LoadOlder()
    {
        _limit += PageSize;
        Messages.Clear();
        LoadMessages();
    }

    // MARK: Composing

    partial void OnDraftChanged(string value)
    {
        UpdateCounter();
        UpdateMentions();
    }

    private void UpdateCounter()
    {
        var bytes = (Draft ?? "").Utf8Length() + (ReplyingTo is { } r ? MentionUtilities.BuildReplyText(r.SenderDisplay, r.Text).Utf8Length() : 0);
        var max = MaxBytes;
        OverLimit = bytes > max;
        ByteCounter = bytes > max * 0.7 ? $"{bytes}/{max}" : "";
    }

    private void UpdateMentions()
    {
        MentionSuggestions.Clear();
        var q = MentionUtilities.DetectActiveMention(Draft ?? "");
        if (q is null || Kind == ConversationKind.Direct && Contact is null) { ShowMentions = false; return; }
        var names = new List<string>();
        if (Kind == ConversationKind.Channel)
            names.AddRange(Messages.Where(m => !m.IsOutgoing && m.SenderName is not null).Select(m => m.SenderName!).Reverse().Distinct());
        names.AddRange(Core.Contacts.GetAll().Where(c => c.ContactType == ContactType.Chat).Select(c => c.Name));
        foreach (var n in names.Distinct().Where(n => q.Length == 0 || n.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(8)) MentionSuggestions.Add(n);
        ShowMentions = MentionSuggestions.Count > 0;
    }

    [RelayCommand]
    private void InsertMention(string name)
    {
        var text = Draft ?? "";
        var at = text.LastIndexOf('@');
        if (at >= 0) text = text[..at];
        Draft = text + MentionUtilities.CreateMention(name) + " ";
        ShowMentions = false;
    }

    [RelayCommand]
    private async Task Send()
    {
        var text = (Draft ?? "").Trim();
        if (text.Length == 0 || !CanPost) return;
        if (ReplyingTo is { } r) text = MentionUtilities.BuildReplyText(r.SenderDisplay, r.Text) + text;
        if (text.Utf8Length() > MaxBytes)
        {
            await AppHost.Dialogs.ShowError(L.T("Message too long"), L.F("Messages can be at most {0} bytes here. Emoji and accented letters use more than one byte.", MaxBytes));
            return;
        }
        if (!Core.IsConnected)
        {
            await AppHost.Dialogs.ShowError(L.T("Not connected"), L.T("Connect to your radio to send messages."));
            return;
        }
        await Guard(async () =>
        {
            switch (Kind)
            {
                case ConversationKind.Direct: Core.Messages.QueueDirectMessage(Contact!, text, ReplyingTo?.Id); break;
                case ConversationKind.Channel: Core.Messages.QueueChannelMessage(Channel!.Idx, text); break;
                case ConversationKind.Room: Core.Rooms.Post(Room!, text); break;
            }
            Draft = "";
            ReplyingTo = null;
            SaveDraft();
            ClearDivider();
            // It's saved already: show it right away rather than after the change notification's short delay.
            LoadMessages();
            await Task.CompletedTask;
        }, L.T("Send"));
    }

    [RelayCommand]
    private void CancelReply() { ReplyingTo = null; UpdateCounter(); }

    [RelayCommand]
    public void AppendEmoji(string emoji) => Draft = (Draft ?? "") + emoji;

    [RelayCommand]
    private Task OpenLink(LinkDetector.Token token) => HandleLinkAsync(token);

    private void LoadDraft()
    {
        Draft = Kind switch
        {
            ConversationKind.Direct => Contact?.DraftText ?? "",
            ConversationKind.Channel => Channel?.DraftText ?? "",
            _ => "",
        };
    }

    private void SaveDraft()
    {
        var d = string.IsNullOrWhiteSpace(Draft) ? null : Draft;
        if (Kind == ConversationKind.Direct && Contact is not null && Contact.DraftText != d) Core.Db.SetContactDraft(Contact.Id, d);
        if (Kind == ConversationKind.Channel && Channel is not null && Core.RadioId is { } rid && Channel.DraftText != d) Core.Db.SetChannelDraft(rid, Channel.Idx, d);
    }

    // MARK: Message actions

    [RelayCommand]
    private void Reply(MessageItemViewModel m)
    {
        ReplyingTo = m;
        UpdateCounter();
        FocusComposerRequested?.Invoke();
    }

    public event Action? FocusComposerRequested;

    [RelayCommand]
    private async Task React(ReactionRequest req)
    {
        if (req.Message.Record is not { } rec) return;
        if (!Core.IsConnected) { await AppHost.Dialogs.ShowError(L.T("Not connected"), L.T("Connect to your radio to react.")); return; }
        await Guard(() => Core.Messages.SendReactionAsync(rec, req.Emoji), L.T("Reaction"));
        QuickEmoji.Clear();
        foreach (var e in Core.Settings.Current.RecentEmoji.Take(6)) QuickEmoji.Add(e);
    }

    [RelayCommand]
    private async Task Copy(MessageItemViewModel m) => await AppHost.Dialogs.CopyToClipboard(m.Text);

    [RelayCommand]
    private void Resend(MessageItemViewModel m)
    {
        if (m.RoomRecord is { } rr) { Core.Rooms.Retry(rr); return; }
        if (m.Record is not { } rec) return;
        if (rec.ChannelIndex is not null) Core.Messages.ResendChannel(rec.Id);
        else Core.Messages.ResendDirect(rec.Id);
    }

    [RelayCommand]
    private async Task Delete(MessageItemViewModel m)
    {
        if (m.Record is { } rec && await AppHost.Dialogs.Confirm(L.T("Delete message?"), L.T("This removes the message from this PC only."), L.T("Delete"), true))
            Core.Messages.DeleteMessage(rec);
    }

    [RelayCommand]
    private async Task ShowDetails(MessageItemViewModel m) =>
        await AppHost.Dialogs.ShowDialog(new MessageDetailsViewModel(m), L.T("Message details"), 520, 560);

    [RelayCommand]
    private async Task BlockSender(MessageItemViewModel m)
    {
        if (m.SenderName is not { } name) return;
        if (!await AppHost.Dialogs.Confirm(L.F("Block {0}?", name), L.T("Messages from this sender will be hidden on all channels. You can unblock them in Contacts ▸ Blocked."), L.T("Block"), true)) return;
        Core.Contacts.BlockChannelSender(name);
        LoadMessages();
    }

    [RelayCommand]
    private void MentionSender(MessageItemViewModel m)
    {
        if (m.SenderName is { } n) Draft = MentionUtilities.AppendMention(n, Draft ?? "");
        FocusComposerRequested?.Invoke();
    }

    [RelayCommand]
    private async Task OpenInfo()
    {
        switch (Kind)
        {
            case ConversationKind.Direct:
                _main.Navigate(Page.Contacts);
                _main.Contacts.Select(Contact!.Id);
                break;
            case ConversationKind.Channel:
                await AppHost.Dialogs.ShowDialog(new ChannelInfoViewModel(Channel!), L.T("Channel info"), 520, 640);
                RefreshHeader();
                break;
            case ConversationKind.Room:
                await AppHost.Dialogs.ShowDialog(new RoomInfoViewModel(Room!), L.T("Room info"), 520, 560);
                RefreshHeader();
                break;
        }
    }

    [RelayCommand]
    private async Task SignInToRoom()
    {
        var contact = Core.Db.GetContactByKey(Room!.RadioId, Room.PublicKey);
        if (contact is null) { await AppHost.Dialogs.ShowError(L.T("Room not found"), L.T("This room is no longer in your radio's contacts.")); return; }
        await RoomLoginFlow.JoinAsync(contact);
        RefreshHeader();
    }

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Clear history?"), L.T("Delete all messages in this conversation from this PC?"), L.T("Clear"), true)) return;
        switch (Kind)
        {
            case ConversationKind.Direct: Core.Db.ClearDirectMessages(Contact!.Id); break;
            case ConversationKind.Channel: Core.Channels.ClearMessages(Channel!); break;
            case ConversationKind.Room: Core.Rooms.ClearHistory(Room!); break;
        }
        Messages.Clear();
        LoadMessages();
        Core.Notify(DataKind.Conversations);
    }

    [RelayCommand]
    private void ShowOnMap(string mapUri)
    {
        if (MeshCoreUrl.ParseMap(mapUri) is { } p)
        {
            _main.Navigate(Page.Map);
            _main.Map.CenterOn(p.Lat, p.Lon, 14, L.T("Shared location"));
        }
    }

    public async Task HandleLinkAsync(LinkDetector.Token token)
    {
        switch (token.Kind)
        {
            case LinkDetector.TokenKind.Url:
                AppHost.Dialogs.OpenUrl(token.Target!);
                break;
            case LinkDetector.TokenKind.Coordinate:
                ShowOnMap(token.Target!);
                break;
            case LinkDetector.TokenKind.Hashtag:
                if (await AppHost.Dialogs.Confirm(L.F("Join {0}?", token.Target!.ToLowerInvariant()), L.T("Join this public hashtag channel on your radio?"), L.T("Join")))
                    await Guard(async () =>
                    {
                        var ch = await Core.Channels.JoinHashtagAsync(token.Target!);
                        _main.Chats.OpenByKeyForce(MessageService.ChannelKey(ch.Idx));
                    }, L.T("Join channel"));
                break;
            case LinkDetector.TokenKind.Mention:
                var c = Core.Contacts.GetAll().FirstOrDefault(x => x.Name == token.Target);
                if (c is not null) { _main.Navigate(Page.Contacts); _main.Contacts.Select(c.Id); }
                break;
            case LinkDetector.TokenKind.MeshCoreLink:
                await DeepLinks.HandleAsync(token.Target!, _main);
                break;
            case LinkDetector.TokenKind.Contact:
                if (ContactShareUtilities.Parse(token.Target!) is not { } shared) break;
                var existing = Core.Contacts.GetAll().FirstOrDefault(x => x.PublicKey.AsSpan().SequenceEqual(shared.PublicKey));
                if (existing is not null) { _main.Navigate(Page.Contacts); _main.Contacts.Select(existing.Id); break; }
                if (Core.SelfInfo?.PublicKey is { } mine && mine.AsSpan().SequenceEqual(shared.PublicKey))
                {
                    await AppHost.Dialogs.ShowError(L.T("That's you"), L.T("This is your own radio's contact card."));
                    break;
                }
                if (!await AppHost.Dialogs.Confirm(L.F("Add {0}?", shared.Name), Formatters.AddContactPrompt(shared.Type, shared.PublicKey), L.T("Add"))) break;
                await Guard(async () =>
                {
                    var rec = await Core.Contacts.AddAsync(shared.PublicKey, shared.Name, shared.Type);
                    _main.Navigate(Page.Contacts);
                    _main.Contacts.Select(rec.Id);
                }, L.T("Add contact"));
                break;
        }
    }
}

public sealed record ReactionRequest(MessageItemViewModel Message, string Emoji);
