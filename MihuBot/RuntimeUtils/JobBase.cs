using System.Net.Mime;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.DotNet.Helix.Client;
using Microsoft.DotNet.Helix.Client.Models;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using Octokit;
using static MihuBot.Helpers.HetznerClient;

namespace MihuBot.RuntimeUtils;

public abstract class JobBase
{
    protected TimeSpan MaxJobDuration { get; set; } = TimeSpan.FromHours(5);

    public const string IssueRepositoryOwner = "MihuBot";
    public const string IssueRepositoryName = "runtime-utils";
    protected const int CommentLengthLimit = 65_000;

    private readonly int IdleTimeoutMs;

    public readonly DateTime StartTime = DateTime.UtcNow;
    protected readonly RuntimeUtilsService Parent;
    private readonly RollingLog _logs = new(100_000);
    private long _artifactsCount;
    private long _totalArtifactsSize;
    protected readonly CancellationTokenSource _idleTimeoutCts = new();
    protected readonly List<(string FileName, string Url, long Size)> Artifacts = new();

    protected TaskCompletionSource JobCompletionTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CommentInfo GitHubComment { get; }
    public string GithubCommenterLogin { get; }

    protected virtual string RepoOwner => GitHubComment?.RepoOwner() ?? "dotnet";
    protected virtual string RepoName => GitHubComment?.RepoName() ?? "runtime";

    protected Logger Logger => Parent.Logger;
    public GitHubClient Github => Parent.Github;
    protected HttpClient Http => Parent.Http;
    protected IConfigurationService ConfigurationService => Parent.ConfigurationService;
    protected HetznerClient Hetzner => Parent.Hetzner;

    public Stopwatch Stopwatch { get; private set; } = Stopwatch.StartNew();
    public bool Completed => !Stopwatch.IsRunning;

    protected bool SuppressTrackingIssue { get; set; }
    public Issue TrackingIssue { get; private set; }
    public PullRequest PullRequest { get; private set; }

    private string _jobTitle;
    public string JobTitle
    {
        get
        {
            return _jobTitle ??= Create();

            string Create()
            {
                string title =
                    PullRequest is not null ? $"[{PullRequest.User.Login}] {PullRequest.Title}" :
                    Metadata.TryGetValue("PrRepo", out string prRepo) ? $"{prRepo}/{Metadata["PrBranch"]}" :
                    GitHubComment is not null ? $"For {GithubCommenterLogin} in {RepoOwner}/{RepoName}#{GitHubComment.Issue.Number}" :
                    GithubCommenterLogin is not null ? $"For {GithubCommenterLogin}" :
                    $"{StartTime.ToISODateTime()}";

                return $"[{JobTitlePrefix}] {title}".TruncateWithDotDotDot(80);
            }
        }
    }

    private string JobType => GetType().Name;

    public abstract string JobTitlePrefix { get; }

    public string TestedPROrBranchLink { get; set; }
    public string JobId { get; } = Guid.NewGuid().ToString("N");
    public string ExternalId { get; } = Snowflake.NextString();

    private string _firstErrorMessage;
    protected string FirstErrorMessage => _firstErrorMessage;
    protected virtual bool PostErrorAsGitHubComment => false;
    private bool _manuallyCancelled;

    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime? InitialRemoteRunnerContact { get; set; }
    public SystemHardwareInfo LastSystemInfo { get; set; }
    public string LastProgressSummary { get; set; }

    public bool ShouldDeleteVM { get; set; }
    public string RemoteLoginCredentials { get; protected set; }

    public string CustomArguments
    {
        get => Metadata["CustomArguments"];
        set => Metadata["CustomArguments"] = value;
    }

    public string ProgressUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/api/RuntimeUtils/Jobs/Progress?jobId={ExternalId}";
    public string ProgressDashboardUrl => $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/runtime-utils/{ExternalId}";

    public int TotalProgressSiteViews;
    public int CurrentProgressSiteViews;

    public JobBase(RuntimeUtilsService parent, string githubCommenterLogin, string arguments, CommentInfo comment = null)
    {
        Parent = parent;
        GitHubComment = comment;
        GithubCommenterLogin = githubCommenterLogin ?? comment?.User?.Login;

        IdleTimeoutMs = Parent.ConfigurationService.GetOrDefault(null, $"{nameof(RuntimeUtilsService)}.{nameof(IdleTimeoutMs)}", 5 * 60 * 1000);

        Metadata.Add("JobId", JobId);
        Metadata.Add("ExternalId", ExternalId);
        Metadata.Add("JobType", JobType);
        Metadata.Add("JobStartTime", StartTime.Ticks.ToString());

        arguments ??= string.Empty;
        arguments = arguments.SplitLines()[0].Trim();
        Metadata.Add("CustomArguments", arguments);

        var containerClient = Parent.RunnerPersistentStateBlobContainerClient;
        Uri sasUri = containerClient.GenerateSasUri(BlobContainerSasPermissions.Read, DateTimeOffset.UtcNow.Add(MaxJobDuration));
        Metadata.Add("PersistentStateSasUri", sasUri.AbsoluteUri);

        ShouldDeleteVM = GetConfigFlag("ShouldDeleteVM", true);
        SuppressTrackingIssue = IsFromAdmin && CustomArguments.Contains("-noTrackingIssue", StringComparison.OrdinalIgnoreCase);

        TestedPROrBranchLink = comment?.HtmlUrl;

        Logger.DebugLog($"Starting {JobType}: {ProgressDashboardUrl}");
    }

    public JobBase(RuntimeUtilsService parent, BranchReference branch, string githubCommenterLogin, string arguments)
        : this(parent, githubCommenterLogin, arguments)
    {
        InitMetadata("dotnet/runtime", "main", branch.Repository, branch.Branch.Name);

        TestedPROrBranchLink = $"https://github.com/{branch.Repository}/tree/{branch.Branch.Name}";
    }

    public JobBase(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment)
        : this(parent, githubCommenterLogin, arguments, comment)
    {
        PullRequest = pullRequest;

        InitMetadata(
            PullRequest.Base.Repository.FullName, PullRequest.Base.Ref,
            PullRequest.Head.Repository.FullName, PullRequest.Head.Ref);

        TestedPROrBranchLink = PullRequest.HtmlUrl;
    }

    private void InitMetadata(string baseRepo, string baseBranch, string prRepo, string prBranch)
    {
        Metadata.Add("BaseRepo", baseRepo);
        Metadata.Add("BaseBranch", baseBranch);
        Metadata.Add("PrRepo", prRepo);
        Metadata.Add("PrBranch", prBranch);
    }

    protected bool ShouldLinkToPROrBranch =>
        GetConfigFlag("LinkToPR", true) &&
        !CustomArguments.Contains("-NoPRLink", StringComparison.OrdinalIgnoreCase);

    public bool UseArm => CustomArguments.Contains("-arm", StringComparison.OrdinalIgnoreCase);
    protected string Architecture => UseArm ? "ARM64" : "X64";
    protected bool Fast => CustomArguments.Contains("-fast", StringComparison.OrdinalIgnoreCase);
    protected bool UseWindows => CustomArguments.Contains("-win", StringComparison.OrdinalIgnoreCase);
    protected bool UseHetzner => CustomArguments.Contains("-hetzner", StringComparison.OrdinalIgnoreCase);

    public bool IsFromAdmin => Parent.CheckGitHubAdminPermissions(GithubCommenterLogin);

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
        Log("Starting ...");

        if (!ProgramState.AzureEnabled)
        {
            Log("No Azure support. Aborting ...");
            NotifyJobCompletion();
            return;
        }

        if (CustomArguments.Contains("-noTimeLimit", StringComparison.OrdinalIgnoreCase))
        {
            MaxJobDuration = IsFromAdmin ? TimeSpan.FromDays(7) : MaxJobDuration * 2;
        }

        ShouldMentionJobInitiator = GetConfigFlag("ShouldMentionJobInitiator", true);

        Exception initializationException = null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await ParsePRListAsync(CustomArguments, "dependsOn", timeoutCts.Token);
            await ParsePRListAsync(CustomArguments, "combineWith", timeoutCts.Token);

            await InitializeAsync(timeoutCts.Token);

            Metadata.Add("JobMaxEndTime", (StartTime + MaxJobDuration).Ticks.ToString());
        }
        catch (Exception ex)
        {
            initializationException = ex;
        }

        bool startGithubActions = RunUsingGitHubActions && initializationException is null;

        if (SuppressTrackingIssue)
        {
            if (startGithubActions)
            {
                await Logger.DebugAsync($"Can't use GH Actions when tracking issue is suppressed: <{ProgressDashboardUrl}>");
            }
        }
        else
        {
            string customArguments = !string.IsNullOrWhiteSpace(CustomArguments)
                ? $"Using arguments: ````{CustomArguments}````"
                : string.Empty;

            TrackingIssue = await Github.Issue.Create(
                IssueRepositoryOwner,
                IssueRepositoryName,
                new NewIssue(JobTitle)
                {
                    Body =
                        $"""
                        Job is in progress - see {ProgressDashboardUrl}
                        {(ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}
                        {customArguments}

                        {(startGithubActions ? $"<!-- RUN_AS_GITHUB_ACTION_{ExternalId} -->" : "")}
                        """
                });
        }

        string logsArtifactPath = null;

        try
        {
            using var jobTimeoutCts = new CancellationTokenSource(MaxJobDuration);
            var jobTimeout = jobTimeoutCts.Token;

            using var idleCtsReg = _idleTimeoutCts.Token.Register(() =>
            {
                if (FirstErrorMessage is null)
                {
                    _firstErrorMessage = "Job idle timeout exceeded, terminating ...";
                    Log(FirstErrorMessage);
                }

                jobTimeoutCts.Cancel();
            });

            using var jobTimeoutReg = jobTimeout.Register(() =>
            {
                if (FirstErrorMessage is null)
                {
                    _firstErrorMessage = "Job duration exceeded, terminating ...";
                    Log(FirstErrorMessage);
                }

                JobCompletionTcs.TrySetCanceled();
            });

            Log($"Using custom arguments: '{CustomArguments}'");

            if (initializationException is not null)
            {
                throw initializationException;
            }

            if (RunUsingGitHubActions)
            {
                Log("Starting runner on GitHub actions ...");
            }

            if (RunUsingAzurePipelines)
            {
                await AzurePipelinesHelper.TriggerWebhookAsync(Logger, Http, "MihuBot", "MihuBot",
                    Parent.Configuration["RuntimeUtils:AzurePipelinesWebhookSecret"], $"{{\"job_id\":\"{ExternalId}\"}}");

                Log("Starting runner on Azure Pipelines ...");
                _idleTimeoutCts.CancelAfter(IdleTimeoutMs * 4);
            }

            await RunJobAsyncCore(jobTimeout);

            LastSystemInfo = null;

            await SaveLogsArtifactAsync();
        }
        catch (Exception ex)
        {
            LastSystemInfo = null;

            Log($"Uncaught exception: {ex}");

            try
            {
                await SaveLogsArtifactAsync();
            }
            catch { }

            if (!_manuallyCancelled)
            {
                await Logger.DebugAsync(ex.ToString());
            }

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
                Artifacts = Artifacts.Select(a => new CompletedJobRecord.Artifact(a.FileName, a.Url, a.Size)).ToArray(),
                LogsArtifactUrl = logsArtifactPath is null ? null : Parent.LogsStorage.GetFileUrl(logsArtifactPath, TimeSpan.MaxValue, writeAccess: false)
            });

            if (ShouldMentionJobInitiator && GithubCommenterLogin is not null && TrackingIssue is not null)
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
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(_logs.ToString()));

            try
            {
                string path = $"{ExternalId}.txt";
                await Parent.LogsStorage.UploadAsync(path, stream, CancellationToken.None);
                logsArtifactPath = path;
            }
            catch (Exception ex)
            {
                await Logger.DebugAsync(ex.ToString());
            }

            stream.Position = 0;
            await ArtifactReceivedAsync("logs.txt", stream, CancellationToken.None);
        }
    }

    protected virtual Task InitializeAsync(CancellationToken jobTimeout) => Task.CompletedTask;

    protected abstract Task RunJobAsyncCore(CancellationToken jobTimeout);

    private async Task ParsePRListAsync(string arguments, string argumentName, CancellationToken cancellationToken)
    {
        string argument = $"-{argumentName} ";

        int offset = arguments.IndexOf(argument, StringComparison.OrdinalIgnoreCase);
        if (offset < 0) return;

        arguments = arguments.Substring(offset + argument.Length);

        int length = arguments.IndexOf(' ');
        if (length >= 0)
        {
            arguments = arguments.Substring(0, length);
        }

        List<(string Repo, string Branch)> branches = [];

        foreach (string arg in arguments.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(arg => arg.Trim('#', '<', '>')))
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(branches.Count, 100);

            cancellationToken.ThrowIfCancellationRequested();

            if (GitHubHelper.TryParseIssueOrPRNumber(arg, out int prNumber) && prNumber is > 0 and < 1_000_000_000)
            {
                try
                {
                    PullRequest prInfo = await Github.PullRequest.Get(RepoOwner, RepoName, prNumber);
                    string repo = prInfo.Head.Repository.FullName;
                    string branch = prInfo.Head.Ref;

                    Log($"PR {prNumber}: {repo}/{branch}");
                    branches.Add((repo, branch));
                }
                catch
                {
                    Log($"Failed to get PR info for {prNumber}");
                    throw;
                }
            }

            if (await GitHubHelper.TryParseGithubRepoAndBranch(Github, arg) is { } repoBranch)
            {
                Log($"Branch: {repoBranch.Repository}/{repoBranch.Branch.Name}");
                branches.Add((repoBranch.Repository, repoBranch.Branch.Name));
            }
        }

        string prInfoStr = string.Join(',', branches.Select(pr => $"{pr.Repo};{pr.Branch}"));
        Log($"Adding {argumentName}: {prInfoStr}");
        Metadata.Add(argumentName, prInfoStr);
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

    private async Task UpdateIssueBodyAsync(string newBody)
    {
        if (TrackingIssue is null)
        {
            Log($"No tracking issue. New body:\n{newBody}");
            return;
        }

        IssueUpdate update = TrackingIssue.ToUpdate();
        update.Body = newBody;
        await Github.Issue.Update(IssueRepositoryOwner, IssueRepositoryName, TrackingIssue.Number, update);
    }

    protected async Task SetFinalTrackingIssueBodyAsync(string customInfo = "")
    {
        string error = FirstErrorMessage is { } message
            ? $"\n```\n{message}\n```\n"
            : string.Empty;

        string runnerDelay = InitialRemoteRunnerContact.HasValue
            ? $" (remote runner delay: {(InitialRemoteRunnerContact.Value - StartTime).ToElapsedTime(true)})"
            : string.Empty;

        string customArguments = !string.IsNullOrWhiteSpace(CustomArguments)
            ? $"Using arguments: ````{CustomArguments}````"
            : string.Empty;

        string mainCommitLink = TryFindLogLine(l => l.Contains("main commit: ", StringComparison.Ordinal)) is { } mainCommit
            ? $"Main commit: https://github.com/{Metadata["BaseRepo"]}/commit/{mainCommit.Split(' ')[^1]}"
            : string.Empty;

        string prCommitLink = TryFindLogLine(l => l.Contains("pr commit: ", StringComparison.Ordinal)) is { } prCommit
            ? $"PR commit: https://github.com/{Metadata["PrRepo"]}/commit/{prCommit.Split(' ')[^1]}"
            : string.Empty;

        await UpdateIssueBodyAsync(
            $$"""
            [Job]({{ProgressDashboardUrl}}) completed in {{GetElapsedTime()}}{{runnerDelay}}.
            {{(ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}}
            {{customArguments}}
            {{mainCommitLink}}
            {{prCommitLink}}
            {{error}}

            {{customInfo}}

            {{GetArtifactList()}}
            """);
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

    public void Log(string line)
    {
        TimeSpan elapsed = Stopwatch.Elapsed;
        int hours = elapsed.Hours;
        int minutes = elapsed.Minutes;
        int seconds = elapsed.Seconds;

        RawLogsReceived([$"[{hours:D2}:{minutes:D2}:{seconds:D2}]* {line}"]);
    }

    public void RawLogsReceived(ReadOnlySpan<string> lines)
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
                    await Logger.DebugAsync($"Failed to post comment for message '{_firstErrorMessage}'", ex);
                }
            });
        }
    }

    public async Task ArtifactReceivedAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _artifactsCount) > 128)
        {
            Interlocked.Decrement(ref _artifactsCount);
            Log($"Too many artifacts received, skipping {fileName}");
            return;
        }

        Stream newStream = await InterceptArtifactAsync(fileName, contentStream, cancellationToken);
        contentStream = newStream ?? contentStream;

        BlobClient blobClient = Parent.ArtifactsBlobContainerClient.GetBlobClient($"{ExternalId}/{fileName}");

        var options = new BlobUploadOptions
        {
            AccessTier = Path.GetExtension(fileName) == ".txt" ? AccessTier.Hot : AccessTier.Cool,
            HttpHeaders = new BlobHttpHeaders { ContentType = MediaTypeMap.GetMediaType(fileName) }
        };
        await blobClient.UploadAsync(contentStream, options, cancellationToken);

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
                Log($"Artifact '{fileName}' was not saved because it would exceed the {GetRoughSizeString(ArtifactSizeLimit)} limit");
                blobClient.DeleteIfExists(cancellationToken: cancellationToken);
                return;
            }

            Artifacts.Add((fileName, entry.ShortUrl, size));
            _totalArtifactsSize += size;
        }

        Log($"Saved artifact '{fileName}' to {entry.ShortUrl} ({GetRoughSizeString(size)})");
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
        if (JobCompletionTcs.TrySetResult())
        {
            Logger.DebugLog($"Finished job {ProgressDashboardUrl} in {GetElapsedTime()}");
        }

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
        Log(message);
        _idleTimeoutCts.Cancel();
    }

    protected async Task<bool> TrySignalAvailableRunnerAsync()
    {
        Log("Checking for available runners");

        string runnerId;

        for (int attempt = 1; ; attempt++)
        {
            runnerId = Parent.TrySignalAvailableRunner(this);

            if (runnerId is not null)
            {
                break;
            }

            if (attempt <= 3)
            {
                await Task.Delay(attempt * 100);
                continue;
            }

            return false;
        }

        Log($"Signaled an available runner: {runnerId}");

        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed.TotalSeconds < 20)
        {
            if (InitialRemoteRunnerContact.HasValue)
            {
                return true;
            }

            await Task.Delay(500);
        }

        Log("Signaled runner did not respond");
        return false;
    }

    protected async Task RunOnNewVirtualMachineAsync(int defaultAzureCoreCount, CancellationToken jobTimeout)
    {
        string linuxStartupScript =
            $"""
            wget https://mihubot.xyz/api/RuntimeUtils/Jobs/Metadata/{JobId} &
            apt-get update
            apt-get install -y dotnet-sdk-8.0
            cd /home
            git clone --no-tags --single-branch --progress https://github.com/MihaZupan/runtime-utils
            cd runtime-utils/Runner
            HOME=/root JOB_ID={JobId} dotnet run -c Release
            """;

        string windowsStartupScript =
            $"""
            winget install -e --id Git.Git
            git clone --no-tags --single-branch --progress https://github.com/MihaZupan/runtime-utils
            cd runtime-utils/Runner

            Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile 'dotnet-install.ps1'
            ./dotnet-install.ps1 -Verbose -Channel '8.0' -InstallDir dotnet-install

            $env:JOB_ID = '{JobId}';
            dotnet-install/dotnet run -c Release
            """;

        string cloudInitScript =
            $"""
            #cloud-config
                runcmd:
            """;
        cloudInitScript = $"{cloudInitScript}\n{string.Join('\n', linuxStartupScript.SplitLines().Select(line => $"        - {line}"))}";

        bool useIntelCpu = CustomArguments.Contains("-intel", StringComparison.OrdinalIgnoreCase);
        bool useHetzner = GetConfigFlag("ForceHetzner", false) || UseHetzner;

        bool useHelix =
            CustomArguments.Contains("-helix", StringComparison.OrdinalIgnoreCase) ||
            UseWindows;

        if (useHelix)
        {
            await RunAsHelixJobAsync(jobTimeout);
        }
        else if (useHetzner)
        {
            await RunHetznerVirtualMachineAsync(jobTimeout);
        }
        else
        {
            await RunAzureVirtualMachineAsync(jobTimeout);
        }

        async Task RunAzureVirtualMachineAsync(CancellationToken jobTimeout)
        {
            if (Fast)
            {
                defaultAzureCoreCount *= 2;
            }

            // Larger VMs have enough RAM for a ram disk.
            bool needsFastSideDisk = defaultAzureCoreCount < 8;

            string cpuType = UseArm ? "ARM64" : (useIntelCpu ? "X64Intel" : "X64Amd");

            string defaultVmSize =
                UseArm ? (needsFastSideDisk ? "DXpds_v6" : "DXps_v6") :
                useIntelCpu ? "DXds_v5" :
                //fast ? "FXas_v6" :
                (needsFastSideDisk ? "DXads_v6" : "DXas_v6");
            defaultVmSize = $"Standard_{defaultVmSize.Replace("X", defaultAzureCoreCount.ToString())}";

            string vmConfigName = $"{(Fast ? "Fast" : "")}{cpuType}";
            string vmSize = GetConfigFlag($"Azure.VMSize{vmConfigName}", defaultVmSize);

            string templateJson = await Http.GetStringAsync("https://gist.githubusercontent.com/MihaZupan/5385b7153709beae35cdf029eabf50eb/raw/AzureVirtualMachineTemplate.json", jobTimeout);

            string password = $"{JobId}aA1";

            var armClient = new ArmClient(ProgramState.AzureCredential);
            var subscription = await armClient.GetDefaultSubscriptionAsync(jobTimeout);

            bool deploymentComplete = false;

            AzureLocation[] locations = [AzureLocation.EastUS2, AzureLocation.EastUS, AzureLocation.WestUS3];

            for (int locationIndex = 0; locationIndex < locations.Length; locationIndex++)
            {
                AzureLocation location = locations[locationIndex];
                try
                {
                    Log($"Creating a new Azure VM ({vmSize}) in {location.DisplayName} ...");
                    await CreateDeploymentAsync(location);
                    break;
                }
                catch (RequestFailedException ex) when (!deploymentComplete && locationIndex < locations.Length - 1)
                {
                    Log($"Failed to create VM in {location.DisplayName}: {ex.ErrorCode} {ex.Message}. Retrying ...");
                }
            }

            async Task CreateDeploymentAsync(AzureLocation location)
            {
                var deploymentContent = new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
                {
                    Template = BinaryData.FromString(templateJson),
                    Parameters = BinaryData.FromObjectAsJson(new
                    {
                        runnerId = new { value = JobId },
                        osDiskSizeGiB = new { value = int.Parse(GetConfigFlag($"Azure.VMDisk{vmConfigName}", (defaultAzureCoreCount * 8).ToString())) },
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

                Log("Creating a new Azure resource group for this deployment ...");

                string resourceGroupName = $"runtime-utils-runner-{location.Name}-{JobId}";
                var resourceGroupData = new ResourceGroupData(location);
                var resourceGroups = subscription.GetResourceGroups();
                var resourceGroup = (await resourceGroups.CreateOrUpdateAsync(WaitUntil.Completed, resourceGroupName, resourceGroupData, jobTimeout)).Value;

                try
                {
                    Log($"Starting deployment of Azure VM ({vmSize}) ...");
                    _idleTimeoutCts.CancelAfter(IdleTimeoutMs * 4);

                    string deploymentName = $"runner-deployment-{location.Name}-{JobId}";
                    var armDeployments = resourceGroup.GetArmDeployments();
                    var deployment = (await armDeployments.CreateOrUpdateAsync(WaitUntil.Completed, deploymentName, deploymentContent, jobTimeout)).Value;

                    Log("Azure deployment complete");
                    deploymentComplete = true;

                    if ((await resourceGroup.GetPublicIPAddresses().GetAllAsync(jobTimeout).FirstOrDefaultAsync(jobTimeout)) is { } ip)
                    {
                        RemoteLoginCredentials = $"ssh runner@{ip.Data.IPAddress}  {password}";
                    }

                    await JobCompletionTcs.Task;
                }
                finally
                {
                    if (ShouldDeleteVM)
                    {
                        Log("Deleting the VM resource group");

                        QueueResourceDeletion(async () =>
                        {
                            await resourceGroup.DeleteAsync(WaitUntil.Completed, cancellationToken: CancellationToken.None);
                        }, resourceGroupName);
                    }
                    else
                    {
                        Log("Configuration opted not to delete the VM");
                    }
                }
            }
        }

        async Task RunHetznerVirtualMachineAsync(CancellationToken jobTimeout)
        {
            string cpuType = UseArm ? "ARM64" : (useIntelCpu ? "X64Intel" : "X64Amd");

            string serverType = Fast
                ? GetConfigFlag($"Hetzner.VMSizeFast{cpuType}", UseArm ? "cax41" : (useIntelCpu ? "cx52" : "cpx51"))
                : GetConfigFlag($"Hetzner.VMSize{cpuType}", UseArm ? "cax31" : (useIntelCpu ? "cx42" : "cpx41"));

            Log($"Starting a Hetzner VM ({serverType}) ...");

            HetznerServerResponse server = await Hetzner.CreateServerAsync(
                $"runner-{JobId}",
                GetConfigFlag($"HetznerImage{Architecture}", "ubuntu-22.04"),
                GetConfigFlag($"HetznerLocation{cpuType}", "hel1"),
                serverType,
                cloudInitScript,
                jobTimeout);

            HetznerServerResponse.HetznerServer serverInfo = server.Server ?? throw new Exception("No server info");
            HetznerServerResponse.HetznerServerType vmType = serverInfo.ServerType;

            try
            {
                Log($"VM starting (CpuType={cpuType} CPU={vmType?.Cores} Memory={vmType?.Memory}) ...");

                if (serverInfo.PublicNet.Ipv4?.Ip is { } ip)
                {
                    RemoteLoginCredentials = $"ssh root@{ip}  {server.RootPassword}";
                }

                await JobCompletionTcs.Task;
            }
            finally
            {
                if (ShouldDeleteVM)
                {
                    Log("Deleting the VM");

                    QueueResourceDeletion(async () =>
                    {
                        await Hetzner.DeleteServerAsync(serverInfo.Id, CancellationToken.None);
                    }, serverInfo.Id.ToString());
                }
                else
                {
                    Log("Configuration opted not to delete the VM");
                }
            }
        }

        async Task RunAsHelixJobAsync(CancellationToken jobTimeout)
        {
            string queueId = UseWindows
                ? (UseArm ? "windows.11.arm64.open" : "windows.11.amd64.client.open")
                : (UseArm ? "ubuntu.2204.armarch.open" : "ubuntu.2204.amd64.open");

            Log($"Submitting a Helix job ({queueId}) ...");

            IHelixApi api = ApiFactory.GetAnonymous();

            ISentJob job = await api.Job.Define()
                .WithType($"MihuBot/runtime-utils/{JobType}")
                .WithTargetQueue(queueId)
                .WithCreator("MihuBot")
                .WithSource($"MihuBot/{Snowflake.FromString(ExternalId)}/{GithubCommenterLogin}")
                .DefineWorkItem("runner")
                .WithCommand(UseWindows ? "PowerShell -NoProfile -ExecutionPolicy Bypass -Command \"& './start-runner.ps1'\"" : "sudo -s bash ./start-runner.sh")
                .WithSingleFilePayload(UseWindows ? "start-runner.ps1" : "start-runner.sh", UseWindows ? windowsStartupScript : linuxStartupScript)
                .AttachToJob()
                .SendAsync(cancellationToken: jobTimeout);

            try
            {
                Stopwatch jobDelayStopwatch = Stopwatch.StartNew();

                JobSummary summary = await api.Job.SummaryAsync(job.CorrelationId, jobTimeout);
                Log($"Job queued {summary.DetailsUrl} ...");

                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

                    while (!JobCompletionTcs.Task.IsCompleted && await timer.WaitForNextTickAsync(jobTimeout))
                    {
                        JobDetails details = await api.Job.DetailsAsync(job.CorrelationId, jobTimeout);

                        if (details.WorkItems.Waiting > 0 || details.WorkItems.Unscheduled > 0)
                        {
                            Log($"Waiting for Helix job to start ({(int)jobDelayStopwatch.Elapsed.TotalSeconds} sec) ...");

                            if (jobDelayStopwatch.ElapsedMilliseconds > IdleTimeoutMs * 10)
                            {
                                _idleTimeoutCts.Cancel();
                            }

                            continue;
                        }

                        if (details.WorkItems.Running == 0 && details.WorkItems.Finished > 0)
                        {
                            Log("No more running Helix work items.");
                            break;
                        }
                    }
                }
                catch when (_idleTimeoutCts.IsCancellationRequested) { }

                await JobCompletionTcs.Task;
            }
            finally
            {
                if (_idleTimeoutCts.IsCancellationRequested)
                {
                    Log("Cancelling the Helix job");

                    QueueResourceDeletion(async () =>
                    {
                        await api.Job.CancelAsync(job.CorrelationId, job.HelixCancellationToken, CancellationToken.None);
                    }, job.CorrelationId);
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
                    await Logger.DebugAsync($"Failed to delete resource {info}", ex);
                }
            }, CancellationToken.None);
        }
    }
}
