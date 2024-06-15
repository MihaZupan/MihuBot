using Azure.Core;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager;
using Azure;
using Octokit;
using System.IO.Compression;
using static MihuBot.Helpers.HetznerClient;

namespace MihuBot.RuntimeUtils;

public sealed class JitDiffJob : JobBase
{
    private string _frameworksDiffSummary;
    private readonly TempFile _frameworksDiffsZipFile = new("zip");
    private readonly Dictionary<(string DasmFile, bool Main), TempFile> _frameworksDiffFiles = new();

    public string Architecture => UseArm ? "ARM64" : "X64";

    private bool UseArm => CustomArguments.Contains("-arm", StringComparison.OrdinalIgnoreCase);
    private bool UseIntelCpu => CustomArguments.Contains("-intel", StringComparison.OrdinalIgnoreCase);
    private bool Fast => CustomArguments.Contains("-fast", StringComparison.OrdinalIgnoreCase);
    private bool IncludeKnownNoise => CustomArguments.Contains("-includeKnownNoise", StringComparison.OrdinalIgnoreCase);
    private bool IncludeNewMethodRegressions => CustomArguments.Contains("-includeNewMethodRegressions", StringComparison.OrdinalIgnoreCase);
    private bool IncludeRemovedMethodImprovements => CustomArguments.Contains("-includeRemovedMethodImprovements", StringComparison.OrdinalIgnoreCase);

    private bool ShouldDeleteVM => GetConfigFlag("ShouldDeleteVM", true);
    private bool ShouldPostDiffsComment => GetConfigFlag("ShouldPostDiffsComment", true);

    public bool UseHetzner =>
        GetConfigFlag("ForceHetzner", false) ||
        CustomArguments.Contains("-hetzner", StringComparison.OrdinalIgnoreCase);

    private string _jobTitle;
    public override string JobTitle => _jobTitle ??= PullRequest is null
        ? $"[{Architecture}] {Metadata["PrRepo"]}/{Metadata["PrBranch"]}".TruncateWithDotDotDot(99)
        : $"[{Architecture}] [{PullRequest.User.Login}] {PullRequest.Title}".TruncateWithDotDotDot(99);

    public JitDiffJob(RuntimeUtilsService parent, string repository, string branch, string githubCommenterLogin, string arguments)
        : base(parent, repository, branch, githubCommenterLogin, arguments)
    { }

    public JitDiffJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override string GetInitialIssueBody()
    {
        return
            $"""
            Job is in progress - see {ProgressDashboardUrl}
            {(FromGithubComment && ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}

            """;
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        try
        {
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
                $"[Job]({ProgressDashboardUrl}) completed in {GetElapsedTime()}.\n" +
                (gotAnyDiffs && ShouldLinkToPROrBranch ? TestedPROrBranchLink : "") +
                "\n\n" +
                (gotAnyDiffs ? frameworksDiffs : "") +
                (gotAnyDiffs ? GetArtifactList() : ""));

            if (gotAnyDiffs && ShouldPostDiffsComment)
            {
                await PostDiffExamplesAsync();
            }
        }
        finally
        {
            _frameworksDiffsZipFile.Dispose();

            foreach (TempFile file in _frameworksDiffFiles.Values)
            {
                file.Dispose();
            }
        }
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (fileName == "diff-frameworks.txt")
        {
            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 128 * 1024, cancellationToken);
            _frameworksDiffSummary = Encoding.UTF8.GetString(bytes);
            return replacement;
        }

        if (fileName == "jit-diffs-frameworks.zip")
        {
            LogsReceived("Saving jit-diffs-frameworks.zip");

            await using (var fs = File.OpenWrite(_frameworksDiffsZipFile.Path))
            {
                await contentStream.CopyToAsync(fs, cancellationToken);
            }

            return File.OpenRead(_frameworksDiffsZipFile.Path);
        }

        return null;
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

                return
                    line.Contains("CORINFO_HELP_CLASSINIT_SHARED_DYNAMICCLASS", StringComparison.Ordinal) ||
                    line.Contains("ProcessorIdCache:RefreshCurrentProcessorId", StringComparison.Ordinal) ||
                    line.Contains("Interop+Sys:SchedGetCpu()", StringComparison.Ordinal);
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

    private string CreateCloudInitScript()
    {
        return
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
    }

    private async Task RunAzureVirtualMachineAsync(CancellationToken jobTimeout)
    {
        string cpuType = UseArm ? "ARM64" : (UseIntelCpu ? "X64Intel" : "X64Amd");

        string defaultVmSize = UseArm ? "DXpds_v5" : (UseIntelCpu ? "DXds_v5" : "DXads_v6");
        defaultVmSize = $"Standard_{defaultVmSize.Replace("X", Fast ? "16" : "8")}";

        string vmConfigName = $"{(Fast ? "Fast" : "")}{cpuType}";
        string vmSize = GetConfigFlag($"Azure.VMSize{vmConfigName}", defaultVmSize);

        string templateJson = await Http.GetStringAsync("https://gist.githubusercontent.com/MihaZupan/5385b7153709beae35cdf029eabf50eb/raw/AzureVirtualMachineTemplate.json", jobTimeout);

        var deploymentContent = new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
        {
            Template = BinaryData.FromString(templateJson),
            Parameters = BinaryData.FromObjectAsJson(new
            {
                runnerId = new { value = ExternalId },
                osDiskSizeGiB = new { value = int.Parse(GetConfigFlag($"Azure.VMDisk{vmConfigName}", "64")) },
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
            _idleTimeoutCts.CancelAfter(IdleTimeoutMs * 4);

            string deploymentName = $"runner-deployment-{ExternalId}";
            var armDeployments = resourceGroup.GetArmDeployments();
            var deployment = (await armDeployments.CreateOrUpdateAsync(WaitUntil.Completed, deploymentName, deploymentContent, jobTimeout)).Value;

            LogsReceived("Azure deployment complete");

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
}
