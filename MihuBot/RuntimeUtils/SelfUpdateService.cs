using System.Diagnostics;
using MihuBot.Configuration;
using Octokit;

#nullable enable

namespace MihuBot.RuntimeUtils;

// Polls GitHub for new commits on the deployment branch. When a new commit is
// detected, invokes the local build script (deploy/build-latest.sh) to produce
// next_update/artifacts.tar.gz and signals shutdown so the external runner loop
// applies the update. Failures are surfaced via the debug logger.
public sealed class SelfUpdateService : IHostedService
{
    // Defaults; overridable via ConfigurationService keys under "SelfUpdate.".
    private const string DefaultOwner = "MihaZupan";
    private const string DefaultRepo = "MihuBot";
    private const string DefaultBranch = "main";
    private const string DefaultBuildScript = "/usr/local/bin/build-latest.sh";
    private const int DefaultPollIntervalSeconds = 2 * 60;
    private const int DefaultBuildTimeoutSeconds = 30 * 60;

    private readonly Logger _logger;
    private readonly GitHubClient _github;
    private readonly IConfigurationService _configuration;
    private readonly ServiceConfiguration _serviceConfiguration;

    private CancellationTokenSource? _stoppingCts;
    private Task? _pollTask;

    // Last SHA we attempted to build. Used to avoid retrying a failing SHA on
    // every poll; we only reattempt once main has moved past it.
    private string? _lastAttemptedSha;

    public SelfUpdateService(Logger logger, GitHubClient github, IConfigurationService configuration, ServiceConfiguration serviceConfiguration)
    {
        _logger = logger;
        _github = github;
        _configuration = configuration;
        _serviceConfiguration = serviceConfiguration;
    }

    private string GetString(string key, string defaultValue) =>
        _configuration.TryGet(null, key, out string value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Only auto-update in production (Linux containers). On dev boxes we do
        // not want the service polling GitHub or shelling out to build scripts.
        if (!OperatingSystem.IsLinux())
        {
            return Task.CompletedTask;
        }

        _stoppingCts = new CancellationTokenSource();

        using (ExecutionContext.SuppressFlow())
        {
            _pollTask = Task.Run(() => PollLoopAsync(_stoppingCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stoppingCts is null)
        {
            return;
        }

        try
        {
            _stoppingCts.Cancel();
        }
        catch { }

        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.WaitAsync(cancellationToken);
            }
            catch { }
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        // Delay initial poll so we don't race host startup / immediately rebuild
        // if we come up on a slightly stale build (e.g. right after a manual
        // deploy).
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch (OperationCanceledException) { return; }

        // This loop is the whole feature; nothing except an explicit stop signal
        // may exit it. Every exception (network, GitHub API, build script,
        // etc.) is swallowed so a bad build can never disable the update
        // mechanism. Logger.DebugAsync swallows its own exceptions.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync($"SelfUpdate poll failed: {ex}", truncateToFile: true);
            }

            int pollSeconds = _configuration.GetOrDefault(null, "SelfUpdate.PollIntervalSeconds", DefaultPollIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (_serviceConfiguration.PauseSelfUpdate)
        {
            return;
        }

        string owner = GetString("SelfUpdate.Owner", DefaultOwner);
        string repo = GetString("SelfUpdate.Repo", DefaultRepo);
        string branch = GetString("SelfUpdate.Branch", DefaultBranch);

        Reference branchRef = await _github.Git.Reference.Get(owner, repo, $"heads/{branch}");
        string latestSha = branchRef.Object.Sha;
        string currentSha = SharedHelpers.GetCommitId();

        if (string.Equals(latestSha, currentSha, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(latestSha, _lastAttemptedSha, StringComparison.OrdinalIgnoreCase))
        {
            // We already tried building this SHA and it failed; wait for main
            // to move before trying again to avoid tight rebuild loops.
            return;
        }

        // Record the attempt BEFORE running the build so that even if the build
        // throws unexpectedly we won't keep retrying the same broken SHA on
        // every poll.
        _lastAttemptedSha = latestSha;

        _logger.DebugLog($"SelfUpdate: {owner}/{repo}@{branch} is at {latestSha}, current build is {currentSha}. Starting build ...");

        bool success;
        string output;
        try
        {
            (success, output) = await RunBuildAsync(owner, repo, branch, cancellationToken);
        }
        catch (Exception ex)
        {
            success = false;
            output = $"Build threw unexpectedly: {ex}";
        }

        if (success)
        {
            await _logger.DebugAsync($"SelfUpdate: build for {latestSha} succeeded, initiating restart.");
            ProgramState.BotStopTCS.TrySetResult();
        }
        else
        {
            await _logger.DebugAsync($"SelfUpdate: build for {latestSha} failed.\n\n{output}", truncateToFile: true);
        }
    }

    private async Task<(bool Success, string Output)> RunBuildAsync(string owner, string repo, string branch, CancellationToken cancellationToken)
    {
        string script = GetString("SelfUpdate.BuildScript", DefaultBuildScript);
        int timeoutSeconds = _configuration.GetOrDefault(null, "SelfUpdate.BuildTimeoutSeconds", DefaultBuildTimeoutSeconds);

        var outputSb = new StringBuilder();
        void Append(string? line)
        {
            if (line is null) return;
            lock (outputSb) outputSb.AppendLine(line);
        }

        string outTarball;
        try
        {
            string nextUpdateDir = Path.Combine(Environment.CurrentDirectory, "next_update");
            Directory.CreateDirectory(nextUpdateDir);
            outTarball = Path.Combine(nextUpdateDir, "artifacts.tar.gz");
            try { File.Delete(outTarball); } catch { }
        }
        catch (Exception ex)
        {
            return (false, $"Failed to prepare next_update dir: {ex}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = script,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(outTarball);
        psi.Environment["MIHUBOT_REPO_URL"] = $"https://github.com/{owner}/{repo}";
        psi.Environment["MIHUBOT_BRANCH"] = branch;

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start build script '{script}': {ex}");
        }

        // Hard cap the build. Linked with the outer stopping token so shutdown
        // also aborts an in-flight build promptly. Either cancellation path
        // just kills the process and reports failure - it never leaks out of
        // this method.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            Append(cancellationToken.IsCancellationRequested
                ? "[SelfUpdate] Build aborted: service is stopping."
                : $"[SelfUpdate] Build timed out after {timeoutSeconds}s.");
        }
        catch (Exception ex)
        {
            Append($"[SelfUpdate] WaitForExitAsync threw: {ex}");
        }

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Append($"[SelfUpdate] Kill failed: {ex}"); }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); } catch { }
        }

        bool success;
        try
        {
            success = process.HasExited && process.ExitCode == 0 && File.Exists(outTarball);
        }
        catch (Exception ex)
        {
            Append($"[SelfUpdate] Failed to inspect process exit: {ex}");
            success = false;
        }

        if (!success)
        {
            try { File.Delete(outTarball); } catch { }
        }

        string output;
        lock (outputSb) output = outputSb.ToString();

        return (success, output);
    }
}
