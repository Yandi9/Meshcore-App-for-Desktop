namespace MeshCore;

internal static class PacketSize
{
    public const int Contact = 147;
    public const int SelfInfoMinimum = 57;
    public const int MessageSentMinimum = 9;
    public const int ContactMessageV1Minimum = 12;
    public const int ContactMessageV3Minimum = 15;
    public const int ChannelMessageV1Minimum = 7;
    public const int ChannelMessageV3Minimum = 10;
    public const int PrivateKeyMinimum = 64;
    public const int BatteryMinimum = 2;
    public const int BatteryExtended = 10;
    public const int SignStartMinimum = 5;
    public const int DeviceInfoV3Full = 79;
    public const int AckMinimum = 4;
    public const int AckWithTripTime = 8;
    public const int ContactsStartMinimum = 4;
    public const int CoreStatsMinimum = 9;
    public const int RadioStatsMinimum = 12;
    public const int PacketStatsMinimum = 24;
    public const int PacketStatsWithReceiveErrors = 28;
    public const int ChannelInfoMinimum = 49;
    public const int ContactDeletedPublicKey = 32;
    public const int StatusResponseMinimum = 58;
    public const int TraceDataMinimum = 11;
    public const int RawDataMinimum = 3;
    public const int ControlDataMinimum = 4;
    public const int PathDiscoveryMinimum = 9;
    public const int LoginSuccessMinimum = 7;
    public const int LoginSuccessExtended = 13;
    public const int BinaryResponseStatusBase = 48;
    public const int BinaryResponseStatusWithRxAirtime = 52;
    public const int BinaryResponseStatusWithReceiveErrors = 56;
    public const int ChannelDatagramMinimum = 8;
    public const int DefaultFloodScopeNameField = 31;
    public const int DefaultFloodScopeKeyBytes = 16;
    public const int DefaultFloodScopeSet = DefaultFloodScopeNameField + DefaultFloodScopeKeyBytes;
}

/// <summary>Parses frames received from the radio into <see cref="MeshEvent"/>s.</summary>
public static class PacketParser
{
    public static MeshEvent Parse(byte[] data)
    {
        if (data.Length == 0) return new MeshEvent.ParseFailure(data, "Empty packet");
        var first = data[0];
        if (!Enum.IsDefined(typeof(ResponseCode), first))
            return new MeshEvent.ParseFailure(data, $"Unknown response code: 0x{first:X2}");
        var code = (ResponseCode)first;
        var p = data.From(1);
        try
        {
            return code switch
            {
                ResponseCode.Ok => p.Length >= 4 ? new MeshEvent.Ok(p.ReadUInt32LE(0)) : new MeshEvent.Ok(null),
                ResponseCode.Error => new MeshEvent.Error(p.Length > 0 ? p[0] : null),
                ResponseCode.Battery => ParseBattery(p),
                ResponseCode.CurrentTime => p.Length >= 4
                    ? new MeshEvent.CurrentTime(Bytes.FromEpoch(p.ReadUInt32LE(0)))
                    : Fail(p, $"CurrentTime response too short: {p.Length} < 4"),
                ResponseCode.Disabled => new MeshEvent.Disabled("private_key_export_disabled"),
                ResponseCode.SelfInfo => Parsers.ParseSelfInfo(p),
                ResponseCode.DeviceInfo => Parsers.ParseDeviceInfo(p),
                ResponseCode.PrivateKey => p.Length >= PacketSize.PrivateKeyMinimum
                    ? new MeshEvent.PrivateKey(p.Prefix(PacketSize.PrivateKeyMinimum))
                    : Fail(p, $"PrivateKey response too short: {p.Length} < {PacketSize.PrivateKeyMinimum}"),
                ResponseCode.AdvertPath => Parsers.ParseAdvertPath(p),
                ResponseCode.TuningParams => p.Length >= 8
                    ? new MeshEvent.TuningParamsResponseEvent(new TuningParamsResponse(p.ReadUInt32LE(0) / 1000.0, p.ReadUInt32LE(4) / 1000.0))
                    : Fail(p, $"TuningParamsResponse too short: {p.Length} bytes, need 8"),
                ResponseCode.AutoAddConfig => p.Length >= 1
                    ? new MeshEvent.AutoAddConfigEvent(new AutoAddConfig(p[0], p.Length >= 2 ? p[1] : (byte)0))
                    : Fail(p, $"AutoAddConfig response too short: {p.Length} < 1"),
                ResponseCode.AllowedRepeatFreq => Parsers.ParseAllowedRepeatFreq(p),
                ResponseCode.DefaultFloodScope => Parsers.ParseDefaultFloodScope(p),
                ResponseCode.ContactStart => p.Length >= PacketSize.ContactsStartMinimum
                    ? new MeshEvent.ContactsStart((int)p.ReadUInt32LE(0))
                    : Fail(p, $"ContactStart response too short: {p.Length} < {PacketSize.ContactsStartMinimum}"),
                ResponseCode.Contact => Parsers.ParseContactEvent(p),
                ResponseCode.ContactEnd => new MeshEvent.ContactsEnd(p.Length >= 4 ? Bytes.FromEpoch(p.ReadUInt32LE(0)) : DateTimeOffset.UtcNow),
                ResponseCode.ContactUri => new MeshEvent.ContactUri("meshcore://" + p.ToHex()),
                ResponseCode.MessageSent => p.Length >= PacketSize.MessageSentMinimum
                    ? new MeshEvent.MessageSent(new MessageSentInfo(p[0], p.Slice(1, 4), p.ReadUInt32LE(5)))
                    : Fail(p, $"MessageSent response too short: {p.Length} < {PacketSize.MessageSentMinimum}"),
                ResponseCode.NoMoreMessages => new MeshEvent.NoMoreMessages(),
                ResponseCode.ContactMessageReceived => Parsers.ParseContactMessage(p, v3: false),
                ResponseCode.ContactMessageReceivedV3 => Parsers.ParseContactMessage(p, v3: true),
                ResponseCode.ChannelMessageReceived => Parsers.ParseChannelMessage(p, v3: false),
                ResponseCode.ChannelMessageReceivedV3 => Parsers.ParseChannelMessage(p, v3: true),
                ResponseCode.ChannelDataReceived => Parsers.ParseChannelDatagram(p),
                ResponseCode.Ack => p.Length >= PacketSize.AckMinimum
                    ? new MeshEvent.Acknowledgement(p.Prefix(PacketSize.AckMinimum), p.Length >= PacketSize.AckWithTripTime ? p.ReadUInt32LE(4) : null)
                    : Fail(p, $"Ack response too short: {p.Length} < {PacketSize.AckMinimum}"),
                ResponseCode.MessagesWaiting => new MeshEvent.MessagesWaiting(),
                ResponseCode.Advertisement => p.Length >= PacketBuilder.PublicKeySize
                    ? new MeshEvent.Advertisement(p.Prefix(PacketBuilder.PublicKeySize))
                    : Fail(p, $"Advertisement too short: {p.Length} < 32"),
                ResponseCode.NewAdvertisement => Parsers.ParseNewAdvertisement(p),
                ResponseCode.PathUpdate => p.Length >= PacketBuilder.PublicKeySize
                    ? new MeshEvent.PathUpdate(p.Prefix(PacketBuilder.PublicKeySize))
                    : Fail(p, $"PathUpdate too short: {p.Length} < 32"),
                ResponseCode.StatusResponse => Parsers.ParseStatusResponse(p, StatusLayout.Repeater),
                ResponseCode.TelemetryResponse => p.Length >= 7
                    ? new MeshEvent.TelemetryResponseEvent(new TelemetryResponse(p.Slice(1, 6), null, p.From(7)))
                    : Fail(p, $"TelemetryResponse too short: {p.Length} bytes, need 7"),
                ResponseCode.BinaryResponse => p.Length >= 5
                    ? new MeshEvent.BinaryResponse(p.Slice(1, 4), p.From(5))
                    : Fail(p, $"BinaryResponse too short: {p.Length} < 5"),
                ResponseCode.PathDiscoveryResponse => Parsers.ParsePathDiscovery(p),
                ResponseCode.ControlData => Parsers.ParseControlData(p),
                ResponseCode.ContactDeleted => p.Length >= PacketSize.ContactDeletedPublicKey
                    ? new MeshEvent.ContactDeleted(p.Prefix(PacketSize.ContactDeletedPublicKey))
                    : Fail(p, $"ContactDeleted too short: {p.Length} < 32"),
                ResponseCode.ContactsFull => new MeshEvent.ContactsFull(),
                ResponseCode.LoginSuccess => Parsers.ParseLoginSuccess(p),
                ResponseCode.LoginFailed => new MeshEvent.LoginFailed(p.Length >= 7 ? p.Slice(1, 6) : null),
                ResponseCode.SignStart => p.Length >= PacketSize.SignStartMinimum
                    ? new MeshEvent.SignStart((int)p.ReadUInt32LE(1))
                    : Fail(p, $"SignStart response too short: {p.Length} < {PacketSize.SignStartMinimum}"),
                ResponseCode.Signature => new MeshEvent.Signature(p),
                ResponseCode.Stats => Parsers.ParseStats(p),
                ResponseCode.ChannelInfo => Parsers.ParseChannelInfo(p),
                ResponseCode.CustomVars => Parsers.ParseCustomVars(p),
                ResponseCode.RawData => p.Length >= PacketSize.RawDataMinimum
                    ? new MeshEvent.RawData(new RawDataInfo(Bytes.SnrFromByte(p[0]), (sbyte)p[1], p.From(3)))
                    : Fail(p, $"RawData too short: {p.Length} bytes, need 3"),
                ResponseCode.LogData => Parsers.ParseLogData(p),
                ResponseCode.TraceData => Parsers.ParseTraceData(p),
                _ => Fail(p, $"Unhandled response code {code}"),
            };
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            return Fail(p, $"Malformed {code} frame: {ex.Message}");
        }
    }

    internal static MeshEvent Fail(byte[] data, string reason) => new MeshEvent.ParseFailure(data, reason);

    private static MeshEvent ParseBattery(byte[] p)
    {
        if (p.Length < PacketSize.BatteryMinimum)
            return Fail(p, $"Battery response too short: {p.Length} < {PacketSize.BatteryMinimum}");
        if (p.Length > PacketSize.BatteryMinimum && p.Length < PacketSize.BatteryExtended)
            return Fail(p, $"Battery response has partial extended payload: {p.Length} < {PacketSize.BatteryExtended}");
        int? used = null, total = null;
        if (p.Length >= PacketSize.BatteryExtended)
        {
            used = (int)p.ReadUInt32LE(2);
            total = (int)p.ReadUInt32LE(6);
        }
        return new MeshEvent.Battery(new BatteryInfo(p.ReadUInt16LE(0), used, total));
    }
}

/// <summary>Individual frame parsers (ported from the Swift MeshCore Parsers namespace).</summary>
public static class Parsers
{
    public static MeshEvent ParseSelfInfo(byte[] d)
    {
        if (d.Length < PacketSize.SelfInfoMinimum)
            return PacketParser.Fail(d, $"SelfInfo response too short: {d.Length} < {PacketSize.SelfInfoMinimum}");
        var o = 0;
        var advType = d[o++];
        var txPower = (sbyte)d[o++];
        var maxTx = (sbyte)d[o++];
        var pk = d.Slice(o, 32); o += 32;
        var lat = d.ReadInt32LE(o) / 1_000_000.0; o += 4;
        var lon = d.ReadInt32LE(o) / 1_000_000.0; o += 4;
        var multiAcks = d[o++];
        var advLoc = d[o++];
        var telemetry = d[o++];
        var manualAdd = d[o++] > 0;
        var freq = d.ReadUInt32LE(o) / 1000.0; o += 4;
        var bw = d.ReadUInt32LE(o) / 1000.0; o += 4;
        var sf = d[o++];
        var cr = d[o++];
        var name = d.From(o).DecodeUtf8Lossy().TrimControl();
        return new MeshEvent.SelfInfoEvent(new SelfInfo(advType, txPower, maxTx, pk, lat, lon, multiAcks, advLoc,
            (byte)((telemetry >> 4) & 0b11), (byte)((telemetry >> 2) & 0b11), (byte)(telemetry & 0b11),
            manualAdd, freq, bw, sf, cr, name));
    }

    public static MeshEvent ParseDeviceInfo(byte[] d)
    {
        if (d.Length < 1) return PacketParser.Fail(d, "DeviceInfo response empty");
        var fw = d[0];
        var o = 1;
        if (fw >= 3 && d.Length < PacketSize.DeviceInfoV3Full)
            return PacketParser.Fail(d, $"DeviceInfo v{fw} response too short: {d.Length} < {PacketSize.DeviceInfoV3Full}");
        int maxContacts = 0, maxChannels = 0;
        uint pin = 0;
        string build = "", model = "", version = "";
        if (fw >= 3)
        {
            maxContacts = d[o++] * 2;
            maxChannels = d[o++];
            pin = d.ReadUInt32LE(o); o += 4;
            build = d.Slice(o, 12).TrimAtNull().DecodeUtf8Lossy().TrimControl(); o += 12;
            model = d.Slice(o, 40).TrimAtNull().DecodeUtf8Lossy().TrimControl(); o += 40;
            version = d.Slice(o, 20).TrimAtNull().DecodeUtf8Lossy().TrimControl(); o += 20;
        }
        var clientRepeat = false;
        if (fw >= 9 && o >= PacketSize.DeviceInfoV3Full && d.Length > o) { clientRepeat = d[o] != 0; o++; }
        byte pathHashMode = 0;
        if (fw >= 10 && o >= PacketSize.DeviceInfoV3Full && d.Length > o) pathHashMode = d[o];
        return new MeshEvent.DeviceInfo(new DeviceCapabilities(fw, maxContacts, maxChannels, pin, build, model, version, clientRepeat, pathHashMode));
    }

    public static MeshEvent ParseCustomVars(byte[] d)
    {
        var vars = new Dictionary<string, string>();
        var raw = d.TryDecodeUtf8();
        if (string.IsNullOrEmpty(raw)) return new MeshEvent.CustomVars(vars);
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf(':');
            if (idx > 0 && idx < pair.Length - 1) vars[pair[..idx]] = pair[(idx + 1)..];
            else if (idx > 0 && idx == pair.Length - 1) { /* empty value dropped like Swift split */ }
        }
        return new MeshEvent.CustomVars(vars);
    }

    public static MeshContact? ParseContactData(byte[] d)
    {
        if (d.Length < PacketSize.Contact) return null;
        var o = 0;
        var pk = d.Slice(o, 32); o += 32;
        var typeByte = d[o++];
        var type = Enum.IsDefined(typeof(ContactType), typeByte) ? (ContactType)typeByte : ContactType.Chat;
        var flags = (ContactFlags)d[o++];
        var pathLen = d[o++];
        if (pathLen != 0xFF && PathEncoding.Decode(pathLen) is null) return null;
        var actual = pathLen == 0xFF ? 0 : PathEncoding.Decode(pathLen)!.Value.ByteLength;
        var pathField = d.Slice(o, 64); o += 64;
        var path = actual > 0 ? pathField.Prefix(actual) : [];
        var name = d.Slice(o, 32).TrimAtNull().DecodeLongestValidUtf8Prefix().TrimControl(); o += 32;
        var lastAdvert = Bytes.FromEpoch(d.ReadUInt32LE(o)); o += 4;
        var lat = d.ReadInt32LE(o) / 1_000_000.0; o += 4;
        var lon = d.ReadInt32LE(o) / 1_000_000.0; o += 4;
        var lastMod = Bytes.FromEpoch(d.ReadUInt32LE(o));
        return new MeshContact(pk, type, typeByte, flags, pathLen, path, name, lastAdvert, lat, lon, lastMod);
    }

    public static MeshEvent ParseContactEvent(byte[] d)
    {
        if (d.Length >= PacketSize.Contact)
        {
            var pathLen = d[34];
            if (pathLen != 0xFF && PathEncoding.Decode(pathLen) is null)
                return PacketParser.Fail(d, $"Contact response uses reserved path length encoding: 0x{pathLen:X2}");
        }
        var c = ParseContactData(d);
        return c is null ? PacketParser.Fail(d, $"Contact response too short: {d.Length} < {PacketSize.Contact}") : new MeshEvent.Contact(c);
    }

    public static MeshEvent ParseNewAdvertisement(byte[] d)
    {
        var c = ParseContactData(d);
        if (c is not null) return new MeshEvent.NewContact(c);
        return d.Length >= PacketBuilder.PublicKeySize
            ? PacketParser.Fail(d, $"NewAdvertisement has public key but insufficient contact data: {d.Length} < {PacketSize.Contact}")
            : PacketParser.Fail(d, $"NewAdvertisement too short: {d.Length}");
    }

    public static MeshEvent ParseContactMessage(byte[] d, bool v3)
    {
        var min = v3 ? PacketSize.ContactMessageV3Minimum : PacketSize.ContactMessageV1Minimum;
        if (d.Length < min) return PacketParser.Fail(d, $"ContactMessage response too short: {d.Length} < {min}");
        var o = 0;
        double? snr = null;
        if (v3) { snr = Bytes.SnrFromByte(d[o]); o += 3; }
        var prefix = d.Slice(o, 6); o += 6;
        var pathLen = d[o++];
        var txtType = d[o++];
        var ts = Bytes.FromEpoch(d.ReadUInt32LE(o)); o += 4;
        byte[]? sig = null;
        if (txtType == 2)
        {
            if (d.Length < o + 4) return PacketParser.Fail(d, $"ContactMessage signature truncated: {d.Length} < {o + 4}");
            sig = d.Slice(o, 4); o += 4;
        }
        var textBytes = d.From(o);
        var text = textBytes.TryDecodeUtf8() ?? textBytes.DecodeUtf8Lossy();
        return new MeshEvent.ContactMessageReceived(new ContactMessage(prefix, pathLen, txtType, ts, sig, text, snr));
    }

    public static MeshEvent ParseChannelMessage(byte[] d, bool v3)
    {
        var min = v3 ? PacketSize.ChannelMessageV3Minimum : PacketSize.ChannelMessageV1Minimum;
        if (d.Length < min) return PacketParser.Fail(d, $"ChannelMessage response too short: {d.Length} < {min}");
        var o = 0;
        double? snr = null;
        if (v3) { snr = Bytes.SnrFromByte(d[o]); o += 3; }
        var idx = d[o++];
        var pathLen = d[o++];
        var txtType = d[o++];
        var ts = Bytes.FromEpoch(d.ReadUInt32LE(o)); o += 4;
        var textBytes = d.From(o);
        var text = textBytes.TryDecodeUtf8() ?? textBytes.DecodeUtf8Lossy();
        return new MeshEvent.ChannelMessageReceived(new ChannelMessage(idx, pathLen, txtType, ts, text, snr));
    }

    public static MeshEvent ParseChannelDatagram(byte[] d)
    {
        if (d.Length < PacketSize.ChannelDatagramMinimum)
            return PacketParser.Fail(d, $"ChannelDatagram response too short: {d.Length} < {PacketSize.ChannelDatagramMinimum}");
        var o = 0;
        var snr = Bytes.SnrFromByte(d[o]); o += 3;
        var idx = d[o++];
        var pathLen = d[o++];
        var dataType = d.ReadUInt16LE(o); o += 2;
        int declared = d[o++];
        var len = Math.Min(declared, d.Length - o);
        return new MeshEvent.ChannelDataReceived(new ChannelDatagram(idx, pathLen, dataType, d.Slice(o, len), snr));
    }

    public static MeshEvent ParseStatusResponse(byte[] d, StatusLayout layout)
    {
        if (d.Length < PacketSize.StatusResponseMinimum)
            return PacketParser.Fail(d, $"StatusResponse too short: {d.Length} < {PacketSize.StatusResponseMinimum}");
        var prefix = d.Slice(1, 6);
        var body = d.From(7);
        var r = ParseStatusBody(body, prefix, layout, fromBinary: false);
        return r is null ? PacketParser.Fail(d, "StatusResponse malformed") : new MeshEvent.StatusResponseEvent(r);
    }

    /// <summary>Parses status fields from a binary response payload (no reserved byte / prefix).</summary>
    public static StatusResponse? ParseStatusFromBinary(byte[] d, byte[] prefix, StatusLayout layout) =>
        d.Length < PacketSize.BinaryResponseStatusBase ? null : ParseStatusBody(d, prefix, layout, fromBinary: true);

    private static StatusResponse? ParseStatusBody(byte[] d, byte[] prefix, StatusLayout layout, bool fromBinary)
    {
        var o = 0;
        int battery = d.ReadUInt16LE(o); o += 2;
        int txq = d.ReadUInt16LE(o); o += 2;
        int noise = d.ReadInt16LE(o); o += 2;
        int rssi = d.ReadInt16LE(o); o += 2;
        var recv = d.ReadUInt32LE(o); o += 4;
        var sent = d.ReadUInt32LE(o); o += 4;
        var air = d.ReadUInt32LE(o); o += 4;
        var up = d.ReadUInt32LE(o); o += 4;
        var sf = d.ReadUInt32LE(o); o += 4;
        var sd = d.ReadUInt32LE(o); o += 4;
        var rf = d.ReadUInt32LE(o); o += 4;
        var rd = d.ReadUInt32LE(o); o += 4;
        int full = d.ReadUInt16LE(o); o += 2;
        var snr = d.ReadInt16LE(o) / 4.0; o += 2;
        int dd = d.ReadUInt16LE(o); o += 2;
        int fd = d.ReadUInt16LE(o); o += 2;
        if (layout == StatusLayout.Repeater)
        {
            uint rxAir, errs;
            if (fromBinary)
            {
                rxAir = d.Length >= PacketSize.BinaryResponseStatusWithRxAirtime ? d.ReadUInt32LE(o) : 0;
                o += 4;
                errs = d.Length >= PacketSize.BinaryResponseStatusWithReceiveErrors ? d.ReadUInt32LE(o) : 0;
            }
            else
            {
                rxAir = d.ReadUInt32LE(o); o += 4;
                errs = d.Length >= o + 4 ? d.ReadUInt32LE(o) : 0;
            }
            return new StatusResponse(StatusLayout.Repeater, prefix, battery, txq, noise, rssi, recv, sent, air, up, sf, sd, rf, rd, full, snr, dd, fd, rxAir, errs);
        }
        ushort? posted = null, pushed = null;
        var has = fromBinary ? d.Length >= PacketSize.BinaryResponseStatusWithRxAirtime : d.Length >= o + 4;
        if (has) { posted = d.ReadUInt16LE(o); pushed = d.ReadUInt16LE(o + 2); }
        return new StatusResponse(StatusLayout.RoomServer, prefix, battery, txq, noise, rssi, recv, sent, air, up, sf, sd, rf, rd, full, snr, dd, fd, 0, 0, posted, pushed);
    }

    public static StatusResponse RoomServerStatusFromRepeaterLayout(StatusResponse r) =>
        r with
        {
            Layout = StatusLayout.RoomServer,
            RxAirtime = 0,
            ReceiveErrors = 0,
            RoomServerPostedCount = (ushort)(r.RxAirtime & 0xFFFF),
            RoomServerPostPushCount = (ushort)(r.RxAirtime >> 16),
        };

    public static MeshEvent ParseLoginSuccess(byte[] d)
    {
        if (d.Length < PacketSize.LoginSuccessMinimum)
            return new MeshEvent.LoginSuccess(new LoginInfo(0, false, []));
        var prefix = d.Slice(1, 6);
        if (d.Length >= PacketSize.LoginSuccessExtended)
        {
            var isAdmin = d[0] == 1;
            var fwPerm = d[11];
            byte perm = isAdmin ? (byte)0x02 : fwPerm == 0x02 ? (byte)0x01 : (byte)0x00;
            var epoch = d.ReadUInt32LE(7);
            return new MeshEvent.LoginSuccess(new LoginInfo(perm, isAdmin, prefix, epoch == 0 ? null : Bytes.FromEpoch(epoch)));
        }
        return d[0] switch
        {
            1 => new MeshEvent.LoginSuccess(new LoginInfo(0x02, true, prefix)),
            2 => new MeshEvent.LoginSuccess(new LoginInfo(0x00, false, prefix)),
            _ => new MeshEvent.LoginSuccess(new LoginInfo(0x01, false, prefix)),
        };
    }

    public static MeshEvent ParseAdvertPath(byte[] d)
    {
        if (d.Length < 5) return PacketParser.Fail(d, $"AdvertPathResponse too short: {d.Length} bytes, need 5");
        var ts = d.ReadUInt32LE(0);
        var pathLen = d[4];
        if (PathEncoding.Decode(pathLen) is not { } dec)
            return PacketParser.Fail(d, $"AdvertPathResponse uses reserved path length encoding: 0x{pathLen:X2}");
        if (d.Length < 5 + dec.ByteLength) return PacketParser.Fail(d, $"AdvertPathResponse path truncated: {d.Length} < {5 + dec.ByteLength}");
        return new MeshEvent.AdvertPathResponseEvent(new AdvertPathResponse(ts, pathLen, d.Slice(5, dec.ByteLength)));
    }

    public static MeshEvent ParseAllowedRepeatFreq(byte[] d)
    {
        var list = new List<FrequencyRange>();
        for (var o = 0; o + 8 <= d.Length; o += 8) list.Add(new FrequencyRange(d.ReadUInt32LE(o), d.ReadUInt32LE(o + 4)));
        return new MeshEvent.AllowedRepeatFreq(list);
    }

    public static MeshEvent ParseDefaultFloodScope(byte[] d)
    {
        if (d.Length == 0) return new MeshEvent.DefaultFloodScopeEvent(null);
        if (d.Length != PacketSize.DefaultFloodScopeSet)
            return PacketParser.Fail(d, $"DefaultFloodScope response wrong size: {d.Length}, expected 0 or {PacketSize.DefaultFloodScopeSet}");
        var nameBytes = d.Slice(0, PacketSize.DefaultFloodScopeNameField).TrimAtNull();
        var name = nameBytes.TryDecodeUtf8() ?? nameBytes.DecodeUtf8Lossy();
        return new MeshEvent.DefaultFloodScopeEvent(new DefaultFloodScope(name, d.Slice(PacketSize.DefaultFloodScopeNameField, 16)));
    }

    public static MeshEvent ParseChannelInfo(byte[] d)
    {
        if (d.Length < PacketSize.ChannelInfoMinimum)
            return PacketParser.Fail(d, $"ChannelInfo too short: {d.Length} < {PacketSize.ChannelInfoMinimum}");
        var name = d.Slice(1, 32).TrimAtNull().DecodeUtf8Lossy();
        return new MeshEvent.ChannelInfoEvent(new ChannelInfo(d[0], name, d.Slice(33, 16)));
    }

    public static MeshEvent ParseStats(byte[] p)
    {
        if (p.Length < 1) return PacketParser.Fail(p, $"Stats response too short: {p.Length} < 1");
        var d = p.From(1);
        switch (p[0])
        {
            case (byte)StatsType.Core:
                if (d.Length < PacketSize.CoreStatsMinimum) return PacketParser.Fail(d, $"CoreStats too short: {d.Length} < {PacketSize.CoreStatsMinimum}");
                return new MeshEvent.StatsCore(new CoreStats(d.ReadUInt16LE(0), d.ReadUInt32LE(2), d.ReadUInt16LE(6), d[8]));
            case (byte)StatsType.Radio:
                if (d.Length < PacketSize.RadioStatsMinimum) return PacketParser.Fail(d, $"RadioStats too short: {d.Length} < {PacketSize.RadioStatsMinimum}");
                return new MeshEvent.StatsRadio(new RadioStats(d.ReadInt16LE(0), (sbyte)d[2], Bytes.SnrFromByte(d[3]), d.ReadUInt32LE(4), d.ReadUInt32LE(8)));
            case (byte)StatsType.Packets:
                if (d.Length < PacketSize.PacketStatsMinimum) return PacketParser.Fail(d, $"PacketStats too short: {d.Length} < {PacketSize.PacketStatsMinimum}");
                var errs = d.Length >= PacketSize.PacketStatsWithReceiveErrors ? d.ReadUInt32LE(24) : 0;
                return new MeshEvent.StatsPackets(new PacketStats(d.ReadUInt32LE(0), d.ReadUInt32LE(4), d.ReadUInt32LE(8), d.ReadUInt32LE(12), d.ReadUInt32LE(16), d.ReadUInt32LE(20), errs));
            default:
                return PacketParser.Fail(p, $"Unknown stats type: {p[0]}");
        }
    }

    public static MeshEvent ParsePathDiscovery(byte[] d)
    {
        if (d.Length < PacketSize.PathDiscoveryMinimum)
            return PacketParser.Fail(d, $"PathDiscoveryResponse too short: {d.Length} bytes, need {PacketSize.PathDiscoveryMinimum}");
        var prefix = d.Slice(1, 6);
        var o = 7;
        byte outLen = 0, inLen = 0;
        byte[] outPath = [], inPath = [];
        if (d.Length > o)
        {
            outLen = d[o++];
            if (PathEncoding.Decode(outLen) is { ByteLength: > 0 } dec)
            {
                if (d.Length < o + dec.ByteLength) return PacketParser.Fail(d, "PathDiscoveryResponse truncated outbound path");
                outPath = d.Slice(o, dec.ByteLength); o += dec.ByteLength;
            }
        }
        if (d.Length > o)
        {
            inLen = d[o++];
            if (PathEncoding.Decode(inLen) is { ByteLength: > 0 } dec)
            {
                if (d.Length < o + dec.ByteLength) return PacketParser.Fail(d, "PathDiscoveryResponse truncated inbound path");
                inPath = d.Slice(o, dec.ByteLength);
            }
        }
        return new MeshEvent.PathResponse(new PathInfo(prefix, outLen, outPath, inLen, inPath));
    }

    public static MeshEvent ParseControlData(byte[] d)
    {
        if (d.Length < PacketSize.ControlDataMinimum)
            return PacketParser.Fail(d, $"ControlData too short: {d.Length} < {PacketSize.ControlDataMinimum}");
        var snr = Bytes.SnrFromByte(d[0]);
        int rssi = (sbyte)d[1];
        var pathLen = d[2];
        var payloadType = d[3];
        var payload = d.From(4);
        if ((payloadType & 0xF0) == 0x90 && payload.Length >= 5)
        {
            var nodeType = (byte)(payloadType & 0x0F);
            var snrIn = Bytes.SnrFromByte(payload[0]);
            var tag = payload.Slice(1, 4);
            var pk = payload.Length >= 37 ? payload.Slice(5, 32) : payload.Length >= 13 ? payload.Slice(5, 8) : payload.From(5);
            return new MeshEvent.DiscoverResponseEvent(new DiscoverResponse(nodeType, snrIn, snr, rssi, pathLen, tag, pk));
        }
        return new MeshEvent.ControlData(new ControlDataInfo(snr, rssi, pathLen, payloadType, payload));
    }

    public static MeshEvent ParseTraceData(byte[] d)
    {
        if (d.Length < PacketSize.TraceDataMinimum)
            return PacketParser.Fail(d, $"TraceData too short: {d.Length} bytes, need {PacketSize.TraceDataMinimum}");
        int pathLength = d[1];
        var flags = d[2];
        var hashSize = 1 << (flags & 0x03);
        var hopCount = pathLength > 0 ? pathLength / hashSize : 0;
        var tag = d.ReadUInt32LE(3);
        var auth = d.ReadUInt32LE(7);
        const int hashesStart = 11;
        var snrsStart = hashesStart + pathLength;
        var finalOffset = snrsStart + hopCount;
        if (d.Length < finalOffset + 1)
            return PacketParser.Fail(d, $"TraceData too short for path: need {finalOffset + 1}, have {d.Length}");
        var nodes = new List<TraceNode>();
        for (var i = 0; i < hopCount; i++)
        {
            var hash = d.Slice(hashesStart + i * hashSize, hashSize);
            var snr = Bytes.SnrFromByte(d[snrsStart + i]);
            var isDest = hash.All(b => b == 0xFF);
            nodes.Add(new TraceNode(isDest ? null : hash, snr));
        }
        nodes.Add(new TraceNode(null, Bytes.SnrFromByte(d[finalOffset])));
        return new MeshEvent.TraceData(new TraceInfo(tag, auth, flags, (byte)pathLength, nodes));
    }

    public static MeshEvent ParseLogData(byte[] d)
    {
        if (d.Length >= 2)
        {
            var snr = Bytes.SnrFromByte(d[0]);
            int rssi = (sbyte)d[1];
            var payload = d.From(2);
            if (RxLogParser.Parse(snr, rssi, payload) is { } parsed) return new MeshEvent.RxLogData(parsed);
            return new MeshEvent.LogData(new LogDataInfo(snr, rssi, payload));
        }
        if (RxLogParser.Parse(null, null, d) is { } p2) return new MeshEvent.RxLogData(p2);
        return new MeshEvent.LogData(new LogDataInfo(null, null, d));
    }

    // Binary-response payload parsers

    public static NeighboursResponse ParseNeighbours(byte[] d, byte[] prefix, byte[] tag, int prefixLength = 4)
    {
        if (d.Length < 4) return new NeighboursResponse(prefix, tag, 0, []);
        int total = d.ReadInt16LE(0);
        int results = d.ReadInt16LE(2);
        var list = new List<Neighbour>();
        var entry = prefixLength + 5;
        var o = 4;
        for (var i = 0; i < results; i++)
        {
            if (o + entry > d.Length) break;
            var kp = d.Slice(o, prefixLength); o += prefixLength;
            var secs = d.ReadInt32LE(o); o += 4;
            var snr = Bytes.SnrFromByte(d[o]); o += 1;
            list.Add(new Neighbour(kp, secs, snr));
        }
        return new NeighboursResponse(prefix, tag, total, list);
    }

    public static IReadOnlyList<AclEntry> ParseAcl(byte[] d)
    {
        var list = new List<AclEntry>();
        for (var o = 0; o + 7 <= d.Length; o += 7)
        {
            var kp = d.Slice(o, 6);
            if (kp.All(b => b == 0)) continue;
            list.Add(new AclEntry(kp, d[o + 6]));
        }
        return list;
    }

    public static IReadOnlyList<MmaEntry> ParseMma(byte[] d)
    {
        var list = new List<MmaEntry>();
        var o = 0;
        while (o < d.Length)
        {
            if (o + 2 > d.Length) break;
            var ch = d[o];
            var code = d[o + 1];
            o += 2;
            if (!Enum.IsDefined(typeof(LppSensorType), code)) break;
            var t = (LppSensorType)code;
            var size = t.DataSize();
            if (o + size * 3 > d.Length) break;
            var min = LppDecoder.DecodeToDouble(t, d.Slice(o, size)); o += size;
            var max = LppDecoder.DecodeToDouble(t, d.Slice(o, size)); o += size;
            var avg = LppDecoder.DecodeToDouble(t, d.Slice(o, size)); o += size;
            list.Add(new MmaEntry(ch, t.DisplayName(), min, max, avg));
        }
        return list;
    }

    public static IReadOnlyList<string> ParseRegions(byte[] d)
    {
        if (d.Length < 4) throw new MeshCoreException(MeshCoreErrorKind.ParseError, $"Region response too short ({d.Length} bytes)");
        var s = d.From(4).TryDecodeUtf8() ?? throw new MeshCoreException(MeshCoreErrorKind.ParseError, "Invalid UTF-8 in region response");
        s = s.TrimControl();
        if (s.Length == 0) return [];
        return s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0 && x != "*").ToList();
    }
}
