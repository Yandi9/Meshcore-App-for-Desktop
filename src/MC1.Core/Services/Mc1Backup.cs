using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MC1.Core.Models;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Core.Services;

/// <summary>
/// ".mc1backup" files, the backup format of MeshCore One for iPhone/iPad/Mac: a JSON "AppBackupEnvelope"
/// (radios, contacts, channels, messages…) compressed with raw DEFLATE. Dates are seconds since 1970, binary data
/// is base64 and ids are UUIDs.
/// </summary>
public sealed class Mc1Backup
{
    public const string Extension = "mc1backup";
    public const int FormatVersion = 1;
    private const int MaxUncompressedBytes = 512 * 1024 * 1024;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public int Version { get; private init; }
    public DateTimeOffset? ExportDate { get; private init; }
    public string AppVersion { get; private init; } = "";
    public IReadOnlyList<Device> Devices { get; private init; } = [];
    public IReadOnlyList<JsonObject> Contacts { get; private init; } = [];
    public IReadOnlyList<JsonObject> Channels { get; private init; } = [];
    public IReadOnlyList<JsonObject> Messages { get; private init; } = [];
    public IReadOnlyList<JsonObject> RoomMessages { get; private init; } = [];
    public IReadOnlyList<JsonObject> RemoteNodeSessions { get; private init; } = [];
    public IReadOnlyList<JsonObject> BlockedChannelSenders { get; private init; } = [];
    public IReadOnlyList<JsonObject> SavedTracePaths { get; private init; } = [];
    /// <summary>The phone app's preferences (only some have a Windows equivalent).</summary>
    public JsonObject? UserDefaults { get; private init; }
    /// <summary>All of this app's settings, when the backup was made on Windows (ignored by the phone app).</summary>
    public JsonObject? WindowsSettings { get; private init; }

    /// <summary>File name in the phone app's style, e.g. "MC1 Backup 2026-09-30 012823.mc1backup".</summary>
    public static string DefaultFileName(DateTime now) => $"MC1 Backup {now:yyyy-MM-dd HHmmss}.{Extension}";

    /// <summary>One radio in the backup and its settings.</summary>
    public sealed record Device(
        string RadioId, byte[] PublicKey, string NodeName, uint FrequencyKhz, uint BandwidthHz, byte SpreadingFactor, byte CodingRate,
        sbyte TxPower, double Latitude, double Longitude, bool ManualAddContacts, byte MultiAcks, byte TelemetryModeBase,
        byte TelemetryModeLocation, byte TelemetryModeEnvironment, byte AdvertLocationPolicy, bool IsActive, DateTimeOffset? LastConnected,
        int MaxChannels = 0, int MaxContacts = 0, string? AppliedPresetId = null)
    {
        public string Describe => string.IsNullOrWhiteSpace(NodeName) ? PublicKey.ToHex()[..8] : NodeName;
    }

    // MARK: Reading

    /// <summary>Reads a .mc1backup file (raw DEFLATE, zlib or plain JSON are all accepted).</summary>
    public static Mc1Backup Read(byte[] data)
    {
        var json = Decompress(data) ?? throw new InvalidDataException(L.T("This isn't a MeshCore backup (.mc1backup) file."));
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { throw new InvalidDataException(L.T("The backup file is damaged (its contents aren't valid).")); }
        if (root is not JsonObject o || o["devices"] is not JsonArray)
            throw new InvalidDataException(L.T("This isn't a MeshCore backup (.mc1backup) file."));
        var version = (int)Num(o["version"], 1);
        if (version > FormatVersion)
            throw new InvalidDataException(L.F("This backup was made by a newer app version (format {0}). Update MeshCore to read it.", version));
        return new Mc1Backup
        {
            Version = version,
            ExportDate = Date(o["exportDate"]),
            AppVersion = Str(o["appVersion"]) ?? "",
            Devices = Objects(o["devices"]).Select(ParseDevice).ToList(),
            Contacts = Objects(o["contacts"]),
            Channels = Objects(o["channels"]),
            Messages = Objects(o["messages"]),
            RoomMessages = Objects(o["roomMessages"]),
            RemoteNodeSessions = Objects(o["remoteNodeSessions"]),
            BlockedChannelSenders = Objects(o["blockedChannelSenders"]),
            SavedTracePaths = Objects(o["savedTracePaths"]),
            UserDefaults = o["userDefaults"] as JsonObject,
            WindowsSettings = o["windowsSettings"] as JsonObject,
        };
    }

    private static string? Decompress(byte[] data)
    {
        // Plain JSON (e.g. a file that was decompressed by hand)
        var start = 0;
        while (start < data.Length && data[start] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or 0xEF or 0xBB or 0xBF) start++;
        if (start < data.Length && data[start] == (byte)'{') return Encoding.UTF8.GetString(data);
        foreach (var zlibHeader in new[] { false, true })
        {
            try
            {
                using var input = new MemoryStream(data);
                using Stream z = zlibHeader ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int n;
                while ((n = z.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, n);
                    if (output.Length > MaxUncompressedBytes) throw TooLarge();
                }
                if (output.Length == 0) continue;
                var text = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
                if (text.TrimStart().StartsWith('{')) return text;
            }
            catch (InvalidDataException ex) when (ex.Data.Contains(TooLargeMarker)) { throw; }
            catch (Exception) { /* try the next format */ }
        }
        return null;
    }

    /// <summary>Marks the "too large" error so it isn't swallowed while trying the other formats (its text is translated).</summary>
    private const string TooLargeMarker = "MC1.BackupTooLarge";

    private static InvalidDataException TooLarge()
    {
        var ex = new InvalidDataException(L.T("The backup is too large."));
        ex.Data[TooLargeMarker] = true;
        return ex;
    }

    private static Device ParseDevice(JsonObject d) => new(
        Str(d["radioID"]) ?? Str(d["id"]) ?? "",
        Bytes(d["publicKey"]) ?? [],
        Str(d["nodeName"]) ?? "",
        (uint)Num(d["frequency"]),
        (uint)Num(d["bandwidth"]),
        (byte)Num(d["spreadingFactor"]),
        (byte)Num(d["codingRate"]),
        (sbyte)Math.Clamp(Num(d["txPower"]), sbyte.MinValue, sbyte.MaxValue),
        Num(d["latitude"]),
        Num(d["longitude"]),
        Bool(d["manualAddContacts"]),
        (byte)Num(d["multiAcks"]),
        (byte)Num(d["telemetryModeBase"]),
        (byte)Num(d["telemetryModeLoc"]),
        (byte)Num(d["telemetryModeEnv"]),
        (byte)Num(d["advertLocationPolicy"]),
        Bool(d["isActive"]),
        Date(d["lastConnected"]),
        (int)Num(d["maxChannels"]),
        (int)Num(d["maxContacts"]),
        d["appliedRadioPresetID"] is JsonValue pv && pv.TryGetValue<string>(out var presetId) ? presetId : null);

    /// <summary>The radio in the backup that matches <paramref name="publicKey"/>, else the active / most recent one.</summary>
    public Device? PickDevice(byte[]? publicKey)
    {
        if (publicKey is not null && Devices.FirstOrDefault(d => d.PublicKey.AsSpan().SequenceEqual(publicKey)) is { } match) return match;
        return Devices.OrderByDescending(d => d.IsActive).ThenByDescending(d => d.LastConnected ?? DateTimeOffset.MinValue).FirstOrDefault();
    }

    private static bool SameRadio(JsonObject o, Device d) => string.Equals(Str(o["radioID"]), d.RadioId, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<JsonObject> ContactsOf(Device d) => Contacts.Where(c => SameRadio(c, d));
    public IEnumerable<JsonObject> ChannelsOf(Device d) => Channels.Where(c => SameRadio(c, d));
    public IEnumerable<JsonObject> MessagesOf(Device d) => Messages.Where(c => SameRadio(c, d));
    public IEnumerable<JsonObject> SessionsOf(Device d) => RemoteNodeSessions.Where(c => SameRadio(c, d));
    public IEnumerable<JsonObject> BlockedOf(Device d) => BlockedChannelSenders.Where(c => SameRadio(c, d));
    public IEnumerable<JsonObject> TracePathsOf(Device d) => SavedTracePaths.Where(c => SameRadio(c, d));

    public IEnumerable<JsonObject> RoomMessagesOf(Device d)
    {
        var sessions = SessionsOf(d).Select(s => Str(s["id"])).Where(i => i is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RoomMessages.Where(m => Str(m["sessionID"]) is { } sid && sessions.Contains(sid));
    }

    /// <summary>The radio settings, channels and contacts of one radio as a MeshCore node config (to write to a radio).</summary>
    public NodeConfigFile ToNodeConfig(Device d)
    {
        var cfg = new NodeConfigFile
        {
            Name = string.IsNullOrWhiteSpace(d.NodeName) ? null : d.NodeName,
            PublicKey = d.PublicKey.ToHex(),
            PositionSettings = new() { Latitude = d.Latitude.ToString(Inv), Longitude = d.Longitude.ToString(Inv) },
            OtherSettings = new()
            {
                ManualAddContacts = d.ManualAddContacts ? (byte)1 : (byte)0,
                AdvertLocationPolicy = d.AdvertLocationPolicy,
                TelemetryModeBase = d.TelemetryModeBase,
                TelemetryModeLocation = d.TelemetryModeLocation,
                TelemetryModeEnvironment = d.TelemetryModeEnvironment,
                MultiAcks = d.MultiAcks,
            },
        };
        if (d.FrequencyKhz > 0 && d.BandwidthHz > 0 && d.SpreadingFactor > 0)
            cfg.RadioSettings = new() { Frequency = d.FrequencyKhz, Bandwidth = d.BandwidthHz, SpreadingFactor = d.SpreadingFactor, CodingRate = d.CodingRate, TxPower = d.TxPower };
        cfg.Channels = ChannelsOf(d)
            .OrderBy(c => Num(c["index"]))
            .Select(c => (Name: Str(c["name"]) ?? "", Secret: Bytes(c["secret"]) ?? []))
            .Where(c => c.Secret.Length == 16 && (c.Name.Length > 0 || c.Secret.Any(b => b != 0)))
            .Select(c => new NodeConfigFile.ChannelConfig { Name = c.Name, Secret = c.Secret.ToHex() })
            .ToList();
        cfg.Contacts = [];
        foreach (var c in ContactsOf(d))
        {
            var key = Bytes(c["publicKey"]);
            if (key is not { Length: 32 }) continue;
            var pathLen = (byte)Num(c["outPathLength"], 0xFF);
            var path = Bytes(c["outPath"]) ?? [];
            string? outPath = null;
            byte? mode = null;
            if (pathLen != 0xFF && PathEncoding.Decode(pathLen) is { } p)
            {
                var len = Math.Min(path.Length, p.HopCount * p.HashSize);
                outPath = path.AsSpan(0, len).ToArray().ToHex();
                mode = (byte)(pathLen >> 6);
            }
            cfg.Contacts.Add(new NodeConfigFile.ContactConfig
            {
                Type = (byte)Num(c["typeRawValue"], 1),
                Name = Str(c["name"]) ?? "",
                CustomName = Str(c["nickname"]),
                PublicKey = key.ToHex(),
                Flags = (byte)Num(c["flags"]),
                Latitude = Num(c["latitude"]).ToString(Inv),
                Longitude = Num(c["longitude"]).ToString(Inv),
                LastAdvert = (uint)Num(c["lastAdvertTimestamp"]),
                LastModified = (uint)Num(c["lastModified"]),
                OutPath = outPath,
                PathHashMode = mode,
            });
        }
        return cfg;
    }

    // MARK: Chat history import

    public sealed record HistoryResult(int Messages, int RoomPosts, int Skipped);

    /// <summary>Copies one radio's messages (and room posts) from the backup into this app's history for <paramref name="radioId"/>.</summary>
    public HistoryResult ImportHistory(MeshApp app, Device d, string? radioId = null)
    {
        radioId ??= app.RequireRadioId();
        var db = app.Db;
        var contactKeys = ContactsOf(d).Where(c => Str(c["id"]) is not null)
            .ToDictionary(c => Str(c["id"])!, c => Bytes(c["publicKey"]), StringComparer.OrdinalIgnoreCase);
        var channelSecrets = ChannelsOf(d).ToDictionary(c => (int)Num(c["index"]), c => (Name: Str(c["name"]) ?? "", Secret: Bytes(c["secret"]) ?? []));
        var localChannels = db.GetChannels(radioId);
        var localContacts = new Dictionary<string, ContactRecord?>(StringComparer.OrdinalIgnoreCase);
        int imported = 0, posts = 0, skipped = 0;

        foreach (var m in MessagesOf(d))
        {
            var text = Str(m["text"]);
            if (text is null) { skipped++; continue; }
            var ts = (long)Num(m["timestamp"]);
            var wireTs = m["senderTimestamp"] is { } st ? (long)Num(st) : ts;
            string? contactId = null;
            int? channelIdx = null;
            if (m["channelIndex"] is { } ci && ci.GetValueKind() == JsonValueKind.Number)
            {
                var idx = (int)Num(ci);
                // Match the channel by its secret: the slot number may differ on this radio.
                var local = channelSecrets.TryGetValue(idx, out var bc)
                    ? localChannels.FirstOrDefault(c => c.Secret.AsSpan().SequenceEqual(bc.Secret)) ?? localChannels.FirstOrDefault(c => c.Idx == idx && c.Name == bc.Name)
                    : localChannels.FirstOrDefault(c => c.Idx == idx);
                if (local is null) { skipped++; continue; }
                channelIdx = local.Idx;
            }
            else if (Str(m["contactID"]) is { } backupContact)
            {
                if (!localContacts.TryGetValue(backupContact, out var lc))
                {
                    lc = contactKeys.TryGetValue(backupContact, out var key) && key is { Length: 32 } ? db.GetContactByKey(radioId, key) : null;
                    localContacts[backupContact] = lc;
                }
                if (lc is null) { skipped++; continue; }
                contactId = lc.Id;
            }
            else { skipped++; continue; }

            var sender = Str(m["senderNodeName"]);
            var dedup = DeduplicationKey.ContentBased(contactId, channelIdx, channelIdx is null ? null : sender, wireTs, text);
            if (db.GetMessageByDedup(radioId, dedup) is not null) { skipped++; continue; }
            var createdMs = ToMs(Date(m["createdAt"])) ?? ts * 1000;
            var routeType = m["routeType"] is { } rt ? (int)Num(rt, -1) : -1;
            db.SaveMessage(new MessageRecord
            {
                RadioId = radioId,
                ContactId = contactId,
                ChannelIndex = channelIdx,
                Text = text,
                Timestamp = ts,
                CreatedAt = createdMs,
                SortDate = ToMs(Date(m["sortDate"])) ?? createdMs,
                Direction = (int)Num(m["direction"]),
                Status = (int)Num(m["status"], (int)MessageStatus.Delivered),
                TextType = (int)Num(m["textType"]),
                AckCode = m["ackCode"] is { } ack ? (long)Num(ack) : null,
                PathLength = (int)Num(m["pathLength"]),
                Snr = m["snr"] is { } snr ? Num(snr) : null,
                PathNodes = Bytes(m["pathNodes"]),
                SenderKeyPrefix = Bytes(m["senderKeyPrefix"]),
                SenderName = sender,
                IsRead = true,
                RoundTripTime = m["roundTripTime"] is { } rtt ? (long)Num(rtt) : null,
                HeardRepeats = (int)Num(m["heardRepeats"]),
                SendCount = Math.Max(1, (int)Num(m["sendCount"], 1)),
                RetryAttempt = (int)Num(m["retryAttempt"]),
                MaxRetryAttempts = (int)Num(m["maxRetryAttempts"]),
                DedupKey = dedup,
                ContainsSelfMention = Bool(m["containsSelfMention"]),
                MentionSeen = true,
                ReactionSummary = Str(m["reactionSummary"]),
                RouteType = routeType,
                RegionScope = Str(m["regionScope"]),
                SenderTimestamp = m["senderTimestamp"] is { } sts ? (long)Num(sts) : null,
                TimestampCorrected = Bool(m["timestampCorrected"]),
            });
            if (contactId is not null) db.UpdateContactLastMessage(contactId, createdMs);
            imported++;
        }

        // Room server posts: matched to this app's room sessions by the room's public key.
        var sessionKeys = SessionsOf(d).Where(s => Str(s["id"]) is not null)
            .ToDictionary(s => Str(s["id"])!, s => Bytes(s["publicKey"]), StringComparer.OrdinalIgnoreCase);
        var localSessions = new Dictionary<string, RemoteSessionRecord?>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in RoomMessagesOf(d))
        {
            var sid = Str(m["sessionID"])!;
            if (!localSessions.TryGetValue(sid, out var session))
            {
                session = sessionKeys.TryGetValue(sid, out var key) && key is { Length: 32 } ? db.GetRemoteSessionByKey(radioId, key) : null;
                localSessions[sid] = session;
            }
            var text = Str(m["text"]);
            if (session is null || text is null) { skipped++; continue; }
            var author = Bytes(m["authorKeyPrefix"]) ?? [];
            var ts = (long)Num(m["timestamp"]);
            var dedup = Str(m["deduplicationKey"]) ?? $"room-{session.Id}-{ts}-{author.ToHex()}-{text.GetHashCode():x}";
            if (db.RoomMessageExists(session.Id, dedup)) { skipped++; continue; }
            db.SaveRoomMessage(new RoomMessageRecord
            {
                SessionId = session.Id,
                AuthorKeyPrefix = author,
                AuthorName = Str(m["authorName"]) ?? "",
                Text = text,
                Timestamp = ts,
                CreatedAt = ToMs(Date(m["createdAt"])) ?? ts * 1000,
                IsFromSelf = Bool(m["isFromSelf"]),
                Status = (int)Num(m["statusRawValue"], (int)MessageStatus.Delivered),
                DedupKey = dedup,
                AckCode = m["ackCode"] is { } ack ? (long)Num(ack) : null,
                RetryAttempt = (int)Num(m["retryAttempt"]),
            });
            posts++;
        }

        app.Notify(DataKind.Messages);
        app.Notify(DataKind.RoomMessages);
        app.Notify(DataKind.Conversations);
        return new HistoryResult(imported, posts, skipped);
    }

    // MARK: Writing

    public sealed record ExportSummary(int Radios, int Contacts, int Channels, int Messages, int SavedPaths);

    /// <summary>
    /// Builds a .mc1backup of this PC: for every radio its settings, contacts, channels, messages and saved trace
    /// paths, plus the app's settings. Works while disconnected (radio settings as they were last seen).
    /// </summary>
    public static byte[] Export(MeshApp app, string appVersion, out ExportSummary summary)
    {
        var db = app.Db;
        var radios = db.GetRadios();
        if (radios.Count == 0) throw new InvalidOperationException(L.T("There's nothing to back up yet — connect a radio first."));
        var now = DateTimeOffset.UtcNow;
        var devices = new JsonArray();
        var contacts = new JsonArray();
        var channels = new JsonArray();
        var messages = new JsonArray();
        var paths = new JsonArray();

        foreach (var radio in radios)
        {
            var (self, caps) = app.GetRadioInfo(radio.Id);
            var radioUuid = Uuid(radio.Id);
            var device = new JsonObject
            {
                ["id"] = Uuid("device:" + radio.Id),
                ["radioID"] = radioUuid,
                ["publicKey"] = B64(self?.PublicKey ?? radio.PublicKey),
                ["nodeName"] = self?.Name ?? radio.Name,
                ["firmwareVersion"] = (byte)Math.Clamp(caps?.FirmwareVersion ?? radio.FirmwareCode, 0, 255),
                ["firmwareVersionString"] = caps?.Version ?? radio.FirmwareVersion,
                ["manufacturerName"] = caps?.Model ?? radio.Model,
                ["buildDate"] = caps?.FirmwareBuild ?? radio.FirmwareBuild,
                ["maxContacts"] = Math.Clamp(caps?.MaxContacts ?? radio.MaxContacts, 0, ushort.MaxValue),
                ["maxChannels"] = Math.Clamp(caps?.MaxChannels ?? radio.MaxChannels, 0, 255),
                ["frequency"] = (uint)Math.Round((self?.RadioFrequency ?? 0) * 1000),
                ["bandwidth"] = (uint)Math.Round((self?.RadioBandwidth ?? 0) * 1000),
                ["spreadingFactor"] = self?.RadioSpreadingFactor ?? 0,
                ["codingRate"] = self?.RadioCodingRate ?? 0,
                ["txPower"] = self?.TxPower ?? 0,
                ["maxTxPower"] = self?.MaxTxPower ?? 0,
                ["latitude"] = self?.Latitude ?? 0,
                ["longitude"] = self?.Longitude ?? 0,
                ["blePin"] = caps?.BlePin ?? 0,
                ["clientRepeat"] = caps?.ClientRepeat ?? false,
                ["pathHashMode"] = caps?.PathHashMode ?? 0,
                ["manualAddContacts"] = self?.ManualAddContacts ?? false,
                ["autoAddConfig"] = 0,
                ["autoAddMaxHops"] = 0,
                ["multiAcks"] = self?.MultiAcks ?? 0,
                ["telemetryModeBase"] = self?.TelemetryModeBase ?? 0,
                ["telemetryModeLoc"] = self?.TelemetryModeLocation ?? 0,
                ["telemetryModeEnv"] = self?.TelemetryModeEnvironment ?? 0,
                ["advertLocationPolicy"] = self?.AdvertisementLocationPolicy ?? 0,
                ["lastConnected"] = Seconds(radio.LastConnected > 0 ? Time.FromMs(radio.LastConnected) : now),
                ["lastContactSync"] = (uint)Math.Max(0, radio.LastContactSync),
                ["isActive"] = radio.Id == app.RadioId,
                ["connectionMethods"] = new JsonArray(),
                ["knownRegions"] = new JsonArray(KnownRegions(radio).Select(r => (JsonNode?)r).ToArray()),
            };
            if (radio.OcvPreset is { } ocv) device["ocvPreset"] = ocv;
            if (app.Settings.Current.AppliedRadioPresets.TryGetValue(radio.Id, out var presetId)) device["appliedRadioPresetID"] = presetId;
            if (radio.CustomOcv is { } cocv) device["customOCVArrayString"] = cocv;
            devices.Add(device);

            var contactList = db.GetContacts(radio.Id);
            foreach (var c in contactList)
            {
                var o = new JsonObject
                {
                    ["id"] = Uuid(c.Id),
                    ["radioID"] = radioUuid,
                    ["publicKey"] = B64(c.PublicKey),
                    ["name"] = c.Name,
                    ["typeRawValue"] = (byte)c.TypeRaw,
                    ["flags"] = (byte)c.Flags,
                    ["outPathLength"] = (byte)c.OutPathLength,
                    ["outPath"] = B64(c.OutPath),
                    ["lastAdvertTimestamp"] = (uint)Math.Max(0, c.LastAdvert),
                    ["latitude"] = c.Latitude,
                    ["longitude"] = c.Longitude,
                    ["lastModified"] = (uint)Math.Max(0, c.LastModified),
                    ["isBlocked"] = c.IsBlocked,
                    ["isMuted"] = c.IsMuted,
                    ["isFavorite"] = c.IsFavorite,
                    ["unreadCount"] = 0,
                    ["unreadMentionCount"] = 0,
                };
                if (c.LastHeard > 0) o["lastHeardTimestamp"] = (uint)(c.LastHeard / 1000);
                if (!string.IsNullOrWhiteSpace(c.Nickname)) o["nickname"] = c.Nickname;
                if (c.LastMessageAt > 0) o["lastMessageDate"] = Seconds(Time.FromMs(c.LastMessageAt));
                contacts.Add(o);
            }

            var channelList = db.GetChannels(radio.Id).Where(c => c.IsConfigured).ToList();
            foreach (var ch in channelList)
            {
                var o = new JsonObject
                {
                    ["id"] = Uuid(ch.Id),
                    ["radioID"] = radioUuid,
                    ["index"] = (byte)ch.Idx,
                    ["name"] = ch.Name,
                    ["secret"] = B64(ch.Secret),
                    ["isEnabled"] = true,
                    ["unreadCount"] = 0,
                    ["unreadMentionCount"] = 0,
                    ["notificationLevel"] = IosNotificationLevel(ch.NotificationLevel),
                    ["isFavorite"] = false,
                    ["floodScopeModeRawValue"] = ch.FloodScope is null ? "inherit" : ch.FloodScope == "all" ? "allRegions" : "specific",
                };
                if (ch.FloodScope is { } fs && fs != "all") o["regionScope"] = fs.Replace("region:", "");
                if (ch.LastMessageAt > 0) o["lastMessageDate"] = Seconds(Time.FromMs(ch.LastMessageAt));
                channels.Add(o);
            }

            var list = new List<MessageRecord>();
            foreach (var c in contactList) list.AddRange(db.GetDirectMessages(c.Id, int.MaxValue));
            foreach (var ch in channelList) list.AddRange(db.GetChannelMessages(radio.Id, ch.Idx, int.MaxValue));
            foreach (var m in list) messages.Add(MessageJson(m, radioUuid));

            foreach (var t in db.GetTracePaths(radio.Id))
                paths.Add(new JsonObject
                {
                    ["id"] = Uuid(t.Id),
                    ["radioID"] = radioUuid,
                    ["name"] = t.Name,
                    ["pathBytes"] = B64(t.Path),
                    ["hashSize"] = Math.Max(1, t.HashSize),
                    ["createdDate"] = Seconds(Time.FromMs(t.CreatedAt)),
                    ["runs"] = new JsonArray(),
                });
        }

        var envelope = new JsonObject
        {
            ["appBuild"] = "windows",
            ["appVersion"] = appVersion,
            ["blockedChannelSenders"] = new JsonArray(),
            ["channels"] = channels,
            ["contacts"] = contacts,
            ["devices"] = devices,
            ["discoveredNodes"] = new JsonArray(),
            ["exportDate"] = Seconds(now),
            ["manifest"] = new JsonObject
            {
                ["blockedChannelSenderCount"] = 0,
                ["channelCount"] = channels.Count,
                ["contactCount"] = contacts.Count,
                ["deviceCount"] = devices.Count,
                ["discoveredNodeCount"] = 0,
                ["messageCount"] = messages.Count,
                ["messageRepeatCount"] = 0,
                ["nodeStatusSnapshotCount"] = 0,
                ["reactionCount"] = 0,
                ["remoteNodeSessionCount"] = 0,
                ["roomMessageCount"] = 0,
                ["savedTracePathCount"] = paths.Count,
            },
            ["messageRepeats"] = new JsonArray(),
            ["messages"] = messages,
            ["nodeStatusSnapshots"] = new JsonArray(),
            ["reactions"] = new JsonArray(),
            ["remoteNodeSessions"] = new JsonArray(),
            ["roomMessages"] = new JsonArray(),
            ["savedTracePaths"] = paths,
            ["userDefaults"] = UserDefaultsFrom(app.Settings.Current),
            ["version"] = FormatVersion,
            // Everything else about this app's settings; the phone app ignores it.
            ["windowsSettings"] = JsonNode.Parse(app.Settings.Export()),
        };
        summary = new ExportSummary(devices.Count, contacts.Count, channels.Count, messages.Count, paths.Count);
        var json = Encoding.UTF8.GetBytes(envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(json);
        return output.ToArray();
    }

    private static IEnumerable<string> KnownRegions(RadioRecord radio)
    {
        try { return radio.KnownRegions is { Length: > 0 } k ? JsonSerializer.Deserialize<List<string>>(k) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private static JsonObject MessageJson(MessageRecord m, string radioUuid)
    {
        var o = new JsonObject
        {
            ["id"] = Uuid(m.Id),
            ["radioID"] = radioUuid,
            ["text"] = m.Text,
            ["timestamp"] = (uint)Math.Max(0, m.Timestamp),
            ["createdAt"] = Seconds(Time.FromMs(m.CreatedAt)),
            ["sortDate"] = Seconds(Time.FromMs(m.SortDate)),
            ["direction"] = m.Direction,
            ["status"] = m.Status,
            ["textType"] = (byte)m.TextType,
            ["pathLength"] = (byte)m.PathLength,
            ["isRead"] = true,
            ["heardRepeats"] = m.HeardRepeats,
            ["sendCount"] = m.SendCount,
            ["retryAttempt"] = m.RetryAttempt,
            ["maxRetryAttempts"] = m.MaxRetryAttempts,
            ["linkPreviewFetched"] = false,
            ["containsSelfMention"] = m.ContainsSelfMention,
            ["mentionSeen"] = true,
            ["failureSeen"] = true,
            ["timestampCorrected"] = m.TimestampCorrected,
            ["regionScopeMatches"] = new JsonArray(),
        };
        if (m.ContactId is { } cid) o["contactID"] = Uuid(cid);
        if (m.ChannelIndex is { } idx) o["channelIndex"] = (byte)idx;
        if (m.AckCode is { } ack) o["ackCode"] = (uint)ack;
        if (m.Snr is { } snr) o["snr"] = snr;
        if (m.PathNodes is { Length: > 0 } pn) o["pathNodes"] = B64(pn);
        if (m.SenderKeyPrefix is { Length: > 0 } sk) o["senderKeyPrefix"] = B64(sk);
        if (m.SenderName is { } sn) o["senderNodeName"] = sn;
        if (m.RoundTripTime is { } rtt) o["roundTripTime"] = (uint)rtt;
        if (m.DedupKey is { } dk) o["deduplicationKey"] = dk;
        if (m.SenderTimestamp is { } st) o["senderTimestamp"] = (uint)st;
        if (m.ReactionSummary is { } rs) o["reactionSummary"] = rs;
        if (m.RouteType is >= 0 and <= 3) o["routeType"] = (byte)m.RouteType;
        if (m.RegionScope is { } rsc) o["regionScope"] = rsc;
        return o;
    }

    /// <summary>The preferences the phone app shares with this one, under its own names.</summary>
    private static JsonObject UserDefaultsFrom(AppSettings s) => new()
    {
        ["appColorSchemePreference"] = s.Theme.ToLowerInvariant() switch { "light" => "light", "dark" => "dark", _ => "system" },
        ["showInlineImages"] = s.ShowInlineImages,
        ["linkPreviewsEnabled"] = s.ShowLinkPreviews,
        ["showMapPreviewThumbnails"] = s.ShowMapPreviews,
        ["recentEmojis"] = new JsonArray(s.RecentEmoji.Select(e => (JsonNode?)e).ToArray()),
        ["notifyContactMessages"] = s.NotifyDirectMessages,
        ["notifyChannelMessages"] = s.NotifyChannelMessages,
        ["notifyRoomMessages"] = s.NotifyRoomMessages,
        ["notifyNewContacts"] = s.NotifyNewContacts,
        ["notificationSoundEnabled"] = s.NotificationSound,
        ["notifyLowBattery"] = s.NotifyLowBattery,
    };

    // MARK: Restore (while disconnected)

    public sealed record RestoreOptions(bool Name, bool RadioSettings, bool Position, bool OtherSettings, bool Channels, bool Contacts,
        bool Messages, bool SavedPaths, bool AppSettings);

    public sealed record RestoreResult(string RadioId, string RadioName, int Contacts, int Channels, int Messages, int SavedPaths,
        bool SettingsRestored, bool RadioWritePending);

    /// <summary>
    /// Restores one radio's data from the backup into this app while disconnected. Contacts, channels, messages and
    /// saved paths go into the app right away; name, radio settings, channels and contacts are written to the radio
    /// itself the next time it connects (see <see cref="PendingConfigService"/>).
    /// </summary>
    /// <param name="targetRadioId">This app's radio to restore into (null: the one with the same key, or a new one).</param>
    public RestoreResult Restore(MeshApp app, Device d, string? targetRadioId, RestoreOptions o)
    {
        if (app.IsConnected) throw new InvalidOperationException(L.T("Disconnect the radio before restoring a backup."));
        var db = app.Db;
        var target = targetRadioId is not null ? db.GetRadio(targetRadioId) : null;
        target ??= db.GetRadios().FirstOrDefault(r => r.PublicKey.AsSpan().SequenceEqual(d.PublicKey));
        if (target is null)
        {
            // A radio this PC hasn't seen: keep its data under its own key until it connects.
            target = new RadioRecord
            {
                Id = d.PublicKey.ToHex(),
                Name = d.NodeName,
                PublicKey = d.PublicKey,
                MaxChannels = d.MaxChannels,
                MaxContacts = d.MaxContacts,
            };
            db.UpsertRadio(target);
        }
        var radioId = target.Id;
        var cfg = ToNodeConfig(d);
        var pending = new PendingRadioConfig { RadioId = radioId, RadioName = target.Name, CreatedAt = DateTimeOffset.Now };
        int contacts = 0, channels = 0, paths = 0;

        if (o.Contacts)
            foreach (var c in cfg.Contacts ?? [])
            {
                var key = MeshCore.Bytes.FromHex(c.PublicKey);
                if (key is not { Length: 32 }) continue;
                if (db.GetContactByKey(radioId, key) is not null) continue;
                var rec = db.SaveContactFrame(radioId, NodeConfigService.ToMeshContact(c));
                if (!string.IsNullOrWhiteSpace(c.CustomName)) { rec.Nickname = c.CustomName; db.UpsertContact(rec); }
                pending.Contacts.Add(c);
                contacts++;
            }

        if (o.Channels)
        {
            var local = db.GetChannels(radioId);
            var used = local.Select(c => c.Idx).ToHashSet();
            var max = target.MaxChannels > 0 ? target.MaxChannels : d.MaxChannels > 0 ? d.MaxChannels : 8;
            foreach (var c in cfg.Channels ?? [])
            {
                var secret = MeshCore.Bytes.FromHex(c.Secret);
                if (secret is not { Length: 16 } || local.Any(l => l.Secret.AsSpan().SequenceEqual(secret))) continue;
                var slot = Enumerable.Range(0, max).FirstOrDefault(i => !used.Contains(i), -1);
                if (slot < 0) break;
                used.Add(slot);
                db.UpsertChannel(new ChannelRecord { RadioId = radioId, Idx = slot, Name = c.Name, Secret = secret });
                pending.Channels.Add(new PendingChannel { Idx = slot, Name = c.Name, Secret = c.Secret });
                channels++;
            }
        }

        var messages = 0;
        if (o.Messages) messages = ImportHistory(app, d, radioId).Messages;

        if (o.SavedPaths)
        {
            var existing = db.GetTracePaths(radioId);
            foreach (var t in TracePathsOf(d))
            {
                var path = Bytes(t["pathBytes"]) ?? [];
                var name = Str(t["name"]) ?? "Saved path";
                if (path.Length == 0 || existing.Any(e => e.Name == name && e.Path.AsSpan().SequenceEqual(path))) continue;
                db.SaveTracePath(new TracePathRecord
                {
                    RadioId = radioId,
                    Name = name,
                    Path = path,
                    HashSize = Math.Max(1, (int)Num(t["hashSize"], 1)),
                    CreatedAt = ToMs(Date(t["createdDate"])) ?? Time.Now(),
                });
                paths++;
            }
        }

        var settingsRestored = o.AppSettings && RestoreSettings(app);

        pending.Sections = (o.Name && cfg.Name is not null ? ConfigSections.Identity : 0)
                           | (o.RadioSettings && cfg.RadioSettings is not null ? ConfigSections.Radio : 0)
                           | (o.Position ? ConfigSections.Position : 0)
                           | (o.OtherSettings ? ConfigSections.Other : 0);
        pending.Config = new NodeConfigFile
        {
            Name = cfg.Name,
            RadioSettings = cfg.RadioSettings,
            PositionSettings = cfg.PositionSettings,
            OtherSettings = cfg.OtherSettings,
        };
        var hasWrite = pending.Sections != ConfigSections.None || pending.Channels.Count > 0 || pending.Contacts.Count > 0;
        if (hasWrite) app.PendingConfig.Save(pending);
        if (o.RadioSettings && d.AppliedPresetId is { } presetId) app.Settings.Update(st => st.AppliedRadioPresets[radioId] = presetId);

        app.SetOfflineRadio(radioId);
        app.Notify(DataKind.Contacts);
        app.Notify(DataKind.Channels);
        app.Notify(DataKind.TracePaths);
        app.Notify(DataKind.Conversations);
        return new RestoreResult(radioId, target.Name, contacts, channels, messages, paths, settingsRestored, hasWrite);
    }

    /// <summary>App settings: all of them from a Windows backup (keeping this PC's connection details), or the shared ones from a phone backup.</summary>
    private bool RestoreSettings(MeshApp app)
    {
        var current = app.Settings.Current;
        if (WindowsSettings is { } ws)
        {
            try
            {
                var restored = JsonSerializer.Deserialize<AppSettings>(ws.ToJsonString(), SettingsStore.JsonOptions);
                if (restored is null) return false;
                restored.LastConnection = current.LastConnection;
                restored.RecentConnections = current.RecentConnections;
                restored.BluetoothPin = current.BluetoothPin;
                restored.LastConversation = current.LastConversation;
                restored.StartWithWindows = current.StartWithWindows;
                app.Settings.Import(JsonSerializer.Serialize(restored, SettingsStore.JsonOptions));
                return true;
            }
            catch (JsonException) { return false; }
        }
        if (UserDefaults is not { } u) return false;
        app.Settings.Update(s =>
        {
            if (Str(u["appColorSchemePreference"]) is { } scheme) s.Theme = scheme switch { "light" => "Light", "dark" => "Dark", _ => "System" };
            if (u["showInlineImages"] is JsonValue a) s.ShowInlineImages = Bool(a);
            if (u["linkPreviewsEnabled"] is JsonValue b) s.ShowLinkPreviews = Bool(b);
            if (u["showMapPreviewThumbnails"] is JsonValue c) s.ShowMapPreviews = Bool(c);
            if (u["notifyContactMessages"] is JsonValue d) s.NotifyDirectMessages = Bool(d);
            if (u["notifyChannelMessages"] is JsonValue e) s.NotifyChannelMessages = Bool(e);
            if (u["notifyRoomMessages"] is JsonValue f) s.NotifyRoomMessages = Bool(f);
            if (u["notifyNewContacts"] is JsonValue g) s.NotifyNewContacts = Bool(g);
            if (u["notificationSoundEnabled"] is JsonValue h) s.NotificationSound = Bool(h);
            if (u["notifyLowBattery"] is JsonValue i) s.NotifyLowBattery = Bool(i);
            if (u["recentEmojis"] is JsonArray emoji && emoji.Count > 0) s.RecentEmoji = emoji.Select(x => Str(x)).Where(x => x is not null).Take(12).ToList()!;
        });
        return true;
    }

    /// <summary>Windows: All=0, MentionsOnly=1, Muted=2. iPhone app: muted=0, mentionsOnly=1, all=2.</summary>
    private static int IosNotificationLevel(int windows) => windows switch { 2 => 0, 1 => 1, _ => 2 };

    // MARK: JSON helpers

    private static IReadOnlyList<JsonObject> Objects(JsonNode? n) => n is JsonArray a ? a.OfType<JsonObject>().ToList() : [];

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Num(JsonNode? n, double fallback = 0)
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, Inv, out var p)) return p;
        try { return v.GetValue<JsonElement>() is { ValueKind: JsonValueKind.Number } e ? e.GetDouble() : fallback; }
        catch { return fallback; }
    }

    private static bool Bool(JsonNode? n) => n is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : Num(v) != 0);

    private static byte[]? Bytes(JsonNode? n)
    {
        if (n is JsonArray a) return a.Select(x => (byte)Num(x)).ToArray();
        if (Str(n) is not { } s) return null;
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return MeshCore.Bytes.FromHex(s); }
    }

    private static DateTimeOffset? Date(JsonNode? n)
    {
        if (n is not JsonValue) return null;
        if (Str(n) is { } iso) return DateTimeOffset.TryParse(iso, Inv, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        var secs = Num(n, double.NaN);
        if (double.IsNaN(secs)) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(secs * 1000)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static long? ToMs(DateTimeOffset? d) => d?.ToUnixTimeMilliseconds();

    private static double Seconds(DateTimeOffset d) => Math.Round(d.ToUnixTimeMilliseconds() / 1000.0, 3);

    private static string B64(byte[]? data) => Convert.ToBase64String(data ?? []);

    /// <summary>The id as a UUID: GUIDs are kept, anything else gets a stable name-based UUID.</summary>
    private static string Uuid(string id)
    {
        if (Guid.TryParse(id, out var g)) return g.ToString().ToUpperInvariant();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("meshcore-windows:" + id));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(hash, 0, 16);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}
