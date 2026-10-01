using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using MC1.Core.Utilities;
using MeshCore;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace MC1.Core.Simulation;

/// <summary>
/// An in-process MeshCore companion radio simulator. Implements the companion protocol closely enough to exercise
/// every feature of the app without hardware (used for the "Demo radio" connection and automated tests).
/// </summary>
public sealed class SimulatedRadio : IMeshTransport
{
    private sealed class Node
    {
        public required string Name { get; set; }
        public required byte[] PublicKey { get; init; }
        public required byte[] PrivateScalar { get; init; }
        public ContactType Type { get; init; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public byte PathLen { get; set; } = 0xFF;
        public byte[] Path { get; set; } = [];
        public ContactFlags Flags { get; set; }
        public uint LastAdvert { get; set; }
        public uint LastModified { get; set; }
        public bool InContacts { get; set; } = true;
        public string AdminPassword { get; init; } = "password";
        public string GuestPassword { get; init; } = "";
    }

    private Channel<byte[]> _rx = Channel.CreateUnbounded<byte[]>();
    private readonly object _lock = new();
    private readonly Queue<byte[]> _messageQueue = new();
    private readonly List<Node> _nodes = new();
    private readonly (string Name, byte[] Secret)[] _channels = new (string, byte[])[40];
    private readonly byte[] _selfPriv64;
    private readonly byte[] _selfPub;
    private CancellationTokenSource? _cts;
    private readonly Random _rng;
    private readonly bool _chatter;

    private string _name = "Demo Radio";
    private double _lat = 1.0000, _lon = 1.0000;
    private double _freq = 910.525, _bw = 62.5;

    /// <summary>Changes the radio settings as if another app had (tests).</summary>
    public void SetRadioForTest(double freqMHz, double bwKHz, byte sf, byte cr)
    {
        _freq = freqMHz;
        _bw = bwKHz;
        _sf = sf;
        _cr = cr;
    }
    private byte _sf = 7, _cr = 5;
    private sbyte _tx = 20;
    private bool _manualAdd;
    private byte _telemetryMode, _advLocPolicy, _multiAcks;
    private AutoAddConfig _autoAdd = new(AutoAddConfig.ContactsBit | AutoAddConfig.RepeatersBit | AutoAddConfig.RoomServersBit);
    private byte _pathHashMode;
    private uint _blePin = 123456;
    private uint _clockOffset;
    private string? _defaultScopeName;
    private int _signLength;

    public SimulatedRadio(int seed = 42, bool chatter = true)
    {
        _rng = new Random(seed);
        _chatter = chatter;
        (_selfPriv64, _selfPub) = NewIdentity(_rng);
        AddNode("Alice", ContactType.Chat, 1.0064, 1.0278, [0x3C]);
        AddNode("Bob", ContactType.Chat, 0.9838, 1.0446, []);
        AddNode("Hilltop Repeater", ContactType.Repeater, 1.0332, 1.0086, []);
        AddNode("Ridge RPT", ContactType.Repeater, 0.9619, 0.9677, []);
        AddNode("Town Square", ContactType.Room, 0.9906, 1.0291, [0x3C]);
        AddNode("Weather Station", ContactType.Sensor, 1.0218, 1.0577, []);
        _nodes[2].PathLen = 0; // direct neighbour
        _nodes[3].PathLen = 1; _nodes[3].Path = [_nodes[2].PublicKey[0]];
        _nodes[0].PathLen = 1; _nodes[0].Path = [_nodes[2].PublicKey[0]];
        var newcomer = AddNode("Carol", ContactType.Chat, 0.9728, 1.0188, []);
        newcomer.InContacts = false;
        _channels[0] = ("Public", ChannelSecrets.PublicChannelSecret);
        _channels[1] = ("#test", ChannelSecrets.HashSecret("#test"));
        for (var i = 2; i < _channels.Length; i++) _channels[i] = ("", new byte[16]);
        var now = Bytes.NowEpoch();
        foreach (var n in _nodes) { n.LastAdvert = now - (uint)_rng.Next(60, 7200); n.LastModified = now - 3600; }
        lock (_lock)
        {
            QueueContactMessage(_nodes[0], "Hey! Welcome to the MeshCore demo 👋", now - 600);
            QueueChannelMessage(0, "Bob", "Morning mesh! Anyone hearing the new repeater?", now - 900);
            QueueChannelMessage(1, "Alice", "Testing 1-2-3 on #test", now - 300);
        }
    }

    public string Description => "Demo radio";
    public bool IsConnected { get; private set; }
    public bool SupportsPipelinedReads => true;
    public ChannelReader<byte[]> Received => _rx.Reader;
    public byte[] SelfPublicKey => _selfPub;

    private static (byte[] Priv64, byte[] Pub) NewIdentity(Random rng)
    {
        var seed = new byte[32];
        rng.NextBytes(seed);
        var pub = new byte[32];
        Ed25519.GeneratePublicKey(seed, 0, pub, 0);
        var h = SHA512.HashData(seed);
        h[0] &= 248; h[31] &= 63; h[31] |= 64;
        return (h, pub);
    }

    private Node AddNode(string name, ContactType type, double lat, double lon, byte[] path)
    {
        var (priv, pub) = NewIdentity(_rng);
        var n = new Node { Name = name, PublicKey = pub, PrivateScalar = priv, Type = type, Lat = lat, Lon = lon, Path = path, PathLen = path.Length == 0 ? (byte)0xFF : (byte)path.Length };
        _nodes.Add(n);
        return n;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _rx = Channel.CreateUnbounded<byte[]>();
        IsConnected = true;
        _cts = new CancellationTokenSource();
        if (_chatter) _ = Task.Run(() => ChatterLoop(_cts.Token));
        lock (_lock) if (_messageQueue.Count > 0) Push([0x83]);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        _cts?.Cancel();
        _rx.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    /// <summary>Pushes a NEW_ADVERT for a node that isn't in the radio's contacts yet.</summary>
    public void TriggerNewNodeAdvert()
    {
        lock (_lock)
        {
            var carol = _nodes.First(x => x.Name == "Carol");
            carol.LastAdvert = Now();
            Push(new byte[] { 0x8A }.Concat(ContactFrame(carol)).ToArray());
        }
    }

    /// <summary>Simulates the link dropping (for reconnect tests).</summary>
    public void SimulateDisconnect() => _ = DisconnectAsync();

    private void Push(byte[] frame) { if (IsConnected) _rx.Writer.TryWrite(frame); }

    private void PushLater(int ms, Func<byte[]?> make, CancellationToken? ct = null)
    {
        var token = ct ?? _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ms, token).ConfigureAwait(false); } catch { return; }
            if (make() is { } f) Push(f);
        });
    }

    private uint Now() => Bytes.NowEpoch() + _clockOffset;

    public Task SendAsync(byte[] cmd, CancellationToken ct = default)
    {
        if (!IsConnected) throw new TransportException("Not connected");
        lock (_lock) Handle(cmd);
        return Task.CompletedTask;
    }

    private static readonly byte[] Ok = [0x00];
    private static byte[] Err(ErrorCode c) => [0x01, (byte)c];

    private Node? FindByPrefix(byte[] prefix) => _nodes.FirstOrDefault(n => n.PublicKey.StartsWith(prefix));

    private void Handle(byte[] c)
    {
        var code = (CommandCode)c[0];
        switch (code)
        {
            case CommandCode.AppStart: Push(SelfInfoFrame()); break;
            case CommandCode.DeviceQuery:
                Push(new ByteWriter(0x0D, 10).U8(40 / 2 * 5).U8(40).U32(_blePin)
                    .Raw("29 Sep 2026"u8.ToArray().PaddedOrTruncated(12)).Raw("Demo Radio (simulated)"u8.ToArray().PaddedOrTruncated(40))
                    .Raw("v1.16.0-sim"u8.ToArray().PaddedOrTruncated(20)).U8(0).U8(_pathHashMode).ToArray());
                break;
            case CommandCode.GetBattery: Push(new ByteWriter(0x0C).U16((ushort)(3950 + _rng.Next(-20, 20))).U32(128).U32(1024).ToArray()); break;
            case CommandCode.GetTime: Push(new ByteWriter(0x09).U32(Now()).ToArray()); break;
            case CommandCode.SetTime: _clockOffset = c.ReadUInt32LE(1) - Bytes.NowEpoch(); Push(Ok); break;
            case CommandCode.SetName: _name = Encoding.UTF8.GetString(c, 1, c.Length - 1); Push(Ok); break;
            case CommandCode.SetCoordinates: _lat = c.ReadInt32LE(1) / 1e6; _lon = c.ReadInt32LE(5) / 1e6; Push(Ok); break;
            case CommandCode.SetTxPower: _tx = (sbyte)c[1]; Push(Ok); break;
            case CommandCode.SetRadio: _freq = c.ReadUInt32LE(1) / 1000.0; _bw = c.ReadUInt32LE(5) / 1000.0; _sf = c[9]; _cr = c[10]; Push(Ok); break;
            case CommandCode.SetOtherParams:
                _manualAdd = c[1] != 0; _telemetryMode = c[2]; _advLocPolicy = c[3]; if (c.Length > 4) _multiAcks = c[4];
                Push(Ok); break;
            case CommandCode.GetAutoAddConfig: Push([0x19, _autoAdd.Bitmask, _autoAdd.MaxHops]); break;
            case CommandCode.SetAutoAddConfig: _autoAdd = new AutoAddConfig(c[1], c.Length > 2 ? c[2] : (byte)0); Push(Ok); break;
            case CommandCode.SetPathHashMode: _pathHashMode = c[2]; Push(Ok); break;
            case CommandCode.SetDevicePin: _blePin = c.ReadUInt32LE(1); Push(Ok); break;
            case CommandCode.GetRepeatFreq: Push(new ByteWriter(0x1A).U32(902_000).U32(928_000).ToArray()); break;
            case CommandCode.GetTuningParams: Push(new ByteWriter(0x17).U32(0).U32(1000).ToArray()); break;
            case CommandCode.SetTuning: Push(Ok); break;
            case CommandCode.GetCustomVars: Push(new ByteWriter(0x15).Utf8("gps:1,gps.interval:900").ToArray()); break;
            case CommandCode.SetCustomVar: Push(Ok); break;
            case CommandCode.ExportPrivateKey: Push(new byte[] { 0x0E }.Concat(_selfPriv64).ToArray()); break;
            case CommandCode.ImportPrivateKey: Push(Ok); break;
            case CommandCode.Reboot: PushLater(200, () => { _ = DisconnectAsync(); return null; }); break;
            case CommandCode.FactoryReset: Push(Ok); break;
            case CommandCode.GetStats:
                Push(c[1] switch
                {
                    0 => new ByteWriter(0x18, 0).U16(3950).U32(86400).U16(0).U8(0).ToArray(),
                    1 => new ByteWriter(0x18, 1).U16(unchecked((ushort)(short)(-112 + _rng.Next(-4, 5)))).I8((sbyte)(-85 + _rng.Next(-10, 10))).I8((sbyte)(_rng.Next(-20, 40))).U32(120).U32(900).ToArray(),
                    _ => new ByteWriter(0x18, 2).U32(1200).U32(300).U32(200).U32(100).U32(900).U32(300).U32(2).ToArray(),
                });
                break;
            case CommandCode.GetContacts:
                var since = c.Length >= 5 ? c.ReadUInt32LE(1) : 0;
                var list = _nodes.Where(n => n.InContacts && n.LastModified > since).ToList();
                Push(new ByteWriter(0x02).U32((uint)list.Count).ToArray());
                foreach (var n in list) Push(new byte[] { 0x03 }.Concat(ContactFrame(n)).ToArray());
                Push(new ByteWriter(0x04).U32(_nodes.Where(n => n.InContacts).Select(n => n.LastModified).DefaultIfEmpty(0u).Max()).ToArray());
                break;
            case CommandCode.GetContactByKey:
                var byKey = _nodes.FirstOrDefault(n => n.InContacts && n.PublicKey.SequenceEquals(c.Slice(1, 32)));
                Push(byKey is null ? Err(ErrorCode.NotFound) : new byte[] { 0x03 }.Concat(ContactFrame(byKey)).ToArray());
                break;
            case CommandCode.UpdateContact:
                var key = c.Slice(1, 32);
                var node = _nodes.FirstOrDefault(n => n.PublicKey.SequenceEquals(key));
                if (node is null)
                {
                    var name = c.Slice(100, 32).TrimAtNull().DecodeUtf8Lossy();
                    node = new Node { Name = name, PublicKey = key, PrivateScalar = new byte[64], Type = (ContactType)c[33] };
                    _nodes.Add(node);
                }
                node.InContacts = true;
                node.Flags = (ContactFlags)c[34];
                node.PathLen = c[35];
                node.Path = node.PathLen == 0xFF ? [] : c.Slice(36, PathEncoding.Decode(node.PathLen)?.ByteLength ?? 0);
                node.LastModified = Now();
                Push(Ok);
                break;
            case CommandCode.RemoveContact:
                var rm = _nodes.FirstOrDefault(n => n.InContacts && n.PublicKey.SequenceEquals(c.Slice(1, 32)));
                if (rm is null) Push(Err(ErrorCode.NotFound)); else { rm.InContacts = false; Push(Ok); }
                break;
            case CommandCode.ResetPath:
                var rp = FindByPrefix(c.Slice(1, 32));
                if (rp is null) Push(Err(ErrorCode.NotFound)); else { rp.PathLen = 0xFF; rp.Path = []; rp.LastModified = Now(); Push(Ok); }
                break;
            case CommandCode.ShareContact: Push(Ok); break;
            case CommandCode.ExportContact:
                var ex = c.Length > 1 ? FindByPrefix(c.Slice(1, 32)) : null;
                var pk = ex?.PublicKey ?? _selfPub;
                Push(new byte[] { 0x0B, 0x11, 0x80 }.Concat(pk).Concat(Encoding.UTF8.GetBytes(ex?.Name ?? _name)).ToArray());
                break;
            case CommandCode.ImportContact: Push(Ok); break;
            case CommandCode.GetChannel:
                var idx = c[1];
                if (idx >= _channels.Length) { Push(Err(ErrorCode.NotFound)); break; }
                Push(new ByteWriter(0x12, idx).Raw(Encoding.UTF8.GetBytes(_channels[idx].Name).PaddedOrTruncated(32)).Raw(_channels[idx].Secret).ToArray());
                break;
            case CommandCode.SetChannel:
                _channels[c[1]] = (c.Slice(2, 32).TrimAtNull().DecodeUtf8Lossy(), c.Slice(34, 16));
                Push(Ok);
                break;
            case CommandCode.GetMessage:
                Push(_messageQueue.Count > 0 ? _messageQueue.Dequeue() : [0x0A]);
                break;
            case CommandCode.SendMessage: HandleSendMessage(c); break;
            case CommandCode.SendChannelMessage: HandleSendChannel(c); break;
            case CommandCode.SendAdvertisement: Push(Ok); break;
            case CommandCode.SendLogin: HandleLogin(c); break;
            case CommandCode.SendLogout: Push(Ok); break;
            case CommandCode.SendStatusRequest: HandleBinary(c.Slice(1, 32), BinaryRequestType.Status, []); break;
            case CommandCode.GetSelfTelemetry:
                if (c.Length >= 36) HandleBinary(c.Slice(4, 32), BinaryRequestType.Telemetry, []);
                else Push(new byte[] { 0x8B, 0 }.Concat(_selfPub.Prefix(6)).Concat(new LppEncoder().AddVoltage(1, 3.95).AddTemperature(1, 24.5).Encode()).ToArray());
                break;
            case CommandCode.BinaryRequest: HandleBinary(c.Slice(1, 32), (BinaryRequestType)c[33], c.From(34)); break;
            case CommandCode.PathDiscovery: HandlePathDiscovery(c.Slice(2, 32)); break;
            case CommandCode.SendTrace: HandleTrace(c); break;
            case CommandCode.SetFloodScope: Push(Ok); break;
            case CommandCode.SetDefaultFloodScope: _defaultScopeName = c.Length > 1 ? c.Slice(1, 31).TrimAtNull().DecodeUtf8Lossy() : null; Push(Ok); break;
            case CommandCode.GetDefaultFloodScope:
                Push(_defaultScopeName is null ? [0x1C] : new ByteWriter(0x1C).Raw(Encoding.UTF8.GetBytes(_defaultScopeName).PaddedOrTruncated(31)).Raw(new FloodScope.Region(_defaultScopeName).ScopeKey()).ToArray());
                break;
            case CommandCode.SendControlData: HandleControl(c); break;
            case CommandCode.SignStart: _signLength = 0; Push(new ByteWriter(0x13, 0).U32(8192).ToArray()); break;
            case CommandCode.SignData: _signLength += c.Length - 1; Push(Ok); break;
            case CommandCode.SignFinish: Push(new byte[] { 0x14 }.Concat(RandomNumberGenerator.GetBytes(64)).ToArray()); break;
            case CommandCode.SendAnonReq: HandleBinary(c.Slice(1, 32), BinaryRequestType.Status, [], anonRegions: true); break;
            case CommandCode.HasConnection: Push(Ok); break;
            case CommandCode.GetAdvertPath: Push(new ByteWriter(0x16).U32(Now() - 120).U8(1).U8(_nodes[2].PublicKey[0]).ToArray()); break;
            default: Push(Err(ErrorCode.UnsupportedCommand)); break;
        }
    }

    private byte[] SelfInfoFrame() => new ByteWriter(0x05).U8(1).I8(_tx).I8(22).Raw(_selfPub)
        .I32((int)(_lat * 1e6)).I32((int)(_lon * 1e6)).U8(_multiAcks).U8(_advLocPolicy).U8(_telemetryMode).U8(_manualAdd ? (byte)1 : (byte)0)
        .U32((uint)Math.Round(_freq * 1000)).U32((uint)Math.Round(_bw * 1000)).U8(_sf).U8(_cr).Utf8(_name).ToArray();

    private static byte[] ContactFrame(Node n) => new ByteWriter()
        .Raw(n.PublicKey).U8((byte)n.Type).U8((byte)n.Flags).U8(n.PathLen).Raw(n.Path.PaddedOrTruncated(64))
        .Raw(n.Name.Utf8PaddedOrTruncated(32)).U32(n.LastAdvert).I32((int)(n.Lat * 1e6)).I32((int)(n.Lon * 1e6)).U32(n.LastModified).ToArray();

    private void QueueContactMessage(Node from, string text, uint ts, byte textType = 0, byte[]? signature = null)
    {
        var w = new ByteWriter(0x10).I8((sbyte)(_rng.Next(-10, 40))).U8(0).U8(0).Raw(from.PublicKey.Prefix(6)).U8(from.PathLen == 0xFF ? (byte)2 : from.PathLen).U8(textType).U32(ts);
        if (textType == 2) w.Raw(signature ?? from.PublicKey.Prefix(4));
        w.Utf8(text);
        _messageQueue.Enqueue(w.ToArray());
    }

    private void QueueChannelMessage(int idx, string sender, string text, uint ts)
    {
        _messageQueue.Enqueue(new ByteWriter(0x11).I8((sbyte)_rng.Next(-8, 30)).U8(0).U8(0).U8((byte)idx).U8(2).U8(0).U32(ts).Utf8($"{sender}: {text}").ToArray());
    }

    private void Notify() => Push([0x83]);

    private byte[] RawGroupPacket(int channel, string sender, string text, uint ts, byte[] path)
    {
        var secret = _channels[channel].Secret;
        var enc = ChannelCrypto.Encrypt(ts, 0, $"{sender}: {text}", secret);
        var payload = new byte[] { ChannelCrypto.ChannelHash(secret) }.Concat(enc).ToArray();
        var header = (byte)(((byte)PayloadType.GroupText << 2) | (byte)RouteType.Flood);
        return new byte[] { header, (byte)path.Length }.Concat(path).Concat(payload).ToArray();
    }

    private byte[] LogFrame(byte[] raw, double snr, int rssi) =>
        new byte[] { 0x88, unchecked((byte)(sbyte)Math.Round(snr * 4)), unchecked((byte)(sbyte)rssi) }.Concat(raw).ToArray();

    private void HandleSendMessage(byte[] c)
    {
        var isCli = c[1] == 0x01;
        var attempt = c[2];
        var ts = c.ReadUInt32LE(3);
        var dest = c.Slice(7, 6);
        var text = Encoding.UTF8.GetString(c, 13, c.Length - 13);
        var node = _nodes.FirstOrDefault(n => n.InContacts && n.PublicKey.StartsWith(dest));
        if (node is null) { Push(Err(ErrorCode.NotFound)); return; }
        var ack = AckCodeBuilder.ExpectedAck(ts, attempt, text, _selfPub);
        var flood = node.PathLen == 0xFF;
        Push(new ByteWriter(0x06, flood ? (byte)1 : (byte)0).Raw(ack).U32(flood ? 6000u : 3000u).ToArray());
        if (isCli) { HandleCli(node, text); return; }
        // "Bob" is out of range on first attempt to exercise retries; everyone else ACKs promptly.
        if (node.Name == "Bob" && attempt == 0) return;
        PushLater(400 + _rng.Next(600), () => new byte[] { 0x82 }.Concat(ack).Concat(BitConverter.GetBytes((uint)(500 + _rng.Next(900)))).ToArray());
        if (node.Type == ContactType.Room)
        {
            PushLater(1500, () =>
            {
                lock (_lock) QueueContactMessage(node, text, Now(), 2, _selfPub.Prefix(4));
                return [0x83];
            });
            return;
        }
        if (node.Type != ContactType.Chat) return;
        // Reactions aren't answered.
        if (ReactionParser.IsReactionText(text, true)) return;
        PushLater(2000 + _rng.Next(1500), () =>
        {
            lock (_lock)
            {
                var reply = text.Contains('?') ? "Good question — let me check and get back to you." : $"Got it: \"{text}\" 👍";
                QueueContactMessage(node, reply, Now());
                var shared = DirectMessageCrypto.ComputeSharedSecret(node.PrivateScalar, Ed25519ToX25519.ConvertPublicKey(_selfPub)!)!;
                var packet = DirectMessageCrypto.Encrypt(_selfPub[0], node.PublicKey[0], Now(), 0, reply, shared);
                var header = (byte)(((byte)PayloadType.TextMessage << 2) | (byte)RouteType.Direct);
                Push(LogFrame(new byte[] { header, (byte)node.Path.Length }.Concat(node.Path).Concat(packet).ToArray(), 7.5, -80));
            }
            return [0x83];
        });
    }

    private void HandleSendChannel(byte[] c)
    {
        var idx = c[2];
        var ts = c.ReadUInt32LE(3);
        var text = Encoding.UTF8.GetString(c, 7, c.Length - 7);
        if (idx >= _channels.Length || !_channels[idx].Secret.Any(b => b != 0)) { Push(Err(ErrorCode.NotFound)); return; }
        Push(Ok);
        // Echoes from two repeaters → "heard repeats".
        PushLater(700, () => LogFrame(RawGroupPacket(idx, _name, text, ts, [_nodes[2].PublicKey[0]]), 9.25, -71));
        PushLater(1600, () => LogFrame(RawGroupPacket(idx, _name, text, ts, [_nodes[2].PublicKey[0], _nodes[3].PublicKey[0]]), 2.5, -97));
        if (ReactionParser.IsReactionText(text, false)) return;
        if (text.Contains("@[") || _rng.NextDouble() < 0.5)
        {
            var reactTo = text;
            PushLater(2500, () =>
            {
                lock (_lock)
                {
                    var reaction = ReactionParser.BuildChannelReaction("👍", _name, reactTo, ts);
                    var now = Now();
                    QueueChannelMessage(idx, "Alice", reaction, now);
                    Push(LogFrame(RawGroupPacket(idx, "Alice", reaction, now, [_nodes[2].PublicKey[0]]), 6, -82));
                }
                return [0x83];
            });
        }
    }

    private void HandleLogin(byte[] c)
    {
        var key = c.Slice(1, 32);
        var pwd = Encoding.UTF8.GetString(c, 33, c.Length - 33);
        var node = _nodes.FirstOrDefault(n => n.PublicKey.SequenceEquals(key));
        if (node is null) { Push(Err(ErrorCode.NotFound)); return; }
        var tag = RandomNumberGenerator.GetBytes(4);
        Push(new ByteWriter(0x06, 1).Raw(tag).U32(2000).ToArray());
        var admin = pwd == node.AdminPassword;
        var guest = pwd == node.GuestPassword || node.Type == ContactType.Room && pwd == "hello";
        PushLater(800, () => admin || guest
            ? new ByteWriter(0x85, admin ? (byte)1 : (byte)0).Raw(key.Prefix(6)).U32(Now()).U8(admin ? (byte)3 : guest && node.Type == ContactType.Room ? (byte)2 : (byte)0).U8(0).ToArray()
            : new byte[] { 0x86, 0 }.Concat(key.Prefix(6)).ToArray());
        if ((admin || guest) && node.Type == ContactType.Room)
        {
            PushLater(2500, () =>
            {
                lock (_lock)
                {
                    var alice = _nodes[0];
                    QueueContactMessage(node, "Welcome to Town Square! Be kind 🙂", Now() - 3600, 2, alice.PublicKey.Prefix(4));
                    QueueContactMessage(node, "Market on Saturday at 9am.", Now() - 1800, 2, _nodes[1].PublicKey.Prefix(4));
                }
                return [0x83];
            });
        }
    }

    private void HandleBinary(byte[] key, BinaryRequestType type, byte[] payload, bool anonRegions = false)
    {
        var node = _nodes.FirstOrDefault(n => n.PublicKey.SequenceEquals(key));
        if (node is null) { Push(Err(ErrorCode.NotFound)); return; }
        var tag = RandomNumberGenerator.GetBytes(4);
        Push(new ByteWriter(0x06, 1).Raw(tag).U32(1500).ToArray());
        byte[] body;
        if (anonRegions) body = new ByteWriter().U32(Now()).Utf8("usa,florida,*").ToArray();
        else body = type switch
        {
            BinaryRequestType.Status => new ByteWriter().U16((ushort)(4050 + _rng.Next(-30, 30))).U16(0).U16(unchecked((ushort)(short)(-110 + _rng.Next(-3, 3))))
                .U16(unchecked((ushort)(short)(-78 + _rng.Next(-5, 5)))).U32(15000).U32(4200).U32(3600).U32(864000 + (uint)_rng.Next(1000))
                .U32(2100).U32(2100).U32(9000).U32(6000).U16(0).U16(unchecked((ushort)(short)(_rng.Next(-10, 40)))).U16(12).U16(340).U32(7200).U32(3).ToArray(),
            BinaryRequestType.Telemetry => new LppEncoder().AddVoltage(1, 4.05 + _rng.NextDouble() * 0.05).AddTemperature(1, 27.5 + _rng.NextDouble()).AddHumidity(2, 68)
                .AddBarometer(2, 1013.2).AddGps(1, node.Lat, node.Lon, 12).Encode(),
            BinaryRequestType.Neighbours => NeighboursBody(node),
            BinaryRequestType.OwnerInfo => Encoding.UTF8.GetBytes($"v1.16.0\n{node.Name}\nOwner: Demo Club | demo@example.com"),
            BinaryRequestType.Acl => _selfPub.Prefix(6).Concat(new byte[] { 3 }).Concat(_nodes[0].PublicKey.Prefix(6)).Concat(new byte[] { 1 }).ToArray(),
            BinaryRequestType.Mma => new byte[] { 1, 116, 0x01, 0x90, 0x01, 0x9A, 0x01, 0x95, 1, 103, 0x00, 0xF0, 0x01, 0x18, 0x01, 0x04 },
            BinaryRequestType.KeepAlive => [],
            _ => [],
        };
        if (type == BinaryRequestType.KeepAlive) return;
        PushLater(900 + _rng.Next(600), () => new byte[] { 0x8C, 0 }.Concat(tag).Concat(body).ToArray());
    }

    private byte[] NeighboursBody(Node node)
    {
        var others = _nodes.Where(n => n != node && n.InContacts && (n.Type == ContactType.Repeater || n.Type == ContactType.Room)).ToList();
        var w = new ByteWriter().U16((ushort)(others.Count + 1)).U16((ushort)(others.Count + 1));
        foreach (var n in others) w.Raw(n.PublicKey.Prefix(4)).I32(_rng.Next(30, 3600)).I8((sbyte)_rng.Next(-20, 40));
        w.Raw(_selfPub.Prefix(4)).I32(20).I8(24);
        return w.ToArray();
    }

    private void HandleCli(Node node, string text)
    {
        string? prefix = null;
        var body = text;
        if (CliResponse.SplitEchoedPrefix(text) is { } s) { prefix = s.Prefix; body = s.Body; }
        var cmd = body.Trim().ToLowerInvariant();
        var reply = cmd switch
        {
            "ver" => "v1.16.0-sim (Build: 29-Sep-2026)",
            "get name" => node.Name,
            "get radio" => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_freq:0.000},{_bw:0.0},{_sf},{_cr}"),
            "get tx" => "22",
            "get repeat" => "on",
            "get advert.interval" => "120",
            "get flood.advert.interval" => "12",
            "get flood.max" => "64",
            "get lat" => node.Lat.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture),
            "get lon" => node.Lon.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture),
            "get owner.info" => "Demo Club|demo@example.com",
            "get guest.password" => "hello",
            "get allow.read.only" => "on",
            "clock" => DateTime.UtcNow.ToString("H:mm - d/M/yyyy") + " UTC",
            "neighbors" => "A1B2C3D4:120:24\nE5F6A7B8:300:8",
            "help" => "Commands: ver, clock, get/set <param>, neighbors, advert, reboot, password <new>",
            _ when cmd.StartsWith("set ") || cmd.StartsWith("time ") || cmd == "advert" || cmd.StartsWith("password ") => "OK",
            _ => "Error: unknown command",
        };
        PushLater(700, () =>
        {
            lock (_lock) QueueContactMessage(node, (prefix ?? "") + reply, Now(), 1);
            return [0x83];
        });
    }

    private void HandlePathDiscovery(byte[] key)
    {
        var node = _nodes.FirstOrDefault(n => n.PublicKey.SequenceEquals(key));
        if (node is null) { Push(Err(ErrorCode.NotFound)); return; }
        Push(new ByteWriter(0x06, 1).Raw(RandomNumberGenerator.GetBytes(4)).U32(3000).ToArray());
        var hop = _nodes[2].PublicKey[0];
        PushLater(1500, () =>
        {
            lock (_lock) { node.PathLen = 1; node.Path = [hop]; node.LastModified = Now(); }
            return new ByteWriter(0x8D, 0).Raw(key.Prefix(6)).U8(1).U8(hop).U8(1).U8(hop).ToArray();
        });
    }

    private void HandleTrace(byte[] c)
    {
        var tag = c.ReadUInt32LE(1);
        var auth = c.ReadUInt32LE(5);
        var flags = c[9];
        var path = c.From(10);
        Push(new ByteWriter(0x06, 0).Raw(RandomNumberGenerator.GetBytes(4)).U32(2500).ToArray());
        var hashSize = 1 << (flags & 3);
        var hops = path.Length / hashSize;
        PushLater(900 + 250 * hops, () =>
        {
            var w = new ByteWriter(0x89, 0, (byte)path.Length, flags).U32(tag).U32(auth).Raw(path);
            for (var i = 0; i < hops; i++) w.I8((sbyte)(_rng.Next(-20, 45)));
            w.I8((sbyte)_rng.Next(0, 40));
            return w.ToArray();
        });
        if (hops == 1)
        {
            // Zero-hop ping: the RX log sees the trace coming back.
            var raw = new byte[] { (byte)(((byte)PayloadType.Trace << 2) | (byte)RouteType.Direct), 1, unchecked((byte)(sbyte)_rng.Next(0, 40)) }
                .Concat(BitConverter.GetBytes(tag)).Concat(BitConverter.GetBytes(auth)).Concat(new byte[] { flags }).ToArray();
            PushLater(600, () => LogFrame(raw, 8, -70));
        }
    }

    private void HandleControl(byte[] c)
    {
        var type = c[1];
        if ((type & 0xF0) != 0x80) { Push(Ok); return; }
        var filter = c[2];
        var tag = c.ReadUInt32LE(3);
        Push(Ok);
        var i = 0;
        foreach (var n in _nodes.Where(n => (filter & (1 << (int)n.Type)) != 0))
        {
            var node = n;
            PushLater(400 + 500 * i++, () => new ByteWriter(0x8E).I8((sbyte)_rng.Next(-10, 40)).I8((sbyte)_rng.Next(-110, -60)).U8(0)
                .U8((byte)(0x90 | (byte)node.Type)).I8((sbyte)_rng.Next(-10, 40)).U32(tag).Raw(node.PublicKey).ToArray());
        }
    }

    private async Task ChatterLoop(CancellationToken ct)
    {
        var lines = new[]
        {
            ("Bob", "Anyone out near the beach today?"),
            ("Alice", "Signal is great from the library roof 📡"),
            ("Bob", "Just added a solar panel to my node ☀️"),
            ("Alice", "@[" + "Demo Radio" + "] you're coming in loud and clear"),
            ("Bob", "Weather looks good for the hike tomorrow"),
        };
        var i = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(45 + _rng.Next(30)), ct).ConfigureAwait(false); } catch { return; }
            lock (_lock)
            {
                var (who, text) = lines[i++ % lines.Length];
                text = text.Replace("Demo Radio", _name);
                var now = Now();
                QueueChannelMessage(0, who, text, now);
                Push(LogFrame(RawGroupPacket(0, who, text, now, [_nodes[2].PublicKey[0]]), 5.5, -88));
                var n = _nodes[_rng.Next(_nodes.Count)];
                if (n.InContacts)
                {
                    n.LastAdvert = now;
                    n.LastModified = now;
                    Push(new byte[] { 0x80 }.Concat(n.PublicKey).ToArray());
                }
                if (i == 2)
                {
                    var carol = _nodes.First(x => x.Name == "Carol");
                    carol.LastAdvert = now;
                    Push(new byte[] { 0x8A }.Concat(ContactFrame(carol)).ToArray());
                }
            }
            Notify();
        }
    }
}
