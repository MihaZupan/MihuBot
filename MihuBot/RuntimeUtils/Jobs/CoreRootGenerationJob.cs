namespace MihuBot.RuntimeUtils.Jobs;

public sealed class CoreRootGenerationJob : JobBase
{
    public override string JobTitlePrefix => $"CoreRootGen {Architecture}";

    // Generating past entries is compute heavy, so prefer free Helix machines over paying for our own VMs.
    protected override bool UseHelix => base.UseHelix || GetConfigFlag("CoreRootGenerationUseHelix", true);

    public CoreRootGenerationJob(RuntimeUtilsService parent, string githubCommenterLogin, string arguments)
        : base(parent, githubCommenterLogin, arguments)
    {
        TestedPROrBranchLink = "https://github.com/dotnet/runtime";
    }

    protected override Task InitializeAsync(CancellationToken jobTimeout)
    {
        SuppressTrackingIssue = true;

        MaxJobDuration = TimeSpan.FromHours(12);

        Metadata.Add("CoreRootSasUri", Parent.CoreRoot.Storage.GetContainerUrl(MaxJobDuration, writeAccess: true));

        return Task.CompletedTask;
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await RunOnNewVirtualMachineAsync(defaultAzureCoreCount: 4, jobTimeout);
    }
}
