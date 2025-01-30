using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class CoreRootService
{
    private readonly GitHubClient _github;
    private readonly IDbContextFactory<MihuBotDbContext> _dbContextFactory;
    private readonly Logger _logger;

    public readonly BlobContainerClient CoreRootBlobContainerClient;

    public CoreRootService(GitHubClient github, IDbContextFactory<MihuBotDbContext> dbContextFactory, Logger logger, IConfiguration configuration)
    {
        _github = github;
        _dbContextFactory = dbContextFactory;
        _logger = logger;

        if (Program.AzureEnabled)
        {
            CoreRootBlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString-RuntimeUtils"],
                "coreroot");
        }
    }

    public static bool TryValidate(ref string arch, ref string os, ref string type)
    {
        arch = arch?.ToLowerInvariant();
        os = os?.ToLowerInvariant();
        type = type?.ToLowerInvariant();
        return arch is "x64" or "arm64" && os is "windows" or "linux" && type is "release" or "checked";
    }

    public async Task<IEnumerable<CoreRootEntry>> ListAsync(string @base, string head, string arch, string os, string type)
    {
        CompareResult result = await _github.Repository.Commit.Compare("dotnet", "runtime", @base, head,
            new ApiOptions { PageCount = 1, PageSize = 100 });

        List<CoreRootEntry> entries = new(result.Commits.Count);

        foreach (GitHubCommit commit in result.Commits)
        {
            if (await GetAsync(commit.Sha, arch, os, type) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public async Task<bool> SaveAsync(string sha, string arch, string os, string type, string blobName)
    {
        if (await GetAsync(sha, arch, os, type) is not null)
        {
            await _logger.DebugAsync($"CoreRoot conflict for `{sha}/{arch}/{os}/{type} - {blobName}`");
            return false;
        }

        BlobClient blob = CoreRootBlobContainerClient.GetBlobClient(blobName);

        if (!await blob.ExistsAsync())
        {
            return false;
        }

        await using MihuBotDbContext context = _dbContextFactory.CreateDbContext();

        context.CoreRoot.Add(new CoreRootDbEntry
        {
            Sha = sha,
            Arch = arch,
            Os = os,
            Type = type,
            CreatedOn = DateTime.UtcNow,
            BlobName = blob.Name,
        });

        await context.SaveChangesAsync();

        return true;
    }

    public async Task<CoreRootEntry> GetAsync(string sha, string arch, string os, string type)
    {
        await using MihuBotDbContext context = _dbContextFactory.CreateDbContext();

        CoreRootDbEntry entry = await context.CoreRoot.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Sha == sha && e.Arch == arch && e.Os == os && e.Type == type);

        if (entry is null || (DateTime.UtcNow - entry.CreatedOn).TotalDays > 60)
        {
            return null;
        }

        BlobClient blob = CoreRootBlobContainerClient.GetBlobClient(entry.BlobName);
        Uri sasUri = blob.GenerateSasUri(BlobSasPermissions.Read, DateTime.UtcNow.AddHours(8));

        return new CoreRootEntry
        {
            Sha = entry.Sha,
            Arch = entry.Arch,
            Os = entry.Os,
            Type = entry.Type,
            Url = sasUri.AbsoluteUri,
            CreatedOn = entry.CreatedOn,
        };
    }

    [Table("coreRoot")]
    public sealed class CoreRootDbEntry
    {
        [Key]
        public string Sha { get; set; }
        public string Arch { get; set; }
        public string Os { get; set; }
        public string Type { get; set; }
        public DateTime CreatedOn { get; set; }
        public string BlobName { get; set; }
    }

    public sealed class CoreRootEntry
    {
        public string Sha { get; set; }
        public string Arch { get; set; }
        public string Os { get; set; }
        public string Type { get; set; }
        public string Url { get; set; }
        public DateTime CreatedOn { get; set; }
    }
}
