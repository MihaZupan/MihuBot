using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using MihuBot.Storage;
using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class CoreRootService : PeriodicBackgroundService
{
    public const string ContainerName = "coreroot";

    /// <summary>How long the underlying blob storage keeps CoreRoot archives around.</summary>
    private const int StorageRetentionDays = 365;

    /// <summary>
    /// How long we keep the metadata pointing at those archives.
    /// Archives are compressed using a previous CoreRoot as a ZStandard prefix, so a blob may still be
    /// needed as a prefix after its own metadata entry is no longer interesting. Dropping the metadata
    /// ahead of the storage expiration guarantees that any prefix we still hand out remains downloadable.
    /// </summary>
    private const int MetadataRetentionDays = StorageRetentionDays - 3;

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
            ContainerDbEntry entry = storage.EnsureContainerAsync(ContainerName, "runtime-utils", isPublic: true, TimeSpan.FromDays(StorageRetentionDays)).GetAwaiter().GetResult();
            return new StorageClient(http, entry.Name, entry.SasKey, entry.IsPublic);
        });
    }

    protected override async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        await using MihuBotDbContext context = _dbContextFactory.CreateDbContext();

        DateTime maxAge = DateTime.UtcNow - TimeSpan.FromDays(MetadataRetentionDays);

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

    public async Task<bool> SaveAsync(string sha, string arch, string os, string type, string blobName, string prefixBlobName, DateTime commitTime)
    {
        if (await GetAsync(sha, arch, os, type) is not null)
        {
            await _logger.DebugAsync($"CoreRoot conflict for `{sha}/{arch}/{os}/{type} - {blobName}`");
            return false;
        }

        prefixBlobName = string.IsNullOrEmpty(prefixBlobName) ? null : prefixBlobName;

        await using MihuBotDbContext context = _dbContextFactory.CreateDbContext();

        if (prefixBlobName is not null)
        {
            // Prefix chains are not allowed: a consumer only ever downloads one prefix, so the archive it
            // is used with must be decompressible with that prefix alone. Enforce that the prefix exists
            // and is itself standalone, otherwise this entry would be impossible to decompress.
            CoreRootDbEntry prefixEntry = await context.CoreRoot.AsNoTracking()
                .FirstOrDefaultAsync(e => e.BlobName == prefixBlobName);

            if (prefixEntry is null)
            {
                await _logger.DebugAsync($"CoreRoot `{blobName}` references unknown prefix `{prefixBlobName}`");
                return false;
            }

            if (prefixEntry.PrefixBlobName is not null)
            {
                await _logger.DebugAsync($"CoreRoot `{blobName}` references prefix `{prefixBlobName}`, which is itself a delta of `{prefixEntry.PrefixBlobName}`");
                return false;
            }
        }

        context.CoreRoot.Add(new CoreRootDbEntry
        {
            Sha = sha,
            Arch = arch,
            Os = os,
            Type = type,
            CommitTime = commitTime,
            CreatedOn = DateTime.UtcNow,
            BlobName = blobName,
            PrefixBlobName = prefixBlobName,
        });

        await context.SaveChangesAsync();

        _logger.DebugLog($"CoreRoot saved: '{blobName}'{(prefixBlobName is null ? " (standalone reference)" : $" (delta of '{prefixBlobName}')")}");

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
            BlobName = entry.BlobName,
            PrefixBlobName = entry.PrefixBlobName,
            PrefixUrl = string.IsNullOrEmpty(entry.PrefixBlobName)
                ? null
                : Storage.GetFileUrl(entry.PrefixBlobName, TimeSpan.FromHours(8), writeAccess: false),
            CommitTime = entry.CommitTime,
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

        /// <summary>
        /// When the underlying commit was authored. CoreRoots are not necessarily generated in commit
        /// order (a job may be started manually for older commits), so this - not <see cref="CreatedOn"/> -
        /// is what places an entry relative to the others in the repository's history.
        /// </summary>
        public DateTime CommitTime { get; set; }

        /// <summary>When this CoreRoot was generated. Drives blob retention, not ordering.</summary>
        public DateTime CreatedOn { get; set; }

        public string BlobName { get; set; }

        /// <summary>The blob that was used as the ZStandard prefix when compressing <see cref="BlobName"/>, if any.</summary>
        public string PrefixBlobName { get; set; }
    }

    public sealed class CoreRootEntry
    {
        public string Sha { get; set; }
        public string Arch { get; set; }
        public string Os { get; set; }
        public string Type { get; set; }
        public string Url { get; set; }
        public string BlobName { get; set; }

        /// <summary>The blob that was used as the ZStandard prefix when compressing this archive, if any.</summary>
        public string PrefixBlobName { get; set; }

        /// <summary>A download link for <see cref="PrefixBlobName"/>. Required to decompress this archive.</summary>
        public string PrefixUrl { get; set; }

        /// <summary>When the underlying commit was authored.</summary>
        public DateTime CommitTime { get; set; }

        /// <summary>When this CoreRoot was generated.</summary>
        public DateTime CreatedOn { get; set; }
    }
}
