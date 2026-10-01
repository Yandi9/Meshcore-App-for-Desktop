using System.Text;

namespace MeshCore;

public sealed partial class MeshCoreSession
{
    public Task<StatusResponse> RequestStatusAsync(byte[] publicKey, ContactType type = ContactType.Repeater, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "requestStatus");
        var layout = type == ContactType.Room ? StatusLayout.RoomServer : StatusLayout.Repeater;
        var prefix = publicKey.Prefix(6);
        return WithSerialization(() => PerformBinaryExchangeAsync(
            PacketBuilder.SendStatusRequest(publicKey), "Status",
            e =>
            {
                if (e is not MeshEvent.StatusResponseEvent s || !s.Response.PublicKeyPrefix.SequenceEquals(prefix)) return null;
                return layout == StatusLayout.RoomServer && s.Response.Layout == StatusLayout.Repeater
                    ? Parsers.RoomServerStatusFromRepeaterLayout(s.Response)
                    : s.Response;
            },
            (payload, _) => Parsers.ParseStatusFromBinary(payload, prefix, layout), ct), ct);
    }

    public Task<TelemetryResponse> RequestTelemetryAsync(byte[] publicKey, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "requestTelemetry");
        var prefix = publicKey.Prefix(6);
        return WithSerialization(() => PerformBinaryExchangeAsync(
            PacketBuilder.GetSelfTelemetry(publicKey), "Telemetry",
            e => e is MeshEvent.TelemetryResponseEvent t && t.Response.PublicKeyPrefix.SequenceEquals(prefix) ? t.Response : null,
            (payload, _) => new TelemetryResponse(prefix, null, payload), ct), ct);
    }

    public Task<OwnerInfoResponse> RequestOwnerInfoAsync(byte[] publicKey, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "requestOwnerInfo");
        return WithSerialization(() => PerformBinaryExchangeAsync<OwnerInfoResponse>(
            PacketBuilder.BinaryRequest(publicKey, BinaryRequestType.OwnerInfo), "Owner info", null,
            (payload, _) =>
            {
                var parts = Encoding.UTF8.GetString(payload).Split('\n', 3);
                return new OwnerInfoResponse(parts.ElementAtOrDefault(0) ?? "", parts.ElementAtOrDefault(1) ?? "", parts.ElementAtOrDefault(2) ?? "");
            }, ct), ct);
    }

    public Task<MmaResponse> RequestMmaAsync(byte[] publicKey, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "requestMMA");
        var payload = new ByteWriter().U32(Bytes.EpochSeconds32(start)).U32(Bytes.EpochSeconds32(end)).Raw([0, 0]).ToArray();
        var prefix = publicKey.Prefix(6);
        return WithSerialization(() => PerformBinaryExchangeAsync<MmaResponse>(
            PacketBuilder.BinaryRequest(publicKey, BinaryRequestType.Mma, payload), "MMA", null,
            (p, tag) => new MmaResponse(prefix, tag, Parsers.ParseMma(p)), ct), ct);
    }

    public Task<AclResponse> RequestAclAsync(byte[] publicKey, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "requestACL");
        var prefix = publicKey.Prefix(6);
        return WithSerialization(() => PerformBinaryExchangeAsync<AclResponse>(
            PacketBuilder.BinaryRequest(publicKey, BinaryRequestType.Acl, [0, 0]), "ACL", null,
            (p, tag) => new AclResponse(prefix, tag, Parsers.ParseAcl(p)), ct), ct);
    }

    public Task<NeighboursResponse> RequestNeighboursAsync(byte[] publicKey, byte count = 255, ushort offset = 0, byte orderBy = 0, byte pubkeyPrefixLength = 4, CancellationToken ct = default)
    {
        RequireFullKey(publicKey, "requestNeighbours");
        var tag = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        var payload = new ByteWriter(0, count).U16(offset).U8(orderBy).U8(pubkeyPrefixLength).U32(tag).ToArray();
        var prefix = publicKey.Prefix(6);
        return WithSerialization(() => PerformBinaryExchangeAsync<NeighboursResponse>(
            PacketBuilder.BinaryRequest(publicKey, BinaryRequestType.Neighbours, payload), "Neighbours", null,
            (p, t) => Parsers.ParseNeighbours(p, prefix, t, pubkeyPrefixLength), ct), ct);
    }

    /// <summary>Pages through a repeater's full neighbour table.</summary>
    public async Task<NeighboursResponse> FetchAllNeighboursAsync(byte[] publicKey, byte orderBy = 0, byte pubkeyPrefixLength = 4, CancellationToken ct = default)
    {
        var all = new List<Neighbour>();
        NeighboursResponse? first = null;
        var total = 0;
        for (var page = 0; page < NeighboursResponse.MaxPaginationPages; page++)
        {
            if (page > 0) await Task.Delay(1000, ct).ConfigureAwait(false);
            var r = await RequestNeighboursAsync(publicKey, 255, (ushort)Math.Min(all.Count, ushort.MaxValue), orderBy, pubkeyPrefixLength, ct).ConfigureAwait(false);
            first ??= r;
            total = r.TotalCount;
            if (r.Neighbours.Count == 0) break;
            all.AddRange(r.Neighbours);
            if (all.Count >= total) break;
        }
        return new NeighboursResponse(first?.PublicKeyPrefix ?? [], first?.Tag ?? [], total, all);
    }

    /// <summary>Asks a repeater which regions it allows (anonymous request).</summary>
    public async Task<IReadOnlyList<string>> RequestRegionsAsync(MeshContact contact, CancellationToken ct = default)
    {
        var isFlood = contact.OutPathLength == 0xFF;
        if (isFlood) await AddContactAsync(contact with { OutPathLength = 0, OutPath = [] }, ct).ConfigureAwait(false);
        try
        {
            return await WithSerialization(() => PerformBinaryExchangeAsync<IReadOnlyList<string>>(
                PacketBuilder.SendAnonReq(contact.PublicKey, AnonRequestType.Regions, isFlood ? (byte)0 : contact.OutPathLength, isFlood ? [] : contact.OutPath),
                "Regions", null, (p, _) => Parsers.ParseRegions(p), ct), ct).ConfigureAwait(false);
        }
        finally
        {
            if (isFlood) { try { await ResetPathAsync(contact.PublicKey, CancellationToken.None).ConfigureAwait(false); } catch { /* best effort */ } }
        }
    }

    /// <summary>
    /// Sends a request that is answered asynchronously by a <see cref="MeshEvent.BinaryResponse"/> tagged with the
    /// expected-ACK code from the <see cref="MeshEvent.MessageSent"/> reply. Retransmits on the firmware's suggested cadence.
    /// Must be called while holding the request lock.
    /// </summary>
    private async Task<T> PerformBinaryExchangeAsync<T>(byte[] request, string operation, Func<MeshEvent, T?>? matchRouted,
        Func<byte[], byte[], T?> parse, CancellationToken ct) where T : class
    {
        using var sub = Dispatcher.Subscribe();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Configuration.BinaryRequestOverallTimeout);
        var token = cts.Token;
        var expectedTags = new List<byte[]>();
        var acceptedSent = false;
        var cadence = Configuration.BinaryRequestRetransmitInterval ?? TimeSpan.Zero;
        var suggested = false;
        var nextRetransmit = DateTime.MaxValue;

        await SendRawAsync(request, ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var wait = nextRetransmit == DateTime.MaxValue ? Timeout.InfiniteTimeSpan : nextRetransmit - DateTime.UtcNow;
                if (wait != Timeout.InfiniteTimeSpan && wait <= TimeSpan.Zero)
                {
                    try { await SendRawAsync(request, token).ConfigureAwait(false); } catch (MeshCoreException) { /* keep waiting */ }
                    nextRetransmit = DateTime.UtcNow + cadence;
                    continue;
                }
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (wait != Timeout.InfiniteTimeSpan) waitCts.CancelAfter(wait);
                bool has;
                try { has = await sub.Reader.WaitToReadAsync(waitCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                if (!has) throw new MeshCoreException(MeshCoreErrorKind.ConnectionLost);
                while (sub.Reader.TryRead(out var e))
                {
                    switch (e)
                    {
                        case MeshEvent.MessageSent sent:
                            // Firmware assigns a new tag per (re)transmit; accept a response to any of them.
                            expectedTags.Add(sent.Info.ExpectedAck);
                            acceptedSent = true;
                            var withHeadroom = TimeSpan.FromMilliseconds(sent.Info.SuggestedTimeoutMs * SessionConfiguration.BinaryRetransmitRttHeadroom);
                            if (withHeadroom > cadence) cadence = withHeadroom;
                            if (!suggested && Configuration.BinaryRequestRetransmitInterval is not null)
                            {
                                suggested = true;
                                nextRetransmit = DateTime.UtcNow + cadence;
                            }
                            break;
                        case MeshEvent.Error err:
                            if (!acceptedSent) throw MeshCoreException.Device(err.Code);
                            break;
                        case MeshEvent.BinaryResponse br when expectedTags.Any(t => br.Tag.SequenceEquals(t)):
                            return parse(br.Data, br.Tag)
                                ?? throw new MeshCoreException(MeshCoreErrorKind.ParseError, $"{operation} binary response unparseable ({br.Data.Length} bytes)");
                        case MeshEvent.ConnectionStateChanged { State.Kind: ConnectionStateKind.Disconnected }:
                            throw new MeshCoreException(MeshCoreErrorKind.ConnectionLost);
                        default:
                            if (matchRouted?.Invoke(e) is { } routed) return routed;
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw MeshCoreException.Timeout();
        }
    }
}
