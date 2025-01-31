using Azure.Storage.Sas;

namespace MihuBot.RuntimeUtils;

public sealed class CoreRootGenerationJob : JobBase
{
    public override string JobTitlePrefix => $"CoreRootGen {Architecture}";

    public CoreRootGenerationJob(RuntimeUtilsService parent, string githubCommenterLogin, string arguments)
        : base(parent, githubCommenterLogin, arguments)
    {
        TestedPROrBranchLink = "https://github.com/dotnet/runtime";
    }

    protected override Task InitializeAsync(CancellationToken jobTimeout)
    {
        SuppressTrackingIssue = true;

        MaxJobDuration = TimeSpan.FromHours(12);

        var containerClient = Parent.CoreRoot.CoreRootBlobContainerClient;
        Uri sasUri = containerClient.GenerateSasUri(BlobContainerSasPermissions.All, DateTimeOffset.UtcNow.Add(MaxJobDuration));
        Metadata.Add("CoreRootSasUri", sasUri.AbsoluteUri);

        return Task.CompletedTask;
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        // TODO: Run on a spot VM?
        await RunOnNewVirtualMachineAsync(defaultAzureCoreCount: 4, jobTimeout: jobTimeout);
    }
}
