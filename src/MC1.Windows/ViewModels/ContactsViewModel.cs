using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MC1.Windows.Services;
using MeshCore;

namespace MC1.Windows.ViewModels;

public enum ContactSegment { Contacts, Discover }
public enum ContactTypeFilter { All, Companions, Repeaters, Rooms, Sensors, Favorites, Blocked }

public sealed class ContactRow
{
    public required ContactRecord Contact { get; init; }
    public string Name => Contact.DisplayName;
    public string Detail { get; init; } = "";
    public string IconKey => Formatters.ContactIcon(Contact.ContactType);
    public string Color => Formatters.ContactColor(Contact.ContactType);
    public string AvatarText => Formatters.Initials(Contact.DisplayName);
    public string? Emoji => Formatters.AvatarEmoji(Contact.DisplayName);
    public int Unread => Contact.UnreadCount;
    public bool HasUnread => Contact.UnreadCount > 0;
    public string UnreadText => Contact.UnreadCount > 99 ? "99+" : Contact.UnreadCount.ToString();
    public bool IsFavorite => Contact.IsFavorite;
    public string Heard { get; init; } = "";
}

public sealed class DiscoveredRow
{
    public required DiscoveredNodeRecord Node { get; init; }
    public string Name => Node.Name;
    public string Detail => $"{Formatters.TypeName(Node.ContactType)} · {L.F("heard {0}", Formatters.AgoMs(Node.LastHeard))}" + (Node.HasLocation ? " · " + L.T("has location") : "");
    public string IconKey => Formatters.ContactIcon(Node.ContactType);
    public string? Emoji => Formatters.AvatarEmoji(Node.Name);
    public string Color => Formatters.ContactColor(Node.ContactType);
}

public sealed partial class ContactsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly Debouncer _reload;
    private List<ContactRecord> _contacts = new();

    public ContactsViewModel(MainWindowViewModel main)
    {
        _main = main;
        _reload = new Debouncer(TimeSpan.FromMilliseconds(200), Reload);
        OnData(c => { if (c.Kind is DataKind.Contacts or DataKind.Discovered or DataKind.Radio) _reload.Trigger(); });
    }

    public ObservableCollection<ContactRow> Contacts { get; } = new();
    public ObservableCollection<DiscoveredRow> Discovered { get; } = new();
    public ObservableCollection<ContactRecord> BlockedContacts { get; } = new();
    public ObservableCollection<BlockedSenderRecord> BlockedSenders { get; } = new();
    public IReadOnlyList<ContactTypeFilter> TypeFilters { get; } = Enum.GetValues<ContactTypeFilter>();

    [ObservableProperty] private ContactSegment _segment;
    [ObservableProperty] private ContactTypeFilter _typeFilter;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ContactRow? _selected;
    [ObservableProperty] private ContactDetailViewModel? _detail;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isContactsSegment = true;
    [ObservableProperty] private bool _isDiscoverSegment;
    /// <summary>The "Blocked" chip is picked: blocked contacts and channel senders are shown instead of the list.</summary>
    public bool IsBlockedFilter => TypeFilter == ContactTypeFilter.Blocked;
    public bool IsContactList => TypeFilter != ContactTypeFilter.Blocked;
    public bool HasNoBlocked => BlockedContacts.Count == 0 && BlockedSenders.Count == 0;

    partial void OnSegmentChanged(ContactSegment value)
    {
        IsContactsSegment = value == ContactSegment.Contacts;
        IsDiscoverSegment = value == ContactSegment.Discover;
        Reload();
    }

    partial void OnTypeFilterChanged(ContactTypeFilter value)
    {
        OnPropertyChanged(nameof(IsBlockedFilter));
        OnPropertyChanged(nameof(IsContactList));
        ApplyFilter();
    }
    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(ContactRow? value)
    {
        if (value is null) return;
        if (Detail?.Contact.Id == value.Contact.Id) { Detail.Refresh(); return; }
        Detail = new ContactDetailViewModel(value.Contact, _main);
    }

    [RelayCommand] private void ShowContacts() => Segment = ContactSegment.Contacts;
    [RelayCommand] private void ShowDiscover() => Segment = ContactSegment.Discover;
    [RelayCommand]
    private void ShowBlocked()
    {
        Segment = ContactSegment.Contacts;
        TypeFilter = ContactTypeFilter.Blocked;
    }

    public void Select(string contactId)
    {
        Segment = ContactSegment.Contacts;
        TypeFilter = ContactTypeFilter.All;
        Search = "";
        Reload();
        Selected = Contacts.FirstOrDefault(c => c.Contact.Id == contactId);
    }

    public void Reload()
    {
        _contacts = Core.Contacts.GetAll().ToList();
        ApplyFilter();
        Discovered.Clear();
        foreach (var d in Core.Contacts.GetDiscovered()) Discovered.Add(new DiscoveredRow { Node = d });
        _blockedSenders = Core.Contacts.BlockedSenders().ToList();
        var max = Core.Capabilities?.MaxContacts ?? Core.Radio?.MaxContacts ?? 0;
        CountText = max > 0 ? L.F("{0} of {1} on radio", _contacts.Count, max) : L.Plural(_contacts.Count, "{0} contact", "{0} contacts");
        Detail?.Refresh();
    }

    private List<BlockedSenderRecord> _blockedSenders = [];

    private void ApplyFilter()
    {
        var q = Search.Trim();
        var selectedId = Selected?.Contact.Id;
        bool Matches(string name, string key) =>
            q.Length == 0 || name.Contains(q, StringComparison.OrdinalIgnoreCase) || key.StartsWith(q, StringComparison.OrdinalIgnoreCase);
        // Blocked contacts and channel senders have their own chip.
        BlockedContacts.Clear();
        foreach (var b in _contacts.Where(c => c.IsBlocked && Matches(c.DisplayName, c.PublicKeyHex)).OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            BlockedContacts.Add(b);
        BlockedSenders.Clear();
        foreach (var bs in _blockedSenders.Where(b => Matches(b.Name, ""))) BlockedSenders.Add(bs);
        OnPropertyChanged(nameof(HasNoBlocked));
        var rows = _contacts.Where(c => !c.IsBlocked && TypeFilter != ContactTypeFilter.Blocked).Where(c => TypeFilter switch
            {
                ContactTypeFilter.Companions => c.ContactType == ContactType.Chat,
                ContactTypeFilter.Repeaters => c.ContactType == ContactType.Repeater,
                ContactTypeFilter.Rooms => c.ContactType == ContactType.Room,
                ContactTypeFilter.Sensors => c.ContactType == ContactType.Sensor,
                ContactTypeFilter.Favorites => c.IsFavorite,
                _ => true,
            })
            .Where(c => q.Length == 0 || c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || c.PublicKeyHex.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.IsFavorite).ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ContactRow
            {
                Contact = c,
                Detail = $"{Formatters.TypeName(c.ContactType)} · {c.RouteDescription}",
                Heard = Formatters.AgoMs(Math.Max(c.LastHeard, c.LastAdvert * 1000)),
            }).ToList();
        Contacts.Clear();
        foreach (var r in rows) Contacts.Add(r);
        if (selectedId is not null)
        {
            var again = Contacts.FirstOrDefault(c => c.Contact.Id == selectedId);
            if (again is not null) Selected = again;
        }
    }

    [RelayCommand]
    private async Task AddContact()
    {
        var input = await AppHost.Dialogs.Prompt(L.T("Add contact"), L.T("Paste a meshcore:// contact link, a shared contact token, or an exported contact card (hex)."), watermark: "meshcore://contact/add?…");
        if (string.IsNullOrWhiteSpace(input)) return;
        await Guard(async () =>
        {
            var rec = await Core.Contacts.ImportAsync(input);
            Reload();
            if (rec is not null) Select(rec.Id);
        }, L.T("Add contact"));
    }

    [RelayCommand]
    private async Task Advertise(string flood) => await Guard(async () =>
    {
        await Core.Device.SendAdvertAsync(flood == "flood");
        _main.ShowToast(L.T("Advert sent"), flood == "flood" ? L.T("Your node was announced across the mesh.") : L.T("Your node was announced to nearby nodes."));
    }, L.T("Advertise"));

    [RelayCommand]
    private async Task Resync() => await Guard(async () =>
    {
        await Core.SyncContactsAsync(force: true);
        Reload();
    }, L.T("Refresh contacts"));

    [RelayCommand]
    private async Task ShowMyCard()
    {
        if (Core.SelfInfo is null) { await AppHost.Dialogs.ShowError(L.T("Not connected"), L.T("Connect to your radio first.")); return; }
        await AppHost.Dialogs.ShowDialog(new ShareContactViewModel(null), L.T("My contact"), 440, 560);
    }

    [RelayCommand]
    private async Task AddDiscovered(DiscoveredRow row) => await Guard(async () =>
    {
        var rec = await Core.Contacts.AddDiscoveredAsync(row.Node);
        Reload();
        _main.ShowToast(L.T("Contact added"), L.F("{0} was added to your radio.", rec.DisplayName));
    }, L.T("Add contact"));

    [RelayCommand]
    private void DeleteDiscovered(DiscoveredRow row) { Core.Contacts.DeleteDiscovered(row.Node); Reload(); }

    [RelayCommand]
    private void ClearDiscovered() { Core.Contacts.ClearDiscovered(); Reload(); }

    [RelayCommand]
    private void ShowDiscoveredOnMap(DiscoveredRow row)
    {
        if (!row.Node.HasLocation) return;
        _main.Navigate(Page.Map);
        _main.Map.CenterOn(row.Node.Latitude, row.Node.Longitude, 13, row.Node.Name);
    }

    [RelayCommand]
    private void Unblock(ContactRecord c) { Core.Contacts.SetBlocked(c, false); Reload(); }

    [RelayCommand]
    private void UnblockSender(BlockedSenderRecord s) { Core.Contacts.UnblockChannelSender(s.Name); Reload(); }
}

public sealed partial class ContactDetailViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public ContactDetailViewModel(ContactRecord contact, MainWindowViewModel main)
    {
        _main = main;
        Contact = contact;
        Refresh();
    }

    public ContactRecord Contact { get; private set; }
    public ObservableCollection<DetailRow> Info { get; } = new();
    public ObservableCollection<DetailRow> Telemetry { get; } = new();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _typeText = "";
    [ObservableProperty] private string _iconKey = "Icon.Account";
    [ObservableProperty] private string _color = "#2463EB";
    [ObservableProperty] private string _avatarText = "";
    [ObservableProperty] private string? _emoji;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isBlocked;
    [ObservableProperty] private bool _canMessage;
    [ObservableProperty] private bool _canManage;
    [ObservableProperty] private bool _isRoom;
    [ObservableProperty] private bool _hasLocation;
    [ObservableProperty] private string _routeText = "";
    [ObservableProperty] private string? _actionResult;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private NotificationLevel _notificationLevel;
    [ObservableProperty] private bool _hasTelemetry;
    [ObservableProperty] private bool _shareTelemetryBase;
    [ObservableProperty] private bool _shareTelemetryLocation;
    [ObservableProperty] private bool _shareTelemetryEnvironment;
    public IReadOnlyList<NotificationLevel> Levels { get; } = Enum.GetValues<NotificationLevel>();
    public string PublicKey => Convert.ToHexString(Contact.PublicKey);

    public void Refresh()
    {
        Contact = Core.Db.GetContact(Contact.Id) ?? Contact;
        var c = Contact;
        Name = c.DisplayName;
        TypeText = Formatters.TypeName(c.ContactType) + (c.Nickname is not null ? " · " + L.F("advertised as \"{0}\"", c.Name) : "");
        IconKey = Formatters.ContactIcon(c.ContactType);
        Color = Formatters.ContactColor(c.ContactType);
        AvatarText = Formatters.Initials(c.DisplayName);
        Emoji = Formatters.AvatarEmoji(c.DisplayName);
        IsFavorite = c.IsFavorite;
        IsBlocked = c.IsBlocked;
        CanMessage = c.ContactType is ContactType.Chat or ContactType.Room or ContactType.Sensor;
        CanManage = c.ContactType is ContactType.Repeater or ContactType.Room or ContactType.Sensor;
        IsRoom = c.ContactType == ContactType.Room;
        HasLocation = c.HasLocation;
        RouteText = c.IsFloodRouted ? L.T("Flood (no known path)") : c.HopCount == 0 ? L.T("Direct (neighbour)") : L.Plural(c.HopCount, "{0} hop: {1}", "{0} hops: {1}",
            string.Join(" → ", PathEncoding.HopHexes(c.OutPath, c.HashSize).Select(h => Core.Contacts.ResolveHopName(Convert.FromHexString(h)) is { } n ? $"{n} ({h})" : h)));
        _refreshing = true;
        NotificationLevel = (NotificationLevel)c.NotificationLevel;
        ShareTelemetryBase = c.ContactFlags.HasFlag(ContactFlags.TelemetryBase);
        ShareTelemetryLocation = c.ContactFlags.HasFlag(ContactFlags.TelemetryLocation);
        ShareTelemetryEnvironment = c.ContactFlags.HasFlag(ContactFlags.TelemetryEnvironment);
        _refreshing = false;
        Info.Clear();
        Info.Add(new(L.T("Public key"), Convert.ToHexString(c.PublicKey)[..16] + "…"));
        Info.Add(new(L.T("Last advert"), Formatters.AgoSeconds(c.LastAdvert)));
        Info.Add(new(L.T("Last heard"), Formatters.AgoMs(c.LastHeard)));
        if (c.InboundHops is { } ih) Info.Add(new(L.T("Advert reached us in"), L.Plural(ih, "{0} hop", "{0} hops")));
        if (c.HasLocation)
        {
            Info.Add(new(L.T("Location"), Formatters.Coordinates(c.Latitude, c.Longitude)));
            if (Core.SelfInfo is { } self && (self.Latitude != 0 || self.Longitude != 0))
                Info.Add(new(L.T("Distance"), Formatters.Distance(RfCalculator.Distance(self.Latitude, self.Longitude, c.Latitude, c.Longitude))));
        }
    }

    private bool _refreshing;

    partial void OnNotificationLevelChanged(NotificationLevel value)
    {
        if (!_refreshing) Core.Contacts.SetNotificationLevel(Contact, value);
    }

    /// <summary>Runs a radio action; <paramref name="failed"/> turns the error message into the whole (translated) failure sentence.</summary>
    private async Task Run(Func<Task> action, Func<string, string> failed)
    {
        if (!Core.IsConnected) { await AppHost.Dialogs.ShowError(L.T("Not connected"), L.T("Connect to your radio first.")); return; }
        Busy = true;
        ActionResult = null;
        try { await action(); }
        catch (Exception ex) { ActionResult = failed(ex.Message); }
        finally { Busy = false; Refresh(); }
    }

    [RelayCommand]
    private void Message()
    {
        if (Contact.ContactType == ContactType.Room) { _ = RoomLoginFlow.JoinAsync(Contact); return; }
        _main.Chats.OpenByKeyForce(MessageService.DmKey(Contact.Id));
    }

    [RelayCommand]
    private async Task Ping() => await Run(async () =>
    {
        ActionResult = L.T("Pinging…");
        var r = await Core.Contacts.PingAsync(Contact);
        ActionResult = r.Success ? L.F("Reply in {0} ms · SNR there {1:0.#} dB · back {2:0.#} dB", r.LatencyMs, r.SnrThere, r.SnrBack) : L.F("No reply ({0})", r.Error);
    }, e => L.F("Ping failed: {0}", e));

    [RelayCommand]
    private async Task RequestTelemetry() => await Run(async () =>
    {
        ActionResult = L.T("Requesting telemetry…");
        var t = await Core.Remote.RequestTelemetryAsync(Contact);
        Telemetry.Clear();
        foreach (var p in t.DataPoints) Telemetry.Add(new DetailRow(L.F("{0} (ch {1})", p.Type.DisplayName(), p.Channel), p.FormattedValue));
        HasTelemetry = Telemetry.Count > 0;
        ActionResult = Telemetry.Count == 0 ? L.T("The node sent no telemetry (it may not share it with you).") : L.Plural(Telemetry.Count, "Telemetry received ({0} value).", "Telemetry received ({0} values).");
    }, e => L.F("Telemetry failed: {0}", e));

    [RelayCommand]
    private async Task ToggleFavorite() => await Run(() => Core.Contacts.SetFavoriteAsync(Contact, !Contact.IsFavorite), e => L.F("Favorite failed: {0}", e));

    [RelayCommand]
    private async Task SaveTelemetryPermissions() => await Run(async () =>
    {
        var f = ContactFlags.None;
        if (ShareTelemetryBase) f |= ContactFlags.TelemetryBase;
        if (ShareTelemetryLocation) f |= ContactFlags.TelemetryLocation;
        if (ShareTelemetryEnvironment) f |= ContactFlags.TelemetryEnvironment;
        await Core.Contacts.SetTelemetryPermissionsAsync(Contact, f);
        ActionResult = L.T("Telemetry sharing updated.");
    }, e => L.F("Telemetry permissions failed: {0}", e));

    [RelayCommand]
    private async Task Rename()
    {
        var name = await AppHost.Dialogs.Prompt(L.T("Nickname"), L.F("Local nickname for {0} (leave empty to use the advertised name).", Contact.Name), Contact.Nickname ?? "");
        if (name is null) return;
        Core.Contacts.SetNickname(Contact, name);
        Refresh();
    }

    [RelayCommand]
    private async Task Share() => await AppHost.Dialogs.ShowDialog(new ShareContactViewModel(Contact), L.F("Share {0}", Contact.DisplayName), 440, 600);

    [RelayCommand]
    private async Task ResetPath() => await Run(async () => { await Core.Contacts.ResetPathAsync(Contact); ActionResult = L.T("Path reset to flood."); }, e => L.F("Reset path failed: {0}", e));

    [RelayCommand]
    private async Task DiscoverPath() => await Run(async () =>
    {
        ActionResult = L.T("Discovering path…");
        var tcs = new TaskCompletionSource<PathInfo>();
        void Handler(ContactRecord c, PathInfo p) { if (c.Id == Contact.Id) tcs.TrySetResult(p); }
        Core.Contacts.PathDiscovered += Handler;
        try
        {
            await Core.Contacts.DiscoverPathAsync(Contact);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            ActionResult = done == tcs.Task ? L.T("Path updated.") : L.T("No path response yet (it may still arrive).");
        }
        finally { Core.Contacts.PathDiscovered -= Handler; }
    }, e => L.F("Discover path failed: {0}", e));

    [RelayCommand]
    private async Task EditPath()
    {
        var current = Contact.IsFloodRouted ? "" : string.Join(",", PathEncoding.HopHexes(Contact.OutPath, Contact.HashSize));
        var input = await AppHost.Dialogs.Prompt(L.T("Edit out path"), L.T("Enter repeater hashes separated by commas (e.g. A1,B2). Leave empty for flood."), current);
        if (input is null) return;
        var hops = input.Split([',', ' ', '>'], StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim()).ToList();
        if (hops.Count == 0) { await Run(() => Core.Contacts.ResetPathAsync(Contact), e => L.F("Edit path failed: {0}", e)); return; }
        var size = hops[0].Length / 2;
        if (size is < 1 or > 3 || hops.Any(h => h.Length != size * 2 || Bytes.FromHex(h) is null))
        {
            await AppHost.Dialogs.ShowError(L.T("Invalid path"), L.T("Each hop must be 1–3 bytes of hex, all the same length."));
            return;
        }
        await Run(() => Core.Contacts.SetPathAsync(Contact, hops.SelectMany(h => Convert.FromHexString(h)).ToArray(), size), e => L.F("Edit path failed: {0}", e));
    }

    [RelayCommand]
    private async Task Manage()
    {
        if (!Core.IsConnected) { await AppHost.Dialogs.ShowError(L.T("Not connected"), L.T("Connect to your radio first.")); return; }
        await AppHost.Dialogs.ShowDialog(new NodeManagementViewModel(Contact), L.F("Manage {0}", Contact.DisplayName), 920, 740);
    }

    [RelayCommand]
    private void ShowOnMap()
    {
        _main.Navigate(Page.Map);
        _main.Map.CenterOn(Contact.Latitude, Contact.Longitude, 14, Contact.DisplayName);
    }

    [RelayCommand]
    private void ToggleBlock()
    {
        Core.Contacts.SetBlocked(Contact, !Contact.IsBlocked);
        Refresh();
    }

    [RelayCommand]
    private async Task Remove()
    {
        if (!await AppHost.Dialogs.Confirm(L.F("Remove {0}?", Contact.DisplayName), L.T("Removes the contact from your radio. Message history on this PC is deleted too."), L.T("Remove"), true)) return;
        await Run(() => Core.Contacts.RemoveAsync(Contact, deleteMessages: true), e => L.F("Remove contact failed: {0}", e));
        _main.Contacts.Reload();
    }

    [RelayCommand]
    private async Task ShareOverMesh() => await Run(async () => { await Core.Contacts.ShareOverMeshAsync(Contact); ActionResult = L.T("Contact advert re-broadcast to nearby nodes."); }, e => L.F("Share failed: {0}", e));

    [RelayCommand]
    private async Task CopyKey() { await AppHost.Dialogs.CopyToClipboard(Convert.ToHexString(Contact.PublicKey)); ActionResult = L.T("Public key copied."); }
}

public sealed partial class ShareContactViewModel : ViewModelBase
{
    public ShareContactViewModel(ContactRecord? contact)
    {
        if (contact is null)
        {
            Name = Core.SelfName;
            Uri = Core.Contacts.SelfContactUri();
            Key = Core.SelfInfo is { } s ? Convert.ToHexString(s.PublicKey) : "";
            Token = Core.SelfInfo is { } s2 ? ContactShareUtilities.FormatShare(s2.PublicKey, ContactType.Chat, s2.Name) : "";
            IsSelf = true;
        }
        else
        {
            Name = contact.DisplayName;
            Uri = ContactService.ContactUri(contact);
            Key = Convert.ToHexString(contact.PublicKey);
            Token = ContactShareUtilities.FormatShare(contact.PublicKey, contact.ContactType, contact.Name);
        }
        Qr = QrService.Render(Uri);
    }

    public string Name { get; }
    public string Uri { get; }
    public string Key { get; }
    public string Token { get; }
    public bool IsSelf { get; }
    public Avalonia.Media.Geometry? Qr { get; }
    [ObservableProperty] private string? _message;

    [RelayCommand] private async Task CopyLink() { await AppHost.Dialogs.CopyToClipboard(Uri); Message = L.T("Link copied to clipboard."); }
    [RelayCommand] private async Task CopyToken() { await AppHost.Dialogs.CopyToClipboard(Token); Message = L.T("Chat token copied — paste it into any conversation."); }
    [RelayCommand] private async Task CopyKey() { await AppHost.Dialogs.CopyToClipboard(Key); Message = L.T("Public key copied."); }

    [RelayCommand]
    private async Task SaveQr()
    {
        var path = await AppHost.Dialogs.PickSaveFile(L.T("Save QR code"), $"{Name}-meshcore.png", "png", L.T("PNG image"));
        if (path is null) return;
        await File.WriteAllBytesAsync(path, QrService.RenderPng(Uri));
        Message = L.T("QR code saved.");
    }

    [RelayCommand]
    private async Task ExportCard() => await Guard(async () =>
    {
        var card = await Core.Contacts.ExportCardAsync(IsSelf ? null : Core.Contacts.GetAll().FirstOrDefault(c => Convert.ToHexString(c.PublicKey) == Key));
        await AppHost.Dialogs.CopyToClipboard(card);
        Message = L.T("Signed advert card copied (can be imported by other MeshCore apps).");
    }, L.T("Export card"));
}
