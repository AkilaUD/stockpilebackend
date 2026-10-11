namespace Stockpile.Api.RateLimiting;

/// <summary>
/// Threat-model §7 abuse detection: for a sensitive policy a burst is
/// <see cref="_burstThreshold"/> rejected requests from one partition within
/// <see cref="_burstWindow"/>. The signal fires once per window fill, not on
/// every rejection, so operators see patterns instead of 429 noise.
/// <para>
/// Detection (this class) stays synchronous and side-effect free except for a
/// queue of crossed bursts + the Warning log (EventId 2200). Persistence is
/// the API caller's job: <see cref="DrainAsync"/> hands each queued burst to a
/// writer (task 1.29 wires it to <c>ISecurityEventService</c> →
/// RateLimitHit), keeping detection independent of storage.
/// </para>
/// </summary>
internal sealed partial class RateLimitBurstMonitor
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Policy, string Partition), PartitionState> _states = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<BurstSignal> _pending = new();
    private readonly int _burstThreshold;
    private readonly TimeSpan _burstWindow;
    private readonly TimeProvider _timeProvider;
    private ILogger? _logger;

    /// <summary>One crossed burst, drained in FIFO order.</summary>
    public readonly record struct BurstSignal(
        string Policy, string Partition, int BurstThreshold, int BurstWindowSeconds);

    /// <summary>Initializes the monitor with its burst definition.</summary>
    public RateLimitBurstMonitor(int burstThreshold, TimeSpan burstWindow, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(burstThreshold, 2);
        _burstThreshold = burstThreshold;
        _burstWindow = burstWindow;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Attaches the logging sink; called once after the host builds (ILogger needs DI).</summary>
    public void AttachLogger(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<RateLimitBurstMonitor>();
    }

    /// <summary>
    /// Counts one rejected request for a partition and raises the burst signal
    /// the moment the window fills. State is bounded by abuse actually seen:
    /// a partition untouched for a full window starts clean, so memory returns
    /// to the misbehaving partitions only.
    /// </summary>
    public void Record(string policy, string partition)
    {
        var now = _timeProvider.GetUtcNow();
        var key = (policy, partition);

        lock (_gate)
        {
            // A partition with no activity for a full window starts clean —
            // nothing older than the window could contribute to a new burst.
            if (!_states.TryGetValue(key, out var state) || now - state.LastRecorded >= _burstWindow)
            {
                state = new PartitionState();
                _states[key] = state;
            }

            while (state.Rejections.Count > 0 && now - state.Rejections.Peek() >= _burstWindow)
            {
                state.Rejections.Dequeue();
            }

            state.Rejections.Enqueue(now);
            state.LastRecorded = now;
            var overThreshold = state.Rejections.Count >= _burstThreshold;

            if (overThreshold && !state.Crossed)
            {
                state.Crossed = true;
                _pending.Enqueue(new BurstSignal(policy, partition, _burstThreshold, (int)_burstWindow.TotalSeconds));
                if (_logger is { } logger)
                {
                    LogBurst(logger, policy, partition, _burstThreshold, (int)_burstWindow.TotalSeconds);
                }
            }
            else if (!overThreshold)
            {
                state.Crossed = false;
            }
        }
    }

    /// <summary>
    /// Hands every queued burst to <paramref name="persist"/> exactly once and
    /// clears the queue. Called from the rejection path (async) so bursts reach
    /// storage on the request that crossed the threshold.
    /// </summary>
    public async Task DrainAsync(
        Func<BurstSignal, CancellationToken, Task> persist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persist);

        List<BurstSignal> signals = [];
        while (_pending.TryDequeue(out BurstSignal signal))
        {
            signals.Add(signal);
        }

        foreach (BurstSignal signal in signals)
        {
            await persist(signal, cancellationToken);
        }
    }

    /// <summary>Rejection timestamps within the window for one (policy, partition).</summary>
    private sealed class PartitionState
    {
        public Queue<DateTimeOffset> Rejections { get; } = new();

        /// <summary>Last rejection time — used to forget partitions that stop misbehaving.</summary>
        public DateTimeOffset LastRecorded { get; set; }

        /// <summary>True while the burst flag is raised; cleared when the window drains below threshold.</summary>
        public bool Crossed { get; set; }
    }

    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Warning,
        Message = "Rate-limit burst: policy {PolicyName}, partition {Partition} — {BurstThreshold} rejections within {BurstWindowSeconds}s (persisted as a RateLimitHit security event)")]
    private static partial void LogBurst(
        ILogger logger, string policyName, string partition, int burstThreshold, int burstWindowSeconds);
}
