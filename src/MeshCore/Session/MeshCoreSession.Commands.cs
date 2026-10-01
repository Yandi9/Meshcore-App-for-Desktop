namespace MeshCore;

public sealed partial class MeshCoreSession
{
    // MARK: Device configuration

    public Task<SelfInfo> SendAppStartAsync(CancellationToken ct = default) =>
        SendAndWaitAsync(PacketBuilder.AppStart(Configuration.ClientIdentifier), e => (e as MeshEvent.SelfInfoEvent)?.Info, ct: ct);

    public Task<DeviceCapabilities> QueryDeviceAsync(CancellationToken ct = default) =>
        SendAndWaitAsync(PacketBuilder.DeviceQuery(), e => (e as MeshEvent.DeviceInfo)?.Info, ct: ct);

    public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct = default) =>
        SendAndWaitAsync(PacketBuilder.GetBattery(), e => (e as MeshEvent.Battery)?.Info, ct: ct);

    public async Task<DateTimeOffset> GetTimeAsync(CancellationToken ct = default)
    {
        var r = await SendAndWaitAsync<object>(PacketBuilder.GetTime(), e => e is MeshEvent.CurrentTime t ? t.Time : null, ct: ct).ConfigureAwait(false);
        return (DateTimeOffset)r;
    }

    public Task SetTimeAsync(DateTimeOffset date, CancellationToken ct = default) => SendSimpleCommandAsync(PacketBuilder.SetTime(date), ct);
    public Task SetNameAsync(string name, CancellationToken ct = default) => SendSimpleCommandAsync(PacketBuilder.SetName(name), ct);
    public Task SetCoordinatesAsync(double lat, double lon, CancellationToken ct = default) => SendSimpleCommandAsync(PacketBuilder.SetCoordinates(lat, lon), ct);
    public Task SetTxPowerAsync(sbyte power, CancellationToken ct = default) => SendSimpleCommandAsync(PacketBuilder.SetTxPower(power), ct);

    public Task SetRadioAsync(double frequencyMHz, double bandwidthKHz, byte sf, byte cr, bool? clientRepeat = null, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SetRadio(frequencyMHz, bandwidthKHz, sf, cr, clientRepeat), ct);

    public async Task<IReadOnlyList<FrequencyRange>> GetRepeatFreqAsync(CancellationToken ct = default) =>
        (IReadOnlyList<FrequencyRange>)await SendAndWaitAsync<object>(PacketBuilder.GetRepeatFreq(), e => (e as MeshEvent.AllowedRepeatFreq)?.Ranges, ct: ct).ConfigureAwait(false);

    public Task SetTuningAsync(uint rxDelay, uint af, CancellationToken ct = default) => SendSimpleCommandAsync(PacketBuilder.SetTuning(rxDelay, af), ct);

    public Task<TuningParamsResponse> GetTuningParamsAsync(CancellationToken ct = default) =>
        SendAndWaitWithErrorAsync(PacketBuilder.GetTuningParams(), e => (e as MeshEvent.TuningParamsResponseEvent)?.Response, ct: ct);

    public Task SetOtherParamsAsync(bool manualAdd, byte telEnv, byte telLoc, byte telBase, byte advLocPolicy, byte? multiAcks = null, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SetOtherParams(manualAdd, telEnv, telLoc, telBase, advLocPolicy, multiAcks), ct);

    public Task SetDevicePinAsync(uint pin, CancellationToken ct = default) => SendSimpleCommandAsync(PacketBuilder.SetDevicePin(pin), ct);

    public Task SetTelemetryModeBaseAsync(byte mode, CancellationToken ct = default) => MutateOtherParamsAsync(c => c.TelemetryModeBase = (byte)(mode & 0b11), ct);
    public Task SetTelemetryModeLocationAsync(byte mode, CancellationToken ct = default) => MutateOtherParamsAsync(c => c.TelemetryModeLocation = (byte)(mode & 0b11), ct);
    public Task SetTelemetryModeEnvironmentAsync(byte mode, CancellationToken ct = default) => MutateOtherParamsAsync(c => c.TelemetryModeEnvironment = (byte)(mode & 0b11), ct);
    public Task SetManualAddContactsAsync(bool enabled, CancellationToken ct = default) => MutateOtherParamsAsync(c => c.ManualAddContacts = enabled, ct);
    public Task SetMultiAcksAsync(byte count, CancellationToken ct = default) => MutateOtherParamsAsync(c => c.MultiAcks = count, ct);
    public Task SetAdvertisementLocationPolicyAsync(byte policy, CancellationToken ct = default) => MutateOtherParamsAsync(c => c.AdvertisementLocationPolicy = policy, ct);

    public async Task MutateOtherParamsAsync(Action<OtherParamsConfig> transform, CancellationToken ct = default)
    {
        await _otherParamsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SelfInfo ??= await SendAppStartAsync(ct).ConfigureAwait(false);
            var cfg = OtherParamsConfig.From(SelfInfo);
            transform(cfg);
            await SetOtherParamsAsync(cfg.ManualAddContacts, cfg.TelemetryModeEnvironment, cfg.TelemetryModeLocation,
                cfg.TelemetryModeBase, cfg.AdvertisementLocationPolicy, cfg.MultiAcks, ct).ConfigureAwait(false);
            SelfInfo = await SendAppStartAsync(ct).ConfigureAwait(false);
        }
        finally { _otherParamsLock.Release(); }
    }

    public Task<AutoAddConfig> GetAutoAddConfigAsync(CancellationToken ct = default) =>
        SendAndWaitAsync(PacketBuilder.GetAutoAddConfig(), e => (e as MeshEvent.AutoAddConfigEvent)?.Config, ct: ct);

    public Task SetAutoAddConfigAsync(AutoAddConfig config, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SetAutoAddConfig(config), ct);

    public Task SetPathHashModeAsync(byte mode, CancellationToken ct = default)
    {
        if (mode > PathEncoding.MaxPathHashMode) throw new MeshCoreException(MeshCoreErrorKind.InvalidInput, "Path hash mode must be 0, 1, or 2");
        return SendSimpleCommandAsync(PacketBuilder.SetPathHashMode(mode), ct);
    }

    /// <summary>Reboot does not produce a response.</summary>
    public Task RebootAsync(CancellationToken ct = default) => SendRawAsync(PacketBuilder.Reboot(), ct);

    public Task FactoryResetAsync(CancellationToken ct = default) => SendSimpleCommandAsync(PacketBuilder.FactoryReset(), ct);

    public Task<TelemetryResponse> GetSelfTelemetryAsync(CancellationToken ct = default)
    {
        var prefix = SelfInfo?.PublicKey.Prefix(6);
        return SendAndWaitAsync(PacketBuilder.GetSelfTelemetry(), e =>
            e is MeshEvent.TelemetryResponseEvent t && (prefix is null || t.Response.PublicKeyPrefix.SequenceEquals(prefix)) ? t.Response : null, ct: ct);
    }

    public Task<IReadOnlyDictionary<string, string>> GetCustomVarsAsync(CancellationToken ct = default) =>
        SendAndWaitWithErrorAsync(PacketBuilder.GetCustomVars(), e => (e as MeshEvent.CustomVars)?.Vars, ct: ct);

    public Task SetCustomVarAsync(string key, string value, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SetCustomVar(key, value), ct);

    public Task<byte[]> ExportPrivateKeyAsync(CancellationToken ct = default) =>
        SendAndMatchAsync<byte[]>(PacketBuilder.ExportPrivateKey(), e => e switch
        {
            MeshEvent.PrivateKey k => Match<byte[]>.Ok(k.Key),
            MeshEvent.Disabled => Match<byte[]>.Fail(new MeshCoreException(MeshCoreErrorKind.FeatureDisabled)),
            MeshEvent.Error err => Match<byte[]>.Fail(MeshCoreException.Device(err.Code)),
            _ => Match<byte[]>.Skip,
        }, ct: ct);

    public async Task ImportPrivateKeyAsync(byte[] key, CancellationToken ct = default)
    {
        if (key.Length != PacketBuilder.PrivateKeySize)
            throw new MeshCoreException(MeshCoreErrorKind.InvalidInput, "Full 64-byte private key required");
        var ok = await SendAndMatchAsync<object>(PacketBuilder.ImportPrivateKey(key), e => e switch
        {
            MeshEvent.Ok { Value: null } => Match<object>.Ok(true),
            MeshEvent.Disabled => Match<object>.Ok(false),
            MeshEvent.Error { Code: not null } err => Match<object>.Fail(MeshCoreException.Device(err.Code)),
            _ => Match<object>.Skip,
        }, ct: ct).ConfigureAwait(false);
        if (ok is false) throw new MeshCoreException(MeshCoreErrorKind.FeatureDisabled);
        SelfInfo = await SendAppStartAsync(ct).ConfigureAwait(false);
    }

    public Task<CoreStats> GetStatsCoreAsync(CancellationToken ct = default) =>
        SendAndWaitAsync(PacketBuilder.GetStatsCore(), e => (e as MeshEvent.StatsCore)?.Stats, ct: ct);

    public Task<RadioStats> GetStatsRadioAsync(CancellationToken ct = default) =>
        SendAndWaitAsync(PacketBuilder.GetStatsRadio(), e => (e as MeshEvent.StatsRadio)?.Stats, ct: ct);

    public Task<PacketStats> GetStatsPacketsAsync(CancellationToken ct = default) =>
        SendAndWaitAsync(PacketBuilder.GetStatsPackets(), e => (e as MeshEvent.StatsPackets)?.Stats, ct: ct);

    public Task SetFloodScopeAsync(byte[] scopeKey, CancellationToken ct = default) => SendSimpleCommandAsync(PacketBuilder.SetFloodScope(scopeKey), ct);
    public Task SetFloodScopeUnscopedAsync(CancellationToken ct = default) => SendSimpleCommandAsync(PacketBuilder.SetFloodScopeUnscoped(), ct);

    public Task SetDefaultFloodScopeAsync(string name, FloodScope scope, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SetDefaultFloodScope(name, scope), ct);

    public async Task<DefaultFloodScope?> GetDefaultFloodScopeAsync(CancellationToken ct = default)
    {
        var box = await SendAndMatchAsync<Box<DefaultFloodScope?>>(PacketBuilder.GetDefaultFloodScope(), e => e switch
        {
            MeshEvent.DefaultFloodScopeEvent s => Match<Box<DefaultFloodScope?>>.Ok(new Box<DefaultFloodScope?>(s.Scope)),
            MeshEvent.Error err => Match<Box<DefaultFloodScope?>>.Fail(MeshCoreException.Device(err.Code)),
            _ => Match<Box<DefaultFloodScope?>>.Skip,
        }, ct: ct).ConfigureAwait(false);
        return box.Value;
    }

    private sealed record Box<T>(T Value);

    // MARK: Contacts

    public async Task<IReadOnlyList<MeshContact>> EnsureContactsAsync(bool force = false, CancellationToken ct = default)
    {
        if (force || Contacts.NeedsRefresh || Contacts.IsEmpty)
            return (await GetContactsAsync(Contacts.LastModified, ct).ConfigureAwait(false)).Contacts;
        return Contacts.Contacts;
    }

    /// <summary>Streams the radio's contact table (optionally only contacts modified since a watermark).</summary>
    public Task<ContactFetchResult> GetContactsAsync(DateTimeOffset? since = null, CancellationToken ct = default) =>
        WithSerialization(async () =>
        {
            using var sub = Dispatcher.Subscribe();
            await SendRawAsync(PacketBuilder.GetContacts(since), ct).ConfigureAwait(false);
            var received = new List<MeshContact>();
            int? total = null;
            var started = DateTime.UtcNow;
            while (true)
            {
                if (DateTime.UtcNow - started > Configuration.ContactStreamHardTimeout) throw MeshCoreException.Timeout();
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(Configuration.ContactStreamInactivityTimeout);
                MeshEvent e;
                try
                {
                    if (!await sub.Reader.WaitToReadAsync(idle.Token).ConfigureAwait(false)) throw new MeshCoreException(MeshCoreErrorKind.ConnectionLost);
                    if (!sub.Reader.TryRead(out e!)) continue;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw MeshCoreException.Timeout(); }
                switch (e)
                {
                    case MeshEvent.ContactsStart s: total = s.Count; break;
                    case MeshEvent.Contact c: received.Add(c.Value); break;
                    case MeshEvent.ContactsEnd end:
                        foreach (var c in received) Contacts.Store(c);
                        Contacts.MarkClean(end.LastModified);
                        return new ContactFetchResult(received, total, end.LastModified);
                    case MeshEvent.Error err: throw MeshCoreException.Device(err.Code);
                    case MeshEvent.ConnectionStateChanged { State.Kind: ConnectionStateKind.Disconnected }:
                        throw new MeshCoreException(MeshCoreErrorKind.ConnectionLost);
                }
            }
        }, ct);

    public async Task<MeshContact?> GetContactAsync(byte[] publicKey, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "getContact");
        var box = await SendAndMatchAsync<Box<MeshContact?>>(PacketBuilder.GetContactByKey(publicKey), e => e switch
        {
            MeshEvent.Contact c when c.Value.PublicKey.SequenceEquals(publicKey) => Match<Box<MeshContact?>>.Ok(new(c.Value)),
            MeshEvent.Error => Match<Box<MeshContact?>>.Ok(new(null)),
            _ => Match<Box<MeshContact?>>.Skip,
        }, ct: ct).ConfigureAwait(false);
        return box.Value;
    }

    public Task ResetPathAsync(byte[] publicKey, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "resetPath");
        return SendSimpleCommandAsync(PacketBuilder.ResetPath(publicKey), ct);
    }

    public Task RemoveContactAsync(byte[] publicKey, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "removeContact");
        return SendSimpleCommandAsync(PacketBuilder.RemoveContact(publicKey), ct);
    }

    public Task ShareContactAsync(byte[] publicKey, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "shareContact");
        return SendSimpleCommandAsync(PacketBuilder.ShareContact(publicKey), ct);
    }

    public Task<string> ExportContactAsync(byte[]? publicKey = null, CancellationToken ct = default)
    {
        if (publicKey is not null) RequireFullKey(publicKey, "exportContact");
        var hex = publicKey?.ToHex();
        return SendAndWaitAsync(PacketBuilder.ExportContact(publicKey), e =>
            e is MeshEvent.ContactUri u && (hex is null || u.Uri.Contains(hex, StringComparison.OrdinalIgnoreCase)) ? u.Uri : null, ct: ct);
    }

    public Task ImportContactAsync(byte[] cardData, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.ImportContact(cardData), ct);

    public Task AddContactAsync(MeshContact contact, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.UpdateContact(contact), ct);

    public Task UpdateContactAsync(MeshContact contact, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.UpdateContact(contact), ct);

    public Task ChangeContactPathAsync(MeshContact contact, byte[] path, byte hashSize = 1, CancellationToken ct = default)
    {
        var len = path.Length == 0 ? PathEncoding.FloodSentinel : PathEncoding.Encode(hashSize, path.Length / hashSize);
        return UpdateContactAsync(contact with { OutPathLength = len, OutPath = path }, ct);
    }

    public Task ChangeContactFlagsAsync(MeshContact contact, ContactFlags flags, CancellationToken ct = default) =>
        UpdateContactAsync(contact with { Flags = flags }, ct);

    public Task<AdvertPathResponse> GetAdvertPathAsync(byte[] publicKey, CancellationToken ct = default) =>
        SendAndWaitWithErrorAsync(PacketBuilder.GetAdvertPath(publicKey), e => (e as MeshEvent.AdvertPathResponseEvent)?.Response, ct: ct);

    // MARK: Messaging

    public Task<MessageSentInfo> SendMessageAsync(byte[] destination, string text, DateTimeOffset timestamp, byte attempt = 0, CancellationToken ct = default) =>
        SendAndWaitWithErrorAsync(PacketBuilder.SendMessage(destination, text, timestamp, attempt), e => (e as MeshEvent.MessageSent)?.Info, ct: ct);

    /// <summary>Sends a DM and waits for the ACK, retrying and falling back to flood routing (Swift sendMessageWithRetry).</summary>
    public async Task<MessageSentInfo?> SendMessageWithRetryAsync(byte[] destination, string text, DateTimeOffset timestamp,
        int maxAttempts = 3, int floodAfter = 2, int maxFloodAttempts = 2, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        RequireFullKey(destination, "retry with path reset");
        int attempts = 0, floodAttempts = 0;
        var isFlood = false;
        while (attempts < maxAttempts && (!isFlood || floodAttempts < maxFloodAttempts))
        {
            ct.ThrowIfCancellationRequested();
            if (attempts == floodAfter && !isFlood)
            {
                try { await ResetPathAsync(destination, ct).ConfigureAwait(false); isFlood = true; }
                catch (Exception) { /* keep trying */ }
            }
            var sent = await SendMessageAsync(destination.Prefix(6), text, timestamp, (byte)attempts, ct).ConfigureAwait(false);
            var ackTimeout = timeout ?? TimeSpan.FromMilliseconds(sent.SuggestedTimeoutMs * SessionConfiguration.RetryAckTimeoutMultiplier);
            var ack = await WaitForEventAsync(e => e is MeshEvent.Acknowledgement a && a.Code.SequenceEquals(sent.ExpectedAck), ackTimeout, ct).ConfigureAwait(false);
            if (ack is not null) return sent;
            attempts++;
            if (isFlood) floodAttempts++;
        }
        return null;
    }

    public Task SendAdvertisementAsync(bool flood = false, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SendAdvertisement(flood), ct);

    /// <summary>Fetches one queued message; concurrent callers share the in-flight request.</summary>
    public Task<MessageResult> GetMessageAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        lock (_getMessageLock)
        {
            if (_inFlightGetMessage is { IsCompleted: false } inFlight) return inFlight;
            var t = PerformGetMessageAsync(timeout, ct);
            _inFlightGetMessage = t;
            return t;
        }
    }

    private Task<MessageResult> PerformGetMessageAsync(TimeSpan? timeout, CancellationToken ct) =>
        WithSerialization<MessageResult>(async () =>
        {
            using var sub = Dispatcher.Subscribe(e => e is MeshEvent.ContactMessageReceived or MeshEvent.ChannelMessageReceived
                or MeshEvent.ChannelDataReceived or MeshEvent.NoMoreMessages or MeshEvent.Error or MeshEvent.ConnectionStateChanged);
            await SendRawAsync(PacketBuilder.GetMessage(), ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? Configuration.DefaultTimeout);
            try
            {
                await foreach (var e in sub.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    switch (e)
                    {
                        case MeshEvent.ContactMessageReceived m: return new MessageResult.Contact(m.Message);
                        case MeshEvent.ChannelMessageReceived m: return new MessageResult.Channel(m.Message);
                        case MeshEvent.ChannelDataReceived d: return new MessageResult.Datagram(d.Datagram);
                        case MeshEvent.NoMoreMessages: return new MessageResult.NoMoreMessages();
                        case MeshEvent.Error err: throw MeshCoreException.Device(err.Code);
                        case MeshEvent.ConnectionStateChanged: throw new MeshCoreException(MeshCoreErrorKind.ConnectionLost);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            throw MeshCoreException.Timeout();
        }, ct);

    public Task<MessageSentInfo> SendCommandAsync(byte[] destination, string command, DateTimeOffset? timestamp = null, CancellationToken ct = default) =>
        SendAndWaitWithErrorAsync(PacketBuilder.SendCommand(destination, command, timestamp ?? DateTimeOffset.UtcNow), e => (e as MeshEvent.MessageSent)?.Info, ct: ct);

    public Task SendChannelMessageAsync(byte channel, string text, DateTimeOffset timestamp, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SendChannelMessage(channel, text, timestamp), ct);

    public Task SendChannelDataAsync(byte channelIndex, ushort dataType, byte[] payload, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SendChannelData(channelIndex, dataType, payload), ct);

    public Task<MessageSentInfo> SendLoginAsync(byte[] destination, string password, CancellationToken ct = default) =>
        SendAndWaitWithErrorAsync(PacketBuilder.SendLogin(destination, password), e => (e as MeshEvent.MessageSent)?.Info, ct: ct);

    public Task SendLogoutAsync(byte[] destination, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SendLogout(destination), ct);

    public Task<MessageSentInfo> SendStatusRequestAsync(byte[] destination, CancellationToken ct = default) =>
        SendAndWaitWithErrorAsync(PacketBuilder.SendStatusRequest(destination), e => (e as MeshEvent.MessageSent)?.Info, ct: ct);

    public Task<MessageSentInfo> SendPathDiscoveryAsync(byte[] destination, CancellationToken ct = default) =>
        SendAndWaitWithErrorAsync(PacketBuilder.SendPathDiscovery(destination), e => (e as MeshEvent.MessageSent)?.Info, ct: ct);

    public Task<MessageSentInfo> SendTraceAsync(uint? tag, uint? authCode, byte flags, byte[] path, CancellationToken ct = default)
    {
        if (path.Length == 0) throw new MeshCoreException(MeshCoreErrorKind.InvalidInput, "Trace requires at least one path byte");
        var t = tag ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        var a = authCode ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        return SendAndWaitWithErrorAsync(PacketBuilder.SendTrace(t, a, flags, path), e => (e as MeshEvent.MessageSent)?.Info, ct: ct);
    }

    public Task<MessageSentInfo> SendKeepAliveAsync(byte[] publicKey, uint syncSince, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "sendKeepAlive");
        var payload = new ByteWriter().U32(syncSince).ToArray();
        return SendAndWaitWithErrorAsync(PacketBuilder.BinaryRequest(publicKey, BinaryRequestType.KeepAlive, payload), e => (e as MeshEvent.MessageSent)?.Info, ct: ct);
    }

    // MARK: Channels

    public Task<ChannelInfo> GetChannelAsync(byte index, CancellationToken ct = default) =>
        SendAndWaitAsync(PacketBuilder.GetChannel(index), e => e is MeshEvent.ChannelInfoEvent c && c.Info.Index == index ? c.Info : null, ct: ct);

    /// <summary>Reads several channel slots, pipelining requests when the transport allows it.</summary>
    public async Task<(IReadOnlyList<ChannelInfo> Received, IReadOnlyList<byte> Missing)> GetChannelsAsync(IReadOnlyList<byte> indices, CancellationToken ct = default)
    {
        if (indices.Count == 0) return ([], []);
        if (!Transport.SupportsPipelinedReads)
        {
            var list = new List<ChannelInfo>();
            var missing = new List<byte>();
            foreach (var i in indices)
            {
                try { list.Add(await GetChannelAsync(i, ct).ConfigureAwait(false)); }
                catch (MeshCoreException ex) when (ex.Kind == MeshCoreErrorKind.Timeout) { missing.Add(i); }
            }
            return (list, missing);
        }
        return await WithSerialization<(IReadOnlyList<ChannelInfo>, IReadOnlyList<byte>)>(async () =>
        {
            var requested = indices.ToList();
            var set = new HashSet<byte>(requested);
            var window = Math.Max(1, Configuration.ChannelPipelineWindow);
            var collected = new Dictionary<byte, ChannelInfo>();
            using var sub = Dispatcher.Subscribe(e => e is MeshEvent.ChannelInfoEvent or MeshEvent.ConnectionStateChanged);
            var next = Math.Min(window, requested.Count);
            for (var i = 0; i < next; i++) await SendRawAsync(PacketBuilder.GetChannel(requested[i]), ct).ConfigureAwait(false);
            var started = DateTime.UtcNow;
            while (collected.Count < requested.Count)
            {
                if (DateTime.UtcNow - started > Configuration.ChannelPipelineHardTimeout) break;
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(Configuration.ChannelPipelineIdleTimeout);
                try
                {
                    if (!await sub.Reader.WaitToReadAsync(idle.Token).ConfigureAwait(false)) break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                while (sub.Reader.TryRead(out var e))
                {
                    if (e is MeshEvent.ConnectionStateChanged) throw new MeshCoreException(MeshCoreErrorKind.ConnectionLost);
                    if (e is not MeshEvent.ChannelInfoEvent ci || !set.Contains(ci.Info.Index) || collected.ContainsKey(ci.Info.Index)) continue;
                    collected[ci.Info.Index] = ci.Info;
                    if (next < requested.Count)
                    {
                        try { await SendRawAsync(PacketBuilder.GetChannel(requested[next]), ct).ConfigureAwait(false); } catch { /* counted as missing */ }
                        next++;
                    }
                }
            }
            var received = collected.OrderBy(k => k.Key).Select(k => k.Value).ToList();
            var missingList = requested.Where(i => !collected.ContainsKey(i)).ToList();
            return ((IReadOnlyList<ChannelInfo>)received, (IReadOnlyList<byte>)missingList);
        }, ct).ConfigureAwait(false);
    }

    public Task SetChannelAsync(byte index, string name, byte[] secret, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SetChannel(index, name, secret), ct);

    // MARK: Signing / control

    public async Task<byte[]> SignAsync(byte[] data, int chunkSize = 120, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var max = (int)await SendAndWaitAsync<object>(PacketBuilder.SignStart(), e => e is MeshEvent.SignStart s ? s.MaxLength : null, ct: ct).ConfigureAwait(false);
        if (data.Length > max) throw new MeshCoreException(MeshCoreErrorKind.DataTooLarge, $"Data too large to sign ({data.Length} > {max})");
        for (var o = 0; o < data.Length; o += chunkSize)
            await SendSimpleCommandAsync(PacketBuilder.SignData(data.Slice(o, chunkSize)), ct).ConfigureAwait(false);
        return await SendAndWaitAsync(PacketBuilder.SignFinish(), e => (e as MeshEvent.Signature)?.Data,
            timeout ?? TimeSpan.FromTicks(Configuration.DefaultTimeout.Ticks * 3), ct).ConfigureAwait(false);
    }

    public Task SendControlDataAsync(byte type, byte[] payload, CancellationToken ct = default) =>
        SendSimpleCommandAsync(PacketBuilder.SendControlData(type, payload), ct);

    /// <summary>Broadcasts a zero-hop node discovery request; responses arrive as <see cref="MeshEvent.DiscoverResponseEvent"/>.</summary>
    public async Task<uint> SendNodeDiscoverRequestAsync(byte filter, bool prefixOnly = true, uint? tag = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var t = tag ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        await SendSimpleCommandAsync(PacketBuilder.SendNodeDiscoverRequest(filter, prefixOnly, t, since is { } s ? Bytes.EpochSeconds32(s) : null), ct).ConfigureAwait(false);
        return t;
    }
}
