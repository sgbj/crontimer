// https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/PeriodicTimer.cs

using Cronos;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;

namespace Sgbj.Cron;

/// <summary>Provides a cron timer similar to <see cref="PeriodicTimer"/> that enables waiting asynchronously for timer ticks.</summary>
/// <remarks>
/// This timer is intended to be used only by a single consumer at a time: only one call to <see cref="WaitForNextTickAsync" />
/// may be in flight at any given moment. <see cref="Dispose"/> may be used concurrently with an active <see cref="WaitForNextTickAsync" />
/// to interrupt it and cause it to return false.
/// </remarks>
public sealed class CronTimer : IDisposable
{
    private readonly CronExpression _expression;
    private readonly TimeZoneInfo _zone;
    private bool _canceled;
    private readonly Timer? _timer;
    private readonly State? _state;

    /// <summary>Initializes the timer with the given cron expression and <see cref="TimeZoneInfo.Utc"/> zone.</summary>
    /// <param name="expression">The cron expression to use.</param>
    public CronTimer(string expression)
        : this(expression, TimeZoneInfo.Utc)
    {
    }

    /// <summary>Initializes the timer with the given cron expression and time zone.</summary>
    /// <param name="expression">The cron expression to use.</param>
    /// <param name="zone">The time zone to use.</param>
    public CronTimer(string expression, TimeZoneInfo zone)
        : this(CronExpression.Parse(expression), zone)
    {
    }

    /// <summary>Initializes the timer with the given cron expression and <see cref="TimeZoneInfo.Utc"/> zone.</summary>
    /// <param name="expression">The cron expression to use. Use <see cref="CronExpression.Parse(string, CronFormat)"/> for non-standard cron expressions.</param>
    public CronTimer(CronExpression expression)
        : this(expression, TimeZoneInfo.Utc)
    {
    }

    /// <summary>Initializes the timer with the given cron expression and time zone.</summary>
    /// <param name="expression">The cron expression to use. Use <see cref="CronExpression.Parse(string, CronFormat)"/> for non-standard cron expressions.</param>
    /// <param name="zone">The time zone to use.</param>
    public CronTimer(CronExpression expression, TimeZoneInfo zone)
    {
        _expression = expression;
        _zone = zone;
        _state = new();
        _timer = new(s => ((State)s!).Signal(), _state, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Wait for the next tick of the timer, or for the timer to be stopped.</summary>
    /// <param name="cancellationToken">
    /// A <see cref="CancellationToken"/> to use to cancel the asynchronous wait. If cancellation is requested, it affects only the single wait operation;
    /// the underlying timer continues firing.
    /// </param>
    /// <returns>A task that will be completed due to the timer firing, <see cref="Dispose"/> being called to stop the timer, or cancellation being requested.</returns>
    /// <remarks>
    /// <see cref="WaitForNextTickAsync"/> may only be used by one consumer at a time, and may be used concurrently with a single call to <see cref="Dispose"/>.
    /// </remarks>
    public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var next = _expression.GetNextOccurrence(now, _zone);

        if (next is null)
        {
            return new(false);
        }

        var delay = next.Value - now;

        lock (_timer)
        {
            if (_canceled)
            {
                return new(false);
            }

            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }

        return _state.WaitForNextTickAsync(this, cancellationToken);
    }

    /// <summary>Stops the timer and releases associated managed resources.</summary>
    /// <remarks>
    /// <see cref="Dispose"/> will cause an active wait with <see cref="WaitForNextTickAsync"/> to complete with a value of false.
    /// All subsequent <see cref="WaitForNextTickAsync"/> invocations will produce a value of false.
    /// </remarks>
    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (_timer != null)
        {
            lock (_timer)
            {
                if (!_canceled)
                {
                    _canceled = true;
                    _timer.Dispose();
                }
            }
        }

        _state?.Signal(stopping: true);
    }

    ~CronTimer() => Dispose();

    /// <summary>Core implementation for the cron timer.</summary>
    private sealed class State : IValueTaskSource<bool>
    {
        /// <summary>The associated <see cref="CronTimer" />.</summary>
#pragma warning disable IDE0052 // Remove unread private members
        private CronTimer? _owner;
#pragma warning restore IDE0052 // Remove unread private members
        /// <summary>Core of the <see cref="IValueTaskSource{TResult}" /> implementation.</summary>
        private ManualResetValueTaskSourceCore<bool> _mrvtsc;
        /// <summary>Cancellation registration for any active <see cref="WaitForNextTickAsync" /> call.</summary>
        private CancellationTokenRegistration _ctr;
        /// <summary>Whether the timer has been stopped.</summary>
        private bool _stopped;
        /// <summary>Whether there's a pending notification to be received. This could be due to the timer firing, the timer being stopped, or cancellation being requested.</summary>
        private bool _signaled;
        /// <summary>Whether there's a <see cref="WaitForNextTickAsync" /> call in flight.</summary>
        private bool _activeWait;

        /// <summary>Wait for the next tick of the timer, or for the timer to be stopped.</summary>
        public ValueTask<bool> WaitForNextTickAsync(CronTimer owner, CancellationToken cancellationToken)
        {
            lock (this)
            {
                if (_activeWait)
                {
                    // WaitForNextTickAsync should only be used by one consumer at a time. Failing to do so is an error.
                    throw new InvalidOperationException("Operation is not valid due to the current state of the object.");
                }

                // If cancellation has already been requested, short-circuit.
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromCanceled<bool>(cancellationToken);
                }

                // If the timer has a pending tick or has been stopped, we can complete synchronously.
                if (_signaled)
                {
                    // Reset the signal for subsequent consumers, but only if we're not stopped. Since
                    // stopping the timer is one way, any subsequent calls should also complete synchronously
                    // with false, and thus we leave _signaled pinned at true.
                    if (!_stopped)
                    {
                        _signaled = false;
                    }

                    return new(!_stopped);
                }

                Debug.Assert(!_stopped, "Unexpectedly stopped without _signaled being true.");

                // Set up for the wait and return a task that will be signaled when the
                // timer fires, stop is called, or cancellation is requested.
                _owner = owner;
                _activeWait = true;
                _ctr = cancellationToken.UnsafeRegister(static (state, cancellationToken) => ((State)state!).Signal(cancellationToken: cancellationToken), this);

                return new(this, _mrvtsc.Version);
            }
        }

        /// <summary>Signal that the timer has either fired or been stopped.</summary>
        public void Signal(bool stopping = false, CancellationToken cancellationToken = default)
        {
            bool completeTask = false;

            lock (this)
            {
                _stopped |= stopping;
                if (!_signaled)
                {
                    _signaled = true;
                    completeTask = _activeWait;
                }
            }

            if (completeTask)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // If cancellation is requested just before the UnsafeRegister call, it's possible this will end up being invoked
                    // as part of the WaitForNextTickAsync call and thus as part of holding the lock.  The goal of completeTask
                    // was to escape that lock, so that we don't invoke any synchronous continuations from the ValueTask as part
                    // of completing _mrvtsc.  However, in that case, we also haven't returned the ValueTask to the caller, so there
                    // won't be any continuations yet, which makes this safe.
                    _mrvtsc.SetException(ExceptionDispatchInfo.SetCurrentStackTrace(new OperationCanceledException(cancellationToken)));
                }
                else
                {
                    Debug.Assert(!Monitor.IsEntered(this));
                    _mrvtsc.SetResult(true);
                }
            }
        }

        /// <inheritdoc/>
        bool IValueTaskSource<bool>.GetResult(short token)
        {
            // Dispose of the cancellation registration.  This is done outside of the below lock in order
            // to avoid a potential deadlock due to waiting for a concurrent cancellation callback that might
            // in turn try to take the lock.  For valid usage, GetResult is only called once _ctr has been
            // successfully initialized before WaitForNextTickAsync returns to its synchronous caller, and
            // there should be no race conditions accessing it, as concurrent consumption is invalid. If there
            // is invalid usage, with GetResult used erroneously/concurrently, the worst that happens is cancellation
            // may not take effect for the in-flight operation, with its registration erroneously disposed.
            // Note we use Dispose rather than Unregister (which wouldn't risk deadlock) so that we know that thecancellation callback associated with this operation
            // won't potentially still fire after we've completed this GetResult and a new operation
            // has potentially started.
            _ctr.Dispose();

            lock (this)
            {
                try
                {
                    _mrvtsc.GetResult(token);
                }
                finally
                {
                    _mrvtsc.Reset();
                    _ctr = default;
                    _activeWait = false;
                    _owner = null;
                    if (!_stopped)
                    {
                        _signaled = false;
                    }
                }

                return !_stopped;
            }
        }

        /// <inheritdoc/>
        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _mrvtsc.GetStatus(token);

        /// <inheritdoc/>
        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _mrvtsc.OnCompleted(continuation, state, token, flags);
    }
}
