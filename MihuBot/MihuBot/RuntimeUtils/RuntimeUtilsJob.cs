﻿using Azure.Core;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using Azure;
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

    public Stopwatch Stopwatch { get; private set; } = new();
    public PullRequest PullRequest { get; private set; }
    public Issue TrackingIssue { get; private set; }
    public bool Completed => _jobCompletionTcs.Task.IsCompleted;

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
    private bool Fast => CustomArguments.Contains("-fast", StringComparison.OrdinalIgnoreCase);
    private bool IncludeNewMethodRegressions => CustomArguments.Contains("-includeNewMethodRegressions", StringComparison.OrdinalIgnoreCase);
    private bool IncludeRemovedMethodImprovements => CustomArguments.Contains("-includeRemovedMethodImprovements", StringComparison.OrdinalIgnoreCase);

    public bool UseHetzner =>
        GetConfigFlag("ForceHetzner", false) || UseArm || Fast ||
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

    private bool ShouldDeleteContainer => GetConfigFlag("ShouldDeleteContainer", true);

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

    public async Task RunJobAsync()
    {
        Stopwatch.Start();

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
                await RunAzureContainerInstanceAsync(jobTimeout);
            }

            await ArtifactReceivedAsync("build-logs.txt", new MemoryStream(Encoding.UTF8.GetBytes(_logs.ToString())), jobTimeout);

            Stopwatch.Stop();

            const string DetailsStart = "<details>\n<summary>Diffs</summary>\n\n";
            const string DetailsEnd = "\n</details>\n";

            bool shouldHideDiffs = _frameworksDiffSummary?.Length > CommentLengthLimit / 2;

            string frameworksDiffs =
                $"### Diffs\n\n" +
                (shouldHideDiffs ? DetailsStart : "") +
                $"```\n" +
                $"{_frameworksDiffSummary}\n" +
                $"```\n" +
                (shouldHideDiffs ? DetailsEnd : "") +
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

    private async Task RunAzureContainerInstanceAsync(CancellationToken jobTimeout)
    {
        var armClient = new ArmClient(Program.AzureCredential);
        var subscription = await armClient.GetDefaultSubscriptionAsync(jobTimeout);
        var resourceGroup = (await subscription.GetResourceGroupAsync("runtime-utils", jobTimeout)).Value;

        double memoryInGB = 16;
        double cpuCount = 4;

        if (ConfigurationService.TryGet(null, "RuntimeUtils.Azure.MemoryGB", out string memoryInGBString))
        {
            memoryInGB = double.Parse(memoryInGBString, CultureInfo.InvariantCulture);
        }

        if (ConfigurationService.TryGet(null, "RuntimeUtils.Azure.CPUCount", out string cpuCountString))
        {
            cpuCount = double.Parse(cpuCountString, CultureInfo.InvariantCulture);
        }

        var container = new ContainerInstanceContainer(
            $"runner-{ExternalId}",
            "runtimeutils.azurecr.io/runner:latest",
            new ContainerResourceRequirements(new ContainerResourceRequestsContent(memoryInGB, cpuCount)));

        container.EnvironmentVariables.Add(new ContainerEnvironmentVariable("JOB_ID") { Value = JobId });

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
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);

                containerGroupResource = (await containerGroupResource.GetAsync(jobTimeout)).Value;
            }
            while (containerGroupResource.Data?.Containers?.FirstOrDefault()?.InstanceView?.CurrentState is { } state && state.FinishOn is null);
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

            NotifyJobCompletion();
        }
    }

    private async Task RunHetznerVirtualMachineAsync(CancellationToken jobTimeout)
    {
        string serverType = Fast
            ? GetConfigFlag($"HetznerServerTypeFast{Architecture}", UseArm ? "cax41" : "cpx51")
            : GetConfigFlag($"HetznerServerType{Architecture}", UseArm ? "cax31" : "cpx41");

        if (serverType is "cpx41" or "cpx51" or "cax31" or "cax41" &&
            !CustomArguments.Contains("force-frameworks-", StringComparison.OrdinalIgnoreCase))
        {
            CustomArguments += " -force-frameworks-parallel";
        }

        LogsReceived($"Starting a Hetzner VM ({serverType}) ...");

        string userData =
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

        HetznerServerResponse server = await Hetzner.CreateServerAsync(
            $"runner-{ExternalId}",
            GetConfigFlag($"HetznerImage{Architecture}", "ubuntu-22.04"),
            GetConfigFlag($"HetznerLocation{Architecture}", UseArm ? "fsn1" : "ash"),
            serverType,
            userData,
            jobTimeout);

        HetznerServerResponse.HetznerServer serverInfo = server.Server ?? throw new Exception("No server info");
        HetznerServerResponse.HetznerServerType vmType = serverInfo.ServerType;

        try
        {
            LogsReceived($"VM starting (Arch={vmType?.Architecture} CPU={vmType?.Cores} Memory={vmType?.Memory}) ...");

            await _jobCompletionTcs.Task.WaitAsync(jobTimeout);
        }
        finally
        {
            if (ShouldDeleteContainer)
            {
                LogsReceived("Deleting the VM");

                try
                {
                    await Hetzner.DeleteServerAsync(serverInfo.Id, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    await Logger.DebugAsync($"Failed to delete Hetzner VM {serverInfo.Id}: {ex}");
                }
            }
            else
            {
                LogsReceived("Configuration opted not to delete the VM");
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

            string changes = JitDiffUtils.GetCommentMarkdown(allChanges, CommentLengthLimit, regressions, out bool truncated);

            Logger.DebugLog($"Found {allChanges.Length} changes, comment length={changes.Length} for {nameof(regressions)}={regressions}");

            if (changes.Length != 0)
            {
                if (truncated)
                {
                    changes = $"{changes}\n\nLarger list of diffs: {await PostLargeDiffGistAsync(allChanges, regressions)}";
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

        async Task<string[]> GetDiffMarkdownAsync((string Description, string DasmFile, string Name)[] diffs)
        {
            if (diffs.Length == 0)
            {
                return Array.Empty<string>();
            }

            bool includeRemovedMethod = IncludeRemovedMethodImprovements;
            bool IncludeNewMethod = IncludeNewMethodRegressions;

            return await diffs
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
        }

        async Task ExtractFrameworksDiffsZipAsync()
        {
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
        }
    }
}
