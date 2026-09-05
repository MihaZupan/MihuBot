using Azure.Storage.Sas;
using MihuBot.DB.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils.Jobs;

public sealed class JitDiffJob : JobBase
{
    public override string JobTitlePrefix => $"JitDiff {Architecture}";

    public JitDiffJob(RuntimeUtilsService parent, BranchReference branch, string githubCommenterLogin, string arguments)
        : base(parent, branch, githubCommenterLogin, arguments)
    { }

    public JitDiffJob(RuntimeUtilsService parent, string baseRepository, string baseBranch, string baseCommit, string patchContent, string testedLink, string githubCommenterLogin, string arguments, bool forceHelix)
        : base(parent, baseRepository, baseBranch, baseCommit, patchContent, testedLink, githubCommenterLogin, arguments, forceHelix)
    { }

    public JitDiffJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override Task InitializeAsync(CancellationToken jobTimeout)
    {
        var container = Parent.JitDiffExtraAssembliesBlobContainerClient;

        if (container is null)
        {
            // Without blob storage the runner skips diffing extra assemblies.
            Log("Extra assemblies storage is not configured, they won't be diffed");
            return Task.CompletedTask;
        }

        var expiry = DateTimeOffset.UtcNow.Add(MaxJobDuration);

        var blobClient = container.GetBlobClient(NuGetExtraAssembliesJob.FullBlobName);
        Metadata.Add("JitDiffExtraAssembliesUrl", blobClient.GenerateSasUri(BlobSasPermissions.Read, expiry).AbsoluteUri);

        var subsetBlobClient = container.GetBlobClient(NuGetExtraAssembliesJob.SubsetBlobName);
        Metadata.Add("JitDiffExtraAssembliesSubsetUrl", subsetBlobClient.GenerateSasUri(BlobSasPermissions.Read, expiry).AbsoluteUri);

        return Task.CompletedTask;
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        bool nuget = CustomArguments.Contains("-nuget", StringComparison.OrdinalIgnoreCase);

        if (!nuget &&
            !CustomArguments.Contains("-jitutils", StringComparison.OrdinalIgnoreCase) &&
            await TrySignalAvailableRunnerAsync())
        {
            await JobCompletionTcs.Task;
        }
        else
        {
            int coreCount = nuget ? 32 : 16;
            await RunOnNewVirtualMachineAsync(defaultAzureCoreCount: coreCount, jobTimeout);
        }

        LastSystemInfo = null;

        await SetFinalTrackingIssueBodyAsync(HasDiffExamples
            ? $"[Browse JIT diff results and examples]({DiffExamplesUrl})"
            : "");
    }
}
