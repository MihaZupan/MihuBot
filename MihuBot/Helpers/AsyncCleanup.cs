namespace MihuBot.Helpers;

internal sealed class AsyncCleanup
{
    private readonly List<Exception> _errors = [];

    public void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _errors.Add(ex);
        }
    }

    public async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _errors.Add(ex);
        }
    }

    public void ThrowIfFailed()
    {
        if (_errors.Count > 0)
        {
            throw new AggregateException("One or more cleanup operations failed.", _errors);
        }
    }
}
