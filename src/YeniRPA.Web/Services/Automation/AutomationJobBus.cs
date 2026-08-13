using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// The progress channel every automation module reports through, plus the single-run lock that
/// keeps two runs from driving the same browser at once.
///
/// The RPA project this was ported from streamed progress over SignalR. This app deliberately ships
/// with no external runtime dependencies (Chart.js and the fonts are vendored), and pulling in a
/// SignalR client would break that for a stream that only ever flows server to browser — so the
/// events are published as Server-Sent Events instead, which needs no client library at all.
/// </summary>
public sealed class AutomationJobBus
{
    /// <summary>
    /// Events retained for the current run. A browser that connects (or reloads) mid-run replays
    /// them before it starts receiving live output, so a refresh does not lose the log.
    /// </summary>
    const int ReplayLimit = 500;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
    readonly List<string> _replay = [];

    /// <summary>Guards publish/subscribe as one step, so a new subscriber can neither miss an event
    /// nor receive it twice (once in the replay, once live).</summary>
    readonly object _sync = new();

    int _running;
    string? _runningModule;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Which module holds the run slot, or <c>null</c> when nothing is running.</summary>
    public string? RunningModule => Volatile.Read(ref _runningModule);

    /// <summary>Claims the run slot and clears the previous run's log. False when one is running.</summary>
    public bool TryBeginRun(string module)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return false;

        Volatile.Write(ref _runningModule, module);
        lock (_sync) _replay.Clear();
        return true;
    }

    /// <summary>Releases the run slot. The log is left in place until the next run starts.</summary>
    public void EndRun()
    {
        Volatile.Write(ref _runningModule, null);
        Interlocked.Exchange(ref _running, 0);
    }

    public void Started(string module, int total) =>
        Publish(new { type = "started", module, total });

    public void Log(string message) =>
        Publish(new { type = "log", message });

    public void Progress(int completed, int total) =>
        Publish(new { type = "progress", completed, total });

    public void Done(int processed, IReadOnlyList<string> failed) =>
        Publish(new { type = "done", processed, failed });

    /// <summary>
    /// Yields this run's replay buffer and then every event published while the caller stays
    /// subscribed. Each item is a JSON object ready to be written as one SSE frame.
    /// </summary>
    public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A browser that stops reading must not be able to stall the run: the oldest frames are
        // dropped instead, and the run keeps going.
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        var id = Guid.NewGuid();
        string[] backlog;

        lock (_sync)
        {
            backlog = [.. _replay];
            _subscribers[id] = channel;
        }

        try
        {
            foreach (var payload in backlog)
                yield return payload;

            await foreach (var payload in channel.Reader.ReadAllAsync(cancellationToken))
                yield return payload;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    void Publish(object payload)
    {
        // Serialised inside the lock is wasteful, but the alternative — serialising outside it —
        // buys nothing here: publishing is a handful of non-blocking channel writes.
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        lock (_sync)
        {
            _replay.Add(json);
            if (_replay.Count > ReplayLimit)
                _replay.RemoveAt(0);

            foreach (var subscriber in _subscribers.Values)
                subscriber.Writer.TryWrite(json);
        }
    }
}
