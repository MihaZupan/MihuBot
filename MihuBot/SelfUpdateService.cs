using MihuBot.Configuration;
using Octokit;
using System.Buffers.Binary;

#nullable enable

namespace MihuBot;

// Polls GitHub for new commits on the deployment branch. A new commit is only built
// if it's signed by a trusted key and is newer than what we're running.
// If so, invokes the local build script (deploy/build-latest.sh) to produce
// next_update/artifacts.tar.gz and signals shutdown so the external runner loop
// applies the update. Failures are surfaced via the debug logger.
public sealed class SelfUpdateService : PeriodicBackgroundService
{
    // Defaults; overridable via ConfigurationService keys under "SelfUpdate.".
    private const string DefaultOwner = "MihaZupan";
    private const string DefaultRepo = "MihuBot";
    private const string DefaultBranch = "main";
    private const string DefaultBuildScript = "/usr/local/bin/build-latest.sh";
    // Not configurable. Excludes "web-flow", so web UI commits aren't auto-deployed.
    private const string TrustedCommitter = "MihaZupan";

    // SSH public key blobs the signature must have been made with. GitHub's own
    // verification would accept any key on the account, including a newly added one.
    // Overridable (comma-separated) via "SelfUpdate.TrustedSigningKeys" for rotation.
    private const string DefaultTrustedSigningKeys = "AAAAGnNrLXNzaC1lZDI1NTE5QG9wZW5zc2guY29tAAAAIPBDYKbB0EY+xYLyaRCHjyRyBUfwt8yyxWYyNhUgYD8qAAAABHNzaDo=";
    private const int DefaultPollIntervalSeconds = 1 * 60;
    private const int DefaultBuildTimeoutSeconds = 30 * 60;

    private readonly Logger _logger;
    private readonly GitHubClient _github;
    private readonly IConfigurationService _configuration;
    private readonly ServiceConfiguration _serviceConfiguration;

    // Last SHA we attempted to build. Used to avoid retrying a failing SHA on
    // every poll; we only reattempt once main has moved past it.
    private string? _lastAttemptedSha;

    // Only used to avoid logging the same rejection on every poll.
    private string? _lastRejectedSha;

    public SelfUpdateService(Logger logger, GitHubClient github, IConfigurationService configuration, ServiceConfiguration serviceConfiguration)
        : base(new PeriodicTaskOptions
        {
            // The first poll only happens after a full interval so we don't race host startup / immediately
            // rebuild if we come up on a slightly stale build (e.g. right after a manual deploy).
            Interval = TimeSpan.FromSeconds(DefaultPollIntervalSeconds),
            FailureBackoff = TimeSpan.Zero,
        }, logger)
    {
        _logger = logger;
        _github = github;
        _configuration = configuration;
        _serviceConfiguration = serviceConfiguration;
    }

    private string GetString(string key, string defaultValue) =>
        _configuration.TryGet(null, key, out string value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;

    // Nothing except an explicit stop signal may exit this loop. Every exception (network,
    // GitHub API, build script, etc.) only fails a single poll, so a bad build can never
    // disable the update mechanism.
    protected override Task RunIterationAsync(CancellationToken cancellationToken) => PollOnceAsync(cancellationToken);

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        // Only auto-update in production (Linux containers). On dev boxes we do
        // not want the service polling GitHub or shelling out to build scripts.
        if (!OperatingSystem.IsLinux() || _serviceConfiguration.PauseSelfUpdate)
        {
            return;
        }

        string owner = GetString("SelfUpdate.Owner", DefaultOwner);
        string repo = GetString("SelfUpdate.Repo", DefaultRepo);
        string branch = GetString("SelfUpdate.Branch", DefaultBranch);

        Reference branchRef = await _github.Git.Reference.Get(owner, repo, $"heads/{branch}");
        string latestSha = branchRef.Object.Sha;
        string currentSha = BuildInfo.GetCommitId();

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

        if (await VerifyCommitAsync(owner, repo, latestSha, currentSha) is { } rejectionReason)
        {
            if (!string.Equals(latestSha, _lastRejectedSha, StringComparison.OrdinalIgnoreCase))
            {
                _lastRejectedSha = latestSha;
                await _logger.DebugAsync($"SelfUpdate: refusing to build {owner}/{repo}@{branch} ({latestSha}): {rejectionReason}");
            }
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
            (success, output) = await RunBuildAsync(owner, repo, branch, latestSha, cancellationToken);
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

    // Returns null if the commit is safe to build, otherwise the reason it was
    // rejected. Anything we can't positively verify counts as a rejection.
    private async Task<string?> VerifyCommitAsync(string owner, string repo, string latestSha, string currentSha)
    {
        try
        {
            GitHubCommit commit = await _github.Repository.Commit.Get(owner, repo, latestSha);

            if (commit.Commit?.Verification is not { Verified: true } verification)
            {
                string reason = commit.Commit?.Verification?.Reason.StringValue ?? "no verification information";
                return $"the commit signature is not verified ({reason}).";
            }

            // Verified: true means the signature matches a key on the committer's account.
            string? committer = commit.Committer?.Login;
            if (!string.Equals(committer, TrustedCommitter, StringComparison.OrdinalIgnoreCase))
            {
                return $"it was committed by an untrusted account ('{committer ?? "unknown"}', signature reason: {verification.Reason}).";
            }

            string? signingKey = TryGetSshSignaturePublicKey(verification.Signature);
            if (signingKey is null || !GetTrustedSigningKeys().Contains(signingKey, StringComparer.Ordinal))
            {
                return $"it was not signed with a trusted SSH key (got '{signingKey ?? "an unrecognized signature format"}').";
            }

            if (string.IsNullOrEmpty(currentSha) || currentSha == "unknown")
            {
                return "the currently running build doesn't report a commit id, so we can't tell whether this would be a downgrade.";
            }

            CompareResult comparison = await _github.Repository.Commit.Compare(owner, repo, currentSha, latestSha);

            // "ahead" means the new commit descends from what we're running. "diverged"
            // means history was rewritten (force push), which is fine as long as the new
            // commit is actually newer - it's signed by us either way. "behind" and
            // "identical" are downgrades / no-ops.
            if (string.Equals(comparison.Status, "diverged", StringComparison.OrdinalIgnoreCase))
            {
                GitHubCommit current = await _github.Repository.Commit.Get(owner, repo, currentSha);

                DateTimeOffset? latestDate = commit.Commit?.Committer?.Date;
                DateTimeOffset? currentDate = current.Commit?.Committer?.Date;

                if (latestDate is null || currentDate is null || latestDate <= currentDate)
                {
                    return $"history diverged from the current build {currentSha} and it is not newer ({latestDate?.ToString("u") ?? "unknown"} vs {currentDate?.ToString("u") ?? "unknown"}).";
                }
            }
            else if (!string.Equals(comparison.Status, "ahead", StringComparison.OrdinalIgnoreCase) || comparison.AheadBy <= 0)
            {
                return $"it is not newer than the current build {currentSha} (status: {comparison.Status}, ahead by {comparison.AheadBy}, behind by {comparison.BehindBy}).";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"verification failed: {ex.Message}";
        }
    }

    // Accepts a bare base64 blob or a full "sk-ssh-ed25519@openssh.com AAAA... comment" line.
    private string[] GetTrustedSigningKeys() =>
        GetString("SelfUpdate.TrustedSigningKeys", DefaultTrustedSigningKeys)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static key => key.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, string blob, ..] ? blob : key)
            .ToArray();

    // Per PROTOCOL.sshsig, the armored blob is "SSHSIG" || uint32 version || string publickey || ...
    // Returns the key in the same base64 format GitHub/authorized_keys use, or null if malformed.
    public static string? TryGetSshSignaturePublicKey(string? signature)
    {
        const string Begin = "-----BEGIN SSH SIGNATURE-----";
        const string End = "-----END SSH SIGNATURE-----";

        if (signature is null)
        {
            return null;
        }

        int start = signature.IndexOf(Begin, StringComparison.Ordinal);
        int end = signature.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            return null;
        }

        start += Begin.Length;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(signature.AsSpan(start, end - start).ToString().Replace("\r", "").Replace("\n", "").Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        ReadOnlySpan<byte> remaining = blob;

        if (!remaining.StartsWith("SSHSIG"u8))
        {
            return null;
        }

        remaining = remaining.Slice("SSHSIG"u8.Length);

        // uint32 version
        if (remaining.Length < 4)
        {
            return null;
        }
        remaining = remaining.Slice(4);

        // string publickey
        if (remaining.Length < 4)
        {
            return null;
        }
        uint keyLength = BinaryPrimitives.ReadUInt32BigEndian(remaining);
        remaining = remaining.Slice(4);

        if (keyLength == 0 || keyLength > (uint)remaining.Length)
        {
            return null;
        }

        return Convert.ToBase64String(remaining.Slice(0, (int)keyLength));
    }

    private async Task<(bool Success, string Output)> RunBuildAsync(string owner, string repo, string branch, string sha, CancellationToken cancellationToken)
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
        // Pin the build to the commit we verified - the branch may have moved since.
        psi.Environment["MIHUBOT_COMMIT"] = sha;

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
