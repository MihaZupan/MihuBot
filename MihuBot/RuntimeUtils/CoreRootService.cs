using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using Octokit;
using MihuBot.Configuration;

namespace MihuBot.RuntimeUtils;

public sealed class CoreRootService
{
    private readonly GitHubClient _github;
    private readonly IDbContextFactory<MihuBotDbContext> _dbContextFactory;
    private readonly Logger _logger;

    private readonly BlobContainerClient _coreRootBlobContainerClient;
    public readonly StorageClient Storage;

    public CoreRootService(GitHubClient github, HttpClient http, IDbContextFactory<MihuBotDbContext> dbContextFactory, Logger logger, IConfiguration configuration, IConfigurationService configurationService)
    {
        _github = github;
        _dbContextFactory = dbContextFactory;
        _logger = logger;

        if (!configurationService.TryGet(null, "RuntimeUtils.CoreRootService.SasKey", out string sasKey))
        {
            throw new InvalidOperationException("Missing 'RuntimeUtils.CoreRootService.SasKey'");
        }

        Storage = new StorageClient(http, "coreroot", sasKey, isPublic: true);

        if (Program.AzureEnabled)
        {
            _coreRootBlobContainerClient = new BlobContainerClient(
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

        if (!await Storage.ExistsAsync(blobName))
        {
            _logger.DebugLog($"CoreRoot blob does not exist? '{blobName}'");
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
            BlobName = blobName,
        });

        await context.SaveChangesAsync();

        _logger.DebugLog($"CoreRoot saved: '{blobName}'");

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

        return Remap(entry);
    }

    public async Task<IEnumerable<CoreRootEntry>> AllAsync(string arch, string os, string type)
    {
        await using MihuBotDbContext context = _dbContextFactory.CreateDbContext();

        List<CoreRootDbEntry> entries = await context.CoreRoot.AsNoTracking()
            .Where(e => e.Arch == arch && e.Os == os && e.Type == type)
            .ToListAsync();

        return entries
            .Where(e => e is not null && (DateTime.UtcNow - e.CreatedOn).TotalDays <= 60)
            .Select(Remap)
            .ToArray();
    }

    private CoreRootEntry Remap(CoreRootDbEntry entry)
    {
        string sasUrl;

        if (entry.CreatedOn >= new DateTime(2025, 3, 14))
        {
            sasUrl = Storage.GetFileUrl(entry.BlobName, TimeSpan.FromHours(8), writeAccess: false);
        }
        else
        {
            BlobClient blob = _coreRootBlobContainerClient.GetBlobClient(entry.BlobName);
            sasUrl = blob.GenerateSasUri(BlobSasPermissions.Read, DateTime.UtcNow.AddHours(8)).AbsoluteUri;
        }

        return new CoreRootEntry
        {
            Sha = entry.Sha,
            Arch = entry.Arch,
            Os = entry.Os,
            Type = entry.Type,
            Url = sasUrl,
            CreatedOn = entry.CreatedOn,
        };
    }

    [Table("coreRoot")]
    [Index(nameof(Sha))]
    public sealed class CoreRootDbEntry
    {
        public long Id { get; set; }
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
