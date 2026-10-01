namespace MeshCore;

public enum ConnectionStateKind { Disconnected, Connecting, Connected, Reconnecting, Failed }

public sealed record ConnectionState(ConnectionStateKind Kind, int Attempt = 0, string? Error = null)
{
    public static readonly ConnectionState Disconnected = new(ConnectionStateKind.Disconnected);
    public static readonly ConnectionState Connecting = new(ConnectionStateKind.Connecting);
    public static readonly ConnectionState Connected = new(ConnectionStateKind.Connected);
}

/// <summary>Every event produced by the companion radio (responses and pushes).</summary>
public abstract record MeshEvent
{
    public string CaseName => GetType().Name;

    public sealed record ConnectionStateChanged(ConnectionState State) : MeshEvent;
    public sealed record Ok(uint? Value) : MeshEvent;
    public sealed record Error(byte? Code) : MeshEvent
    {
        public ErrorCode? ErrorCode => Code is { } c && Enum.IsDefined(typeof(ErrorCode), c) ? (ErrorCode)c : null;
    }
    public sealed record SelfInfoEvent(SelfInfo Info) : MeshEvent;
    public sealed record DeviceInfo(DeviceCapabilities Info) : MeshEvent;
    public sealed record Battery(BatteryInfo Info) : MeshEvent;
    public sealed record CurrentTime(DateTimeOffset Time) : MeshEvent;
    public sealed record CustomVars(IReadOnlyDictionary<string, string> Vars) : MeshEvent;
    public sealed record ChannelInfoEvent(ChannelInfo Info) : MeshEvent;
    public sealed record StatsCore(CoreStats Stats) : MeshEvent;
    public sealed record StatsRadio(RadioStats Stats) : MeshEvent;
    public sealed record StatsPackets(PacketStats Stats) : MeshEvent;
    public sealed record AutoAddConfigEvent(AutoAddConfig Config) : MeshEvent;
    public sealed record DefaultFloodScopeEvent(DefaultFloodScope? Scope) : MeshEvent;
    public sealed record AllowedRepeatFreq(IReadOnlyList<FrequencyRange> Ranges) : MeshEvent;
    public sealed record ContactsStart(int Count) : MeshEvent;
    public sealed record Contact(MeshContact Value) : MeshEvent;
    public sealed record ContactsEnd(DateTimeOffset LastModified) : MeshEvent;
    public sealed record NewContact(MeshContact Value) : MeshEvent;
    public sealed record ContactDeleted(byte[] PublicKey) : MeshEvent;
    public sealed record ContactsFull : MeshEvent;
    public sealed record ContactUri(string Uri) : MeshEvent;
    public sealed record MessageSent(MessageSentInfo Info) : MeshEvent;
    public sealed record ContactMessageReceived(ContactMessage Message) : MeshEvent;
    public sealed record ChannelMessageReceived(ChannelMessage Message) : MeshEvent;
    public sealed record ChannelDataReceived(ChannelDatagram Datagram) : MeshEvent;
    public sealed record NoMoreMessages : MeshEvent;
    public sealed record MessagesWaiting : MeshEvent;
    public sealed record Advertisement(byte[] PublicKey) : MeshEvent;
    public sealed record PathUpdate(byte[] PublicKey) : MeshEvent;
    public sealed record Acknowledgement(byte[] Code, uint? TripTime) : MeshEvent;
    public sealed record TraceData(TraceInfo Info) : MeshEvent;
    public sealed record PathResponse(PathInfo Info) : MeshEvent;
    public sealed record LoginSuccess(LoginInfo Info) : MeshEvent;
    public sealed record LoginFailed(byte[]? PublicKeyPrefix) : MeshEvent;
    public sealed record StatusResponseEvent(StatusResponse Response) : MeshEvent;
    public sealed record TelemetryResponseEvent(TelemetryResponse Response) : MeshEvent;
    public sealed record BinaryResponse(byte[] Tag, byte[] Data) : MeshEvent;
    public sealed record SignStart(int MaxLength) : MeshEvent;
    public sealed record Signature(byte[] Data) : MeshEvent;
    public sealed record Disabled(string Reason) : MeshEvent;
    public sealed record RawData(RawDataInfo Info) : MeshEvent;
    public sealed record LogData(LogDataInfo Info) : MeshEvent;
    public sealed record RxLogData(ParsedRxLogData Info) : MeshEvent;
    public sealed record ControlData(ControlDataInfo Info) : MeshEvent;
    public sealed record DiscoverResponseEvent(DiscoverResponse Response) : MeshEvent;
    public sealed record AdvertPathResponseEvent(AdvertPathResponse Response) : MeshEvent;
    public sealed record TuningParamsResponseEvent(TuningParamsResponse Response) : MeshEvent;
    public sealed record PrivateKey(byte[] Key) : MeshEvent;
    public sealed record ParseFailure(byte[] Data, string Reason) : MeshEvent;
}
