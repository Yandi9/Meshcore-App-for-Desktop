using MeshCore;
using Xunit;

namespace MeshCore.Tests;

public class SessionTests
{
    private static (MeshCoreSession Session, MockTransport Transport) Make(Func<byte[], IEnumerable<byte[]>> onSend)
    {
        var t = new MockTransport { OnSend = onSend };
        var s = new MeshCoreSession(t, SessionConfiguration.Default with { DefaultTimeout = TimeSpan.FromSeconds(2) });
        return (s, t);
    }

    private static IEnumerable<byte[]> Basic(byte[] cmd)
    {
        switch ((CommandCode)cmd[0])
        {
            case CommandCode.AppStart: yield return ParserTests.SelfInfoFrame(); break;
            case CommandCode.DeviceQuery: yield return ParserTests.DeviceInfoFrame(); break;
            case CommandCode.GetBattery: yield return new ByteWriter(0x0C).U16(3900).ToArray(); break;
            case CommandCode.SetName: yield return [0x00]; break;
            case CommandCode.GetContacts:
                yield return new ByteWriter(0x02).U32(2).ToArray();
                yield return new byte[] { 0x03 }.Concat(ParserTests.ContactPayload(Enumerable.Repeat((byte)1, 32).ToArray(), "A")).ToArray();
                yield return new byte[] { 0x03 }.Concat(ParserTests.ContactPayload(Enumerable.Repeat((byte)2, 32).ToArray(), "B")).ToArray();
                yield return new ByteWriter(0x04).U32(1_700_000_500).ToArray();
                break;
            case CommandCode.GetChannel:
                yield return new ByteWriter(0x12, cmd[1]).Raw(System.Text.Encoding.UTF8.GetBytes($"ch{cmd[1]}").PaddedOrTruncated(32)).Raw(new byte[16]).ToArray();
                break;
            case CommandCode.GetMessage: yield return [0x0A]; break;
        }
    }

    [Fact]
    public async Task StartSendsAppStartAndStoresSelfInfo()
    {
        var (s, t) = Make(Basic);
        await s.StartAsync();
        Assert.Equal("MyNode", s.SelfInfo!.Name);
        Assert.Equal((byte)CommandCode.AppStart, t.Sent[0][0]);
        Assert.Equal(ConnectionStateKind.Connected, s.ConnectionState.Kind);
        await s.StopAsync();
        Assert.Equal(ConnectionStateKind.Disconnected, s.ConnectionState.Kind);
    }

    [Fact]
    public async Task QueryAndBattery()
    {
        var (s, _) = Make(Basic);
        await s.StartAsync();
        var d = await s.QueryDeviceAsync();
        Assert.Equal("Heltec V3", d.Model);
        var b = await s.GetBatteryAsync();
        Assert.Equal(3900, b.Level);
        await s.SetNameAsync("New");
    }

    [Fact]
    public async Task StreamsContacts()
    {
        var (s, _) = Make(Basic);
        await s.StartAsync();
        var r = await s.GetContactsAsync();
        Assert.Equal(2, r.Contacts.Count);
        Assert.Equal(2, r.ReportedTotal);
        Assert.Equal(1_700_000_500, r.LastModified!.Value.ToUnixTimeSeconds());
        Assert.False(s.Contacts.NeedsRefresh);
    }

    [Fact]
    public async Task PipelinedChannelRead()
    {
        var (s, t) = Make(Basic);
        await s.StartAsync();
        var (received, missing) = await s.GetChannelsAsync(Enumerable.Range(0, 20).Select(i => (byte)i).ToList());
        Assert.Equal(20, received.Count);
        Assert.Empty(missing);
        Assert.Equal("ch19", received[19].Name);
    }

    [Fact]
    public async Task DeviceErrorSurfaces()
    {
        var (s, _) = Make(cmd => cmd[0] == (byte)CommandCode.AppStart ? [ParserTests.SelfInfoFrame()] : [new byte[] { 0x01, 0x02 }]);
        await s.StartAsync();
        var ex = await Assert.ThrowsAsync<MeshCoreException>(() => s.SetNameAsync("x"));
        Assert.Equal(MeshCoreErrorKind.DeviceError, ex.Kind);
        Assert.Equal(ErrorCode.NotFound, ex.FirmwareError);
    }

    [Fact]
    public async Task TimeoutWhenRadioSilent()
    {
        var (s, _) = Make(cmd => cmd[0] == (byte)CommandCode.AppStart ? [ParserTests.SelfInfoFrame()] : []);
        await s.StartAsync();
        var ex = await Assert.ThrowsAsync<MeshCoreException>(() => s.GetBatteryAsync());
        Assert.Equal(MeshCoreErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task AutoFetchDrainsOnMessagesWaiting()
    {
        var queue = new Queue<byte[]>();
        queue.Enqueue(new ByteWriter(0x11).I8(4).U8(0).U8(0).U8(0).U8(0).U8(0).U32(1_700_000_000).Utf8("Bob: yo").ToArray());
        queue.Enqueue(new ByteWriter(0x10).I8(4).U8(0).U8(0).Raw([1, 2, 3, 4, 5, 6]).U8(0).U8(0).U32(1_700_000_000).Utf8("dm").ToArray());
        var (s, t) = Make(cmd => cmd[0] switch
        {
            (byte)CommandCode.AppStart => [ParserTests.SelfInfoFrame()],
            (byte)CommandCode.GetMessage => [queue.Count > 0 ? queue.Dequeue() : [0x0A]],
            _ => [],
        });
        await s.StartAsync();
        using var sub = s.Subscribe(e => e is MeshEvent.ContactMessageReceived or MeshEvent.ChannelMessageReceived);
        s.StartAutoMessageFetching();
        t.Inject([0x83]);
        var got = new List<MeshEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var e in sub.ReadAllAsync(cts.Token))
        {
            got.Add(e);
            if (got.Count == 2) break;
        }
        Assert.IsType<MeshEvent.ChannelMessageReceived>(got[0]);
        Assert.IsType<MeshEvent.ContactMessageReceived>(got[1]);
    }

    [Fact]
    public async Task DisconnectIsDetected()
    {
        var (s, t) = Make(Basic);
        await s.StartAsync();
        var tcs = new TaskCompletionSource<ConnectionState>();
        s.ConnectionStateChanged += st => { if (st.Kind == ConnectionStateKind.Disconnected) tcs.TrySetResult(st); };
        t.SimulateDrop();
        var st = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ConnectionStateKind.Disconnected, st.Kind);
        Assert.False(s.IsRunning);
    }

    [Fact]
    public async Task BinaryStatusRequestMatchesTag()
    {
        var key = Enumerable.Range(10, 32).Select(i => (byte)i).ToArray();
        MockTransport? tr = null;
        var (s, t) = Make(cmd =>
        {
            if (cmd[0] == (byte)CommandCode.AppStart) return [ParserTests.SelfInfoFrame()];
            if (cmd[0] == (byte)CommandCode.SendStatusRequest)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    var body = new ByteWriter().U16(4000).U16(0).U16(unchecked((ushort)-100)).U16(0)
                        .U32(1).U32(2).U32(3).U32(4).U32(5).U32(6).U32(7).U32(8).U16(0).U16(20).U16(0).U16(0).U32(0).U32(0).ToArray();
                    tr!.Inject(new ByteWriter(0x8C, 0).Raw([9, 9, 9, 9]).Raw(body).ToArray());
                });
                return [new byte[] { 0x06, 0x00, 9, 9, 9, 9, 0xE8, 0x03, 0, 0 }];
            }
            return [];
        });
        tr = t;
        await s.StartAsync();
        var status = await s.RequestStatusAsync(key);
        Assert.Equal(4000, status.Battery);
        Assert.Equal(-100, status.NoiseFloor);
        Assert.Equal(5.0, status.LastSnr);
    }
}
