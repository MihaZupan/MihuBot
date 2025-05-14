namespace MihuBot.Helpers;

public static class TaskHelper
{
    public static Task<T> WaitAsyncAndSupressNotObserved<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted || !cancellationToken.IsCancellationRequested)
        {
            return task;
        }

        _ = task.ContinueWith(static (t, _) => _ = t.Exception, null, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        return task.WaitAsync(cancellationToken);
    }

    public static ValueTask<T> WaitAsyncAndSupressNotObserved<T>(this ValueTask<T> task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted || !cancellationToken.IsCancellationRequested)
        {
            return task;
        }

        Task<T> asTask = WaitAsyncAndSupressNotObserved(task.AsTask(), cancellationToken);
        return new ValueTask<T>(asTask);
    }
}
