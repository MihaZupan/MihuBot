using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using MihuBot.Configuration;
using Octokit;
using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using static MihuBot.Helpers.HetznerClient;

namespace MihuBot.RuntimeUtils;

public sealed class RuntimeUtilsJob
{
    private const string DotnetRuntimeRepoOwner = "dotnet";
    private const string DotnetRuntimeRepoName = "runtime";
    private const string IssueRepositoryOwner = "MihuBot";
    private const string IssueRepositoryName = "runtime-utils";
    private const int CommentLengthLimit = 65_000;
    private const int IdleTimeoutMs = 5 * 60 * 1000;

    private RuntimeUtilsService _parent;
    private readonly string _githubCommenterLogin;
    private readonly RollingLog _logs = new(50_000);
    private readonly List<(string FileName, string Url, long Size)> _artifacts = new();
    private long _artifactsCount;
    private long _totalArtifactsSize;
    private string _frameworksDiffSummary;
    private readonly TempFile _frameworksDiffsZipFile = new("zip");
    private readonly Dictionary<(string DasmFile, bool Main), TempFile> _frameworksDiffFiles = new();
    private readonly CancellationTokenSource _idleTimeoutCts = new();
    private readonly TaskCompletionSource _jobCompletionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool FromGithubComment => _githubCommenterLogin is not null;

    private Logger Logger => _parent.Logger;
    private GitHubClient Github => _parent.Github;
    private HttpClient Http => _parent.Http;
    private IConfiguration Configuration => _parent.Configuration;
    private IConfigurationService ConfigurationService => _parent.ConfigurationService;
    private HetznerClient Hetzner => _parent.Hetzner;

    public Stopwatch Stopwatch { get; private set; } = Stopwatch.StartNew();
    public PullRequest PullRequest { get; private set; }
    public Issue TrackingIssue { get; private set; }
    public bool Completed => !Stopwatch.IsRunning;

    public string JobTitle { get; }
    public string TestedPROrBranchLink { get; }
    public string JobId { get; } = Guid.NewGuid().ToString("N");
    public string ExternalId { get; } = Guid.NewGuid().ToString("N");

    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SystemHardwareInfo LastSystemInfo { get; set; }

    private string CustomArguments
    {
        get => Metadata["CustomArguments"];
        set => Metadata["CustomArguments"] = value;
    }

    public string Architecture => UseArm ? "ARM64" : "X64";

    private bool UseArm => CustomArguments.Contains("-arm", StringComparison.OrdinalIgnoreCase);
    private bool UseIntelCpu => CustomArguments.Contains("-intel", StringComparison.OrdinalIgnoreCase);
    private bool Fast => CustomArguments.Contains("-fast", StringComparison.OrdinalIgnoreCase);
    private bool IncludeKnownNoise => CustomArguments.Contains("-includeKnownNoise", StringComparison.OrdinalIgnoreCase);
    private bool IncludeNewMethodRegressions => CustomArguments.Contains("-includeNewMethodRegressions", StringComparison.OrdinalIgnoreCase);
    private bool IncludeRemovedMethodImprovements => CustomArguments.Contains("-includeRemovedMethodImprovements", StringComparison.OrdinalIgnoreCase);

    public bool UseHetzner =>
        GetConfigFlag("ForceHetzner", false) ||
        CustomArguments.Contains("-hetzner", StringComparison.OrdinalIgnoreCase);

    public string ProgressUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/api/RuntimeUtils/Jobs/Progress/{ExternalId}";
    public string ProgressDashboardUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/runtime-utils/{ExternalId}";

    public RuntimeUtilsJob(RuntimeUtilsService parent, string repository, string branch, string githubCommenterLogin, string arguments)
    {
        _parent = parent;
        _githubCommenterLogin = githubCommenterLogin;

        InitMetadata(repository, branch, arguments);

        JobTitle = $"[{Architecture}] {repository}/{branch}".TruncateWithDotDotDot(99);
        TestedPROrBranchLink = $"https://github.com/{repository}/tree/{branch}";
    }

    public RuntimeUtilsJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments)
    {
        _parent = parent;
        PullRequest = pullRequest;
        _githubCommenterLogin = githubCommenterLogin;

        InitMetadata(PullRequest.Head.Repository.FullName, PullRequest.Head.Ref, arguments);

        JobTitle = $"[{Architecture}] [{PullRequest.User.Login}] {PullRequest.Title}".TruncateWithDotDotDot(99);
        TestedPROrBranchLink = PullRequest.HtmlUrl;
    }

    private void InitMetadata(string repository, string branch, string arguments)
    {
        arguments ??= string.Empty;
        arguments = arguments.NormalizeNewLines().Split('\n')[0].Trim();

        Metadata.Add("JobId", JobId);
        Metadata.Add("ExternalId", ExternalId);
        Metadata.Add("PrRepo", repository);
        Metadata.Add("PrBranch", branch);
        Metadata.Add("CustomArguments", arguments);

        BlobClient blobClient = _parent.RunnerBaselineBlobContainerClient.GetBlobClient("RunnerBaseline.tar");
        Uri sasUri = blobClient.GenerateSasUri(BlobSasPermissions.All, DateTimeOffset.UtcNow.AddHours(5));
        Metadata.Add("RunnerBaselineBlob", sasUri.AbsoluteUri);
    }

    private async Task ParsePRListAsync(string arguments, string argument)
    {
        if (TryParseList(arguments, argument) is not int[] prs || prs.Length == 0)
        {
            return;
        }

        LogsReceived($"Found {argument} PRs: {string.Join(", ", prs)}");

        List<(string Repo, string Branch)> prInfos = new();

        foreach (int pr in prs)
        {
            try
            {
                PullRequest prInfo = await Github.PullRequest.Get(DotnetRuntimeRepoOwner, DotnetRuntimeRepoName, pr);
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

        Metadata.Add(argument, string.Join(',', prInfos.Select(pr => $"{pr.Repo};{pr.Branch}")));

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
                .Where(number => uint.TryParse(number, out uint value) && value is > 0 and < 1_000_000_000)
                .Select(int.Parse)
                .ToArray();
        }
    }

    private bool ShouldLinkToPROrBranch =>
        GetConfigFlag("LinkToPR", true) &&
        !CustomArguments.Contains("-NoPRLink", StringComparison.OrdinalIgnoreCase);

    private bool ShouldDeleteVM => GetConfigFlag("ShouldDeleteVM", true);

    private bool ShouldMentionJobInitiator => GetConfigFlag("ShouldMentionJobInitiator", true);

    private bool ShouldPostDiffsComment => GetConfigFlag("ShouldPostDiffsComment", true);

    private bool GetConfigFlag(string name, bool @default)
    {
        if (ConfigurationService.TryGet(null, $"RuntimeUtils.{name}", out string flagStr) &&
            bool.TryParse(flagStr, out bool flag))
        {
            return flag;
        }

        return @default;
    }

    private string GetConfigFlag(string name, string @default)
    {
        if (ConfigurationService.TryGet(null, $"RuntimeUtils.{name}", out string flag))
        {
            return flag;
        }

        return @default;
    }

    private string CreateCloudInitScript()
    {
        return
            $"""
            #cloud-config
                runcmd:
                    - apt-get update
                    - apt-get install -y dotnet-sdk-6.0
                    - cd /home
                    - git clone --no-tags --single-branch --progress https://github.com/MihaZupan/runtime-utils
                    - cd runtime-utils/Runner
                    - HOME=/root JOB_ID={JobId} dotnet run -c Release
            """;
    }

    public async Task RunJobAsync()
    {
        LogsReceived("Starting ...");

        if (!Program.AzureEnabled)
        {
            LogsReceived("No Azure support. Aborting ...");
            NotifyJobCompletion();
            return;
        }

        TrackingIssue = await Github.Issue.Create(
            IssueRepositoryOwner,
            IssueRepositoryName,
            new NewIssue(JobTitle)
            {
                Body = $"Build is in progress - see {ProgressDashboardUrl}\n" + (FromGithubComment && ShouldLinkToPROrBranch ? $"{TestedPROrBranchLink}\n" : "")
            });

        try
        {
            using var jobTimeoutCts = new CancellationTokenSource(TimeSpan.FromHours(5));
            var jobTimeout = jobTimeoutCts.Token;

            using var ctsReg = _idleTimeoutCts.Token.Register(() =>
            {
                LogsReceived("Job idle timeout exceeded, terminating ...");
                jobTimeoutCts.Cancel();
                _jobCompletionTcs.TrySetCanceled();
            });

            LogsReceived($"Using custom arguments: '{CustomArguments}'");

            await ParsePRListAsync(CustomArguments, "dependsOn");
            await ParsePRListAsync(CustomArguments, "combineWith");

            if (UseHetzner)
            {
                await RunHetznerVirtualMachineAsync(jobTimeout);
            }
            else
            {
                await RunAzureVirtualMachineAsync(jobTimeout);
            }

            LastSystemInfo = null;

            await ArtifactReceivedAsync("build-logs.txt", new MemoryStream(Encoding.UTF8.GetBytes(_logs.ToString())), jobTimeout);

            bool shouldHideDiffs = _frameworksDiffSummary?.Length > CommentLengthLimit / 2;

            string frameworksDiffs =
                $"### Diffs\n\n" +
                (shouldHideDiffs ? "<details>\n<summary>Diffs</summary>\n\n" : "") +
                $"```\n" +
                $"{_frameworksDiffSummary}\n" +
                $"```\n" +
                (shouldHideDiffs ? "\n</details>\n" : "") +
                $"\n\n";

            bool gotAnyDiffs = _frameworksDiffSummary is not null;

            await UpdateIssueBodyAsync(
                $"[Build]({ProgressDashboardUrl}) completed in {GetElapsedTime()}.\n" +
                (gotAnyDiffs && ShouldLinkToPROrBranch ? TestedPROrBranchLink : "") +
                "\n\n" +
                (gotAnyDiffs ? frameworksDiffs : "") +
                (gotAnyDiffs ? GetArtifactList() : ""));

            if (gotAnyDiffs && ShouldPostDiffsComment)
            {
                await PostDiffExamplesAsync();
            }

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
            Stopwatch.Stop();

            NotifyJobCompletion();

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

            _frameworksDiffsZipFile.Dispose();

            foreach (TempFile file in _frameworksDiffFiles.Values)
            {
                file.Dispose();
            }
        }
    }

    private async Task RunAzureVirtualMachineAsync(CancellationToken jobTimeout)
    {
        string cpuType = UseArm ? "ARM64" : (UseIntelCpu ? "X64Intel" : "X64Amd");

        string defaultVmSize = UseArm ? "DXpds_v5" : (UseIntelCpu ? "DXds_v5" : "DXads_v5");
        defaultVmSize = $"Standard_{defaultVmSize.Replace("X", Fast ? "16" : "8")}";

        string vmSize = GetConfigFlag($"Azure.VMSize{(Fast ? "Fast" : "")}{cpuType}", defaultVmSize);

        string templateJson = await Http.GetStringAsync("https://gist.githubusercontent.com/MihaZupan/5385b7153709beae35cdf029eabf50eb/raw/AzureVirtualMachineTemplate.json", jobTimeout);

        var deploymentContent = new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
        {
            Template = BinaryData.FromString(templateJson),
            Parameters = BinaryData.FromObjectAsJson(new
            {
                runnerId = new { value = ExternalId },
                osDiskSizeGiB = new { value = 64 },
                virtualMachineSize = new { value = vmSize },
                adminPassword = new { value = $"{JobId}aA1" },
                customData = new { value = Convert.ToBase64String(Encoding.UTF8.GetBytes(CreateCloudInitScript())) },
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

            string deploymentName = $"runner-deployment-{ExternalId}";
            var armDeployments = resourceGroup.GetArmDeployments();
            var deployment = (await armDeployments.CreateOrUpdateAsync(WaitUntil.Completed, deploymentName, deploymentContent, jobTimeout)).Value;

            LogsReceived("Azure deployment complete");

            await _jobCompletionTcs.Task.WaitAsync(jobTimeout);
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

            NotifyJobCompletion();
        }
    }

    private async Task RunHetznerVirtualMachineAsync(CancellationToken jobTimeout)
    {
        string cpuType = UseArm ? "ARM64" : (UseIntelCpu ? "X64Intel" : "X64Amd");

        string serverType = Fast
            ? GetConfigFlag($"Hetzner.VMSizeFast{cpuType}", UseArm ? "cax41" : (UseIntelCpu ? "cx51" : "cpx51"))
            : GetConfigFlag($"Hetzner.VMSize{cpuType}", UseArm ? "cax31" : (UseIntelCpu ? "cx41" : "cpx41"));

        LogsReceived($"Starting a Hetzner VM ({serverType}) ...");

        HetznerServerResponse server = await Hetzner.CreateServerAsync(
            $"runner-{ExternalId}",
            GetConfigFlag($"HetznerImage{Architecture}", "ubuntu-22.04"),
            GetConfigFlag($"HetznerLocation{cpuType}", UseArm || UseIntelCpu ? "fsn1" : "ash"),
            serverType,
            CreateCloudInitScript(),
            jobTimeout);

        HetznerServerResponse.HetznerServer serverInfo = server.Server ?? throw new Exception("No server info");
        HetznerServerResponse.HetznerServerType vmType = serverInfo.ServerType;

        try
        {
            LogsReceived($"VM starting (CpuType={cpuType} CPU={vmType?.Cores} Memory={vmType?.Memory}) ...");

            await _jobCompletionTcs.Task.WaitAsync(jobTimeout);
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

    private void QueueResourceDeletion(Func<Task> func, string info)
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

        if (kb >= 1)
        {
            return $"{(int)kb} KB";
        }

        return $"{size} B";
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
        _idleTimeoutCts.CancelAfter(IdleTimeoutMs);
    }

    public async Task ArtifactReceivedAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _artifactsCount) > 128)
        {
            Interlocked.Decrement(ref _artifactsCount);
            LogsReceived($"Too many artifacts received, skipping {fileName}");
            return;
        }

        bool disposeStreamAfterUpload = false;

        if (fileName == "diff-frameworks.txt")
        {
            using var buffer = new MemoryStream(new byte[128 * 1024]);
            await contentStream.CopyToAsync(buffer, cancellationToken);
            buffer.SetLength(buffer.Position);
            buffer.Position = 0;
            byte[] bytes = buffer.ToArray();
            contentStream = new MemoryStream(bytes);

            _frameworksDiffSummary = Encoding.UTF8.GetString(bytes);
        }
        else if (fileName == "jit-diffs-frameworks.zip")
        {
            LogsReceived("Saving jit-diffs-frameworks.zip");

            await using (var fs = File.OpenWrite(_frameworksDiffsZipFile.Path))
            {
                await contentStream.CopyToAsync(fs, cancellationToken);
            }

            contentStream = File.OpenRead(_frameworksDiffsZipFile.Path);
            disposeStreamAfterUpload = true;
        }

        BlobClient blobClient = _parent.ArtifactsBlobContainerClient.GetBlobClient($"{ExternalId}/{fileName}");

        await blobClient.UploadAsync(contentStream, new BlobUploadOptions
        {
            AccessTier = AccessTier.Hot
        }, cancellationToken);

        if (disposeStreamAfterUpload)
        {
            await contentStream.DisposeAsync();
        }

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

    public void NotifyJobCompletion()
    {
        _jobCompletionTcs.TrySetResult();
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

    private async Task PostDiffExamplesAsync()
    {
        try
        {
            await ExtractFrameworksDiffsZipAsync();

            await PostDiffExamplesAsync(regressions: true);
            await PostDiffExamplesAsync(regressions: false);
        }
        catch (Exception ex)
        {
            Logger.DebugLog($"Failed to post diff examples: {ex}");
        }

        async Task PostDiffExamplesAsync(bool regressions)
        {
            var allChanges = await GetDiffMarkdownAsync(JitDiffUtils.ParseDiffEntries(_frameworksDiffSummary, regressions));

            string changes = JitDiffUtils.GetCommentMarkdown(allChanges.Diffs, CommentLengthLimit, regressions, out bool truncated);

            Logger.DebugLog($"Found {allChanges.Diffs.Length} changes, comment length={changes.Length} for {nameof(regressions)}={regressions}");

            if (changes.Length != 0)
            {
                if (allChanges.NoisyDiffsRemoved)
                {
                    changes = $"{changes}\n\nNote: some changes were skipped as they were likely noise.";
                }

                if (truncated)
                {
                    changes = $"{changes}\n\nLarger list of diffs: {await PostLargeDiffGistAsync(allChanges.Diffs, regressions)}";
                }

                await Github.Issue.Comment.Create(IssueRepositoryOwner, IssueRepositoryName, TrackingIssue.Number, changes);
            }
        }

        async Task<string> PostLargeDiffGistAsync(string[] diffs, bool regressions)
        {
            var newGist = new NewGist
            {
                Description = $"JIT diffs {(regressions ? "regressions" : "improvements")} for {TrackingIssue.HtmlUrl}",
                Public = false
            };

            const int GistLengthLimit = 900 * 1024;

            string md = JitDiffUtils.GetCommentMarkdown(diffs, GistLengthLimit, regressions, out _);

            newGist.Files.Add(regressions ? "Regressions.md" : "Improvements.md", md);

            Gist gist = await Github.Gist.Create(newGist);

            return gist.HtmlUrl;
        }

        async Task<(string[] Diffs, bool NoisyDiffsRemoved)> GetDiffMarkdownAsync((string Description, string DasmFile, string Name)[] diffs)
        {
            if (diffs.Length == 0)
            {
                return (Array.Empty<string>(), false);
            }

            bool noisyMethodsRemoved = false;
            bool includeKnownNoise = IncludeKnownNoise;
            bool includeRemovedMethod = IncludeRemovedMethodImprovements;
            bool IncludeNewMethod = IncludeNewMethodRegressions;

            var result = await diffs
                .ToAsyncEnumerable()
                .Where(diff => includeRemovedMethod || !IsRemovedMethod(diff.Description))
                .Where(diff => IncludeNewMethod || !IsNewMethod(diff.Description))
                .SelectAwait(async diff =>
                {
                    if (!_frameworksDiffFiles.TryGetValue((diff.DasmFile, Main: true), out TempFile mainDiffsFile) ||
                        !_frameworksDiffFiles.TryGetValue((diff.DasmFile, Main: false), out TempFile prDiffsFile))
                    {
                        return string.Empty;
                    }

                    LogsReceived($"Generating diffs for {diff.Name}");

                    StringBuilder sb = new();

                    sb.AppendLine("<details>");
                    sb.AppendLine($"<summary>{diff.Description} - {diff.Name}</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```diff");

                    using var baseFile = new TempFile("txt");
                    using var prFile = new TempFile("txt");

                    await File.WriteAllTextAsync(baseFile.Path, await JitDiffUtils.TryGetMethodDumpAsync(mainDiffsFile.Path, diff.Name));
                    await File.WriteAllTextAsync(prFile.Path, await JitDiffUtils.TryGetMethodDumpAsync(prDiffsFile.Path, diff.Name));

                    List<string> lines = new();
                    await ProcessHelper.RunProcessAsync("git", $"diff --minimal --no-index -U20000 {baseFile} {prFile}", lines);

                    if (lines.Count == 0)
                    {
                        return string.Empty;
                    }
                    else
                    {
                        foreach (string line in lines)
                        {
                            if (ShouldSkipLine(line.AsSpan().TrimStart()))
                            {
                                continue;
                            }

                            if (!includeKnownNoise && LineIsIndicativeOfKnownNoise(line.AsSpan().TrimStart()))
                            {
                                noisyMethodsRemoved = true;
                                return string.Empty;
                            }

                            sb.AppendLine(line);
                        }
                    }

                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                    sb.AppendLine();

                    string result = sb.ToString();

                    Logger.DebugLog($"Generated diff for '{diff.Name}':\n{result}");

                    return result;
                })
                .Where(diff => !string.IsNullOrEmpty(diff))
                .Take(20)
                .ToArrayAsync();

            return (result, noisyMethodsRemoved);

            static bool IsRemovedMethod(ReadOnlySpan<char> description) =>
                description.Contains("-100.", StringComparison.Ordinal);

            static bool IsNewMethod(ReadOnlySpan<char> description) =>
                description.Contains("∞ of base", StringComparison.Ordinal) ||
                description.Contains("Infinity of base", StringComparison.Ordinal);

            static bool ShouldSkipLine(ReadOnlySpan<char> line)
            {
                return
                    line.StartsWith("diff --git", StringComparison.Ordinal) ||
                    line.StartsWith("index ", StringComparison.Ordinal) ||
                    line.StartsWith("+++", StringComparison.Ordinal) ||
                    line.StartsWith("---", StringComparison.Ordinal) ||
                    line.StartsWith("@@", StringComparison.Ordinal) ||
                    line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal) ||
                    line.StartsWith("; ============================================================", StringComparison.Ordinal);
            }

            static bool LineIsIndicativeOfKnownNoise(ReadOnlySpan<char> line)
            {
                if (line.IsEmpty || line[0] is not ('+' or '-'))
                {
                    return false;
                }

                return line.Contains("CORINFO_HELP_CLASSINIT_SHARED_DYNAMICCLASS", StringComparison.Ordinal);
            }
        }

        async Task ExtractFrameworksDiffsZipAsync()
        {
            Stopwatch extractTime = Stopwatch.StartNew();
            LogsReceived("Extracting Frameworks diffs zip ...");

            await using Stream zipStream = File.OpenRead(_frameworksDiffsZipFile.Path);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName;
                string dasmFile = Path.GetFileName(name);

                if (!dasmFile.EndsWith(".dasm", StringComparison.Ordinal) ||
                    !name.Contains("dasmset_", StringComparison.Ordinal))
                {
                    continue;
                }

                bool isMain = name.Contains("dasmset_1", StringComparison.Ordinal);

                var tempFile = new TempFile("txt");
                _frameworksDiffFiles.Add((dasmFile, isMain), tempFile);

                entry.ExtractToFile(tempFile.Path, overwrite: true);
            }

            LogsReceived($"Finished extracting Frameworks diffs zip in {extractTime.Elapsed.TotalSeconds:N1} seconds");
        }
    }
}
