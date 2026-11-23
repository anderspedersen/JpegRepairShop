using System.Diagnostics;

namespace JpegRepair.Core;

/// <summary>
/// Lets long-running work (the searches of "Fix all") be paused and stopped from outside, at checkpoints inside the
/// work itself rather than only between searches. The work calls <see cref="Checkpoint"/> often (from any thread):
/// it blocks there while paused and throws <see cref="OperationCanceledException"/> once stopped. Checkpoints are only
/// placed where nothing has been changed yet, so stopping there leaves the document as it was.
/// </summary>
public sealed class WorkGate
{
    private readonly ManualResetEventSlim _running = new(true);
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _gate = new();
    private long _pausedSince;
    private TimeSpan _paused;

    public CancellationToken Token => _cancel.Token;

    public bool IsPaused => !_running.IsSet;

    public bool IsStopped => _cancel.IsCancellationRequested;

    /// <summary>Total time spent paused so far.</summary>
    public TimeSpan PausedTime
    {
        get
        {
            lock (_gate)
                return _pausedSince == 0 ? _paused : _paused + Stopwatch.GetElapsedTime(_pausedSince);
        }
    }

    /// <summary>Work stops at its next checkpoint until <see cref="Resume"/> or <see cref="Stop"/>.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_pausedSince == 0)
                _pausedSince = Stopwatch.GetTimestamp();
        }

        _running.Reset();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_pausedSince != 0)
                _paused += Stopwatch.GetElapsedTime(_pausedSince);
            _pausedSince = 0;
        }

        _running.Set();
    }

    /// <summary>Work throws <see cref="OperationCanceledException"/> at its next checkpoint (also when paused).</summary>
    public void Stop()
    {
        _cancel.Cancel();
        Resume();
    }

    /// <summary>Blocks while paused; throws <see cref="OperationCanceledException"/> once stopped. Cheap when running.</summary>
    public void Checkpoint()
    {
        if (!_running.IsSet)
            _running.Wait(_cancel.Token);
        _cancel.Token.ThrowIfCancellationRequested();
    }

    /// <summary>True if <paramref name="exception"/> is, or only wraps, cancellation (as Parallel.For reports it).</summary>
    public static bool IsCancellation(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        AggregateException aggregate => aggregate.Flatten().InnerExceptions.All(e => e is OperationCanceledException),
        _ => false,
    };
}
