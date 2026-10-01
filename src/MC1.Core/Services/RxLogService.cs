using System.Text.Json;
using MC1.Core.Models;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Core.Services;

public sealed record RxPathInfo(int PathLength, byte[] PathNodes, int RouteType, string? RegionScope);

/// <summary>Live packet capture: decodes/decrypts over-the-air packets and tracks heard repeats of our channel posts.</summary>
public sealed class RxLogService(MeshApp app)
{
    private readonly object _lock = new();
    private string? _radioId;
    private Dictionary<int, (byte[] Secret, string Name)> _channels = new();
    private Dictionary<byte, List<byte[]>> _x25519ByPrefix = new();
    private Dictionary<string, string> _namesByPrefix = new();
    private byte[]? _privateKey;
    private List<(string Name, byte[] Key)> _scopeKeys = new();
    private int _sinceTrim;

    public event Action<RxLogRecord>? EntryAdded;

    internal void Configure(string radioId)
    {
        _radioId = radioId;
        RefreshChannels();
        RefreshContactKeys();
        RefreshRegions();
    }

    internal void SetPrivateKey(byte[] key)
    {
        lock (_lock) _privateKey = key.Length >= 32 ? key.Prefix(32) : null;
    }

    internal void RefreshChannels()
    {
        if (_radioId is null) return;
        var map = app.Db.GetChannels(_radioId).ToDictionary(c => c.Idx, c => (c.Secret, c.DisplayName));
        lock (_lock) _channels = map;
        _ = Task.Run(ReprocessRecentUndecryptedAsync);
    }

    internal void RefreshContactKeys()
    {
        if (_radioId is null) return;
        var contacts = app.Db.GetContacts(_radioId);
        var byPrefix = new Dictionary<byte, List<byte[]>>();
        var names = new Dictionary<string, string>();
        foreach (var c in contacts)
        {
            if (c.PublicKey.Length != 32) continue;
            if (Ed25519ToX25519.ConvertPublicKey(c.PublicKey) is { } x)
            {
                if (!byPrefix.TryGetValue(c.PublicKey[0], out var list)) byPrefix[c.PublicKey[0]] = list = new();
                list.Add(x);
            }
            names[Convert.ToHexString(c.PublicKey, 0, 1)] = c.DisplayName;
        }
        lock (_lock) { _x25519ByPrefix = byPrefix; _namesByPrefix = names; }
    }

    public void RefreshRegions()
    {
        var regions = KnownRegions();
        var keys = regions.Select(r => (r, TransportCodeRegionResolver.DeriveScopeKey(r))).Where(x => x.Item2 is not null).Select(x => (x.r, x.Item2!)).ToList();
        lock (_lock) _scopeKeys = keys;
    }

    public IReadOnlyList<string> KnownRegions()
    {
        var json = app.Radio?.KnownRegions;
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; } catch { return []; }
    }

    public void SetKnownRegions(IEnumerable<string> regions)
    {
        var list = regions.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r).ToList();
        app.UpdateRadioRecord(r => r.KnownRegions = JsonSerializer.Serialize(list));
        RefreshRegions();
    }

    internal async Task ProcessAsync(ParsedRxLogData p)
    {
        var radioId = _radioId;
        if (radioId is null) return;
        var rec = Decode(p, radioId);

        // Advert over flood: remember inbound hop count for that node.
        if (p.PayloadType == PayloadType.Advert && p.RouteType.IsFlood() && p.PacketPayload.Length >= 32 && PathEncoding.Decode(p.PathLength) is { } dec)
            app.Db.SetInboundHops(radioId, p.PacketPayload.Prefix(32), dec.HopCount);

        if (app.Settings.Current.RxLogEnabled)
        {
            app.Db.SaveRxLog(rec);
            if (++_sinceTrim >= 200)
            {
                _sinceTrim = 0;
                app.Db.TrimRxLog(radioId, Math.Max(500, app.Settings.Current.RxLogMaxEntries));
            }
        }
        ProcessForRepeats(rec);
        try { EntryAdded?.Invoke(rec); } catch { /* ignore */ }
        app.Notify(DataKind.RxLog);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private RxLogRecord Decode(ParsedRxLogData p, string radioId)
    {
        var rec = new RxLogRecord
        {
            RadioId = radioId,
            Snr = p.Snr,
            Rssi = p.Rssi,
            RouteType = (int)p.RouteType,
            PayloadType = p.PayloadTypeBits,
            PayloadVersion = p.PayloadVersion,
            PathLength = p.PathLength,
            PathNodes = p.PathNodes,
            PacketPayload = p.PacketPayload,
            RawPayload = p.RawPayload,
            PacketHash = p.PacketHash,
            TransportCode = p.TransportCode,
            DecryptStatus = (int)DecryptStatus.NotApplicable,
        };
        Dictionary<int, (byte[] Secret, string Name)> channels;
        Dictionary<byte, List<byte[]>> keys;
        byte[]? pk;
        List<(string, byte[])> scopes;
        lock (_lock) { channels = _channels; keys = _x25519ByPrefix; pk = _privateKey; scopes = _scopeKeys; }

        if (p.PayloadType is PayloadType.GroupText or PayloadType.GroupData)
        {
            if (p.PacketPayload.Length >= 1 + ChannelCrypto.MacSize + 16)
            {
                var enc = p.PacketPayload.From(1);
                rec.DecryptStatus = (int)DecryptStatus.NoMatchingKey;
                foreach (var (idx, (secret, name)) in channels)
                {
                    if (ChannelCrypto.Decrypt(enc, secret) is ChannelCrypto.DecryptResult.Success ok)
                    {
                        rec.ChannelIndex = idx;
                        rec.ChannelName = name;
                        rec.SenderTimestamp = ok.Timestamp;
                        rec.DecodedText = ok.Text;
                        rec.DecryptStatus = (int)DecryptStatus.Success;
                        break;
                    }
                }
            }
            else rec.DecryptStatus = (int)DecryptStatus.Pending;
        }
        if (p.PayloadType == PayloadType.TextMessage && p.RouteType is RouteType.Direct or RouteType.TcDirect)
        {
            rec.DecryptStatus = (int)DecryptStatus.DmNoMatchingKey;
            if (pk is not null && p.PacketPayload.Length >= 2 && keys.TryGetValue(p.PacketPayload[1], out var candidates))
            {
                foreach (var k in candidates)
                {
                    if (DirectMessageCrypto.Decrypt(p.PacketPayload, pk, k) is DirectMessageCrypto.DecryptResult.Success ok)
                    {
                        rec.SenderTimestamp = ok.Timestamp;
                        rec.DecodedText = ok.Text;
                        rec.DecryptStatus = (int)DecryptStatus.Success;
                        break;
                    }
                }
            }
        }
        if (p.SenderPubkeyPrefix is { Length: > 0 } sp)
        {
            lock (_lock) rec.FromContactName = _namesByPrefix.GetValueOrDefault(Convert.ToHexString(sp, 0, 1));
        }
        if (p.TransportCode is { Length: >= 2 } tc && scopes.Count > 0)
        {
            var code0 = (ushort)(tc[0] | (tc[1] << 8));
            rec.RegionScope = TransportCodeRegionResolver.MatchRegions(scopes, code0, p.PayloadTypeBits, p.PacketPayload) switch
            {
                RegionMatchResult.Unique u => u.Name,
                RegionMatchResult.Ambiguous a => string.Join(" / ", a.Names),
                _ => null,
            };
        }
        return rec;
    }

    private async Task ReprocessRecentUndecryptedAsync()
    {
        var radioId = _radioId;
        if (radioId is null) return;
        try
        {
            var entries = app.Db.GetRecentRxLogByStatus(radioId, DecryptStatus.NoMatchingKey, Time.Now() - 60_000);
            foreach (var e in entries)
            {
                var parsed = RxLogParser.Parse(e.Snr, e.Rssi, e.RawPayload);
                if (parsed is null) continue;
                var r = Decode(parsed, radioId);
                if (r.DecryptStatus != (int)DecryptStatus.Success) continue;
                app.Db.UpdateRxLogDecryption(e.Id, r.ChannelIndex, r.ChannelName, r.SenderTimestamp, r.DecodedText, DecryptStatus.Success);
                r.Id = e.Id;
                r.ReceivedAt = e.ReceivedAt;
                ProcessForRepeats(r);
            }
        }
        catch (Exception ex) { app.Log.Warn("RxLog", "Reprocess failed: " + ex.Message); }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Counts echoes of our own channel posts (heard repeats) and extra paths of received ones.</summary>
    private void ProcessForRepeats(RxLogRecord e)
    {
        if (e.Payload != PayloadType.GroupText || e.Decrypt != DecryptStatus.Success || e.DecodedText is null || e.ChannelIndex is not { } idx || e.SenderTimestamp is not { } ts) return;
        var radioId = e.RadioId;
        var (sender, text) = ChannelMessageFormat.Parse(e.DecodedText);
        if (sender is null) return;
        if (app.Db.RepeatExistsForRxEntry(e.Id)) return;
        var own = sender == app.SelfName ? app.Db.FindSentChannelMessage(radioId, idx, ts, text) : null;
        if (own is not null)
        {
            app.Db.SaveRepeat(new MessageRepeatRecord { MessageId = own.Id, PathNodes = e.PathNodes, PathLength = e.PathLength, Snr = e.Snr, Rssi = e.Rssi, RxLogEntryId = e.Id, ReceivedAt = e.ReceivedAt });
            app.Db.IncrementHeardRepeats(own.Id);
            app.Notify(DataKind.Messages, MessageService.ChannelKey(idx), own.Id);
            return;
        }
        var dedup = DeduplicationKey.ContentBased(null, idx, sender, ts, text);
        var msg = app.Db.GetMessageByDedup(radioId, dedup);
        if (msg is null) return;
        RecordDistinctPath(msg, e);
    }

    private void RecordDistinctPath(MessageRecord msg, RxLogRecord e)
    {
        if (msg.PathNodes is null)
        {
            app.Db.AdoptIncomingPath(msg.Id, e.PathNodes, e.PathLength);
            return;
        }
        if (msg.PathNodes.SequenceEquals(e.PathNodes)) return;
        if (app.Db.GetRepeats(msg.Id).Any(r => r.PathNodes.SequenceEquals(e.PathNodes))) return;
        app.Db.SaveRepeat(new MessageRepeatRecord { MessageId = msg.Id, PathNodes = e.PathNodes, PathLength = e.PathLength, Snr = e.Snr, Rssi = e.Rssi, RxLogEntryId = e.Id, ReceivedAt = e.ReceivedAt });
        app.Db.IncrementHeardRepeats(msg.Id);
        app.Notify(DataKind.Messages, MessageService.ChannelKey(msg.ChannelIndex ?? 0), msg.Id);
    }

    /// <summary>Finds the over-the-air packet for a just-received channel message to recover its path/route/region.</summary>
    internal RxPathInfo? LookupPathForChannel(string radioId, int channelIndex, uint senderTimestamp, string dedupKey)
    {
        var entries = app.Db.GetRxLogForChannel(radioId, channelIndex, senderTimestamp);
        foreach (var e in entries)
        {
            if (e.DecodedText is null) continue;
            var (sender, body) = ChannelMessageFormat.Parse(e.DecodedText);
            if (DeduplicationKey.ContentBased(null, channelIndex, sender, senderTimestamp, body) != dedupKey) continue;
            return new RxPathInfo(e.PathLength, e.PathNodes, e.RouteType, e.RegionScope);
        }
        return null;
    }

    public IReadOnlyList<RxLogRecord> Recent(int limit = 1000) => _radioId is { } id ? app.Db.GetRxLog(id, limit) : app.RadioId is { } r ? app.Db.GetRxLog(r, limit) : [];

    public void Clear()
    {
        if (app.RadioId is { } id) app.Db.ClearRxLog(id);
        app.Notify(DataKind.RxLog);
    }
}
