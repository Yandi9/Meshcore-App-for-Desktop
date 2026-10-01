using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MeshCore;

/// <summary>A live subscription to session events. Dispose to unsubscribe.</summary>
public sealed class EventSubscription : IDisposable
{
    private readonly EventDispatcher _owner;
    internal readonly Channel<MeshEvent> Channel;
    internal readonly Func<MeshEvent, bool>? Filter;

    internal EventSubscription(EventDispatcher owner, Func<MeshEvent, bool>? filter, int capacity)
    {
        _owner = owner;
        Filter = filter;
        Id = Guid.NewGuid();
        Channel = System.Threading.Channels.Channel.CreateBounded<MeshEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public Guid Id { get; }
    public ChannelReader<MeshEvent> Reader => Channel.Reader;

    public async IAsyncEnumerable<MeshEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await Channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (Channel.Reader.TryRead(out var e)) yield return e;
        }
    }

    public void Dispose() => _owner.Remove(this);
}

/// <summary>Fan-out of parsed radio events to any number of subscribers.</summary>
public sealed class EventDispatcher
{
    private readonly object _lock = new();
    private readonly List<EventSubscription> _subs = new();

    public int DroppedEventCount { get; private set; }
    public int SubscriberCount { get { lock (_lock) return _subs.Count; } }

    public EventSubscription Subscribe(Func<MeshEvent, bool>? filter = null, int capacity = 2048)
    {
        var s = new EventSubscription(this, filter, capacity);
        lock (_lock) _subs.Add(s);
        return s;
    }

    public void Dispatch(MeshEvent e)
    {
        EventSubscription[] snapshot;
        lock (_lock) snapshot = _subs.ToArray();
        foreach (var s in snapshot)
        {
            if (s.Filter is not null && !s.Filter(e)) continue;
            s.Channel.Writer.TryWrite(e);
        }
    }

    internal void Remove(EventSubscription s)
    {
        lock (_lock) _subs.Remove(s);
        s.Channel.Writer.TryComplete();
    }

    public void FinishAll()
    {
        EventSubscription[] snapshot;
        lock (_lock) { snapshot = _subs.ToArray(); _subs.Clear(); }
        foreach (var s in snapshot) s.Channel.Writer.TryComplete();
    }
}
