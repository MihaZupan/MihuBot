namespace MihuBot.Audio;

public sealed class AudioScheduler : IAsyncDisposable
{
    private readonly Queue<IAudioSource> _queue = new();
    private int _bitrateHintKbit = GlobalAudioSettings.MinBitrateKb;

    private IAudioSource _current;
    private TaskCompletionSource _skipTcs;
    private TaskCompletionSource _enqueueTcs;

    public void SetBitrate(int bitrateHintKb)
    {
        _bitrateHintKbit = bitrateHintKb;
    }

    public int QueueLength => _queue.Count;

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

    public void Enqueue(IAudioSource audioSource)
    {
        lock (_queue)
        {
            if (_queue.Count == 0)
            {
                audioSource.StartInitializing(_bitrateHintKbit);
            }

            _queue.Enqueue(audioSource);

            if (_enqueueTcs is not null)
            {
                _enqueueTcs.SetResult();
                _enqueueTcs = null;
            }
        }
    }

    public async Task SkipAsync()
    {
        IAudioSource toDispose = null;

        lock (_queue)
        {
            if (_skipTcs is not null)
            {
                _skipTcs.SetResult();
                _skipTcs = null;
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
                }
            }
        }

        if (toDispose is not null)
        {
            await toDispose.DisposeAsync();
        }
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

            lock (_queue)
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
                        _enqueueTcs = enqueueTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }

                if (candidate is not null)
                {
                    Task<bool> initializedTask = candidate.EnsureInitializedAsync();
                    if (initializedTask.IsCompletedSuccessfully && initializedTask.Result)
                    {
                        _current = candidate;
                        return candidate;
                    }

                    _skipTcs = skipTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            if (enqueueTcs is not null)
            {
                await enqueueTcs.Task.WaitAsync(cancellationToken);
                continue;
            }

            try
            {
                Task<bool> initializedTask = candidate.EnsureInitializedAsync();

                await Task.WhenAny(initializedTask, skipTcs.Task).WaitAsync(cancellationToken);

                lock (_queue)
                {
                    _skipTcs = null;

                    if (!skipTcs.Task.IsCompleted && initializedTask.Result)
                    {
                        _current = candidate;
                        return candidate;
                    }
                }

                await candidate.DisposeAsync();
                continue;
            }
            catch
            {
                await candidate.DisposeAsync();
                throw;
            }
        }
    }

    public bool TryPeekCurrent(out IAudioSource audioSource)
    {
        audioSource = _current;
        return audioSource is not null;
    }

    public async ValueTask DisposeAsync()
    {
        IAudioSource[] toDispose;
        lock (_queue)
        {
            if (_current is not null)
            {
                _queue.Enqueue(_current);
                _current = null;
            }

            toDispose = _queue.ToArray();
            _queue.Clear();
        }

        foreach (IAudioSource audioSource in toDispose)
        {
            await audioSource.DisposeAsync();
        }
    }
}