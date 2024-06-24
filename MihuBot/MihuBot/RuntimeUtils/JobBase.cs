using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using MihuBot.Configuration;
using Octokit;
using System.Runtime.CompilerServices;
using Azure.Storage.Sas;
using Azure.Core;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager;
using Azure;
using static MihuBot.Helpers.HetznerClient;

namespace MihuBot.RuntimeUtils;

public abstract class JobBase
{
    private static readonly TimeSpan MaxJobDuration = TimeSpan.FromHours(5);

    protected const string DotnetRuntimeRepoOwner = "dotnet";
    protected const string DotnetRuntimeRepoName = "runtime";
    protected const string IssueRepositoryOwner = "MihuBot";
    protected const string IssueRepositoryName = "runtime-utils";
    protected const int CommentLengthLimit = 65_000;
    protected const int IdleTimeoutMs = 5 * 60 * 1000;

    public readonly DateTime StartTime = DateTime.UtcNow;
    protected readonly RuntimeUtilsService Parent;
    private readonly RollingLog _logs = new(100_000);
    private long _artifactsCount;
    private long _totalArtifactsSize;
    protected readonly CancellationTokenSource _idleTimeoutCts = new();
    protected readonly List<(string FileName, string Url, long Size)> Artifacts = new();

    protected TaskCompletionSource JobCompletionTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected GitHubComment GitHubComment { get; }
    protected string GithubCommenterLogin { get; }
    public bool FromGithubComment => GithubCommenterLogin is not null;

    protected Logger Logger => Parent.Logger;
    protected GitHubClient Github => Parent.Github;
    protected HttpClient Http => Parent.Http;
    protected IConfigurationService ConfigurationService => Parent.ConfigurationService;
    protected HetznerClient Hetzner => Parent.Hetzner;

    public Stopwatch Stopwatch { get; private set; } = Stopwatch.StartNew();
    public PullRequest PullRequest { get; private set; }
    public Issue TrackingIssue { get; private set; }
    public bool Completed => !Stopwatch.IsRunning;

    public abstract string JobTitle { get; }
    public string TestedPROrBranchLink { get; }
    public string JobId { get; } = Guid.NewGuid().ToString("N");
    public string ExternalId { get; } = Guid.NewGuid().ToString("N");

    private string _firstErrorMessage;
    protected string FirstErrorMessage => _firstErrorMessage;
    protected virtual bool PostErrorAsGitHubComment => false;

    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SystemHardwareInfo LastSystemInfo { get; set; }
    public string LastProgressSummary { get; set; }

    protected string CustomArguments
    {
        get => Metadata["CustomArguments"];
        set => Metadata["CustomArguments"] = value;
    }

    public string ProgressUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/api/RuntimeUtils/Jobs/Progress/{ExternalId}";
    public string ProgressDashboardUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/runtime-utils/{ExternalId}";

    public JobBase(RuntimeUtilsService parent, string repository, string branch, string githubCommenterLogin, string arguments)
    {
        Parent = parent;
        GithubCommenterLogin = githubCommenterLogin;

        InitMetadata(repository, branch, arguments);

        TestedPROrBranchLink = $"https://github.com/{repository}/tree/{branch}";
    }

    public JobBase(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment)
    {
        Parent = parent;
        PullRequest = pullRequest;
        GithubCommenterLogin = githubCommenterLogin;
        GitHubComment = comment;

        InitMetadata(PullRequest.Head.Repository.FullName, PullRequest.Head.Ref, arguments);

        TestedPROrBranchLink = PullRequest.HtmlUrl;
    }

    private void InitMetadata(string repository, string branch, string arguments)
    {
        arguments ??= string.Empty;
        arguments = arguments.SplitLines()[0].Trim();

        Metadata.Add("JobId", JobId);
        Metadata.Add("ExternalId", ExternalId);
        Metadata.Add("PrRepo", repository);
        Metadata.Add("PrBranch", branch);
        Metadata.Add("CustomArguments", arguments);
        Metadata.Add("JobType", GetType().Name);

        var containerClient = Parent.RunnerPersistentStateBlobContainerClient;
        Uri sasUri = containerClient.GenerateSasUri(BlobContainerSasPermissions.Read, DateTimeOffset.UtcNow.Add(MaxJobDuration));
        Metadata.Add("PersistentStateSasUri", sasUri.AbsoluteUri);
    }

    protected bool ShouldLinkToPROrBranch =>
        GetConfigFlag("LinkToPR", true) &&
        !CustomArguments.Contains("-NoPRLink", StringComparison.OrdinalIgnoreCase);

    protected bool UseArm => CustomArguments.Contains("-arm", StringComparison.OrdinalIgnoreCase);
    protected string Architecture => UseArm ? "ARM64" : "X64";

    protected bool ShouldMentionJobInitiator { get; set; }

    protected bool GetConfigFlag(string name, bool @default)
    {
        if (ConfigurationService.TryGet(null, $"RuntimeUtils.{name}", out string flagStr) &&
            bool.TryParse(flagStr, out bool flag))
        {
            return flag;
        }

        return @default;
    }

    protected string GetConfigFlag(string name, string @default)
    {
        if (ConfigurationService.TryGet(null, $"RuntimeUtils.{name}", out string flag))
        {
            return flag;
        }

        return @default;
    }

    protected abstract string GetInitialIssueBody();

    protected virtual bool RunUsingGitHubActions => false;

    public async Task RunJobAsync()
    {
        LogsReceived("Starting ...");

        if (!Program.AzureEnabled)
        {
            LogsReceived("No Azure support. Aborting ...");
            NotifyJobCompletion();
            return;
        }

        ShouldMentionJobInitiator = GetConfigFlag("ShouldMentionJobInitiator", true);

        string initialBody = GetInitialIssueBody();
        if (RunUsingGitHubActions)
        {
            initialBody = $"{initialBody}\n\n<!-- RUN_AS_GITHUB_ACTION_{ExternalId} -->\n";
        }

        TrackingIssue = await Github.Issue.Create(
            IssueRepositoryOwner,
            IssueRepositoryName,
            new NewIssue(JobTitle)
            {
                Body = initialBody
            });

        try
        {
            using var jobTimeoutCts = new CancellationTokenSource(MaxJobDuration);
            var jobTimeout = jobTimeoutCts.Token;

            using var ctsReg = _idleTimeoutCts.Token.Register(() =>
            {
                if (FirstErrorMessage is null)
                {
                    LogsReceived("Job idle timeout exceeded, terminating ...");
                }

                jobTimeoutCts.Cancel();
                JobCompletionTcs.TrySetCanceled();
            });

            LogsReceived($"Using custom arguments: '{CustomArguments}'");

            if (RunUsingGitHubActions)
            {
                LogsReceived("Starting runner on GitHub actions ...");
            }

            await RunJobAsyncCore(jobTimeout);

            LastSystemInfo = null;

            await ArtifactReceivedAsync("logs.txt", new MemoryStream(Encoding.UTF8.GetBytes(_logs.ToString())), CancellationToken.None);
        }
        catch (Exception ex)
        {
            await Logger.DebugAsync(ex.ToString());

            await UpdateIssueBodyAsync(
                $"""
                Something went wrong with the [Job]({ProgressDashboardUrl}) after {GetElapsedTime()} :man_shrugging:

                ```
                {_firstErrorMessage ?? ex.ToString()}
                ```
                """);
        }
        finally
        {
            Stopwatch.Stop();

            NotifyJobCompletion();

            if (FromGithubComment && ShouldMentionJobInitiator)
            {
                try
                {
                    await Github.Issue.Comment.Create(IssueRepositoryOwner, IssueRepositoryName, TrackingIssue.Number, $"@{GithubCommenterLogin}");
                }
                catch (Exception ex)
                {
                    Logger.DebugLog($"Failed to mention job initiator ({GithubCommenterLogin}): {ex}");
                }
            }
        }
    }

    protected abstract Task RunJobAsyncCore(CancellationToken jobTimeout);

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

    protected static string GetRoughSizeString(long size)
    {
        double kb = size / 1024d;
        double mb = kb / 1024d;

        if (mb >= 1)
        {
            return $"{(int)mb} MB";
        }

        if (kb >= 1)
        {
            return $"{(int)kb} KB";
        }

        return $"{size} B";
    }

    protected async Task UpdateIssueBodyAsync(string newBody)
    {
        IssueUpdate update = TrackingIssue.ToUpdate();
        update.Body = newBody;
        await Github.Issue.Update(IssueRepositoryOwner, IssueRepositoryName, TrackingIssue.Number, update);
    }

    protected string GetArtifactList()
    {
        if (Artifacts.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        builder.AppendLine("Artifacts:");

        lock (Artifacts)
        {
            foreach (var (FileName, Url, Size) in Artifacts)
            {
                builder.AppendLine($"- [{FileName}]({Url}) ({GetRoughSizeString(Size)})");
            }
        }

        builder.AppendLine();

        return builder.ToString();
    }

    public void LogsReceived(string line)
    {
        LogsReceived([line]);
    }

    public void LogsReceived(ReadOnlySpan<string> lines)
    {
        _logs.AddLines(lines);
        _idleTimeoutCts.CancelAfter(IdleTimeoutMs);

        foreach (string line in lines)
        {
            // ERROR: System.Exception: foo
            if (line.StartsWith("ERROR: ", StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref _firstErrorMessage, line, null) is null &&
                PullRequest is not null &&
                PostErrorAsGitHubComment)
            {
                PostErrorComment();
            }
        }

        void PostErrorComment()
        {
            if (!GetConfigFlag("PostErrorComments", true))
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    string comment =
                        $"""
                        ```
                        {_firstErrorMessage}
                        ```
                        """;

                    await Github.Issue.Comment.Create(DotnetRuntimeRepoOwner, DotnetRuntimeRepoName, PullRequest.Number, comment);
                }
                catch (Exception ex)
                {
                    await Logger.DebugAsync($"Failed to post comment for message '{_firstErrorMessage}': {ex}");
                }
            });
        }
    }

    public async Task ArtifactReceivedAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _artifactsCount) > 128)
        {
            Interlocked.Decrement(ref _artifactsCount);
            LogsReceived($"Too many artifacts received, skipping {fileName}");
            return;
        }

        Stream newStream = await InterceptArtifactAsync(fileName, contentStream, cancellationToken);
        contentStream = newStream ?? contentStream;

        BlobClient blobClient = Parent.ArtifactsBlobContainerClient.GetBlobClient($"{ExternalId}/{fileName}");

        await blobClient.UploadAsync(contentStream, new BlobUploadOptions
        {
            AccessTier = AccessTier.Hot
        }, cancellationToken);

        if (newStream is not null)
        {
            await newStream.DisposeAsync();
        }

        var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        long size = properties.Value.ContentLength;

        lock (Artifacts)
        {
            const long GB = 1024 * 1024 * 1024;
            const long ArtifactSizeLimit = 16 * GB;

            if (ArtifactSizeLimit - _totalArtifactsSize < size)
            {
                LogsReceived($"Artifact '{fileName}' was not saved because it would exceed the {GetRoughSizeString(ArtifactSizeLimit)} limit");
                blobClient.DeleteIfExists(cancellationToken: cancellationToken);
                return;
            }

            Artifacts.Add((fileName, blobClient.Uri.AbsoluteUri, size));
            _totalArtifactsSize += size;
        }

        LogsReceived($"Saved artifact '{fileName}' to {blobClient.Uri.AbsoluteUri} ({GetRoughSizeString(size)})");
    }

    protected virtual Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken) => Task.FromResult<Stream>(null);

    protected async Task<(byte[] Bytes, Stream ReplacementStream)> ReadArtifactAndReplaceStreamAsync(Stream stream, int lengthLimitBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(new byte[lengthLimitBytes]);
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.SetLength(buffer.Position);
        buffer.Position = 0;
        byte[] bytes = buffer.ToArray();
        return (bytes, new MemoryStream(bytes));
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

    public void NotifyJobCompletion()
    {
        JobCompletionTcs.TrySetResult();
        _idleTimeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
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

                try
                {
                    await Task.Delay(cooldown, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (lastYield.Elapsed.TotalSeconds > 10)
                {
                    lastYield.Restart();
                    yield return null;
                }
            }
        }
    }

    public void FailFast(string message)
    {
        if (Completed || _idleTimeoutCts.IsCancellationRequested)
        {
            return;
        }

        _firstErrorMessage = $"!!! FailFast: {message}";
        LogsReceived(_firstErrorMessage);
        _idleTimeoutCts.Cancel();
    }


    protected async Task RunOnNewVirtualMachineAsync(int defaultAzureCoreCount, CancellationToken jobTimeout)
    {
        string cloudInitScript =
            $"""
            #cloud-config
                runcmd:
                    - apt-get update
                    - apt-get install -y dotnet-sdk-6.0
                    - apt-get install -y dotnet-sdk-8.0
                    - cd /home
                    - git clone --no-tags --single-branch --progress https://github.com/MihaZupan/runtime-utils
                    - cd runtime-utils/Runner
                    - HOME=/root JOB_ID={JobId} dotnet run -c Release
            """;

        bool useIntelCpu = CustomArguments.Contains("-intel", StringComparison.OrdinalIgnoreCase);
        bool fast = CustomArguments.Contains("-fast", StringComparison.OrdinalIgnoreCase);
        bool shouldDeleteVM = GetConfigFlag("ShouldDeleteVM", true);
        bool useHetzner =
            GetConfigFlag("ForceHetzner", false) ||
            CustomArguments.Contains("-hetzner", StringComparison.OrdinalIgnoreCase);

        if (useHetzner)
        {
            await RunHetznerVirtualMachineAsync(jobTimeout);
        }
        else
        {
            await RunAzureVirtualMachineAsync(jobTimeout);
        }

        async Task RunAzureVirtualMachineAsync(CancellationToken jobTimeout)
        {
            string cpuType = UseArm ? "ARM64" : (useIntelCpu ? "X64Intel" : "X64Amd");

            string defaultVmSize =
                UseArm ? "DXpds_v5" :
                useIntelCpu ? "DXds_v5" :
                fast ? "FXas_v6" :
                "DXas_v6";
            defaultVmSize = $"Standard_{defaultVmSize.Replace("X", (fast ? 2 * defaultAzureCoreCount : defaultAzureCoreCount).ToString())}";

            string vmConfigName = $"{(fast ? "Fast" : "")}{cpuType}";
            string vmSize = GetConfigFlag($"Azure.VMSize{vmConfigName}", defaultVmSize);

            string templateJson = await Http.GetStringAsync("https://gist.githubusercontent.com/MihaZupan/5385b7153709beae35cdf029eabf50eb/raw/AzureVirtualMachineTemplate.json", jobTimeout);

            var deploymentContent = new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                Template = BinaryData.FromString(templateJson),
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    runnerId = new { value = ExternalId },
                    osDiskSizeGiB = new { value = int.Parse(GetConfigFlag($"Azure.VMDisk{vmConfigName}", "128")) },
                    virtualMachineSize = new { value = vmSize },
                    adminPassword = new { value = $"{JobId}aA1" },
                    customData = new { value = Convert.ToBase64String(Encoding.UTF8.GetBytes(cloudInitScript)) },
                    imageReference = new
                    {
                        value = new
                        {
                            publisher = "canonical",
                            offer = "0001-com-ubuntu-server-jammy",
                            sku = UseArm ? "22_04-lts-arm64" : "22_04-lts-gen2",
                            version = "latest"
                        }
                    }
                })
            });

            LogsReceived("Creating a new Azure resource group for this deployment ...");

            var armClient = new ArmClient(Program.AzureCredential);
            var subscription = await armClient.GetDefaultSubscriptionAsync(jobTimeout);

            string resourceGroupName = $"runtime-utils-runner-{ExternalId}";
            var resourceGroupData = new ResourceGroupData(AzureLocation.EastUS2);
            var resourceGroups = subscription.GetResourceGroups();
            var resourceGroup = (await resourceGroups.CreateOrUpdateAsync(WaitUntil.Completed, resourceGroupName, resourceGroupData, jobTimeout)).Value;

            try
            {
                LogsReceived($"Starting deployment of Azure VM ({vmSize}) ...");
                _idleTimeoutCts.CancelAfter(IdleTimeoutMs * 4);

                string deploymentName = $"runner-deployment-{ExternalId}";
                var armDeployments = resourceGroup.GetArmDeployments();
                var deployment = (await armDeployments.CreateOrUpdateAsync(WaitUntil.Completed, deploymentName, deploymentContent, jobTimeout)).Value;

                LogsReceived("Azure deployment complete");

                await JobCompletionTcs.Task.WaitAsync(jobTimeout);
            }
            finally
            {
                if (shouldDeleteVM)
                {
                    LogsReceived("Deleting the VM resource group");

                    QueueResourceDeletion(async () =>
                    {
                        await resourceGroup.DeleteAsync(WaitUntil.Completed, cancellationToken: CancellationToken.None);
                    }, resourceGroupName);
                }
                else
                {
                    LogsReceived("Configuration opted not to delete the VM");
                }
            }
        }

        async Task RunHetznerVirtualMachineAsync(CancellationToken jobTimeout)
        {
            string cpuType = UseArm ? "ARM64" : (useIntelCpu ? "X64Intel" : "X64Amd");

            string serverType = fast
                ? GetConfigFlag($"Hetzner.VMSizeFast{cpuType}", UseArm ? "cax41" : (useIntelCpu ? "cx51" : "cpx51"))
                : GetConfigFlag($"Hetzner.VMSize{cpuType}", UseArm ? "cax31" : (useIntelCpu ? "cx41" : "cpx41"));

            LogsReceived($"Starting a Hetzner VM ({serverType}) ...");

            HetznerServerResponse server = await Hetzner.CreateServerAsync(
                $"runner-{ExternalId}",
                GetConfigFlag($"HetznerImage{Architecture}", "ubuntu-22.04"),
                GetConfigFlag($"HetznerLocation{cpuType}", UseArm || useIntelCpu ? "fsn1" : "ash"),
                serverType,
                cloudInitScript,
                jobTimeout);

            HetznerServerResponse.HetznerServer serverInfo = server.Server ?? throw new Exception("No server info");
            HetznerServerResponse.HetznerServerType vmType = serverInfo.ServerType;

            try
            {
                LogsReceived($"VM starting (CpuType={cpuType} CPU={vmType?.Cores} Memory={vmType?.Memory}) ...");

                await JobCompletionTcs.Task.WaitAsync(jobTimeout);
            }
            finally
            {
                if (shouldDeleteVM)
                {
                    LogsReceived("Deleting the VM");

                    QueueResourceDeletion(async () =>
                    {
                        await Hetzner.DeleteServerAsync(serverInfo.Id, CancellationToken.None);
                    }, serverInfo.Id.ToString());
                }
                else
                {
                    LogsReceived("Configuration opted not to delete the VM");
                }
            }
        }

        void QueueResourceDeletion(Func<Task> func, string info)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await func();
                }
                catch (Exception ex)
                {
                    await Logger.DebugAsync($"Failed to delete resource {info}: {ex}");
                }
            });
        }
    }
}
