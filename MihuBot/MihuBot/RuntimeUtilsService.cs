using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using MihuBot.Configuration;
using Octokit;
using System.Runtime.CompilerServices;

namespace MihuBot
{
    public sealed class RuntimeUtilsJob
    {
        private const string IssueRepositoryOwner = "MihuBot";
        private const string IssueRepositoryName = "runtime-utils";

        private RuntimeUtilsService _parent;
        private readonly RollingLog _logs = new(50_000);
        private string _corelibDiffs;
        private string _frameworkDiffs;

        private Logger Logger => _parent.Logger;
        private GitHubClient Github => _parent.Github;
        private IConfiguration Configuration => _parent.Configuration;
        private IConfigurationService ConfigurationService => _parent.ConfigurationService;

        public Stopwatch Stopwatch { get; private set; } = new();
        public PullRequest PullRequest { get; private set; }
        public Issue TrackingIssue { get; private set; }
        public bool Completed { get; private set; }

        public string JobId { get; private set; }
        public string ExternalId { get; private set; }

        public string ProgressUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/api/RuntimeUtils/Jobs/Progress/{ExternalId}";
        public string ProgressDashboardUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/runtime-utils/{ExternalId}";

        public RuntimeUtilsJob(RuntimeUtilsService parent, PullRequest pullRequest, string jobId, string externalId)
        {
            _parent = parent;
            JobId = jobId;
            ExternalId = externalId;
            PullRequest = pullRequest;
        }

        public async Task RunJobAsync()
        {
            Stopwatch.Start();

            LogsReceived("Starting ...");

            if (!Program.AzureEnabled)
            {
                LogsReceived("No Azure support. Aborting ...");
                Completed = true;
                return;
            }

            TrackingIssue = await Github.Issue.Create(
                IssueRepositoryOwner,
                IssueRepositoryName,
                new NewIssue($"[{PullRequest.User.Login}] {PullRequest.Title}")
            {
                Body = $"Build is in progress - see {ProgressDashboardUrl}"
            });

            try
            {
                using var jobTimeoutCts = new CancellationTokenSource(TimeSpan.FromHours(5));
                var jobTimeout = jobTimeoutCts.Token;

                var armClient = new ArmClient(Program.AzureCredential);
                var subscription = await armClient.GetDefaultSubscriptionAsync(jobTimeout);
                var resourceGroup = (await subscription.GetResourceGroupAsync("runtime-utils", jobTimeout)).Value;

                var container = new ContainerInstanceContainer(
                    $"runner-{ExternalId}",
                    "runtimeutils.azurecr.io/runner:latest",
                    new ContainerResourceRequirements(new ContainerResourceRequestsContent(memoryInGB: 16, cpu: 4)));

                container.EnvironmentVariables.Add(new ContainerEnvironmentVariable("JOB_ID") { Value = JobId });
                container.EnvironmentVariables.Add(new ContainerEnvironmentVariable("JOB_PR_REPO") { Value = PullRequest.Head.Repository.FullName });
                container.EnvironmentVariables.Add(new ContainerEnvironmentVariable("JOB_PR_BRANCH") { Value = PullRequest.Head.Ref });

                var containerGroupData = new ContainerGroupData(
                    AzureLocation.EastUS2,
                    new[] { container },
                    ContainerInstanceOperatingSystemType.Linux)
                {
                    RestartPolicy = ContainerGroupRestartPolicy.Never
                };

                containerGroupData.ImageRegistryCredentials.Add(new ContainerGroupImageRegistryCredential("runtimeutils.azurecr.io")
                {
                    Username = "runtimeutils",
                    Password = Configuration["AzureContainerRegistry:Password"]
                });

                LogsReceived("Starting an Azure Container Instance ...");

                var containerGroups = resourceGroup.GetContainerGroups();
                var containerGroupResource = (await containerGroups.CreateOrUpdateAsync(WaitUntil.Completed, container.Name, containerGroupData, jobTimeout)).Value;

                try
                {
                    containerGroupResource = (await containerGroupResource.GetAsync(jobTimeout)).Value;

                    while (containerGroupResource.Data?.Containers?.FirstOrDefault()?.InstanceView?.CurrentState is { } currentState
                        && currentState.FinishOn is null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));

                        containerGroupResource = (await containerGroupResource.GetAsync(jobTimeout)).Value;
                    }
                }
                finally
                {
                    if (!ConfigurationService.TryGet(null, "RuntimeUtils.ShouldDeleteContainer", out string shouldDeleteStr) ||
                        !bool.TryParse(shouldDeleteStr, out bool shouldDelete) ||
                        shouldDelete)
                    {
                        LogsReceived("Deleting the container instance");
                        await containerGroupResource.DeleteAsync(WaitUntil.Completed, CancellationToken.None);
                    }
                    else
                    {
                        LogsReceived("Configuration opted not to delete the container instance");
                    }
                }

                Stopwatch.Stop();

                string corelibDiffs =
                    $"### CoreLib diffs\n\n" +
                    $"```\n" +
                    $"{_corelibDiffs}\n" +
                    $"```\n" +
                    $"\n\n";

                string frameworksDiffs =
                    $"### Frameworks diffs\n\n" +
                    $"```\n" +
                    $"{_frameworkDiffs}\n" +
                    $"```\n";

                await UpdateIssueBodyAsync(
                    $"[Build]({ProgressDashboardUrl}) completed in {GetElapsedTime()}.\n\n" +
                    (_corelibDiffs is not null ? corelibDiffs : "") +
                    (_frameworkDiffs is not null ? frameworksDiffs : ""));
            }
            catch (Exception ex)
            {
                await Logger.DebugAsync(ex.ToString());

                await UpdateIssueBodyAsync($"Something went wrong with the [Build]({ProgressDashboardUrl}) :man_shrugging:");
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
            await Github.Issue.Update(IssueRepositoryOwner, IssueRepositoryName, TrackingIssue.Number, update);
        }

        public void LogsReceived(string line)
        {
            LogsReceived(new[] { line });
        }

        public void LogsReceived(string[] lines)
        {
            _logs.AddLines(lines);
        }

        public async Task ArtifactReceivedAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream(new byte[128 * 1024]);
            await contentStream.CopyToAsync(buffer, cancellationToken);

            byte[] bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Position).ToArray();

            if (fileName == "diff-corelib.txt")
            {
                _corelibDiffs = Encoding.UTF8.GetString(bytes);
            }
            else if (fileName == "diff-frameworks.txt")
            {
                _frameworkDiffs = Encoding.UTF8.GetString(bytes);
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
            Stopwatch lastYield = Stopwatch.StartNew();

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
                    lastYield.Restart();
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

                    if (lastYield.Elapsed.TotalSeconds > 10)
                    {
                        lastYield.Restart();
                        yield return null;
                    }
                }
            }
        }
    }

    public sealed class RuntimeUtilsService
    {
        private readonly Dictionary<string, RuntimeUtilsJob> _jobs = new(StringComparer.Ordinal);

        public readonly Logger Logger;
        public readonly GitHubClient Github;
        public readonly IConfiguration Configuration;
        public readonly IConfigurationService ConfigurationService;

        public RuntimeUtilsService(Logger logger, GitHubClient github, IConfiguration configuration, IConfigurationService configurationService)
        {
            Logger = logger;
            Github = github;
            Configuration = configuration;
            ConfigurationService = configurationService;
        }

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

            var job = new RuntimeUtilsJob(this, pullRequest, jobId, externalId);

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
                    await Logger.DebugAsync(ex.ToString());
                }
            });

            return job;
        }
    }
}
