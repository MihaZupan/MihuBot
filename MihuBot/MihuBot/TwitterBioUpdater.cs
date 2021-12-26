using Tweetinvi;
using Tweetinvi.Parameters;

namespace MihuBot
{
    public sealed class TwitterBioUpdater : IHostedService
    {
        private readonly Logger _logger;
        private readonly ITwitterClient _twitter;
        private readonly CancellationTokenSource _cts;

        public TwitterBioUpdater(Logger logger, ITwitterClient twitter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _twitter = twitter ?? throw new ArgumentNullException(nameof(twitter));
            _cts = new CancellationTokenSource();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(async () =>
            {
                const int MaxFails = 10;
                int failCount = 0;

                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(90));
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    try
                    {
                        var start = new DateTime(2019, 11, 15, 8, 0, 0, DateTimeKind.Utc);
                        var utcNow = DateTime.UtcNow;
                        var minutesSpent = (int)utcNow.Subtract(start).TotalMinutes;

                        await _twitter.AccountSettings.UpdateProfileAsync(new UpdateProfileParameters
                        {
                            Description = $"Performance fan working on @dotnet @Microsoft for the past {minutesSpent} minutes"
                        });

                        failCount = 0;
                    }
                    catch (Exception ex)
                    {
                        failCount++;

                        string message = $"An exception was thrown while processing a Twitter update: {ex}";
                        if (failCount == 1)
                        {
                            await _logger.DebugAsync(message);
                        }
                        else
                        {
                            _logger.DebugLog(message);
                        }

                        if (failCount > MaxFails)
                        {
                            break;
                        }

                        await Task.Delay(TimeSpan.FromMinutes(Math.Pow(2, failCount)));
                    }
                }
            });

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }
    }
}
