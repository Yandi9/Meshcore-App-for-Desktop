namespace MeshCore;

public sealed record SessionConfiguration
{
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public string ClientIdentifier { get; init; } = "MC1W";
    public TimeSpan BinaryRequestOverallTimeout { get; init; } = TimeSpan.FromSeconds(40);
    public TimeSpan? BinaryRequestRetransmitInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ContactStreamInactivityTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ContactStreamHardTimeout { get; init; } = TimeSpan.FromSeconds(180);
    public int ChannelPipelineWindow { get; init; } = 8;
    public TimeSpan ChannelPipelineIdleTimeout { get; init; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan ChannelPipelineHardTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public const double BinaryRetransmitRttHeadroom = 2.0;
    public const double RetryAckTimeoutMultiplier = 1.2;

    public static SessionConfiguration Default { get; } = new();
}

public enum MeshCoreErrorKind
{
    Timeout,
    DeviceError,
    ParseError,
    NotConnected,
    CommandFailed,
    InvalidResponse,
    ContactNotFound,
    DataTooLarge,
    SigningFailed,
    InvalidInput,
    Unknown,
    ConnectionLost,
    SessionNotStarted,
    FeatureDisabled,
}

public sealed class MeshCoreException : Exception
{
    public MeshCoreException(MeshCoreErrorKind kind, string? message = null, byte? deviceCode = null, Exception? inner = null)
        : base(message ?? Describe(kind, deviceCode), inner)
    {
        Kind = kind;
        DeviceCode = deviceCode;
    }

    public MeshCoreErrorKind Kind { get; }
    public byte? DeviceCode { get; }
    public ErrorCode? FirmwareError => DeviceCode is { } c && Enum.IsDefined(typeof(ErrorCode), c) ? (ErrorCode)c : null;

    public static MeshCoreException Timeout() => new(MeshCoreErrorKind.Timeout);
    public static MeshCoreException Device(byte? code) => new(MeshCoreErrorKind.DeviceError, deviceCode: code ?? 0);
    public static MeshCoreException NotConnected() => new(MeshCoreErrorKind.NotConnected);

    private static string Describe(MeshCoreErrorKind kind, byte? code) => kind switch
    {
        MeshCoreErrorKind.Timeout => "The radio did not respond in time.",
        MeshCoreErrorKind.DeviceError => code switch
        {
            1 => "The radio does not support this command.",
            2 => "The radio could not find the requested item.",
            3 => "The radio's table is full.",
            4 => "The radio is in the wrong state for this command.",
            5 => "The radio reported a storage error.",
            6 => "The radio rejected an invalid argument.",
            _ => $"The radio reported error {code}.",
        },
        MeshCoreErrorKind.NotConnected => "Not connected to a radio.",
        MeshCoreErrorKind.ConnectionLost => "The connection to the radio was lost.",
        MeshCoreErrorKind.FeatureDisabled => "This feature is disabled on the radio.",
        MeshCoreErrorKind.SessionNotStarted => "The radio session has not started.",
        _ => kind.ToString(),
    };
}

public abstract record MessageResult
{
    public sealed record Contact(ContactMessage Message) : MessageResult;
    public sealed record Channel(ChannelMessage Message) : MessageResult;
    public sealed record Datagram(ChannelDatagram Value) : MessageResult;
    public sealed record NoMoreMessages : MessageResult;
}

public sealed record ContactFetchResult(IReadOnlyList<MeshContact> Contacts, int? ReportedTotal, DateTimeOffset? LastModified);

/// <summary>Session-side contact cache (mirrors the Swift ContactManager).</summary>
public sealed class ContactManager
{
    private readonly Dictionary<string, MeshContact> _contacts = new();
    private readonly Dictionary<string, MeshContact> _pending = new();
    private readonly object _lock = new();

    public DateTimeOffset? LastModified { get; private set; }
    public bool NeedsRefresh { get; private set; } = true;
    public bool AutoUpdate { get; set; }

    public IReadOnlyList<MeshContact> Contacts { get { lock (_lock) return _contacts.Values.ToList(); } }
    public IReadOnlyList<MeshContact> PendingContacts { get { lock (_lock) return _pending.Values.ToList(); } }
    public bool IsEmpty { get { lock (_lock) return _contacts.Count == 0; } }

    public MeshContact? GetByKeyPrefix(byte[] prefix)
    {
        lock (_lock) return _contacts.Values.FirstOrDefault(c => c.PublicKey.StartsWith(prefix));
    }

    public MeshContact? GetByName(string name, bool exact = false)
    {
        lock (_lock)
            return exact
                ? _contacts.Values.FirstOrDefault(c => string.Equals(c.AdvertisedName, name, StringComparison.OrdinalIgnoreCase))
                : _contacts.Values.FirstOrDefault(c => c.AdvertisedName.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    public void Store(MeshContact c) { lock (_lock) _contacts[c.Id] = c; }
    public void MarkClean(DateTimeOffset lastModified) { lock (_lock) { LastModified = lastModified; NeedsRefresh = false; } }
    public void MarkDirty() { lock (_lock) NeedsRefresh = true; }
    public MeshContact? PopPending(string id) { lock (_lock) { _pending.Remove(id, out var c); return c; } }
    public void FlushPending() { lock (_lock) _pending.Clear(); }
    public void Clear() { lock (_lock) { _contacts.Clear(); _pending.Clear(); LastModified = null; NeedsRefresh = true; } }

    public void TrackChanges(MeshEvent e)
    {
        lock (_lock)
        {
            switch (e)
            {
                case MeshEvent.Contact c: _contacts[c.Value.Id] = c.Value; break;
                case MeshEvent.NewContact n: _pending[n.Value.Id] = n.Value; NeedsRefresh = true; break;
                case MeshEvent.ContactsEnd end: LastModified = end.LastModified; NeedsRefresh = false; break;
                case MeshEvent.Advertisement or MeshEvent.PathUpdate: NeedsRefresh = true; break;
                case MeshEvent.ContactDeleted d:
                    var id = d.PublicKey.ToHex();
                    _contacts.Remove(id);
                    _pending.Remove(id);
                    NeedsRefresh = true;
                    break;
                case MeshEvent.ContactsFull: NeedsRefresh = true; break;
            }
        }
    }
}
