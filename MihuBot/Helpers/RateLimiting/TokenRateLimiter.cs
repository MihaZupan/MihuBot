namespace MihuBot.Helpers.RateLimiting;

public sealed class TokenRateLimiter : IDisposable
{
    private readonly Lock _gate = new();
    private readonly int _tokenLimit;
    private readonly int _queueLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _timer;
    private readonly Queue<Reservation> _reservations = new();
    private readonly LinkedList<PendingReservation> _queue = new();
    private long _chargedTokens;
    private int _queuedTokens;
    private bool _disposed;

    public TokenRateLimiter(int tokenLimit, int queueLimit, TimeSpan? window = null)
        : this(tokenLimit, queueLimit, window ?? TimeSpan.FromMinutes(1), TimeProvider.System)
    {
    }

    internal TokenRateLimiter(int tokenLimit, int queueLimit, TimeSpan window, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokenLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(queueLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _tokenLimit = tokenLimit;
        _queueLimit = queueLimit;
        _window = window;
        _timeProvider = timeProvider;
        _timer = timeProvider.CreateTimer(static state => ((TokenRateLimiter)state).OnTimer(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Charges tokens until the reservation expires, even if Complete is never called.</summary>
    public async Task<Reservation> ReserveAsync(int tokens, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokens);
        cancellationToken.ThrowIfCancellationRequested();

        if (tokens > _tokenLimit)
        {
            throw new InvalidOperationException($"Requested token reservation of {tokens} exceeds the window budget of {_tokenLimit}.");
        }

        PendingReservation pending;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long now = _timeProvider.GetTimestamp();
            ExpireReservations(now);
            DrainQueue(now);

            if (_queue.Count == 0 && tokens <= _tokenLimit - _chargedTokens)
            {
                Reservation reservation = Acquire(tokens, now);
                ScheduleExpiration(now);
                return reservation;
            }

            ScheduleExpiration(now);

            if (tokens > _queueLimit - _queuedTokens)
            {
                throw new InvalidOperationException("Token rate limiter could not reserve the requested capacity.");
            }

            pending = new PendingReservation(this, tokens, cancellationToken);
            _queue.AddLast(pending.Node);
            _queuedTokens += tokens;
        }

        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(static state =>
        {
            var pending = (PendingReservation)state;
            pending.Limiter.Cancel(pending);
        }, pending);

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private Reservation Acquire(int tokens, long now)
    {
        var reservation = new Reservation(this, tokens, now);
        _reservations.Enqueue(reservation);
        _chargedTokens += tokens;
        return reservation;
    }

    private void Complete(Reservation reservation, int actualTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actualTokens);

        lock (_gate)
        {
            if (reservation.Completed)
            {
                throw new InvalidOperationException("Token reservation has already been completed.");
            }

            reservation.Completed = true;

            if (_disposed)
            {
                return;
            }

            long now = _timeProvider.GetTimestamp();
            ExpireReservations(now);

            if (!reservation.Expired)
            {
                _chargedTokens += (long)actualTokens - reservation.Tokens;
                reservation.Tokens = actualTokens;
            }

            DrainQueue(now);
            ScheduleExpiration(now);
        }
    }

    private void ExpireReservations(long now)
    {
        while (_reservations.TryPeek(out Reservation reservation) &&
            _timeProvider.GetElapsedTime(reservation.Timestamp, now) >= _window)
        {
            _reservations.Dequeue();
            _chargedTokens -= reservation.Tokens;
            reservation.Expired = true;
        }
    }

    private void DrainQueue(long now)
    {
        while (_queue.First is { Value: PendingReservation pending })
        {
            if (pending.CancellationToken.IsCancellationRequested)
            {
                RemovePending(pending);
                pending.Completion.SetCanceled(pending.CancellationToken);
                continue;
            }

            if (pending.Tokens > _tokenLimit - _chargedTokens)
            {
                break;
            }

            RemovePending(pending);
            pending.Completion.SetResult(Acquire(pending.Tokens, now));
        }
    }

    private void RemovePending(PendingReservation pending)
    {
        _queue.Remove(pending.Node);
        _queuedTokens -= pending.Tokens;
    }

    private void Cancel(PendingReservation pending)
    {
        lock (_gate)
        {
            if (pending.Node.List is null)
            {
                return;
            }

            RemovePending(pending);
            pending.Completion.SetCanceled(pending.CancellationToken);
            long now = _timeProvider.GetTimestamp();
            ExpireReservations(now);
            DrainQueue(now);
            ScheduleExpiration(now);
        }
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            long now = _timeProvider.GetTimestamp();
            ExpireReservations(now);
            DrainQueue(now);
            ScheduleExpiration(now);
        }
    }

    private void ScheduleExpiration(long now)
    {
        TimeSpan dueTime = Timeout.InfiniteTimeSpan;

        if (_reservations.TryPeek(out Reservation reservation))
        {
            TimeSpan remaining = _window - _timeProvider.GetElapsedTime(reservation.Timestamp, now);
            dueTime = TimeSpan.FromMilliseconds(Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, uint.MaxValue - 1));
        }

        _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _reservations.Clear();
            _chargedTokens = 0;

            while (_queue.First is { Value: PendingReservation pending })
            {
                RemovePending(pending);
                pending.Completion.SetException(new ObjectDisposedException(nameof(TokenRateLimiter)));
            }
        }

        _timer.Dispose();
    }

    public sealed class Reservation
    {
        private readonly TokenRateLimiter _limiter;

        internal long Timestamp { get; }
        internal int Tokens { get; set; }
        internal bool Expired { get; set; }
        internal bool Completed { get; set; }

        internal Reservation(TokenRateLimiter limiter, int tokens, long timestamp)
        {
            _limiter = limiter;
            Tokens = tokens;
            Timestamp = timestamp;
        }

        /// <summary>
        /// Replaces the reserved charge with actual usage exactly once. Zero refunds the full charge;
        /// higher usage creates debt. Corrections never affect capacity after the original expiry.
        /// </summary>
        public void Complete(int actualTokens) => _limiter.Complete(this, actualTokens);
    }

    private sealed class PendingReservation
    {
        public TokenRateLimiter Limiter { get; }
        public int Tokens { get; }
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource<Reservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<PendingReservation> Node { get; }

        public PendingReservation(TokenRateLimiter limiter, int tokens, CancellationToken cancellationToken)
        {
            Limiter = limiter;
            Tokens = tokens;
            CancellationToken = cancellationToken;
            Node = new(this);
        }
    }
}
