namespace MihuBot.Discord.Audio;

public sealed class AudioScheduler : IAsyncDisposable
{
    private readonly Queue<IAudioSource> _queue = new();
    private int _bitrateHintKbit = GlobalAudioSettings.MinBitrateKb;

    private IAudioSource _current;
    private TaskCompletionSource _skipTcs;
    private TaskCompletionSource _enqueueTcs;
    private IAudioSource _autoplay;
    private long _autoplayRevision;
    private long _revision;
    private bool _disposed;
    private IAudioSource _initializing;
    private Task _disposeTask;
    private long _lastSkipAction;

    public long Revision
    {
        get
        {
            lock (_queue)
            {
                return _revision;
            }
        }
    }

    public bool HasAutoplay
    {
        get
        {
            lock (_queue)
            {
                return _autoplay is not null;
            }
        }
    }

    public bool GetAutoplayState(out IAudioSource current, out long revision)
    {
        lock (_queue)
        {
            current = _current;
            revision = _revision;
            return !_disposed && _queue.Count == 0 && _skipTcs is null;
        }
    }

    public void SetBitrate(int bitrateHintKb)
    {
        _bitrateHintKbit = bitrateHintKb;
    }

    public int QueueLength
    {
        get
        {
            lock (_queue)
            {
                return _queue.Count;
            }
        }
    }

    public IAudioSource[] GetQueueSnapshot(int limit, out IAudioSource currentSource)
    {
        lock (_queue)
        {
            currentSource = _current;

            if ((uint)_queue.Count < (uint)limit)
            {
                return _queue.ToArray();
            }

            return _queue.Take(limit).ToArray();
        }
    }

    internal IAudioSource[] GetPlaybackSnapshot()
    {
        lock (_queue)
        {
            List<IAudioSource> sources = [];

            if ((_current ?? _initializing) is { } first)
            {
                sources.Add(first);
            }

            sources.AddRange(_queue);

            if (sources.Count == 0 && _autoplay is not null && _autoplayRevision == _revision)
            {
                sources.Add(_autoplay);
            }

            return sources.ToArray();
        }
    }

    internal bool WasSkippedSince(long spokenAt)
    {
        lock (_queue)
        {
            return spokenAt <= _lastSkipAction;
        }
    }

    internal (IAudioSource Current, IAudioSource[] Queue) GetEditableQueueSnapshot()
    {
        lock (_queue)
        {
            return (_current ?? _initializing ?? (_autoplayRevision == _revision ? _autoplay : null), _queue.ToArray());
        }
    }

    internal async Task ReplaceQueueAsync(IAudioSource[] expected, IAudioSource[] replacement)
    {
        IAudioSource[] removed;

        lock (_queue)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_queue.SequenceEqual(expected, ReferenceEqualityComparer.Instance))
            {
                throw new InvalidOperationException("The queue changed before it could be edited. Please repeat your request.");
            }

            if (replacement.Distinct(ReferenceEqualityComparer.Instance).Count() != replacement.Length ||
                replacement.Any(source => !expected.Contains(source, ReferenceEqualityComparer.Instance)))
            {
                throw new InvalidOperationException("A queue edit can only reorder or remove upcoming tracks.");
            }

            if (expected.SequenceEqual(replacement, ReferenceEqualityComparer.Instance))
            {
                return;
            }

            removed = expected.Where(source => !replacement.Contains(source, ReferenceEqualityComparer.Instance)).ToArray();
            _queue.Clear();

            foreach (IAudioSource source in replacement)
            {
                _queue.Enqueue(source);
            }

            _revision++;

            if (_queue.TryPeek(out IAudioSource next))
            {
                next.StartInitializing(_bitrateHintKbit);
            }

            SignalEnqueue();
        }

        await DisposeSourcesAsync(removed);
    }

    internal AudioSkipBatch SkipTracks(int count, long spokenAt, IAudioSource target = null, Action<IAudioSource> onSkippingCurrent = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        lock (_queue)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (WasSkippedSince(spokenAt))
            {
                return new(0, null, [], Ignored: true);
            }

            IAudioSource[] snapshot = GetPlaybackSnapshot();
            int toSkip = target is null ? Math.Min(count, snapshot.Length) : Array.FindIndex(snapshot, source => ReferenceEquals(source, target));

            if (toSkip < 0)
            {
                return new(0, null, [], TargetMissing: true);
            }

            if (toSkip == 0)
            {
                return new(0, null, []);
            }

            int remaining = toSkip;
            IAudioSource current = _current;
            List<IAudioSource> dispose = [];

            if (current is not null)
            {
                onSkippingCurrent?.Invoke(current);
                _current = null;
                dispose.Add(current);
                remaining--;
            }
            else if (_initializing is not null)
            {
                // The pending selector owns disposal of its canceled initialization candidate.
                _skipTcs?.TrySetResult();
                _skipTcs = null;
                _initializing = null;
                remaining--;
            }

            while (remaining > 0 && _queue.TryDequeue(out IAudioSource source))
            {
                dispose.Add(source);
                remaining--;
            }

            if (_autoplay is not null)
            {
                dispose.Add(_autoplay);
                _autoplay = null;
            }

            _revision++;
            _lastSkipAction = Stopwatch.GetTimestamp();

            if (_queue.TryPeek(out IAudioSource next))
            {
                next.StartInitializing(_bitrateHintKbit);
            }

            SignalEnqueue();
            return new(toSkip, current, dispose.Distinct<IAudioSource>(ReferenceEqualityComparer.Instance).ToArray());
        }
    }

    public void Enqueue(IAudioSource audioSource)
    {
        lock (_queue)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _revision++;

            if (_queue.Count == 0)
            {
                audioSource.StartInitializing(_bitrateHintKbit);
            }

            _queue.Enqueue(audioSource);

            SignalEnqueue();
        }
    }

    public bool TrySetAutoplay(IAudioSource source, long revision)
    {
        lock (_queue)
        {
            if (_disposed || _revision != revision || _queue.Count != 0 || _skipTcs is not null || _autoplay is not null)
            {
                return false;
            }

            _autoplay = source;
            _autoplayRevision = revision;
            source.StartInitializing(_bitrateHintKbit);
            SignalEnqueue();
            return true;
        }
    }

    public async ValueTask ClearAutoplayAsync(bool invalidate = false)
    {
        IAudioSource source;

        lock (_queue)
        {
            if (invalidate)
            {
                _revision++;
            }

            source = _autoplay;
            _autoplay = null;
            SignalEnqueue();
        }

        if (source is not null)
        {
            await DisposeSourceAsync(source);
        }
    }

    private void SignalEnqueue()
    {
        _enqueueTcs?.TrySetResult();
        _enqueueTcs = null;
    }

    public async Task SkipAsync()
    {
        IAudioSource toDispose = null;

        lock (_queue)
        {
            if (_skipTcs is not null || _current is not null || _queue.Count > 0 || _autoplay is not null)
            {
                _lastSkipAction = Stopwatch.GetTimestamp();
            }

            _revision++;

            if (_skipTcs is not null)
            {
                _skipTcs.SetResult();
                _skipTcs = null;
                _initializing = null;
            }
            else
            {
                if (_current is not null)
                {
                    toDispose = _current;
                    _current = null;
                }
                else
                {
                    if (_queue.TryDequeue(out toDispose))
                    {
                        if (_queue.TryPeek(out IAudioSource nextNext))
                        {
                            nextNext.StartInitializing(_bitrateHintKbit);
                        }
                    }
                    else
                    {
                        toDispose = _autoplay;
                        _autoplay = null;
                    }
                }
            }

            SignalEnqueue();
        }

        if (toDispose is not null)
        {
            await DisposeSourceAsync(toDispose);
        }
    }

    public async Task<bool> TrySkipAsync(IAudioSource expectedSource)
    {
        lock (_queue)
        {
            if (!ReferenceEquals(_current, expectedSource) || _current is null)
            {
                return false;
            }

            _current = null;
            _lastSkipAction = Stopwatch.GetTimestamp();
            _revision++;
            SignalEnqueue();
        }

        await DisposeSourceAsync(expectedSource);
        return true;
    }

    public void SkipCurrent(IAudioSource sourceToSkip)
    {
        lock (_queue)
        {
            if (ReferenceEquals(_current, sourceToSkip))
            {
                _current = null;
            }
        }
    }

    public async ValueTask<IAudioSource> GetAudioSourceAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TaskCompletionSource enqueueTcs = null;
            TaskCompletionSource skipTcs = null;
            IAudioSource candidate = null;
            IAudioSource staleAutoplay = null;
            Task<bool> autoplayInitialization = null;
            bool ready = false;

            lock (_queue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_autoplay is not null && _autoplayRevision != _revision)
                {
                    staleAutoplay = _autoplay;
                    _autoplay = null;
                }

                if (staleAutoplay is null)
                {
                    candidate = _current;

                    if (candidate is null)
                    {
                        if (_queue.TryDequeue(out candidate))
                        {
                            if (_queue.TryPeek(out IAudioSource nextNext))
                            {
                                nextNext.StartInitializing(_bitrateHintKbit);
                            }
                        }
                        else
                        {
                            if (_autoplay is not null)
                            {
                                autoplayInitialization = _autoplay.EnsureInitializedAsync();

                                if (autoplayInitialization.IsCompleted)
                                {
                                    if (autoplayInitialization.IsCompletedSuccessfully && autoplayInitialization.Result)
                                    {
                                        candidate = _autoplay;
                                    }
                                    else
                                    {
                                        staleAutoplay = _autoplay;
                                    }

                                    _autoplay = null;
                                    autoplayInitialization = null;
                                }
                            }

                            if (candidate is null)
                            {
                                _enqueueTcs = enqueueTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            }
                        }
                    }

                    if (candidate is not null)
                    {
                        Task<bool> initializedTask = candidate.EnsureInitializedAsync();
                        if (initializedTask.IsCompletedSuccessfully && initializedTask.Result)
                        {
                            if (!ReferenceEquals(_current, candidate))
                            {
                                _revision++;
                            }

                            _current = candidate;
                            ready = true;
                        }
                        else
                        {
                            _initializing = candidate;
                            _skipTcs = skipTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                    }
                }
            }

            if (staleAutoplay is not null)
            {
                await DisposeSourceAsync(staleAutoplay);
                continue;
            }

            if (ready)
            {
                return candidate;
            }

            if (enqueueTcs is not null)
            {
                if (autoplayInitialization is not null)
                {
                    await Task.WhenAny(autoplayInitialization, enqueueTcs.Task).WaitAsync(cancellationToken);
                }
                else
                {
                    await enqueueTcs.Task.WaitAsync(cancellationToken);
                }

                continue;
            }

            try
            {
                Task<bool> initializedTask = candidate.EnsureInitializedAsync();

                await Task.WhenAny(initializedTask, skipTcs.Task).WaitAsync(cancellationToken);
                bool initialized = !skipTcs.Task.IsCompleted && await initializedTask;

                lock (_queue)
                {
                    _skipTcs = null;
                    _initializing = null;

                    if (!_disposed && !skipTcs.Task.IsCompleted && initialized)
                    {
                        _current = candidate;
                        _revision++;
                        return candidate;
                    }
                }

                await DisposeSourceAsync(candidate);

                if (!skipTcs.Task.IsCompleted && !initialized)
                {
                    throw new AudioSourceException(candidate, new InvalidOperationException("Audio initialization failed."));
                }

                continue;
            }
            catch (Exception ex)
            {
                lock (_queue)
                {
                    _skipTcs = null;
                    _initializing = null;
                }

                var cleanup = new AsyncCleanup();
                await cleanup.RunAsync(() => candidate.DisposeAsync().AsTask());

                try
                {
                    cleanup.ThrowIfFailed();
                }
                catch (Exception cleanupException)
                {
                    throw new AudioSourceException(candidate, new AggregateException(ex, cleanupException));
                }

                if (cancellationToken.IsCancellationRequested || ex is AudioSourceException)
                {
                    throw;
                }

                throw new AudioSourceException(candidate, ex);
            }
        }
    }

    public bool TryPeekCurrent(out IAudioSource audioSource)
    {
        lock (_queue)
        {
            audioSource = _current;
            return audioSource is not null;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_queue)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            SignalEnqueue();
            _skipTcs?.TrySetResult();

            if (_autoplay is not null)
            {
                _queue.Enqueue(_autoplay);
                _autoplay = null;
            }

            if (_current is not null)
            {
                _queue.Enqueue(_current);
                _current = null;
            }

            if (_initializing is not null)
            {
                _queue.Enqueue(_initializing);
                _initializing = null;
            }

            IAudioSource[] toDispose = _queue.Distinct<IAudioSource>(ReferenceEqualityComparer.Instance).ToArray();
            _queue.Clear();
            _disposeTask = Task.Run(() => DisposeSourcesAsync(toDispose));
            return new ValueTask(_disposeTask);
        }
    }

    internal static async Task DisposeSourcesAsync(IAudioSource[] toDispose)
    {
        var cleanup = new AsyncCleanup();

        foreach (IAudioSource audioSource in toDispose)
        {
            await cleanup.RunAsync(() => audioSource.DisposeAsync().AsTask());
        }

        cleanup.ThrowIfFailed();
    }

    private static async Task DisposeSourceAsync(IAudioSource source)
    {
        try
        {
            await source.DisposeAsync();
        }
        catch (Exception ex)
        {
            throw new AudioSourceException(source, ex);
        }
    }
}

internal sealed record AudioSkipBatch(int Count, IAudioSource Current, IAudioSource[] ToDispose, bool Ignored = false, bool TargetMissing = false);

internal sealed class AudioSourceException(IAudioSource source, Exception innerException)
    : Exception($"Audio source '{source.Description}' failed.", innerException)
{
    public IAudioSource AudioSource { get; } = source;
}
