#nullable enable

namespace MihuBot.Helpers;

/// <summary>
/// Controls how <see cref="PeriodicTask"/> runs a periodic loop.
/// </summary>
public sealed record PeriodicTaskOptions
{
    /// <summary>How long to wait between iterations.</summary>
    public required TimeSpan Interval { get; init; }

    /// <summary>Run the first iteration right away instead of waiting for <see cref="Interval"/> first.</summary>
    public bool RunImmediately { get; init; }

    /// <summary>
    /// Extra delay after a failed iteration, multiplied by the number of consecutive failures.
    /// Set to <see cref="TimeSpan.Zero"/> to keep running on the normal schedule.
    /// </summary>
    public TimeSpan FailureBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound for the delay produced by <see cref="FailureBackoff"/>.</summary>
    public TimeSpan MaxFailureBackoff { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Optional extra delay to apply for a specific exception, on top of <see cref="FailureBackoff"/>
    /// (e.g. honoring a rate limit's retry-after).
    /// </summary>
    public Func<Exception, TimeSpan>? AdditionalFailureDelay { get; init; }
}

/// <summary>
/// Runs an operation on a timer, with shared retry/backoff/logging behavior.
/// A failing iteration never stops the loop.
/// </summary>
/// <remarks>
/// Use <see cref="PeriodicBackgroundService"/> instead when the loop is the entire service.
/// </remarks>
public static class PeriodicTask
{
    /// <summary>
    /// Report a failure to Discord once exactly this many iterations have failed in a row.
    /// Every failure is always written to the debug log, this only controls the louder notification.
    /// </summary>
    private const int AlertAfterConsecutiveFailures = 2;

    /// <summary>Starts the loop on a background task without flowing the current <see cref="ExecutionContext"/>.</summary>
    public static void Start(string name, PeriodicTaskOptions options, Logger logger, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        using AsyncFlowControl flowControl = ExecutionContext.SuppressFlow();

        _ = Task.Run(() => RunAsync(name, options, logger, action, cancellationToken), CancellationToken.None);
    }

    /// <summary>Runs the loop until <paramref name="cancellationToken"/> is cancelled.</summary>
    public static async Task RunAsync(string name, PeriodicTaskOptions options, Logger logger, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Interval, TimeSpan.Zero);

        try
        {
            await Task.Yield();

            using var timer = new PeriodicTimer(options.Interval);

            int consecutiveFailureCount = 0;
            bool runNow = options.RunImmediately;

            while (runNow || await timer.WaitForNextTickAsync(cancellationToken))
            {
                runNow = false;

                try
                {
                    await action(cancellationToken);

                    consecutiveFailureCount = 0;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    consecutiveFailureCount++;

                    string errorMessage = $"{name}: ({consecutiveFailureCount}): {ex}";

                    logger.DebugLog(errorMessage);

                    if (consecutiveFailureCount == AlertAfterConsecutiveFailures)
                    {
                        try
                        {
                            await logger.DebugAsync(errorMessage, truncateToFile: true);
                        }
                        catch (Exception alertEx)
                        {
                            Console.WriteLine($"{name}: failed to report a failure: {alertEx}");
                        }
                    }

                    TimeSpan backoff = options.FailureBackoff * consecutiveFailureCount;
                    if (backoff > options.MaxFailureBackoff)
                    {
                        backoff = options.MaxFailureBackoff;
                    }

                    backoff += options.AdditionalFailureDelay?.Invoke(ex) ?? TimeSpan.Zero;

                    if (backoff > TimeSpan.Zero)
                    {
                        await Task.Delay(backoff, cancellationToken);
                    }
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            string message = $"{name}: the periodic task stopped unexpectedly: {ex}";

            Console.WriteLine(message);

            _ = logger.DebugAsync(message, truncateToFile: true);
        }
    }
}

/// <summary>
/// A <see cref="BackgroundService"/> whose work is a single <see cref="PeriodicTask"/> loop.
/// </summary>
public abstract class PeriodicBackgroundService : BackgroundService
{
    private readonly Logger _logger;

    protected PeriodicTaskOptions Options { get; }

    /// <summary>Name used in log messages.</summary>
    protected virtual string Name => GetType().Name;

    protected PeriodicBackgroundService(PeriodicTaskOptions options, Logger logger)
    {
        Options = options;
        _logger = logger;
    }

    /// <summary>Runs once per <see cref="PeriodicTaskOptions.Interval"/>. Throwing only fails that one iteration.</summary>
    protected abstract Task RunIterationAsync(CancellationToken cancellationToken);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return PeriodicTask.RunAsync(Name, Options, _logger, RunIterationAsync, stoppingToken);
    }
}
