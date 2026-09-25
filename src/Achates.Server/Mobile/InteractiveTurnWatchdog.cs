namespace Achates.Server.Mobile;

/// <summary>Tracks useful work independently of socket traffic and connection lifetime.</summary>
internal sealed class InteractiveTurnWatchdog : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _idleLimit;
    private readonly TimeSpan _totalLimit;
    private readonly Action<string> _onTimeout;
    private readonly long _started;
    private long _lastProgress;
    private readonly ITimer _timer;
    private bool _stopped;
    public string? TimeoutReason { get; private set; }

    public InteractiveTurnWatchdog(InteractiveConfig? config, Action<string> onTimeout,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _idleLimit = TimeSpan.FromSeconds(config?.IdleTimeoutSeconds is > 0 ? config.IdleTimeoutSeconds.Value : 300);
        _totalLimit = TimeSpan.FromSeconds(config?.MaxDurationSeconds is > 0 ? config.MaxDurationSeconds.Value : 3600);
        _onTimeout = onTimeout;
        _started = _lastProgress = _time.GetTimestamp();
        _timer = _time.CreateTimer(_ => Check(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Progress()
    {
        lock (_gate)
            if (!_stopped) _lastProgress = _time.GetTimestamp();
    }

    private void Check()
    {
        lock (_gate)
        {
            if (_stopped) return;
            var now = _time.GetTimestamp();
            var reason = _time.GetElapsedTime(_started, now) >= _totalLimit ? "duration_limit"
                : _time.GetElapsedTime(_lastProgress, now) >= _idleLimit ? "idle_timeout" : null;
            if (reason is null) return;
            TimeoutReason = reason;
            _stopped = true;
            _onTimeout(reason);
        }
    }

    // Stop before persistence: saving/broadcasting a completed answer must not time it out.
    public void Stop() { lock (_gate) _stopped = true; }
    public void Dispose() { Stop(); _timer.Dispose(); }
}
