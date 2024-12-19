using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using MihuBot.Configuration;
using Octokit;
using System.Runtime.CompilerServices;
using static MihuBot.Helpers.HetznerClient;

namespace MihuBot.RuntimeUtils;

public abstract class JobBase
{
    private static readonly TimeSpan MaxJobDuration = TimeSpan.FromHours(5);

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

    public GitHubComment GitHubComment { get; }
    public string GithubCommenterLogin { get; }

    protected virtual string RepoOwner => GitHubComment?.RepoOwner ?? "dotnet";
    protected virtual string RepoName => GitHubComment?.RepoName ?? "runtime";

    protected Logger Logger => Parent.Logger;
    public GitHubClient Github => Parent.Github;
    protected HttpClient Http => Parent.Http;
    protected IConfigurationService ConfigurationService => Parent.ConfigurationService;
    protected HetznerClient Hetzner => Parent.Hetzner;

    public Stopwatch Stopwatch { get; private set; } = Stopwatch.StartNew();
    public PullRequest PullRequest { get; private set; }
    public Issue TrackingIssue { get; private set; }
    public bool Completed => !Stopwatch.IsRunning;

    private string _jobTitle;
    public string JobTitle => _jobTitle ??= PullRequest is null
        ? $"[{JobTitlePrefix}] {Metadata["PrRepo"]}/{Metadata["PrBranch"]}".TruncateWithDotDotDot(99)
        : $"[{JobTitlePrefix}] [{PullRequest.User.Login}] {PullRequest.Title}".TruncateWithDotDotDot(99);

    public abstract string JobTitlePrefix { get; }

    public string TestedPROrBranchLink { get; }
    public string JobId { get; } = Guid.NewGuid().ToString("N");
    public string ExternalId { get; } = Snowflake.NextString();

    private string _firstErrorMessage;
    protected string FirstErrorMessage => _firstErrorMessage;
    protected virtual bool PostErrorAsGitHubComment => false;
    private bool _manuallyCancelled;

    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SystemHardwareInfo LastSystemInfo { get; set; }
    public string LastProgressSummary { get; set; }

    public bool ShouldDeleteVM { get; set; }
    public string RemoteLoginCredentials { get; private set; }

    public string CustomArguments
    {
        get => Metadata["CustomArguments"];
        set => Metadata["CustomArguments"] = value;
    }

    public string ProgressUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/api/RuntimeUtils/Jobs/Progress/{ExternalId}";
    public string ProgressDashboardUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/runtime-utils/{ExternalId}";

    public int TotalProgressSiteViews;
    public int CurrentProgressSiteViews;

    private JobBase(RuntimeUtilsService parent, string githubCommenterLogin)
    {
        Parent = parent;
        GithubCommenterLogin = githubCommenterLogin;

        ShouldDeleteVM = GetConfigFlag("ShouldDeleteVM", true);
    }

    public JobBase(RuntimeUtilsService parent, BranchReference branch, string githubCommenterLogin, string arguments)
        : this(parent, githubCommenterLogin)
    {
        InitMetadata("dotnet/runtime", "main", branch.Repository, branch.Branch.Name, arguments);

        TestedPROrBranchLink = $"https://github.com/{branch.Repository}/tree/{branch.Branch.Name}";
    }

    public JobBase(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment)
        : this(parent, githubCommenterLogin)
    {
        PullRequest = pullRequest;
        GitHubComment = comment;

        InitMetadata(
            PullRequest.Base.Repository.FullName, PullRequest.Base.Ref,
            PullRequest.Head.Repository.FullName, PullRequest.Head.Ref,
            arguments);

        TestedPROrBranchLink = PullRequest.HtmlUrl;
    }

    private void InitMetadata(string baseRepo, string baseBranch, string prRepo, string prBranch, string arguments)
    {
        arguments ??= string.Empty;
        arguments = arguments.SplitLines()[0].Trim();

        Metadata.Add("JobId", JobId);
        Metadata.Add("ExternalId", ExternalId);
        Metadata.Add("BaseRepo", baseRepo);
        Metadata.Add("BaseBranch", baseBranch);
        Metadata.Add("PrRepo", prRepo);
        Metadata.Add("PrBranch", prBranch);
        Metadata.Add("CustomArguments", arguments);
        Metadata.Add("JobType", GetType().Name);
        Metadata.Add("JobStartTime", StartTime.Ticks.ToString());

        var containerClient = Parent.RunnerPersistentStateBlobContainerClient;
        Uri sasUri = containerClient.GenerateSasUri(BlobContainerSasPermissions.Read, DateTimeOffset.UtcNow.Add(MaxJobDuration));
        Metadata.Add("PersistentStateSasUri", sasUri.AbsoluteUri);
    }

    protected bool ShouldLinkToPROrBranch =>
        GetConfigFlag("LinkToPR", true) &&
        !CustomArguments.Contains("-NoPRLink", StringComparison.OrdinalIgnoreCase);

    protected bool UseArm => CustomArguments.Contains("-arm", StringComparison.OrdinalIgnoreCase);
    protected string Architecture => UseArm ? "ARM64" : "X64";
    protected bool Fast => CustomArguments.Contains("-fast", StringComparison.OrdinalIgnoreCase);

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

    protected virtual bool RunUsingGitHubActions => false;
    protected virtual bool RunUsingAzurePipelines => false;

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

        Exception initializationException = null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await ParsePRListAsync(CustomArguments, "dependsOn", timeoutCts.Token);
            await ParsePRListAsync(CustomArguments, "combineWith", timeoutCts.Token);

            await InitializeAsync(timeoutCts.Token);
        }
        catch (Exception ex)
        {
            initializationException = ex;
        }

        bool startGithubActions = RunUsingGitHubActions && initializationException is null;

        TrackingIssue = await Github.Issue.Create(
            IssueRepositoryOwner,
            IssueRepositoryName,
            new NewIssue(JobTitle)
            {
                Body =
                    $"""
                    Job is in progress - see {ProgressDashboardUrl}
                    {(ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}

                    {(startGithubActions ? $"<!-- RUN_AS_GITHUB_ACTION_{ExternalId} -->" : "")}
                    """
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

            if (initializationException is not null)
            {
                throw initializationException;
            }

            if (RunUsingGitHubActions)
            {
                LogsReceived("Starting runner on GitHub actions ...");
            }

            if (RunUsingAzurePipelines)
            {
                await AzurePipelinesHelper.TriggerWebhookAsync(Logger, Http, "MihuBot", "MihuBot",
                    Parent.Configuration["RuntimeUtils:AzurePipelinesWebhookSecret"], $"{{\"job_id\":\"{ExternalId}\"}}");

                LogsReceived("Starting runner on Azure Pipelines ...");
                _idleTimeoutCts.CancelAfter(IdleTimeoutMs * 4);
            }

            try
            {
                await RunJobAsyncCore(jobTimeout);
            }
            catch (TaskCanceledException) when (_manuallyCancelled) { }
            finally
            {
                LastSystemInfo = null;
            }

            await SaveLogsArtifactAsync();
        }
        catch (Exception ex)
        {
            LogsReceived($"Uncaught exception: {ex}");

            try
            {
                await SaveLogsArtifactAsync();
            }
            catch { }

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

            await Parent.SaveCompletedJobRecordAsync(new CompletedJobRecord
            {
                ExternalId = ExternalId,
                Title = JobTitle,
                StartedAt = StartTime,
                Duration = Stopwatch.Elapsed,
                TestedPROrBranchLink = TestedPROrBranchLink,
                TrackingIssueUrl = TrackingIssue?.HtmlUrl,
                Metadata = Metadata,
                Artifacts = Artifacts.Select(a => new CompletedJobRecord.Artifact(a.FileName, a.Url, a.Size)).ToArray()
            });

            if (ShouldMentionJobInitiator && GithubCommenterLogin is not null)
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

        async Task SaveLogsArtifactAsync()
        {
            await ArtifactReceivedAsync("logs.txt", new MemoryStream(Encoding.UTF8.GetBytes(_logs.ToString())), CancellationToken.None);
        }
    }

    protected virtual Task InitializeAsync(CancellationToken jobTimeout) => Task.CompletedTask;

    protected abstract Task RunJobAsyncCore(CancellationToken jobTimeout);

    private async Task ParsePRListAsync(string arguments, string argument, CancellationToken cancellationToken)
    {
        if (TryParseList(arguments, argument) is not int[] prs || prs.Length == 0)
        {
            return;
        }

        LogsReceived($"Found {argument} PRs: {string.Join(", ", prs)}");

        ArgumentOutOfRangeException.ThrowIfGreaterThan(prs.Length, 100);

        List<(string Repo, string Branch)> prInfos = new();

        foreach (int pr in prs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                PullRequest prInfo = await Github.PullRequest.Get(RepoOwner, RepoName, pr);
                string repo = prInfo.Head.Repository.FullName;
                string branch = prInfo.Head.Ref;

                LogsReceived($"PR {pr}: {repo}/{branch}");
                prInfos.Add((repo, branch));
            }
            catch
            {
                LogsReceived($"Failed to get PR info for {pr}");
                throw;
            }
        }

        string prInfoStr = string.Join(',', prInfos.Select(pr => $"{pr.Repo};{pr.Branch}"));
        LogsReceived($"Adding {argument}: {prInfoStr}");
        Metadata.Add(argument, prInfoStr);

        static int[] TryParseList(ReadOnlySpan<char> arguments, string argument)
        {
            argument = $"-{argument} ";

            int offset = arguments.IndexOf(argument, StringComparison.OrdinalIgnoreCase);
            if (offset < 0) return null;

            arguments = arguments.Slice(offset + argument.Length);

            int length = arguments.IndexOf(' ');
            if (length >= 0)
            {
                arguments = arguments.Slice(0, length);
            }

            return arguments.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(number => number.TrimStart('#'))
                .Select(number => GitHubHelper.TryParseDotnetRuntimeIssueOrPRNumber(number, out int prNumber) ? prNumber.ToString() : number)
                .Where(number => uint.TryParse(number, out uint value) && value is > 0 and < 1_000_000_000)
                .Select(int.Parse)
                .ToArray();
        }
    }

    public string GetElapsedTime(bool includeSeconds = true) => Stopwatch.Elapsed.ToElapsedTime(includeSeconds);

    public static string GetRoughSizeString(long size)
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

                    await Github.Issue.Comment.Create(RepoOwner, RepoName, PullRequest.Number, comment);
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
            AccessTier = Path.GetExtension(fileName) == ".txt" ? AccessTier.Hot : AccessTier.Cool,
        }, cancellationToken);

        if (newStream is not null)
        {
            await newStream.DisposeAsync();
        }

        var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        long size = properties.Value.ContentLength;

        var entry = await Parent.UrlShortener.CreateAsync(ProgressDashboardUrl, blobClient.Uri);

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

            Artifacts.Add((fileName, entry.ShortUrl, size));
            _totalArtifactsSize += size;
        }

        LogsReceived($"Saved artifact '{fileName}' to {entry.ShortUrl} ({GetRoughSizeString(size)})");
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
        int lastReadCount = 0;

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

                if (read != lines.Length)
                {
                    yield return null;
                }
            }
            else
            {
                if (lastReadCount == lines.Length)
                {
                    yield return null;
                }

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

            lastReadCount = read;
        }
    }

    protected string TryFindLogLine(Predicate<string> predicate)
    {
        int position = 0;
        string[] lines = new string[100];

        while (true)
        {
            int read = _logs.Get(lines, ref position);

            if (read == 0)
            {
                break;
            }

            foreach (string line in lines.AsSpan(0, read))
            {
                if (predicate(line))
                {
                    return line;
                }
            }
        }

        return null;
    }

    public void FailFast(string message, bool cancelledByAuthor)
    {
        if (Completed || _idleTimeoutCts.IsCancellationRequested)
        {
            return;
        }

        _manuallyCancelled = true;

        if (cancelledByAuthor)
        {
            ShouldMentionJobInitiator = false;
        }

        message = $"!!! FailFast: {message}";

        _firstErrorMessage = message;
        LogsReceived(message);
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
                UseArm ? "DXps_v6" :
                useIntelCpu ? "DXds_v5" :
                //fast ? "FXas_v6" :
                "DXas_v6";
            defaultVmSize = $"Standard_{defaultVmSize.Replace("X", (Fast ? 2 * defaultAzureCoreCount : defaultAzureCoreCount).ToString())}";

            string vmConfigName = $"{(Fast ? "Fast" : "")}{cpuType}";
            string vmSize = GetConfigFlag($"Azure.VMSize{vmConfigName}", defaultVmSize);

            string templateJson = await Http.GetStringAsync("https://gist.githubusercontent.com/MihaZupan/5385b7153709beae35cdf029eabf50eb/raw/AzureVirtualMachineTemplate.json", jobTimeout);

            string password = $"{JobId}aA1";

            var deploymentContent = new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                Template = BinaryData.FromString(templateJson),
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    runnerId = new { value = JobId },
                    osDiskSizeGiB = new { value = int.Parse(GetConfigFlag($"Azure.VMDisk{vmConfigName}", "128")) },
                    virtualMachineSize = new { value = vmSize },
                    adminPassword = new { value = password },
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

            string resourceGroupName = $"runtime-utils-runner-{JobId}";
            var resourceGroupData = new ResourceGroupData(AzureLocation.EastUS2);
            var resourceGroups = subscription.GetResourceGroups();
            var resourceGroup = (await resourceGroups.CreateOrUpdateAsync(WaitUntil.Completed, resourceGroupName, resourceGroupData, jobTimeout)).Value;

            try
            {
                LogsReceived($"Starting deployment of Azure VM ({vmSize}) ...");
                _idleTimeoutCts.CancelAfter(IdleTimeoutMs * 4);

                string deploymentName = $"runner-deployment-{JobId}";
                var armDeployments = resourceGroup.GetArmDeployments();
                var deployment = (await armDeployments.CreateOrUpdateAsync(WaitUntil.Completed, deploymentName, deploymentContent, jobTimeout)).Value;

                LogsReceived("Azure deployment complete");

                if ((await resourceGroup.GetPublicIPAddresses().GetAllAsync(jobTimeout).FirstOrDefaultAsync(jobTimeout)) is { } ip)
                {
                    RemoteLoginCredentials = $"ssh runner@{ip.Data.IPAddress}  {password}";
                }

                await JobCompletionTcs.Task.WaitAsync(jobTimeout);
            }
            finally
            {
                if (ShouldDeleteVM)
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

            string serverType = Fast
                ? GetConfigFlag($"Hetzner.VMSizeFast{cpuType}", UseArm ? "cax41" : (useIntelCpu ? "cx51" : "cpx51"))
                : GetConfigFlag($"Hetzner.VMSize{cpuType}", UseArm ? "cax31" : (useIntelCpu ? "cx41" : "cpx41"));

            LogsReceived($"Starting a Hetzner VM ({serverType}) ...");

            HetznerServerResponse server = await Hetzner.CreateServerAsync(
                $"runner-{JobId}",
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

                if (serverInfo.PublicNet.Ipv4?.Ip is { } ip)
                {
                    RemoteLoginCredentials = $"ssh root@{ip}  {server.RootPassword}";
                }

                await JobCompletionTcs.Task.WaitAsync(jobTimeout);
            }
            finally
            {
                if (ShouldDeleteVM)
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
