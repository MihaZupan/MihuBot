using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StorageService.DB;

namespace StorageService.Storage;

public sealed class StorageService
{
    private const long MaxFileSize = 10L * 1024 * 1024 * 1024; // 10 GB
    private const int MaxContainerNameLength = 64;
    private const int MaxFilePathLength = 200;

    private const string AlphaNumeric = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static readonly SearchValues<char> s_containerNameValidChars = SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyz0123456789" + "-_");

    private static readonly SearchValues<char> s_fileNameValidChars = SearchValues.Create(
        AlphaNumeric + "-_./");

    private static readonly SearchValues<char> s_fileNameExtensionValidChars = SearchValues.Create(
        AlphaNumeric);

    private readonly IDbContextFactory<StorageDbContext> _db;
    private readonly ILogger<StorageService> _logger;
    private readonly string _storageDirectory;
    private readonly ConcurrentDictionary<string, byte> _locks = [];

    public StorageService(IDbContextFactory<StorageDbContext> dbContextFactory, ILogger<StorageService> logger)
    {
        _db = dbContextFactory;
        _logger = logger;
        _storageDirectory = $"{Constants.StateDirectory}/Files";

        Directory.CreateDirectory(_storageDirectory);

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));

                while (await timer.WaitForNextTickAsync())
                {
                    try
                    {
                        while (true)
                        {
                            await using StorageDbContext db = _db.CreateDbContext();

                            DateTime now = DateTime.UtcNow;
                            FileDbEntry[] expiredFiles = await db.Files
                                .Where(f => f.ExpiresAt < now)
                                .OrderBy(f => f.ExpiresAt)
                                .Take(1000)
                                .ToArrayAsync(CancellationToken.None);

                            foreach (FileDbEntry file in expiredFiles)
                            {
                                if (!_locks.TryAdd(file.Id, 0))
                                {
                                    continue;
                                }

                                db.Files.Remove(file);

                                try
                                {
                                    File.Delete(GetFullPathForId(file.ContainerId, file.Id));

                                    _logger.LogDebug("Deleted expired file {Path} in {Container}", file.Path, file.ContainerId);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to delete expired file {Id}", file.Id);
                                }
                                finally
                                {
                                    _locks.TryRemove(file.Id, out _);
                                }
                            }

                            await db.SaveChangesAsync();

                            if (expiredFiles.Length == 0)
                            {
                                break;
                            }

                            await Task.Delay(1000);
                        }

                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to clean up expired files");
                    }
                }
            });
        }
    }

    public static bool ValidateContainerName([NotNullWhen(true)] string? name)
    {
        return
            name is { Length: > 1 and <= MaxContainerNameLength } &&
            !name.ContainsAnyExcept(s_containerNameValidChars);
    }

    private static bool ValidateFilePath(string? path)
    {
        if (path is null || path.Length is < 2 or > MaxFilePathLength || path.ContainsAnyExcept(s_fileNameValidChars))
        {
            return false;
        }

        if (path.StartsWith('/') || path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        int dotIndex = path.IndexOf('.');
        if (dotIndex >= 0 && path.AsSpan(dotIndex + 1).ContainsAnyExcept(s_fileNameExtensionValidChars))
        {
            return false;
        }

        return true;
    }

    private static string GenerateNewSasKey()
    {
        return RandomNumberGenerator.GetString(AlphaNumeric, 48);
    }

    private string GetFullPath(string container, string path, out string id)
    {
        // Hash the path to avoid dealing with case sensitivity
        id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{container}/{path}")));

        return GetFullPathForId(container, id);
    }

    private string GetFullPathForId(string container, string id)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(id.Length, SHA256.HashSizeInBytes * 2);

        // Try to keep number of files per directory low. This isn't a security measure (trivial to generate collisions).
        return Path.GetFullPath(Path.Combine(_storageDirectory, container, $"{id[0]}{id[1]}", $"{id[2]}{id[3]}", $"{id[4]}{id[5]}", id));
    }

    public async Task<bool> HasValidAuthorization(HttpContext context, string container)
    {
        Debug.Assert(ValidateContainerName(container));

        if (context.Request.Path.Value?.Length > 2 * (MaxContainerNameLength + MaxFilePathLength))
        {
            return false;
        }

        await using var dbContext = _db.CreateDbContext();

        var containerInfo = await dbContext.Containers.AsNoTracking()
            .Where(c => c.Name == container)
            .FirstOrDefaultAsync(context.RequestAborted);

        if (containerInfo is null)
        {
            return false;
        }

        context.Features.Set(containerInfo);

        bool isReadOnlyRequest = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);

        if (containerInfo.IsPublic && isReadOnlyRequest)
        {
            return true;
        }

        if (containerInfo.SasKey is not { Length: >= 32 })
        {
            Debug.Fail("?");
            return false;
        }

        // Check expiration
        if (!context.Request.Query.TryGetValue("exp", out var expirationStr) ||
            expirationStr.Count != 1 ||
            !DateTime.TryParseExact(expirationStr, "yyyy-MM-dd_HH-mm-ss", DateTimeFormatInfo.InvariantInfo, DateTimeStyles.None, out DateTime expiration) ||
            expiration <= DateTime.UtcNow)
        {
            return false;
        }

        // Check signature format
        Span<byte> requestSignature = stackalloc byte[HMACSHA256.HashSizeInBytes];

        if (!context.Request.Query.TryGetValue("sig", out var signatureValues) ||
            signatureValues.Count != 1 ||
            signatureValues[0] is not { } signatureValue ||
            Base64Url.DecodeFromChars(signatureValue, requestSignature, out int charsConsumed, out int bytesWritten) != OperationStatus.Done ||
            charsConsumed != signatureValue.Length ||
            bytesWritten != requestSignature.Length)
        {
            return false;
        }

        // Check for write access w=1
        if (!context.Request.Query.TryGetValue("w", out var writeAccess) ||
            writeAccess.Count != 1 ||
            writeAccess != "1")
        {
            writeAccess = "0";

            if (!isReadOnlyRequest)
            {
                return false;
            }
        }

        byte[] key = Encoding.UTF8.GetBytes(containerInfo.SasKey);

        // Signature may be scoped to the container or to a particular file
        return
            CheckSignature(key, requestSignature, $"{container}?exp={expirationStr}&w={writeAccess}") ||
            CheckSignature(key, requestSignature, $"{context.Request.Path}?exp={expirationStr}&w={writeAccess}");

        static bool CheckSignature(ReadOnlySpan<byte> key, ReadOnlySpan<byte> requestSignature, string toSign)
        {
            Span<byte> actualSignature = stackalloc byte[HMACSHA256.HashSizeInBytes];

            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(toSign), actualSignature);

            return CryptographicOperations.FixedTimeEquals(actualSignature, requestSignature);
        }
    }

    public async Task<(string? Error, string? SasKey)> TryCreateContainerAsync(string? owner, string? name, bool isPublic, TimeSpan retentionPeriod)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return ("Invalid owner name", null);
        }

        if (!ValidateContainerName(name))
        {
            return ("Invalid container name", null);
        }

        if (retentionPeriod.TotalSeconds < 1)
        {
            return ("Invalid retention period", null);
        }

        await using var dbContext = _db.CreateDbContext();

        var containerInfo = await dbContext.Containers.FindAsync(name);
        if (containerInfo is not null)
        {
            return ("Container already exists", null);
        }

        containerInfo = new ContainerDbEntry
        {
            Name = name,
            Owner = owner,
            IsPublic = isPublic,
            RetentionPeriodSeconds = (long)retentionPeriod.TotalSeconds,
            SasKey = GenerateNewSasKey()
        };

        try
        {
            dbContext.Containers.Add(containerInfo);

            await dbContext.SaveChangesAsync();
        }
        catch
        {
            return ("Failed to save container info", null);
        }

        return (null, containerInfo.SasKey);
    }

    public async Task<ContainerDbEntry[]> GetAllContainersAsync()
    {
        await using var dbContext = _db.CreateDbContext();

        return await dbContext.Containers.AsNoTracking().ToArrayAsync();
    }

    public async Task UploadFileAsync(HttpContext context, string container, string path)
    {
        Debug.Assert(await HasValidAuthorization(context, container));

        if (!ValidateFilePath(path))
        {
            await Results.BadRequest("Invalid path name").ExecuteAsync(context);
            return;
        }

        string fullPath = GetFullPath(container, path, out string id);

        if (File.Exists(fullPath))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        long contentLength = context.Request.ContentLength ?? 0;
        if (contentLength > MaxFileSize)
        {
            context.Response.StatusCode = StatusCodes.Status413RequestEntityTooLarge;
            return;
        }

        if (context.Features.Get<ContainerDbEntry>() is not { } containerInfo)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        if (!_locks.TryAdd(id, 0))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        try
        {
            var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySizeFeature is { IsReadOnly: false })
            {
                bodySizeFeature.MaxRequestBodySize = MaxFileSize;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            await using (FileStream fs = File.Open(fullPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                PreallocationSize = contentLength,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous
            }))
            {
                await context.Request.BodyReader.CopyToAsync(fs, context.RequestAborted);
            }

            DateTime now = DateTime.UtcNow;
            contentLength = new FileInfo(fullPath).Length;

            await using StorageDbContext db = _db.CreateDbContext();

            await db.Files
                .Where(f => f.Id == id)
                .ExecuteDeleteAsync(CancellationToken.None);

            db.Files.Add(new FileDbEntry
            {
                Id = id,
                Path = path,
                ContainerId = container,
                CreatedAt = now,
                ExpiresAt = now.AddSeconds(containerInfo.RetentionPeriodSeconds),
                ContentLength = contentLength
            });

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to upload file {Path} to container {Container}", path, container);

            if (!context.RequestAborted.IsCancellationRequested)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            try
            {
                File.Delete(fullPath);
            }
            catch { }
        }
        finally
        {
            _locks.TryRemove(id, out _);
        }
    }

    public async Task<IResult> DownloadFileAsync(HttpContext context, string container, string path)
    {
        Debug.Assert(await HasValidAuthorization(context, container));

        if (!ValidateFilePath(path))
        {
            return Results.BadRequest("Invalid path name");
        }

        string fullPath = GetFullPath(container, path, out _);

        if (File.Exists(fullPath))
        {
            return Results.File(fullPath);
        }

        return Results.NotFound();
    }

    public async Task<IResult> DeleteFile(HttpContext context, string container, string path)
    {
        string fullPath = GetFullPath(container, path, out string id);

        if (!File.Exists(fullPath))
        {
            return Results.NotFound();
        }

        while (!_locks.TryAdd(id, 0))
        {
            await Task.Delay(10, context.RequestAborted);
        }

        try
        {
            File.Delete(fullPath);

            await using StorageDbContext db = _db.CreateDbContext();

            await db.Files
                .Where(f => f.Id == id)
                .ExecuteDeleteAsync(CancellationToken.None);

            return Results.NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete file {Path} in container {Container}", path, container);

            return Results.InternalServerError();
        }
        finally
        {
            _locks.TryRemove(id, out _);
        }
    }
}
