using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.Resources;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MihuBot.Configuration;
using Octokit;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MihuBot
{
    public sealed class RuntimeUtilsJob
    {
        private const string IssueRepositoryOwner = "MihuBot";
        private const string IssueRepositoryName = "runtime-utils";

        private RuntimeUtilsService _parent;
        private readonly string _githubCommenterLogin;
        private readonly RollingLog _logs = new(50_000);
        private readonly List<(string FileName, string Url, long Size)> _artifacts = new();
        private long _artifactsCount;
        private long _totalArtifactsSize;
        private string _corelibDiffs;
        private string _frameworkDiffs;

        public bool FromGithubComment => _githubCommenterLogin is not null;

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

        public RuntimeUtilsJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string jobId, string externalId)
        {
            _parent = parent;
            JobId = jobId;
            ExternalId = externalId;
            PullRequest = pullRequest;
            _githubCommenterLogin = githubCommenterLogin;
        }

        private bool ShouldLinkToPR => GetConfigFlag("LinkToPR", true);

        private bool ShouldDeleteContainer => GetConfigFlag("ShouldDeleteContainer", true);

        private bool ShouldMentionJobInitiator => GetConfigFlag("ShouldMentionJobInitiator", true);

        private bool GetConfigFlag(string name, bool @default)
        {
            if (ConfigurationService.TryGet(null, $"RuntimeUtils.{name}", out string flagStr) &&
                bool.TryParse(flagStr, out bool flag))
            {
                return flag;
            }

            return @default;
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
                Body = $"Build is in progress - see {ProgressDashboardUrl}\n" + (FromGithubComment && ShouldLinkToPR ? $"{PullRequest.HtmlUrl}\n" : "")
            });

            try
            {
                using var jobTimeoutCts = new CancellationTokenSource(TimeSpan.FromHours(5));
                var jobTimeout = jobTimeoutCts.Token;

                var armClient = new ArmClient(Program.AzureCredential);
                var subscription = await armClient.GetDefaultSubscriptionAsync(jobTimeout);
                var resourceGroup = (await subscription.GetResourceGroupAsync("runtime-utils", jobTimeout)).Value;

                await RunContainerInstanceAsync(resourceGroup, jobTimeout);

                await ArtifactReceivedAsync("build-logs.txt", new MemoryStream(Encoding.UTF8.GetBytes(_logs.ToString())), jobTimeout);

                Stopwatch.Stop();

                string corelibDiffs =
                    $"### CoreLib diffs\n\n" +
                    $"```\n" +
                    $"{_corelibDiffs}\n" +
                    $"```\n" +
                    $"\n\n";

                string frameworksDiffs =
                    $"### Frameworks diffs\n\n" +
                    $"<details>\n<summary>Diffs</summary>\n\n" +
                    $"```\n" +
                    $"{_frameworkDiffs}\n" +
                    $"```\n" +
                    $"\n</details>\n" +
                    $"\n\n";

                bool gotAnyDiffs = _corelibDiffs is not null || _frameworkDiffs is not null;

                await UpdateIssueBodyAsync(
                    $"[Build]({ProgressDashboardUrl}) completed in {GetElapsedTime()}.\n" +
                    (gotAnyDiffs && ShouldLinkToPR ? PullRequest.HtmlUrl : "") +
                    "\n\n" +
                    (_corelibDiffs is not null ? corelibDiffs : "") +
                    (_frameworkDiffs is not null ? frameworksDiffs : "") +
                    (gotAnyDiffs ? GetArtifactList() : ""));

                string GetArtifactList()
                {
                    var builder = new StringBuilder();

                    builder.AppendLine("Artifacts:");

                    lock (_artifacts)
                    {
                        foreach (var (FileName, Url, Size) in _artifacts)
                        {
                            builder.AppendLine($"- [{FileName}]({Url}) ({GetRoughSizeString(Size)})");
                        }
                    }

                    builder.AppendLine();

                    return builder.ToString();
                }
            }
            catch (Exception ex)
            {
                await Logger.DebugAsync(ex.ToString());

                await UpdateIssueBodyAsync($"Something went wrong with the [Build]({ProgressDashboardUrl}) :man_shrugging:");
            }
            finally
            {
                Completed = true;

                if (FromGithubComment && ShouldMentionJobInitiator)
                {
                    try
                    {
                        await Github.Issue.Comment.Create(IssueRepositoryOwner, IssueRepositoryName, TrackingIssue.Number, $"@{_githubCommenterLogin}");
                    }
                    catch (Exception ex)
                    {
                        Logger.DebugLog($"Failed to mention job initiator ({_githubCommenterLogin}): {ex}");
                    }
                }
            }
        }

        private async Task RunContainerInstanceAsync(ResourceGroupResource resourceGroup, CancellationToken jobTimeout)
        {
            double memoryInGB = 32;
            double cpuCount = 8;

            if (ConfigurationService.TryGet(null, "RuntimeUtils.MemoryGB", out string memoryInGBString))
            {
                memoryInGB = double.Parse(memoryInGBString, CultureInfo.InvariantCulture);
            }

            if (ConfigurationService.TryGet(null, "RuntimeUtils.CPUCount", out string cpuCountString))
            {
                cpuCount = double.Parse(cpuCountString, CultureInfo.InvariantCulture);
            }

            var container = new ContainerInstanceContainer(
                $"runner-{ExternalId}",
                "runtimeutils.azurecr.io/runner:latest",
                new ContainerResourceRequirements(new ContainerResourceRequestsContent(memoryInGB, cpuCount)));

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

            LogsReceived($"Starting an Azure Container Instance (CPU={cpuCount} Memory={memoryInGB}) ...");

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
                if (ShouldDeleteContainer)
                {
                    LogsReceived("Deleting the container instance");
                    await containerGroupResource.DeleteAsync(WaitUntil.Completed, CancellationToken.None);
                }
                else
                {
                    LogsReceived("Configuration opted not to delete the container instance");
                }
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

        private static string GetRoughSizeString(long size)
        {
            double kb = size / 1024d;
            double mb = kb / 1024d;

            if (mb >= 1)
            {
                return $"{(int)mb} MB";
            }

            return $"{(int)kb} KB";
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
            if (Interlocked.Increment(ref _artifactsCount) > 128)
            {
                Interlocked.Decrement(ref _artifactsCount);
                LogsReceived($"Too many artifacts received, skipping {fileName}");
                return;
            }

            if (fileName is "diff-corelib.txt" or "diff-frameworks.txt")
            {
                using var buffer = new MemoryStream(new byte[128 * 1024]);
                await contentStream.CopyToAsync(buffer, cancellationToken);
                buffer.SetLength(buffer.Position);
                buffer.Position = 0;
                byte[] bytes = buffer.ToArray();
                contentStream = new MemoryStream(bytes);

                if (fileName == "diff-corelib.txt")
                {
                    _corelibDiffs = Encoding.UTF8.GetString(bytes);
                }
                else if (fileName == "diff-frameworks.txt")
                {
                    _frameworkDiffs = Encoding.UTF8.GetString(bytes);
                }
            }

            BlobClient blobClient = _parent.ArtifactsBlobContainerClient.GetBlobClient($"{ExternalId}/{fileName}");

            await blobClient.UploadAsync(contentStream, new BlobUploadOptions
            {
                AccessTier = AccessTier.Hot
            }, cancellationToken);

            var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            long size = properties.Value.ContentLength;

            lock (_artifacts)
            {
                const long GB = 1024 * 1024 * 1024;
                const long ArtifactSizeLimit = 16 * GB;

                if (ArtifactSizeLimit - _totalArtifactsSize < size)
                {
                    LogsReceived($"Artifact '{fileName}' was not saved because it would exceed the {GetRoughSizeString(ArtifactSizeLimit)} limit");
                    blobClient.DeleteIfExists(cancellationToken: cancellationToken);
                    return;
                }

                _artifacts.Add((fileName, blobClient.Uri.AbsoluteUri, size));
                _totalArtifactsSize += size;
            }

            LogsReceived($"Saved artifact '{fileName}' to {blobClient.Uri.AbsoluteUri} ({GetRoughSizeString(size)})");
        }

        public async Task StreamLogsAsync(StreamWriter writer, CancellationToken cancellationToken)
        {
            await foreach (string line in StreamLogsAsync(cancellationToken))
            {
                if (line is null)
                {
                    await writer.FlushAsync(cancellationToken);
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
        public readonly BlobContainerClient ArtifactsBlobContainerClient;

        public RuntimeUtilsService(Logger logger, GitHubClient github, IConfiguration configuration, IConfigurationService configurationService)
        {
            Logger = logger;
            Github = github;
            Configuration = configuration;
            ConfigurationService = configurationService;

            if (Program.AzureEnabled)
            {
                ArtifactsBlobContainerClient = new BlobContainerClient(
                    configuration["AzureStorage:ConnectionString-RuntimeUtils"],
                    "artifacts");
            }

            Task.Run(async () =>
            {
                List<(int Id, Stopwatch Timestamp)> processedMentions = new();

                DateTimeOffset lastCheckTimeReviewComments = DateTimeOffset.UtcNow;
                DateTimeOffset lastCheckTimeIssueComments = DateTimeOffset.UtcNow;

                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                while (await timer.WaitForNextTickAsync())
                {
                    const string Owner = "dotnet";
                    const string Repo = "runtime";

                    try
                    {
                        processedMentions.RemoveAll(c => c.Timestamp.Elapsed.TotalDays > 14);

                        IReadOnlyList<PullRequestReviewComment> pullReviewComments = await github.PullRequest.ReviewComment.GetAllForRepository(Owner, Repo, new PullRequestReviewCommentRequest
                        {
                            Since = lastCheckTimeReviewComments
                        }, new ApiOptions { PageCount = 100 });

                        if (pullReviewComments.Count > 0)
                        {
                            lastCheckTimeReviewComments = pullReviewComments.Max(c => c.CreatedAt);
                        }

                        foreach (PullRequestReviewComment reviewComment in pullReviewComments)
                        {
                            await Process(reviewComment.Id, reviewComment.PullRequestUrl, reviewComment.Body, reviewComment.User);
                        }

                        IReadOnlyList<IssueComment> issueComments = await github.Issue.Comment.GetAllForRepository(Owner, Repo, new IssueCommentRequest
                        {
                            Since = lastCheckTimeIssueComments,
                            Sort = IssueCommentSort.Created,
                        }, new ApiOptions { PageCount = 100 });

                        if (issueComments.Count > 0)
                        {
                            lastCheckTimeIssueComments = issueComments.Max(c => c.CreatedAt);
                        }

                        var recentTimestamp = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);

                        if (recentTimestamp > lastCheckTimeIssueComments)
                        {
                            lastCheckTimeIssueComments = recentTimestamp;
                        }

                        if (recentTimestamp > lastCheckTimeReviewComments)
                        {
                            lastCheckTimeReviewComments = recentTimestamp;
                        }

                        foreach (IssueComment issueComment in issueComments)
                        {
                            if (issueComment.HtmlUrl.Contains("/pull/", StringComparison.OrdinalIgnoreCase))
                            {
                                await Process(issueComment.Id, issueComment.HtmlUrl, issueComment.Body, issueComment.User);
                            }
                        }

                        async Task Process(int commentId, string pullRequestUrl, string body, User user)
                        {
                            if (user.Type == AccountType.User &&
                                body.Contains("@MihuBot", StringComparison.Ordinal) &&
                                !processedMentions.Any(c => c.Id == commentId))
                            {
                                processedMentions.Add((commentId, Stopwatch.StartNew()));

                                int pullRequestNumber = int.Parse(new Uri(pullRequestUrl, UriKind.Absolute).AbsolutePath.Split('/').Last());

                                PullRequest pullRequest = await github.PullRequest.Get(Owner, Repo, pullRequestNumber);

                                if (pullRequest.State.Value == ItemState.Open)
                                {
                                    if (ConfigurationService.TryGet(null, $"RuntimeUtils.AuthorizedUser.{user.Login}", out string allowedString) &&
                                        bool.TryParse(allowedString, out bool allowed) &&
                                        allowed)
                                    {
                                        StartJob(pullRequest, githubCommenterLogin: user.Login);
                                    }
                                    else
                                    {
                                        if (!user.Login.Equals("msftbot", StringComparison.OrdinalIgnoreCase))
                                        {
                                            await Logger.DebugAsync($"User {user.Login} tried to start a job, but is not authorized. <{pullRequest.HtmlUrl}>");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lastCheckTimeReviewComments = DateTimeOffset.UtcNow;
                        lastCheckTimeIssueComments = DateTime.UtcNow;
                        Logger.DebugLog($"Failed to fetch GitHub notifications: {ex}");
                    }
                }
            });
        }

        public bool TryGetJob(string jobId, bool publicId, out RuntimeUtilsJob job)
        {
            lock (_jobs)
            {
                return _jobs.TryGetValue(jobId, out job) &&
                    (publicId ? job.ExternalId : job.JobId) == jobId;
            }
        }

        public RuntimeUtilsJob StartJob(PullRequest pullRequest, string githubCommenterLogin = null)
        {
            string jobId = Guid.NewGuid().ToString("N");
            string externalId = Guid.NewGuid().ToString("N");

            var job = new RuntimeUtilsJob(this, pullRequest, githubCommenterLogin, jobId, externalId);

            lock (_jobs)
            {
                _jobs.Add(jobId, job);
                _jobs.Add(externalId, job);
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromDays(7));

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
