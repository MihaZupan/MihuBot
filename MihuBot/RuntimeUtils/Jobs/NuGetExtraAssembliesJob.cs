namespace MihuBot.RuntimeUtils.Jobs;

public sealed class NuGetExtraAssembliesJob : JobBase
{
    public const string FullBlobName = "nuget-extra-assemblies.zip";
    public const string SubsetBlobName = "nuget-extra-assemblies-subset.zip";

    public override string JobTitlePrefix => "NuGetExtraAssemblies";

    public NuGetExtraAssembliesJob(RuntimeUtilsService parent, string githubCommenterLogin, string arguments)
        : base(parent, githubCommenterLogin, arguments)
    {
        TestedPROrBranchLink = "https://github.com/dotnet/runtime";
    }

    protected override Task InitializeAsync(CancellationToken jobTimeout)
    {
        SuppressTrackingIssue = true;

        return Task.CompletedTask;
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await RunOnNewVirtualMachineAsync(defaultAzureCoreCount: 16, jobTimeout);
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (fileName is FullBlobName or SubsetBlobName)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"NuGetExtraAssemblies_{ExternalId}_{fileName}");
            try
            {
                using (FileStream tempFs = File.Create(tempPath))
                {
                    await contentStream.CopyToAsync(tempFs, cancellationToken);
                }

                var blobClient = Parent.JitDiffExtraAssembliesBlobContainerClient.GetBlobClient(fileName);
                await blobClient.UploadAsync(tempPath, overwrite: true, cancellationToken);

                // Return a self-cleaning stream for the normal artifact upload.
                // The base ArtifactReceivedAsync will dispose this stream after uploading.
                return new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { }
                throw;
            }
        }

        return null;
    }
}
