namespace MihuBot.Helpers;

public static class TaskHelper
{
    public static async Task<T> WaitAsyncAndSupressNotObserved<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        _ = task.ContinueWith(static (t, _) => _ = t.Exception, null, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        return await task.WaitAsync(cancellationToken);
    }
}
