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

public sealed record DetailRow(string Label, string Value);

public sealed record PathHopRow(string Hash, string Name, string? Detail);

public sealed partial class MessageDetailsViewModel : ViewModelBase
{
    public MessageDetailsViewModel(MessageItemViewModel m)
    {
        Text = m.Text;
        var rows = new List<DetailRow>();
        if (m.Record is { } r)
        {
            rows.Add(new(L.T("Direction"), r.IsOutgoing ? L.T("Sent") : L.T("Received")));
            if (!r.IsOutgoing && r.SenderName is { } s) rows.Add(new(L.T("Sender"), s));
            rows.Add(new(L.T("Sender timestamp"), DateTimeOffset.FromUnixTimeSeconds(r.SenderTimestamp ?? r.Timestamp).LocalDateTime.ToString("G")));
            rows.Add(new(r.IsOutgoing ? L.T("Created") : L.T("Received"), r.Date.LocalDateTime.ToString("G")));
            if (r.TimestampCorrected) rows.Add(new(L.T("Clock"), L.T("Sender's clock was off; time corrected")));
            if (r.IsOutgoing) rows.Add(new(L.T("Status"), m.StatusText));
            if (r.RoundTripTime is { } rtt) rows.Add(new(L.T("Round trip"), $"{rtt} ms"));
            if (r.Snr is { } snr) rows.Add(new("SNR", $"{snr:0.##} dB"));
            if (r.RouteType >= 0) rows.Add(new(L.T("Route"), ((RouteType)r.RouteType).DisplayName()));
            if (r.RegionScope is { } reg) rows.Add(new(L.T("Region"), reg));
            if (r.PathLength != 0xFF && PathEncoding.Decode((byte)r.PathLength) is { } dec) rows.Add(new(L.T("Hops"), dec.HopCount.ToString()));
            if (r.IsOutgoing && r.ChannelIndex is not null) rows.Add(new(L.T("Heard repeats"), r.HeardRepeats.ToString()));
            if (r.SendCount > 1) rows.Add(new(L.T("Times sent"), r.SendCount.ToString()));
            rows.Add(new(L.T("Bytes"), r.Text.Utf8Length().ToString()));
            var hashSize = PathEncoding.Decode((byte)r.PathLength)?.HashSize ?? 1;
            if (r.PathNodes is { Length: > 0 } path)
                foreach (var h in PathEncoding.HopHexes(path, hashSize))
                    Hops.Add(new PathHopRow(h, Core.Contacts.ResolveHopName(Convert.FromHexString(h)) ?? L.T("Unknown repeater"), null));
            foreach (var rep in Core.Db.GetRepeats(r.Id))
                Repeats.Add(new DetailRow(rep.PathNodes.Length == 0 ? L.T("Direct") : PathEncoding.HopHexes(rep.PathNodes, PathEncoding.Decode((byte)rep.PathLength)?.HashSize ?? 1)
                    .Select(x => Core.Contacts.ResolveHopName(Convert.FromHexString(x)) ?? x).Aggregate((a, b) => a + " → " + b),
                    $"SNR {rep.Snr:0.#} · RSSI {rep.Rssi} · {Formatters.Clock(Time.FromMs(rep.ReceivedAt))}"));
            foreach (var rx in Core.Db.GetReactions(r.Id))
                Reactions.Add(new DetailRow(rx.Emoji, rx.SenderName + " · " + Formatters.Clock(Time.FromMs(rx.ReceivedAt))));
            CanShowPathOnMap = Hops.Count > 0;
            _path = r.PathNodes;
            _hashSize = hashSize;
        }
        else if (m.RoomRecord is { } rm)
        {
            rows.Add(new(L.T("Author"), rm.AuthorName));
            rows.Add(new(L.T("Author key"), Convert.ToHexString(rm.AuthorKeyPrefix)));
            rows.Add(new(L.T("Posted"), DateTimeOffset.FromUnixTimeSeconds(rm.Timestamp).LocalDateTime.ToString("G")));
            if (rm.IsFromSelf) rows.Add(new(L.T("Status"), m.StatusText));
        }
        Rows = rows;
    }

    private readonly byte[]? _path;
    private readonly int _hashSize;
    public string Text { get; }
    public IReadOnlyList<DetailRow> Rows { get; }
    public ObservableCollection<PathHopRow> Hops { get; } = new();
    public ObservableCollection<DetailRow> Repeats { get; } = new();
    public ObservableCollection<DetailRow> Reactions { get; } = new();
    public bool HasHops => Hops.Count > 0;
    public bool HasRepeats => Repeats.Count > 0;
    public bool HasReactions => Reactions.Count > 0;
    public bool CanShowPathOnMap { get; }
    public Action? Close { get; set; }

    [RelayCommand]
    private void ShowPathOnMap()
    {
        if (_path is null || AppHost.Main is not { } main) return;
        Close?.Invoke();
        main.Navigate(Page.Map);
        main.Map.ShowPath(PathEncoding.HopHexes(_path, _hashSize).Select(Convert.FromHexString).ToList());
    }
}

public sealed partial class ChannelInfoViewModel : ViewModelBase
{
    private readonly ChannelRecord _channel;

    public ChannelInfoViewModel(ChannelRecord channel)
    {
        _channel = channel;
        Name = channel.Name;
        ShareUri = ChannelService.ShareUri(channel);
        Qr = QrService.Render(ShareUri);
        NotificationLevel = (NotificationLevel)channel.NotificationLevel;
        var fs = channel.FloodScope;
        ScopeMode = fs is null ? 0 : fs == "all" ? 1 : 2;
        ScopeRegion = fs?.StartsWith("region:") == true ? fs[7..] : "";
        Regions = Core.RxLog.KnownRegions();
        SecretHex = Convert.ToHexString(channel.Secret);
        Kind = channel.IsPublic ? L.T("Public channel (well-known key)") : channel.IsHashtag ? L.T("Hashtag channel (key derived from the name)") : L.T("Private channel (shared secret)");
    }

    public string Kind { get; }
    public string ShareUri { get; }
    public Avalonia.Media.Geometry? Qr { get; }
    public string SecretHex { get; }
    public IReadOnlyList<string> Regions { get; }
    public int Slot => _channel.Idx;
    public IReadOnlyList<NotificationLevel> Levels { get; } = Enum.GetValues<NotificationLevel>();
    public IReadOnlyList<string> ScopeModes { get; } = [L.T("Use radio default"), L.T("All regions (unscoped)"), L.T("Specific region")];
    [ObservableProperty] private string _name;
    [ObservableProperty] private NotificationLevel _notificationLevel;
    [ObservableProperty] private int _scopeMode;
    [ObservableProperty] private string _scopeRegion;
    [ObservableProperty] private bool _showSecret;
    [ObservableProperty] private string? _message;
    public Action? Close { get; set; }

    partial void OnNotificationLevelChanged(NotificationLevel value) => Core.Channels.SetNotificationLevel(_channel, value);

    [RelayCommand]
    private void SaveScope()
    {
        var scope = ScopeMode switch { 0 => null, 1 => "all", _ => string.IsNullOrWhiteSpace(ScopeRegion) ? null : "region:" + ScopeRegion.Trim() };
        Core.Channels.SetFloodScope(_channel, scope);
        Message = L.T("Flood scope saved.");
    }

    [RelayCommand]
    private async Task Rename() => await Guard(async () =>
    {
        await Core.Channels.RenameAsync(_channel, Name.Trim());
        Message = L.T("Renamed.");
    }, L.T("Rename channel"));

    [RelayCommand]
    private async Task CopyLink() { await AppHost.Dialogs.CopyToClipboard(ShareUri); Message = L.T("Link copied."); }

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (await AppHost.Dialogs.Confirm(L.T("Clear history?"), L.T("Delete all messages in this channel from this PC?"), L.T("Clear"), true))
            Core.Channels.ClearMessages(_channel);
    }

    [RelayCommand]
    private async Task Remove()
    {
        if (!await AppHost.Dialogs.Confirm(L.F("Remove {0}?", _channel.DisplayName), L.T("The channel slot on your radio will be cleared and its messages deleted from this PC."), L.T("Remove"), true)) return;
        await Guard(async () => { await Core.Channels.RemoveAsync(_channel); Close?.Invoke(); }, L.T("Remove channel"));
    }
}

public sealed partial class RoomInfoViewModel : ViewModelBase
{
    private readonly RemoteSessionRecord _room;

    public RoomInfoViewModel(RemoteSessionRecord room)
    {
        _room = room;
        Name = room.Name;
        Status = !room.IsConnected ? L.T("Not signed in") : room.PermissionLevel switch
        {
            RoomPermission.Admin => L.T("Signed in as admin"),
            RoomPermission.ReadWrite => L.T("Signed in as member"),
            _ => L.T("Signed in as guest (read-only)"),
        };
        IsAdmin = room.IsAdmin;
        HasSavedPassword = Core.Secrets.GetPassword(room.PublicKey) is not null;
        NotificationLevel = (NotificationLevel)room.NotificationLevel;
        Key = Convert.ToHexString(room.PublicKey);
    }

    public string Name { get; }
    public string Status { get; }
    public string Key { get; }
    public bool IsAdmin { get; }
    [ObservableProperty] private bool _hasSavedPassword;
    [ObservableProperty] private NotificationLevel _notificationLevel;
    public IReadOnlyList<NotificationLevel> Levels { get; } = Enum.GetValues<NotificationLevel>();
    public Action? Close { get; set; }

    partial void OnNotificationLevelChanged(NotificationLevel value)
    {
        _room.NotificationLevel = (int)value;
        Core.Db.UpsertRemoteSession(_room);
        Core.Notify(DataKind.Conversations);
    }

    [RelayCommand]
    private void ForgetPassword()
    {
        Core.Remote.ForgetPassword(_room.PublicKey);
        HasSavedPassword = false;
    }

    [RelayCommand]
    private async Task Manage()
    {
        var contact = Core.Db.GetContactByKey(_room.RadioId, _room.PublicKey);
        if (contact is null) return;
        Close?.Invoke();
        await AppHost.Dialogs.ShowDialog(new NodeManagementViewModel(contact), L.F("Manage {0}", contact.DisplayName), 900, 720);
    }

    [RelayCommand]
    private async Task SignOut() => await Guard(async () => { await Core.Remote.LogoutAsync(_room); Close?.Invoke(); }, L.T("Sign out"));

    [RelayCommand]
    private async Task Leave()
    {
        if (!await AppHost.Dialogs.Confirm(L.F("Leave {0}?", _room.Name), L.T("Signs out and removes the room and its posts from this PC."), L.T("Leave"), true)) return;
        await Guard(async () => { await Core.Rooms.LeaveAsync(_room); Close?.Invoke(); }, L.T("Leave room"));
    }
}

/// <summary>Login prompt for rooms and repeaters (password optional for guest access).</summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    public LoginViewModel(ContactRecord node)
    {
        Node = node;
        Title = node.ContactType == ContactType.Room ? L.F("Sign in to {0}", node.DisplayName) : L.F("Log in to {0}", node.DisplayName);
        Password = Core.Secrets.GetPassword(node.PublicKey) ?? "";
        Remember = true;
        Hint = node.ContactType == ContactType.Room
            ? L.T("Leave the password empty to join as a guest (if the room allows it).")
            : L.T("Enter the admin password to manage this repeater, or the guest password for read-only status.");
    }

    public ContactRecord Node { get; }
    public string Title { get; }
    public string Hint { get; }
    [ObservableProperty] private string _password;
    [ObservableProperty] private bool _remember;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _progress;
    public LoginResult? Result { get; private set; }
    public Action? Close { get; set; }

    [RelayCommand]
    private async Task SignIn()
    {
        Error = null;
        Busy = true;
        Progress = L.T("Sending login…");
        try
        {
            var timeout = new Progress<int>(s => Progress = L.F("Waiting for reply (up to {0}s)…", s));
            if (Node.ContactType == ContactType.Room)
            {
                await Core.Rooms.JoinAsync(Node, Password, Remember, timeout);
                Result = new LoginResult(true, false, RoomPermission.Guest, null);
            }
            else Result = await Core.Remote.LoginAsync(Node, Password, Remember, timeout);
            Close?.Invoke();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; Progress = null; }
    }
}

public static class RoomLoginFlow
{
    public static async Task JoinAsync(ContactRecord room)
    {
        if (!AppHost.Core.IsConnected) { await AppHost.Dialogs.ShowError(L.T("Not connected"), L.T("Connect to your radio first.")); return; }
        var vm = new LoginViewModel(room);
        await AppHost.Dialogs.ShowDialog(vm, vm.Title, 440, 360);
        if (vm.Result is { Success: true } && AppHost.Main is { } main)
        {
            var session = AppHost.Core.Remote.GetOrCreateSession(room);
            main.Chats.OpenByKeyForce(MessageService.RoomKey(session.Id));
        }
    }
}

public static class DeepLinks
{
    public static async Task HandleAsync(string url, MainWindowViewModel main)
    {
        var core = AppHost.Core;
        if (MeshCoreUrl.ParseChannel(url) is { } ch)
        {
            if (!await AppHost.Dialogs.Confirm(L.F("Join {0}?", ch.Name), ch.HasHashtagSecretMismatch
                    ? L.T("Warning: this link's key doesn't match the usual key for this hashtag.")
                    : L.T("Add this channel to your radio?"), L.T("Join"))) return;
            try
            {
                var rec = await core.Channels.JoinFromUriAsync(url);
                main.Chats.OpenByKeyForce(MessageService.ChannelKey(rec.Idx));
            }
            catch (Exception ex) { await AppHost.Dialogs.ShowError(L.T("Couldn't join channel"), ex.Message); }
            return;
        }
        if (MeshCoreUrl.ParseContact(url) is { } c)
        {
            if (!await AppHost.Dialogs.Confirm(L.F("Add {0}?", c.Name), Formatters.AddContactPrompt(c.Type, c.PublicKey), L.T("Add"))) return;
            try
            {
                var rec = await core.Contacts.AddAsync(c.PublicKey, c.Name, c.Type);
                main.Navigate(Page.Contacts);
                main.Contacts.Select(rec.Id);
            }
            catch (Exception ex) { await AppHost.Dialogs.ShowError(L.T("Couldn't add contact"), ex.Message); }
            return;
        }
        if (MeshCoreUrl.ParseMap(url) is { } p)
        {
            main.Navigate(Page.Map);
            main.Map.CenterOn(p.Lat, p.Lon, 14, L.T("Shared location"));
        }
    }
}

/// <summary>A contact in the "+" menu's "Share contact" list.</summary>
public sealed record ShareContactItem(string Name, string KeyPrefix, string Kind, string Color, string Initials, string Token, string Search)
{
    public static ShareContactItem From(MC1.Core.Models.ContactRecord c, int hashSize) => new(
        c.DisplayName,
        c.PublicKeyHex[..Math.Clamp(hashSize * 2, 2, c.PublicKeyHex.Length)].ToUpperInvariant(),
        Formatters.TypeName(c.ContactType),
        Formatters.ColorFor(c.PublicKeyHex),
        Formatters.Initials(c.DisplayName),
        MC1.Core.Utilities.ContactShareUtilities.FormatShare(c.PublicKey, c.ContactType, c.Name),
        (c.Name + " " + c.Nickname + " " + c.PublicKeyHex).ToUpperInvariant());
}
