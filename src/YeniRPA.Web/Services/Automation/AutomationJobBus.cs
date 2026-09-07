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
    CancellationTokenSource? _stop;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Which module holds the run slot, or <c>null</c> when nothing is running.</summary>
    public string? RunningModule => Volatile.Read(ref _runningModule);

    /// <summary>
    /// Cancelled when the operator asks the current run to stop. Held here rather than in a runner
    /// because the run slot is what a stop applies to: whichever module holds it is the one being
    /// stopped, and a runner that does not watch this token simply runs to the end as before.
    /// </summary>
    public CancellationToken RunToken => Volatile.Read(ref _stop)?.Token ?? CancellationToken.None;

    /// <summary>Whether a stop has been asked for and the run has not ended yet.</summary>
    public bool StopRequested => Volatile.Read(ref _stop)?.IsCancellationRequested ?? false;

    /// <summary>Claims the run slot and clears the previous run's log. False when one is running.</summary>
    public bool TryBeginRun(string module)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return false;

        Volatile.Write(ref _stop, new CancellationTokenSource());
        Volatile.Write(ref _runningModule, module);
        lock (_sync) _replay.Clear();
        return true;
    }

    /// <summary>Releases the run slot. The log is left in place until the next run starts.</summary>
    public void EndRun()
    {
        // Cleared before the slot is released, so the next run cannot start against the finished
        // run's token and stop itself on the spot.
        var stop = Interlocked.Exchange(ref _stop, null);
        stop?.Dispose();

        Volatile.Write(ref _runningModule, null);
        Interlocked.Exchange(ref _running, 0);
    }

    /// <summary>
    /// Asks the current run to stop at its next safe point. False when nothing is running.
    ///
    /// <para>What has already gone out has gone out — a sent mail cannot be recalled — so this only
    /// promises that nothing further starts. The runner says how far it got.</para>
    /// </summary>
    public bool RequestStop()
    {
        var stop = Volatile.Read(ref _stop);
        if (stop is null || stop.IsCancellationRequested)
            return false;

        try
        {
            // Outside the publish lock: cancellation runs the token's callbacks on this thread, and
            // holding _sync across foreign code is how a deadlock gets built.
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended between the read and the cancel. Nothing to stop.
            return false;
        }

        Log("Stop requested — the mail in flight finishes, then the run stops.");
        return true;
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
