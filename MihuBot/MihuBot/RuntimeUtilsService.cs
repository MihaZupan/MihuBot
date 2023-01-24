using Octokit;
using System.Runtime.CompilerServices;

namespace MihuBot
{
    public sealed class RuntimeUtilsJob
    {
        private const string IssueRepositoryOwner = "MihuBot";
        private const string IssueRepositoryName = "runtime-utils";

        private readonly Logger _logger;
        private readonly GitHubClient _github;
        private readonly RollingLog _logs = new(50_000);
        private string _corelibDiffs;
        private string _frameworkDiffs;

        public Stopwatch Stopwatch { get; private set; } = new();
        public PullRequest PullRequest { get; private set; }
        public Issue TrackingIssue { get; private set; }
        public bool Completed { get; private set; }

        public string JobId { get; private set; }
        public string ExternalId { get; private set; }

        public string ProgressUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/api/RuntimeUtils/Jobs/Progress/{ExternalId}";

        public RuntimeUtilsJob(Logger logger, GitHubClient github, PullRequest pullRequest, string jobId, string externalId)
        {
            _logger = logger;
            _github = github;
            JobId = jobId;
            ExternalId = externalId;
            PullRequest = pullRequest;
        }

        public async Task RunJobAsync()
        {
            Stopwatch.Start();

            LogsReceived(new[] { "Starting ..." });

            TrackingIssue = await _github.Issue.Create(
                IssueRepositoryOwner,
                IssueRepositoryName,
                new NewIssue($"[{PullRequest.User.Login}] {PullRequest.Title}")
            {
                Body = $"Build is in progress - see {ProgressUrl}"
            });

            return;

            try
            {
                await Task.Delay(30_000);

                Stopwatch.Stop();

                await UpdateIssueBodyAsync(
                    $"[Build]({ProgressUrl}) completed in {GetElapsedTime()}.\n\n" +
                    $"### CoreLib diffs\n\n" +
                    $"```\n" +
                    $"{_corelibDiffs}\n" +
                    $"```\n" +
                    $"\n\n" +
                    $"### Frameworks diffs\n\n" +
                    $"```\n" +
                    $"{_frameworkDiffs}\n" +
                    $"```\n");
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync(ex.ToString());

                await UpdateIssueBodyAsync( $"Something went wrong with the [Build]({ProgressUrl}) :man_shrugging:");
            }
            finally
            {
                Completed = true;
            }
        }

        public string GetElapsedTime()
        {
            TimeSpan elapsed = Stopwatch.Elapsed;

            int minutes = elapsed.Minutes;
            string minutesStr = $"{minutes} minute{GetPlural(minutes)}";

            if (elapsed.TotalHours >= 1)
            {
                int hours = (int)elapsed.TotalHours;
                return $"{hours} hour{GetPlural(hours)} {minutesStr}";
            }

            return minutesStr;

            static string GetPlural(int num)
            {
                return num == 1 ? "" : "s";
            }
        }

        private async Task UpdateIssueBodyAsync(string newBody)
        {
            IssueUpdate update = TrackingIssue.ToUpdate();
            update.Body = newBody;
            await _github.Issue.Update(IssueRepositoryOwner, IssueRepositoryName, TrackingIssue.Number, update);
        }

        public void LogsReceived(string[] lines)
        {
            _logs.AddLines(lines);
        }

        public async Task ArtifactReceivedAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream(new byte[128 * 1024]);
            await contentStream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            if (fileName == "diff-corelib.txt")
            {
                _corelibDiffs = Encoding.UTF8.GetString(buffer.ToArray());
            }
            else if (fileName == "diff-frameworks.txt")
            {
                _frameworkDiffs = Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        public async Task StreamLogsAsync(StreamWriter writer, CancellationToken cancellationToken)
        {
            await foreach (string line in StreamLogsAsync(cancellationToken))
            {
                if (line is null)
                {
                    await writer.FlushAsync();
                }
                else
                {
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                }
            }
        }

        public async IAsyncEnumerable<string> StreamLogsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            int position = 0;
            int cooldown = 100;
            string[] lines = new string[100];

            while (!cancellationToken.IsCancellationRequested)
            {
                int read = _logs.Get(lines, ref position);

                for (int i = 0; i < read; i++)
                {
                    yield return lines[i];
                }

                lines.AsSpan(0, read).Clear();

                if (read > 0)
                {
                    cooldown = 0;
                    yield return null;
                }
                else
                {
                    if (Completed)
                    {
                        break;
                    }

                    cooldown = Math.Clamp(cooldown + 10, 100, 1000);
                    await Task.Delay(cooldown, cancellationToken);
                }
            }
        }
    }

    public sealed class RuntimeUtilsService
    {
        private readonly Logger _logger;
        private readonly GitHubClient _github;

        public RuntimeUtilsService(Logger logger, GitHubClient github)
        {
            _logger = logger;
            _github = github;
        }

        private readonly Dictionary<string, RuntimeUtilsJob> _jobs = new(StringComparer.Ordinal);

        public bool TryGetJob(string jobId, bool publicId, out RuntimeUtilsJob job)
        {
            lock (_jobs)
            {
                return _jobs.TryGetValue(jobId, out job) &&
                    (publicId ? job.ExternalId : job.JobId) == jobId;
            }
        }

        public RuntimeUtilsJob StartJob(PullRequest pullRequest)
        {
            string jobId = Guid.NewGuid().ToString("N");
            string externalId = Guid.NewGuid().ToString("N");

            var job = new RuntimeUtilsJob(_logger, _github, pullRequest, jobId, externalId);

            lock (_jobs)
            {
                _jobs.Add(jobId, job);
                _jobs.Add(externalId, job);
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromDays(1));

                lock (_jobs)
                {
                    _jobs.Remove(jobId);
                    _jobs.Remove(externalId);
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await job.RunJobAsync();
                }
                catch (Exception ex)
                {
                    await _logger.DebugAsync(ex.ToString());
                }
            });

            return job;
        }
    }
}
