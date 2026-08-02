using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using MihuBot.Storage;
using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class CoreRootService : PeriodicBackgroundService
{
    public const string ContainerName = "coreroot";

    private const int RetentionDays = 180;

    private readonly GitHubClient _github;
    private readonly IDbContextFactory<MihuBotDbContext> _dbContextFactory;
    private readonly Logger _logger;
    private readonly Lazy<StorageClient> _storage;

    public StorageClient Storage => _storage.Value;

    public CoreRootService(GitHubClient github, HttpClient http, IDbContextFactory<MihuBotDbContext> dbContextFactory, Logger logger, StorageService storage)
        : base(new PeriodicTaskOptions { Interval = TimeSpan.FromMinutes(10) }, logger)
    {
        _github = github;
        _dbContextFactory = dbContextFactory;
        _logger = logger;

        _storage = new Lazy<StorageClient>(() =>
        {
            ContainerDbEntry entry = storage.EnsureContainerAsync(ContainerName, "runtime-utils", isPublic: true, TimeSpan.FromDays(RetentionDays)).GetAwaiter().GetResult();
            return new StorageClient(http, entry.Name, entry.SasKey, entry.IsPublic);
        });
    }

    protected override async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        await using MihuBotDbContext context = _dbContextFactory.CreateDbContext();

        DateTime maxAge = DateTime.UtcNow - TimeSpan.FromDays(RetentionDays);

        await context.CoreRoot
            .Where(e => e.CreatedOn < maxAge)
            .ExecuteDeleteAsync(cancellationToken);
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

        if (entry is null)
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
            .Select(Remap)
            .ToArray();
    }

    private CoreRootEntry Remap(CoreRootDbEntry entry)
    {
        string sasUrl = Storage.GetFileUrl(entry.BlobName, TimeSpan.FromHours(8), writeAccess: false);

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
