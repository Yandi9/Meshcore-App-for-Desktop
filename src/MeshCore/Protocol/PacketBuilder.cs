using System.Text;

namespace MeshCore;

/// <summary>Builds companion-protocol command frames (payload only; transports add framing).</summary>
public static class PacketBuilder
{
    public const int PublicKeySize = 32;
    public const int PrivateKeySize = 64;
    public const int RawDataMaxPathBytes = 64;
    public const int RawDataMaxPayloadBytes = 184;
    public const byte FloodPathSentinel = 0xFF;
    public const int ChannelDataMaxPayloadBytes = 163;
    private const int DefaultScopeNameField = 31;
    private const int DefaultScopeMaxNameBytes = 30;
    private const int DefaultScopeKeyBytes = 16;
    private const double CoordinateScale = 1_000_000;
    private const double RadioScale = 1000;

    public static readonly (double Min, double Max) LatitudeRange = (-90, 90);
    public static readonly (double Min, double Max) LongitudeRange = (-180, 180);
    public static readonly (uint Min, uint Max) FrequencyRangeKHz = (150_000, 2_500_000);
    public static readonly (uint Min, uint Max) BandwidthRangeHz = (7000, 500_000);
    public static readonly (byte Min, byte Max) SpreadingFactorRange = (5, 12);
    public static readonly (byte Min, byte Max) CodingRateRange = (5, 8);
    public const sbyte TxPowerFloor = -9;

    private static byte[] EncodePublicKey(byte[] key) => key.PaddedOrTruncated(PublicKeySize);

    public static int ScaledCoordinate(double degrees, (double Min, double Max) range)
    {
        var clamped = double.IsFinite(degrees) ? Math.Clamp(degrees, range.Min, range.Max) : 0;
        return (int)(clamped * CoordinateScale);
    }

    private static uint ScaledRadioValue(double value, (uint Min, uint Max) range)
    {
        if (!double.IsFinite(value)) return range.Min;
        var scaled = Math.Round(value * RadioScale, MidpointRounding.AwayFromZero);
        return (uint)Math.Clamp(scaled, range.Min, range.Max);
    }

    // MARK: Device

    public static byte[] AppStart(string clientId = "MCore")
    {
        var w = new ByteWriter((byte)CommandCode.AppStart, 0x03);
        w.Raw([0x20, 0x20, 0x20, 0x20, 0x20, 0x20]);
        var id = clientId.Length > 5 ? clientId[..5] : clientId;
        w.Utf8(id);
        return w.ToArray();
    }

    public static byte[] DeviceQuery() => [(byte)CommandCode.DeviceQuery, 0x03];
    public static byte[] GetBattery() => [(byte)CommandCode.GetBattery];
    public static byte[] GetTime() => [(byte)CommandCode.GetTime];

    public static byte[] SetTime(DateTimeOffset date) =>
        new ByteWriter((byte)CommandCode.SetTime).U32(Bytes.EpochSeconds32(date)).ToArray();

    public static byte[] SetName(string name) =>
        new ByteWriter((byte)CommandCode.SetName).Utf8(name.Utf8Prefix(31)).ToArray();

    public static byte[] SetCoordinates(double latitude, double longitude) =>
        new ByteWriter((byte)CommandCode.SetCoordinates)
            .I32(ScaledCoordinate(latitude, LatitudeRange))
            .I32(ScaledCoordinate(longitude, LongitudeRange))
            .Raw([0, 0, 0, 0])
            .ToArray();

    public static byte[] SetTxPower(sbyte power) => [(byte)CommandCode.SetTxPower, unchecked((byte)power)];

    /// <param name="frequencyMHz">e.g. 910.525</param>
    /// <param name="bandwidthKHz">e.g. 62.5</param>
    public static byte[] SetRadio(double frequencyMHz, double bandwidthKHz, byte spreadingFactor, byte codingRate, bool? clientRepeat = null)
    {
        var w = new ByteWriter((byte)CommandCode.SetRadio)
            .U32(ScaledRadioValue(frequencyMHz, FrequencyRangeKHz))
            .U32(ScaledRadioValue(bandwidthKHz, BandwidthRangeHz))
            .U8(spreadingFactor)
            .U8(codingRate);
        if (clientRepeat is { } cr) w.U8(cr ? (byte)1 : (byte)0);
        return w.ToArray();
    }

    public static byte[] GetRepeatFreq() => [(byte)CommandCode.GetRepeatFreq];

    public static byte[] SendAdvertisement(bool flood = false) =>
        flood ? [(byte)CommandCode.SendAdvertisement, 0x01] : [(byte)CommandCode.SendAdvertisement];

    public static byte[] Reboot() => new ByteWriter((byte)CommandCode.Reboot).Utf8("reboot").ToArray();

    // MARK: Contacts

    public static byte[] GetContacts(DateTimeOffset? since = null)
    {
        var w = new ByteWriter((byte)CommandCode.GetContacts);
        if (since is { } s) w.U32(Bytes.EpochSeconds32(s));
        return w.ToArray();
    }

    public static byte[] ResetPath(byte[] publicKey) =>
        new ByteWriter((byte)CommandCode.ResetPath).Raw(publicKey.Prefix(PublicKeySize)).ToArray();

    public static byte[] RemoveContact(byte[] publicKey) =>
        new ByteWriter((byte)CommandCode.RemoveContact).Raw(publicKey.Prefix(PublicKeySize)).ToArray();

    public static byte[] ShareContact(byte[] publicKey) =>
        new ByteWriter((byte)CommandCode.ShareContact).Raw(publicKey.Prefix(PublicKeySize)).ToArray();

    public static byte[] ExportContact(byte[]? publicKey = null)
    {
        var w = new ByteWriter((byte)CommandCode.ExportContact);
        if (publicKey is not null) w.Raw(publicKey.Prefix(PublicKeySize));
        return w.ToArray();
    }

    public static byte[] ImportContact(byte[] cardData) =>
        new ByteWriter((byte)CommandCode.ImportContact).Raw(cardData).ToArray();

    public static byte[] UpdateContact(MeshContact contact) =>
        UpdateContact(contact.PublicKey, contact.TypeRawValue, contact.Flags, contact.OutPathLength, contact.OutPath,
            contact.AdvertisedName, contact.LastAdvertisement, contact.Latitude, contact.Longitude);

    public static byte[] UpdateContact(byte[] publicKey, byte typeRaw, ContactFlags flags, byte outPathLength, byte[] outPath,
        string advertisedName, DateTimeOffset lastAdvertisement, double latitude, double longitude)
    {
        var w = new ByteWriter((byte)CommandCode.UpdateContact)
            .Raw(publicKey.PaddedOrTruncated(32))
            .U8(typeRaw)
            .U8((byte)flags)
            .U8(outPathLength)
            .Raw(outPath.PaddedOrTruncated(64))
            .Raw(advertisedName.Utf8PaddedOrTruncated(32))
            .U32(Bytes.EpochSeconds32(lastAdvertisement))
            .I32(ScaledCoordinate(latitude, LatitudeRange))
            .I32(ScaledCoordinate(longitude, LongitudeRange))
            .Raw([0, 0, 0]);
        return w.ToArray(); // 147 bytes
    }

    // MARK: Messaging

    public static byte[] GetMessage() => [(byte)CommandCode.GetMessage];

    public static byte[] SendMessage(byte[] destination, string text, DateTimeOffset timestamp, byte attempt = 0) =>
        new ByteWriter((byte)CommandCode.SendMessage, 0x00, attempt)
            .U32(Bytes.EpochSeconds32(timestamp))
            .Raw(destination.Prefix(6))
            .Utf8(text)
            .ToArray();

    public static byte[] SendCommand(byte[] destination, string command, DateTimeOffset timestamp) =>
        new ByteWriter((byte)CommandCode.SendMessage, 0x01, 0x00)
            .U32(Bytes.EpochSeconds32(timestamp))
            .Raw(destination.Prefix(6))
            .Utf8(command)
            .ToArray();

    public static byte[] SendChannelMessage(byte channel, string text, DateTimeOffset timestamp) =>
        new ByteWriter((byte)CommandCode.SendChannelMessage, 0x00, channel)
            .U32(Bytes.EpochSeconds32(timestamp))
            .Utf8(text)
            .ToArray();

    public static byte[] SendLogin(byte[] destination, string password) =>
        new ByteWriter((byte)CommandCode.SendLogin).Raw(destination.Prefix(PublicKeySize)).Utf8(password).ToArray();

    public static byte[] SendLogout(byte[] destination) =>
        new ByteWriter((byte)CommandCode.SendLogout).Raw(destination.Prefix(PublicKeySize)).ToArray();

    public static byte[] SendStatusRequest(byte[] destination) =>
        new ByteWriter((byte)CommandCode.SendStatusRequest).Raw(destination.Prefix(PublicKeySize)).ToArray();

    public static byte[] BinaryRequest(byte[] destination, BinaryRequestType type, byte[]? payload = null)
    {
        var w = new ByteWriter((byte)CommandCode.BinaryRequest).Raw(destination.Prefix(PublicKeySize)).U8((byte)type);
        if (payload is not null) w.Raw(payload);
        return w.ToArray();
    }

    // MARK: Channels

    public static byte[] GetChannel(byte index) => [(byte)CommandCode.GetChannel, index];

    public static byte[] SetChannel(byte index, string name, byte[] secret) =>
        new ByteWriter((byte)CommandCode.SetChannel, index)
            .Raw(name.Utf8PaddedOrTruncated(32))
            .Raw(secret.Prefix(16))
            .ToArray();

    // MARK: Stats

    public static byte[] GetStatsCore() => [(byte)CommandCode.GetStats, (byte)StatsType.Core];
    public static byte[] GetStatsRadio() => [(byte)CommandCode.GetStats, (byte)StatsType.Radio];
    public static byte[] GetStatsPackets() => [(byte)CommandCode.GetStats, (byte)StatsType.Packets];

    // MARK: Configuration

    public static byte[] SetTuning(uint rxDelay, uint af) =>
        new ByteWriter((byte)CommandCode.SetTuning).U32(rxDelay).U32(af).Raw([0, 0]).ToArray();

    public static byte[] SetOtherParams(bool manualAddContacts, byte telemetryModeEnvironment, byte telemetryModeLocation,
        byte telemetryModeBase, byte advertisementLocationPolicy, byte? multiAcks = null)
    {
        var telemetryMode = (byte)(((telemetryModeEnvironment & 0b11) << 4) | ((telemetryModeLocation & 0b11) << 2) | (telemetryModeBase & 0b11));
        var w = new ByteWriter((byte)CommandCode.SetOtherParams)
            .U8(manualAddContacts ? (byte)1 : (byte)0)
            .U8(telemetryMode)
            .U8(advertisementLocationPolicy);
        if (multiAcks is { } m) w.U8(m);
        return w.ToArray();
    }

    public static byte[] GetAutoAddConfig() => [(byte)CommandCode.GetAutoAddConfig];
    public static byte[] SetAutoAddConfig(AutoAddConfig config) => [(byte)CommandCode.SetAutoAddConfig, config.Bitmask, config.MaxHops];

    public static byte[] GetSelfTelemetry(byte[]? destination = null)
    {
        var w = new ByteWriter((byte)CommandCode.GetSelfTelemetry, 0x00, 0x00, 0x00);
        if (destination is not null) w.Raw(destination.Prefix(PublicKeySize));
        return w.ToArray();
    }

    public static byte[] SetDevicePin(uint pin) => new ByteWriter((byte)CommandCode.SetDevicePin).U32(pin).ToArray();
    public static byte[] GetCustomVars() => [(byte)CommandCode.GetCustomVars];
    public static byte[] SetCustomVar(string key, string value) => new ByteWriter((byte)CommandCode.SetCustomVar).Utf8(key + ":" + value).ToArray();
    public static byte[] ExportPrivateKey() => [(byte)CommandCode.ExportPrivateKey];
    public static byte[] ImportPrivateKey(byte[] key) => new ByteWriter((byte)CommandCode.ImportPrivateKey).Raw(key).ToArray();

    // MARK: Signing

    public static byte[] SignStart() => [(byte)CommandCode.SignStart];
    public static byte[] SignData(byte[] chunk) => new ByteWriter((byte)CommandCode.SignData).Raw(chunk).ToArray();
    public static byte[] SignFinish() => [(byte)CommandCode.SignFinish];

    // MARK: Paths / trace / scope

    public static byte[] SendPathDiscovery(byte[] destination) =>
        new ByteWriter((byte)CommandCode.PathDiscovery, 0x00).Raw(destination.Prefix(PublicKeySize)).ToArray();

    public static byte[] SendTrace(uint tag, uint authCode, byte flags, byte[]? path = null)
    {
        var w = new ByteWriter((byte)CommandCode.SendTrace).U32(tag).U32(authCode).U8(flags);
        if (path is not null) w.Raw(path);
        return w.ToArray();
    }

    public static byte[] SetFloodScope(byte[] scopeKey) =>
        new ByteWriter((byte)CommandCode.SetFloodScope, 0x00).Raw(scopeKey.Prefix(16)).ToArray();

    public static byte[] SetFloodScopeUnscoped() => [(byte)CommandCode.SetFloodScope, 0x01];

    public static byte[] SendChannelData(byte channelIndex, ushort dataType, byte[] payload, byte pathLength = FloodPathSentinel, byte[]? pathBytes = null)
    {
        var w = new ByteWriter((byte)CommandCode.SendChannelData, channelIndex, pathLength);
        if (pathLength != FloodPathSentinel && pathBytes is not null) w.Raw(pathBytes);
        w.U16(dataType);
        w.Raw(payload.Prefix(ChannelDataMaxPayloadBytes));
        return w.ToArray();
    }

    public static byte[] SetDefaultFloodScope(string name, byte[] scopeKey)
    {
        if (string.IsNullOrEmpty(name)) return [(byte)CommandCode.SetDefaultFloodScope];
        var truncated = name.Utf8Prefix(DefaultScopeMaxNameBytes);
        return new ByteWriter((byte)CommandCode.SetDefaultFloodScope)
            .Raw(Encoding.UTF8.GetBytes(truncated).PaddedOrTruncated(DefaultScopeNameField))
            .Raw(scopeKey.PaddedOrTruncated(DefaultScopeKeyBytes))
            .ToArray();
    }

    public static byte[] SetDefaultFloodScope(string name, FloodScope scope) =>
        scope is FloodScope.Disabled ? SetDefaultFloodScope("", []) : SetDefaultFloodScope(name, scope.ScopeKey());

    public static byte[] GetDefaultFloodScope() => [(byte)CommandCode.GetDefaultFloodScope];

    public static byte[] SendAnonReq(byte[] publicKey, AnonRequestType type, byte pathLength, byte[] path)
    {
        var reversed = (byte[])path.Clone();
        Array.Reverse(reversed);
        return new ByteWriter((byte)CommandCode.SendAnonReq)
            .Raw(publicKey.Prefix(PublicKeySize)).U8((byte)type).U8(pathLength).Raw(reversed).ToArray();
    }

    public static byte[] SetPathHashMode(byte mode) =>
        [(byte)CommandCode.SetPathHashMode, 0x00, Math.Min(mode, (byte)PathEncoding.MaxPathHashMode)];

    public static byte[] FactoryReset() => new ByteWriter((byte)CommandCode.FactoryReset).Utf8("reset").ToArray();

    public static byte[] SendControlData(byte type, byte[] payload) =>
        new ByteWriter((byte)CommandCode.SendControlData, type).Raw(payload).ToArray();

    public static byte[] SendNodeDiscoverRequest(byte filter, bool prefixOnly = true, uint? tag = null, uint? since = null)
    {
        var actualTag = tag ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        var controlType = (byte)((byte)ControlType.NodeDiscoverRequest | (prefixOnly ? 1 : 0));
        var w = new ByteWriter((byte)CommandCode.SendControlData, controlType).U8(filter).U32(actualTag);
        if (since is { } s) w.U32(s);
        return w.ToArray();
    }

    public static byte[] SendRawData(byte[] path, byte[] payload)
    {
        var clamped = path.Prefix(RawDataMaxPathBytes);
        return new ByteWriter((byte)CommandCode.SendRawData).U8((byte)clamped.Length).Raw(clamped).Raw(payload.Prefix(RawDataMaxPayloadBytes)).ToArray();
    }

    public static byte[] HasConnection(byte[] publicKey) => new ByteWriter((byte)CommandCode.HasConnection).Raw(EncodePublicKey(publicKey)).ToArray();
    public static byte[] GetContactByKey(byte[] publicKey) => new ByteWriter((byte)CommandCode.GetContactByKey).Raw(EncodePublicKey(publicKey)).ToArray();
    public static byte[] GetAdvertPath(byte[] publicKey) => new ByteWriter((byte)CommandCode.GetAdvertPath, 0x00).Raw(EncodePublicKey(publicKey)).ToArray();
    public static byte[] GetTuningParams() => [(byte)CommandCode.GetTuningParams];
}
