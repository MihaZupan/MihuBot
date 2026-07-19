namespace MihuBot.Helpers;

public static class TaskHelper
{
    public static Task<T> WaitAsyncAndSupressNotObserved<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            return task;
        }

        _ = task.ContinueWith(static (t, _) => _ = t.Exception, null, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        return task.WaitAsync(cancellationToken);
    }

    public static ValueTask<T> WaitAsyncAndSupressNotObserved<T>(this ValueTask<T> task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            return task;
        }

        Task<T> asTask = WaitAsyncAndSupressNotObserved(task.AsTask(), cancellationToken);
        return new ValueTask<T>(asTask);
    }

    public static void IgnoreExceptions(this Task task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            task.ContinueWith(
                static task => _ = task.Exception?.InnerException,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Current);
        }
    }
}
