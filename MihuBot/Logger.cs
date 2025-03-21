using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using System.Buffers;
using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace MihuBot;

public sealed partial class Logger
{
    public readonly LoggerOptions Options;

    private readonly HttpClient _http;

    private DiscordSocketClient Discord => Options.Discord;

    private int _fileCounter;

    internal static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly IDbContextFactory<LogsDbContext> _dbContextFactory;
    private readonly Channel<LogDbEntry> LogChannel;

    private readonly Channel<(string FileName, string FilePath, SocketUserMessage Message)> MediaFileArchivingChannel;
    private readonly Channel<(string FileName, string FilePath, SocketUserMessage Message, bool Delete)> FileArchivingChannel;
    private readonly BlobContainerClient BlobContainerClient;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> FileArchivingCompletions = new();

    private static readonly FileBackedHashSet _cdnLinksHashSet = new("CdnLinks.txt", StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, (string Ext, string Args)> ConvertableMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".jpg",   (".webp",   "-pix_fmt yuv420p -q 75") },
        { ".jpeg",  (".webp",   "-pix_fmt yuv420p -q 75") },
        { ".png",   (".webp",   "-pix_fmt yuv420p -q 75") },
        { ".wav",   (".mp3",    "-b:a 192k") },
    };

    public Logger(HttpClient httpClient, LoggerOptions options, IConfiguration configuration, IDbContextFactory<LogsDbContext> dbContextFactory)
    {
        _http = httpClient;
        Options = options;
        _dbContextFactory = dbContextFactory;

        if (ProgramState.AzureEnabled)
        {
            BlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString"],
                "discord");
        }

        Directory.CreateDirectory(Options.LogsRoot);
        Directory.CreateDirectory(Options.FilesRoot);

        LogChannel = Channel.CreateUnbounded<LogDbEntry>(new UnboundedChannelOptions() { SingleReader = true });
        MediaFileArchivingChannel = Channel.CreateUnbounded<(string, string, SocketUserMessage)>(new UnboundedChannelOptions() { SingleReader = true });
        FileArchivingChannel = Channel.CreateUnbounded<(string, string, SocketUserMessage, bool)>(new UnboundedChannelOptions() { SingleReader = true });

        Task.Run(ChannelReaderTaskAsync);
        Task.Run(FileArchivingTaskAsync);
        Task.Run(MediaFileArchivingTaskAsync);

        _ = new Timer(_ => DebugLog("Keepalive"), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

        Discord.Log += Discord_LogAsync;
        Discord.LatencyUpdated += LatencyUpdatedAsync;
        Discord.JoinedGuild += JoinedGuildAsync;
        Discord.LeftGuild += LeftGuildAsync;
        Discord.MessageReceived += message => MessageReceivedAsync(message);
        Discord.MessageUpdated += (cacheable, message, _) => MessageReceivedAsync(message, previousId: cacheable.Id);
        Discord.MessageDeleted += MessageDeletedAsync;
        Discord.MessagesBulkDeleted += MessagesBulkDeletedAsync;
        Discord.ReactionAdded += (_, __, reaction) => ReactionAddedAsync(reaction);
        Discord.ReactionRemoved += (_, __, reaction) => ReactionRemovedAsync(reaction);
        Discord.ReactionsCleared += ReactionsClearedAsync;
        Discord.UserVoiceStateUpdated += UserVoiceStateUpdatedAsync;
        Discord.UserBanned += UserBannedAsync;
        Discord.UserUnbanned += UserUnbannedAsync;
        Discord.UserLeft += UserLeftAsync;
        Discord.UserJoined += UserJoinedAsync;
        Discord.UserIsTyping += UserIsTypingAsync;
        Discord.UserUpdated += UserUpdatedAsync;
        Discord.GuildMemberUpdated += (before, after) => UserUpdatedAsync(before.HasValue ? before.Value : null, after);
        Discord.CurrentUserUpdated += UserUpdatedAsync;
        Discord.ChannelCreated += ChannelCreatedAsync;
        Discord.ChannelUpdated += ChannelUpdatedAsync;
        Discord.ChannelDestroyed += ChannelDestroyedAsync;
        Discord.RoleCreated += RoleCreatedAsync;
        Discord.RoleDeleted += RoleDeletedAsync;
        Discord.RoleUpdated += RoleUpdatedAsync;
        Discord.GuildAvailable += GuildAvailableAsync;
        Discord.GuildUnavailable += GuildUnavailableAsync;
        Discord.GuildMembersDownloaded += guild => { DebugLog($"Guild members downloaded for {guild.Name} ({guild.Id})"); return Task.CompletedTask; };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _ = DebugAsync($"UnobservedTaskException: {e.Exception}", truncateToFile: true);
            e.SetObserved();
        };

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    await using LogsDbContext context = _dbContextFactory.CreateDbContext();

                    DateTime before = DateTime.UtcNow.Subtract(TimeSpan.FromDays(90));
                    DateTime after = DateTime.UtcNow.Subtract(TimeSpan.FromDays(92));

                    int rowsDeleted = await context.Logs
                        .Where(log => log.Snowflake >= (long)SnowflakeUtils.ToSnowflake(after))
                        .Where(log => log.Snowflake <= (long)SnowflakeUtils.ToSnowflake(before))
                        .Where(log =>
                            log.Type == EventType.DebugMessage ||
                            log.Type == EventType.UserActiveClientsChanged ||
                            log.Type == EventType.UserActivitiesChanged)
                        .ExecuteDeleteAsync();

                    DebugLog($"Deleted {rowsDeleted} older debug messages");
                }
                catch (Exception ex)
                {
                    await DebugAsync($"Failed to delete old logs: {ex}");
                }
            }
        });
    }

    public async Task OnShutdownAsync()
    {
        Console.WriteLine("Logger OnShutdownAsync ...");
        try
        {
            LogChannel.Writer.TryComplete();

            await LogChannel.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(15));

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Logger failed to shutdown gracefully: {ex}");
        }
    }

    private async Task ChannelReaderTaskAsync()
    {
        List<LogDbEntry> events = new(512);

        while (await LogChannel.Reader.WaitToReadAsync())
        {
            while (events.Count < events.Capacity && LogChannel.Reader.TryRead(out LogDbEntry logEvent))
            {
                events.Add(logEvent);
            }

            try
            {
                await using LogsDbContext context = _dbContextFactory.CreateDbContext();

                context.Logs.AddRange(events);

                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save events: {ex}");
            }

            events.Clear();
        }
    }

    private async Task FileArchivingTaskAsync()
    {
        await foreach (var (FileName, FilePath, Message, Delete) in FileArchivingChannel.Reader.ReadAllAsync())
        {
            if (!ProgramState.AzureEnabled)
            {
                continue;
            }

            try
            {
                string blobName = FilePath
                    .Substring(Options.LogsRoot.Length)
                    .Replace('/', '_')
                    .Replace('\\', '_');

                string extension = Path.GetExtension(FilePath).ToLowerInvariant();

                bool isImage = extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp";

                AccessTier accessTier = extension switch
                {
                    ".txt" => AccessTier.Hot,
                    ".json" => AccessTier.Hot,
                    ".br" => AccessTier.Hot,
                    ".html" => AccessTier.Hot,
                    ".jpg" => AccessTier.Cool,
                    ".jpeg" => AccessTier.Cool,
                    ".png" => AccessTier.Cool,
                    ".gif" => AccessTier.Cool,
                    ".mp3" => AccessTier.Cool,
                    ".wav" => AccessTier.Cool,
                    _ => AccessTier.Archive
                };

                BlobClient blobClient = BlobContainerClient.GetBlobClient(blobName);

                using (FileStream fs = File.OpenRead(FilePath))
                {
                    if (accessTier == AccessTier.Archive && fs.Length < 2 * 1024 * 1024 /* 2 MB */)
                        accessTier = AccessTier.Cool;

                    var blobOptions = new BlobUploadOptions() { AccessTier = accessTier };
                    await blobClient.UploadAsync(fs, blobOptions);
                }

                if (Delete)
                {
                    File.Delete(FilePath);
                }

                if (Message != null)
                {
                    var embed = new EmbedBuilder()
                        .WithAuthor(Message.Author.Username, Message.Author.GetAvatarUrl())
                        .WithTimestamp(Message.Timestamp)
                        .WithTitle($"**{Message.Guild().Name} - {Message.Channel.Name}**: *{FileName}*")
                        .WithDescription(Message.GetJumpUrl())
                        .WithUrl(blobClient.Uri.AbsoluteUri);

                    if (isImage && accessTier != AccessTier.Archive)
                    {
                        embed.WithImageUrl(blobClient.Uri.AbsoluteUri);
                    }

                    await Options.LogsFilesTextChannel.SendMessageAsync(embed: embed.Build());
                }

                DebugLog($"Archived {FilePath}", Message);
            }
            catch (Exception ex)
            {
                DebugLog($"Failed to archive {FilePath}: {ex}", Message);
            }
            finally
            {
                if (FileArchivingCompletions.TryRemove(FilePath, out var tcs))
                    tcs.TrySetResult();
            }
        }
    }

    private async Task MediaFileArchivingTaskAsync()
    {
        await foreach (var (FileName, FilePath, Message) in MediaFileArchivingChannel.Reader.ReadAllAsync())
        {
            try
            {
                (string ext, string args) = ConvertableMediaExtensions[Path.GetExtension(FilePath)];

                string oldPath = FilePath;
                string newPath = Path.ChangeExtension(oldPath, ext);
                string fileName = FileName;

                try
                {
                    await YoutubeHelper.FFMpegConvertAsync(oldPath, newPath, $"-threads 1 {args}");
                    fileName = Path.ChangeExtension(FileName, ext);
                    DebugLog($"Converted {FileName} to {fileName} ({GetFileLengthKB(oldPath)} KB => {GetFileLengthKB(newPath)} KB)", Message);

                    static long GetFileLengthKB(string filePath) => new FileInfo(filePath).Length / 1024;
                }
                catch (Exception ex)
                {
                    DebugLog($"Failed to convert media {FilePath}: {ex}", Message);
                    oldPath = newPath;
                    newPath = FilePath;
                }

                try
                {
                    File.Delete(oldPath);
                }
                catch { }

                FileArchivingChannel.Writer.TryWrite((fileName, newPath, Message, Delete: true));
            }
            catch (Exception ex)
            {
                DebugLog($"Failed to archive media {FilePath}: {ex}", Message);
            }
        }
    }

    public async Task<int> CountLogsAsync(
        DateTime after,
        DateTime before,
        Func<IQueryable<LogDbEntry>, IQueryable<LogDbEntry>> query,
        Func<IEnumerable<LogDbEntry>, IEnumerable<LogDbEntry>> filters = null,
        CancellationToken cancellationToken = default)
    {
        return await GetLogsAsyncCore(after, before, query, async query =>
        {
            if (filters is null)
            {
                return await query.CountAsync(cancellationToken);
            }

            IEnumerable<LogDbEntry> enumerable = query.AsEnumerable();

            if (cancellationToken.CanBeCanceled)
            {
                enumerable = enumerable.Where(e =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                });
            }

            enumerable = filters(enumerable);

            return enumerable.Count();
        });
    }

    public async Task<LogDbEntry[]> GetLogsAsync(
        DateTime after,
        DateTime before,
        Func<IQueryable<LogDbEntry>, IQueryable<LogDbEntry>> query,
        Func<IEnumerable<LogDbEntry>, IEnumerable<LogDbEntry>> filters = null,
        CancellationToken cancellationToken = default)
    {
        return await GetLogsAsyncCore(after, before, query, async query =>
        {
            if (filters is null)
            {
                return await query.ToArrayAsync(cancellationToken);
            }

            IEnumerable<LogDbEntry> enumerable = query.AsEnumerable();

            if (cancellationToken.CanBeCanceled)
            {
                enumerable = enumerable.Where(e =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                });
            }

            enumerable = filters(enumerable);

            return enumerable.ToArray();
        });
    }

    private async Task<TResult> GetLogsAsyncCore<TResult>(
        DateTime after,
        DateTime before,
        Func<IQueryable<LogDbEntry>, IQueryable<LogDbEntry>> query,
        Func<IQueryable<LogDbEntry>, Task<TResult>> processResults)
    {
        await using var context = _dbContextFactory.CreateDbContext();

        IQueryable<LogDbEntry> logQuery = context.Logs.AsNoTracking()
            .Where(log => log.Snowflake >= (long)SnowflakeUtils.ToSnowflake(after))
            .Where(log => log.Snowflake <= (long)SnowflakeUtils.ToSnowflake(before));

        logQuery = logQuery.OrderBy(log => log.Snowflake);

        if (query is not null)
        {
            logQuery = query(logQuery);
        }

        return await processResults(logQuery);
    }

    private void Log(LogDbEntry entry) => LogChannel.Writer.TryWrite(entry);

    public void Log<TContent>(EventType type, ulong guildId = 0, ulong channelId = 0, ulong messageId = 0, ulong userId = 0, string content = null, TContent extraContent = null)
        where TContent : class =>
        Log(LogDbEntry.Create(type, messageId == 0 ? SnowflakeUtils.ToSnowflake(DateTime.UtcNow) : messageId, guildId, channelId, userId, content, extraContent));

    public void Log(EventType type, ulong guildId = 0, ulong channelId = 0, ulong messageId = 0, ulong userId = 0, string content = null) =>
        Log<object>(type, guildId, channelId, messageId, userId, content);

    private void Log<TContent>(EventType type, IChannel channel, ulong messageId = 0, ulong userId = 0, string content = null, TContent extraContent = null)
        where TContent : class =>
        Log(type, (channel as SocketGuildChannel)?.Guild.Id ?? 0, channel?.Id ?? 0, messageId, userId, content, extraContent);

    public void DebugLog(string debugMessage, SocketUserMessage message) =>
        DebugLog(debugMessage, message?.Guild()?.Id ?? 0, message?.Channel.Id ?? 0, message?.Id ?? 0, message?.Author.Id ?? 0);

    public void DebugLog(string debugMessage, ulong guildId = 0, ulong channelId = 0, ulong messageId = 0, ulong userId = 0) =>
        Log<object>(EventType.DebugMessage, guildId, channelId, messageId, userId, debugMessage);

    public async Task DebugAsync(string debugMessage, SocketUserMessage message = null, bool truncateToFile = false)
    {
        try
        {
            DebugLog(debugMessage, message);

            lock (Console.Out)
                Console.WriteLine("DEBUG: " + debugMessage);

            if (debugMessage.Length >= 2000)
            {
                if (truncateToFile)
                {
                    await Options.DebugTextChannel.SendTextFileAsync($"Debug-{Snowflake.Next()}.txt", debugMessage);
                    return;
                }
                else
                {
                    debugMessage = debugMessage.TruncateWithDotDotDot(2000);
                }
            }

            await Options.DebugTextChannel.SendMessageAsync(debugMessage);
        }
        catch { }
    }

    private Task ChannelUpdatedAsync(SocketChannel beforeChannel, SocketChannel afterChannel)
    {
        if (beforeChannel is SocketGuildChannel && afterChannel is SocketGuildChannel after)
        {
            Log(EventType.ChannelUpdated, after, extraContent: ChannelModel.FromSocketChannel(after));
        }
        return Task.CompletedTask;
    }

    private Task ChannelCreatedAsync(SocketChannel channel)
    {
        if (channel is SocketGuildChannel guildChannel)
        {
            Log(EventType.ChannelUpdated, guildChannel, extraContent: ChannelModel.FromSocketChannel(guildChannel));
        }
        return Task.CompletedTask;
    }

    private Task ChannelDestroyedAsync(SocketChannel channel)
    {
        if (channel is SocketGuildChannel guildChannel)
        {
            Log(EventType.ChannelDestroyed, guildChannel, extraContent: ChannelModel.FromSocketChannel(guildChannel));
        }
        return Task.CompletedTask;
    }

    private Task RoleCreatedAsync(SocketRole role)
    {
        Log(EventType.RoleCreated, role.Guild.Id, extraContent: RoleModel.FromSocketRole(role));
        return Task.CompletedTask;
    }

    private Task RoleDeletedAsync(SocketRole role)
    {
        Log(EventType.RoleDeleted, role.Guild.Id, extraContent: RoleModel.FromSocketRole(role));
        return Task.CompletedTask;
    }

    private Task RoleUpdatedAsync(SocketRole before, SocketRole after)
    {
        Log(EventType.RoleUpdated, after.Guild.Id, extraContent: RoleModel.FromSocketRole(after));
        return Task.CompletedTask;
    }

    private async Task JoinedGuildAsync(SocketGuild guild)
    {
        Log(EventType.JoinedGuild, guild.Id);
        await DebugAsync($"Added to {guild.Name} ({guild.Id})");
    }

    private async Task LeftGuildAsync(SocketGuild guild)
    {
        Log(EventType.LeftGuild, guild.Id);
        await DebugAsync($"Left {guild.Name} ({guild.Id})");
    }

    private Task GuildUnavailableAsync(SocketGuild guild)
    {
        Log(EventType.GuildUnavailable, guild.Id);
        return Task.CompletedTask;
    }

    private Task GuildAvailableAsync(SocketGuild guild)
    {
        Log(EventType.GuildAvailable, guild.Id);
        return Task.CompletedTask;
    }

    private Task UserUpdatedAsync(SocketUser beforeUser, SocketUser afterUser)
    {
        var after = afterUser as SocketGuildUser;
        ulong guildId = after?.Guild.Id ?? 0;

        if (after != null)
        {
            var before = beforeUser as SocketGuildUser;

            if (before?.Nickname != after.Nickname)
            {
                Log(EventType.UserNicknameChanged, guildId, userId: after.Id, content: after.Nickname);
            }

            if (before is null || !before.Roles.SequenceIdEquals(after.Roles))
            {
                if (before != null)
                {
                    foreach (SocketRole beforeRole in before.Roles)
                    {
                        if (!after.Roles.Any(beforeRole.Id))
                        {
                            Log(EventType.UserRoleRemoved, guildId, userId: afterUser.Id, extraContent: RoleModel.FromSocketRole(beforeRole));
                        }
                    }
                }

                foreach (SocketRole afterRole in after.Roles)
                {
                    if (before is null || !before.Roles.Any(afterRole.Id))
                    {
                        Log(EventType.UserRoleAdded, guildId, userId: afterUser.Id, extraContent: RoleModel.FromSocketRole(afterRole));
                    }
                }
            }
        }

        if (beforeUser is null || beforeUser.Username != afterUser.Username)
        {
            Log(EventType.UserNicknameChanged, guildId, userId: afterUser.Id, content: afterUser.Username);
        }

        if (beforeUser?.AvatarId != afterUser.AvatarId)
        {
            Log(EventType.UserAvatarIdChanged, guildId, userId: afterUser.Id, content: afterUser.AvatarId);
        }

        if (beforeUser is null ? afterUser.ActiveClients.Count != 0 : !beforeUser.ActiveClients.ToHashSet().SetEquals(afterUser.ActiveClients))
        {
            Log(EventType.UserActiveClientsChanged, guildId, userId: afterUser.Id, content: string.Join(' ', afterUser.ActiveClients));
        }

        if (beforeUser is null ? afterUser.Activities.Count != 0 : !beforeUser.Activities.SequenceEqual(afterUser.Activities))
        {
            Log(EventType.UserActivitiesChanged, guildId, userId: afterUser.Id, extraContent: afterUser.Activities);
        }

        return Task.CompletedTask;
    }

    private Task LatencyUpdatedAsync(int before, int after)
    {
        if (before != after && (before > 100 || after > 100))
            DebugLog($"Latency updated: {before} => {after}");
        return Task.CompletedTask;
    }

    private Task Discord_LogAsync(LogMessage logMessage)
    {
        Log(EventType.DebugMessage, extraContent: LogMessageModel.FromLogMessage(logMessage));
        return Task.CompletedTask;
    }

    private Task UserIsTypingAsync(Cacheable<IUser, ulong> user, Cacheable<IMessageChannel, ulong> channel)
    {
        Log(EventType.UserIsTyping, (channel.Value as SocketGuildChannel)?.Guild.Id ?? 0, channel.Id, userId: user.Id);
        return Task.CompletedTask;
    }

    private Task UserJoinedAsync(SocketGuildUser user)
    {
        Log(EventType.UserJoined, user.Guild.Id, userId: user.Id);
        return Task.CompletedTask;
    }

    private Task UserLeftAsync(SocketGuild guild, SocketUser user)
    {
        Log(EventType.UserLeft, guild.Id, userId: user.Id);
        return Task.CompletedTask;
    }

    private Task UserBannedAsync(SocketUser user, SocketGuild guild)
    {
        Log(EventType.UserBanned, guild.Id, userId: user.Id);
        return Task.CompletedTask;
    }

    private Task UserUnbannedAsync(SocketUser user, SocketGuild guild)
    {
        Log(EventType.UserUnbanned, guild.Id, userId: user.Id);
        return Task.CompletedTask;
    }

    private Task UserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (before.VoiceChannel == after.VoiceChannel)
        {
            var flags = (VoiceStatusUpdateFlags)((int)GetVoiceStatusUpdateFlags(before) >> 16) | GetVoiceStatusUpdateFlags(after);
            Log(EventType.VoiceStatusUpdated, before.VoiceChannel.Guild.Id, before.VoiceChannel.Id, userId: user.Id, content: flags.ToString());
            return Task.CompletedTask;
        }

        bool left = after.VoiceChannel is null;
        bool joined = before.VoiceChannel is null;

        if (!left && !joined)
            left = joined = true; // Switched calls

        if (left)
        {
            Log(EventType.UserLeftVoice, before.VoiceChannel.Guild.Id, before.VoiceChannel.Id, userId: user.Id, content: GetVoiceStatusUpdateFlags(before).ToString());
        }

        if (joined)
        {
            Log(EventType.UserJoinedVoice, after.VoiceChannel.Guild.Id, after.VoiceChannel.Id, userId: user.Id, content: GetVoiceStatusUpdateFlags(after).ToString());
        }

        return Task.CompletedTask;
    }

    private Task ReactionsClearedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
    {
        Log(EventType.ReactionsCleared, (channel.Value as SocketGuildChannel)?.Guild.Id ?? 0, channel.Id, message.Id);
        return Task.CompletedTask;
    }

    private Task ReactionRemovedAsync(SocketReaction reaction)
    {
        Log(EventType.ReactionRemoved, reaction.Channel, reaction.MessageId, reaction.UserId, extraContent: EmoteModel.FromEmote(reaction.Emote));
        return Task.CompletedTask;
    }

    private Task ReactionAddedAsync(SocketReaction reaction)
    {
        Log(EventType.ReactionAdded, reaction.Channel, reaction.MessageId, reaction.UserId, extraContent: EmoteModel.FromEmote(reaction.Emote));
        return Task.CompletedTask;
    }

    private Task MessagesBulkDeletedAsync(IReadOnlyCollection<Cacheable<IMessage, ulong>> cacheables, Cacheable<IMessageChannel, ulong> channel)
    {
        foreach (var cacheable in cacheables)
        {
            Log(EventType.MessageDeleted, (channel.Value as SocketGuildChannel)?.Guild.Id ?? 0, channel.Id, cacheable.Id);
        }

        return Task.CompletedTask;
    }

    private Task MessageDeletedAsync(Cacheable<IMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
    {
        Log(EventType.MessageDeleted, (channel.Value as SocketGuildChannel)?.Guild.Id ?? 0, channel.Id, message.Id);
        return Task.CompletedTask;
    }

    private Task MessageReceivedAsync(SocketMessage message, ulong? previousId = null)
    {
        ulong guildId = (message.Channel as SocketGuildChannel)?.Guild.Id ?? 0;
        ulong channelId = message.Channel.Id;

        Log(previousId.HasValue ? EventType.MessageUpdated : EventType.MessageReceived, guildId, channelId, message.Id, message.Author.Id, message.Content, extraContent: previousId?.ToString());

        if (message is not SocketUserMessage userMessage)
            return Task.CompletedTask;

        if (channelId is Channels.LogText or 750706839431413870ul or Channels.Files)
            return Task.CompletedTask;

        if (message.Author.Id != KnownUsers.MihuBot)
        {
            const string CdnLinkPrefix = "https://cdn.discordapp.com/";

            foreach (var attachment in message.Attachments)
            {
                Log(EventType.FileReceived, guildId, channelId, message.Id, message.Author.Id, extraContent: AttachmentModel.FromAttachment(attachment));

                if (attachment.Url.StartsWith(CdnLinkPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !_cdnLinksHashSet.TryAdd(attachment.Url.Substring(CdnLinkPrefix.Length)))
                {
                    continue;
                }

                Task.Run(async () =>
                {
                    await DownloadFileAsync(
                        attachment.Url,
                        (message.EditedTimestamp ?? message.Timestamp).UtcDateTime,
                        attachment.Id,
                        attachment.Filename,
                        userMessage);
                });
            }

            if (message.Content.Contains('/') &&
                message.Content.Contains(CdnLinkPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        foreach (Match match in CdnLinksRegex().Matches(message.Content))
                        {
                            var url = match.Value;
                            if (Uri.TryCreate(url, UriKind.Absolute, out _) && _cdnLinksHashSet.TryAdd(url.Substring(CdnLinkPrefix.Length)))
                            {
                                Task.Run(async () =>
                                {
                                    await DownloadFileAsync(
                                        url,
                                        (message.EditedTimestamp ?? message.Timestamp).UtcDateTime,
                                        message.Id,
                                        url.SplitLastTrimmed('/'),
                                        userMessage);
                                });
                            }
                        }
                    }
                    catch { }
                });
            }
        }

        if (message.Content.Contains('/') &&
            message.Content.Contains("https://discord.gift/", StringComparison.OrdinalIgnoreCase) &&
            userMessage.Guild() is { } guild && guild.Id != Guilds.PrivateLogs) // Has to match the debug channel
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    List<string> links = GiftMessageRegex().Matches(message.Content)
                        .Select(r => r.Value)
                        .Where(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
                        .ToList();

                    if (links.Count != 0)
                    {
                        string @everyone = Options.DebugTextChannel.Guild.EveryoneRole.Mention;
                        await DebugAsync($"Nitro gifts spotted {@everyone}\n{string.Join('\n', links)}\n\n{message.GetJumpUrl()}");
                    }
                }
                catch { }
            });
        }

        return Task.CompletedTask;
    }

    private async Task DownloadFileAsync(string url, DateTime time, ulong id, string fileName, SocketUserMessage message)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            string filePath = $"{Options.FilesRoot}{time.ToISODateTime()}_{id}_{fileName}";

            using (FileStream fs = File.OpenWrite(filePath))
            {
                using Stream stream = await response.Content.ReadAsStreamAsync();
                await stream.CopyToAsync(fs);
            }

            DebugLog($"Downloaded {filePath}", message);

            if (ConvertableMediaExtensions.ContainsKey(Path.GetExtension(filePath)))
            {
                MediaFileArchivingChannel.Writer.TryWrite((fileName, filePath, message));
            }
            else
            {
                FileArchivingChannel.Writer.TryWrite((fileName, filePath, message, Delete: true));
            }

            int fileCount = Interlocked.Increment(ref _fileCounter);

            if (fileCount % 20 == 0)
            {
                var drive = DriveInfo.GetDrives().MaxBy(d => d.TotalSize);

                long availableMB = drive.AvailableFreeSpace / 1024 / 1024;
                long totalMB = drive.TotalSize / 1024 / 1024;
                double used = (double)availableMB / totalMB;

                string spaceMessage = $"Space available: {availableMB} / {totalMB} MB";

                if (used > 0.75) await DebugAsync(spaceMessage, message);
                else DebugLog(spaceMessage, message);
            }
        }
        catch (Exception ex)
        {
            DebugLog(ex.ToString(), message);
        }
    }

    [Flags]
    public enum VoiceStatusUpdateFlags : uint
    {
        WasMuted = 1 << 0,
        WasDeafened = 1 << 1,
        WasSuppressed = 1 << 2,
        WasSelfMuted = 1 << 3,
        WasSelfDeafened = 1 << 4,
        WasStreaming = 1 << 5,
        IsMuted = 1 << 16,
        IsDeafened = 1 << 17,
        IsSuppressed = 1 << 18,
        IsSelfMuted = 1 << 19,
        IsSelfDeafened = 1 << 20,
        IsStreaming = 1 << 21,
    }

    private static VoiceStatusUpdateFlags GetVoiceStatusUpdateFlags(SocketVoiceState status)
    {
        VoiceStatusUpdateFlags flags = default;
        if (status.IsMuted) flags |= VoiceStatusUpdateFlags.IsMuted;
        if (status.IsDeafened) flags |= VoiceStatusUpdateFlags.IsDeafened;
        if (status.IsSuppressed) flags |= VoiceStatusUpdateFlags.IsSuppressed;
        if (status.IsSelfMuted) flags |= VoiceStatusUpdateFlags.IsSelfMuted;
        if (status.IsSelfDeafened) flags |= VoiceStatusUpdateFlags.IsSelfDeafened;
        if (status.IsStreaming) flags |= VoiceStatusUpdateFlags.IsStreaming;
        return flags;
    }

    public sealed class EmoteModel
    {
        public LogEmote Emote { get; set; }
        public string Emoji { get; set; }

        public static EmoteModel FromEmote(IEmote iEmote) => new()
        {
            Emote = iEmote is Emote emote ? LogEmote.FromEmote(emote) : null,
            Emoji = (iEmote as Emoji)?.Name,
        };
    }

    public sealed class LogEmote
    {
        public string Name { get; set; }
        public ulong Id { get; set; }
        public bool Animated { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string Url { get; set; }

        public static LogEmote FromEmote(Emote emote) => new LogEmote()
        {
            Name = emote.Name,
            Id = emote.Id,
            Animated = emote.Animated,
            CreatedAt = emote.CreatedAt,
            Url = emote.Url,
        };
    }

    public sealed class LogMessageModel
    {
        public LogSeverity Severity { get; set; }
        public string Source { get; set; }
        public string Message { get; set; }
        public string Exception { get; set; }

        public static LogMessageModel FromLogMessage(LogMessage logMessage) => new LogMessageModel()
        {
            Severity = logMessage.Severity,
            Source = logMessage.Source,
            Message = logMessage.Message,
            Exception = logMessage.Exception?.ToString()
        };
    }

    public sealed class AttachmentModel
    {
        public ulong Id { get; set; }
        public string Filename { get; set; }
        public string Url { get; set; }
        public string ProxyUrl { get; set; }
        public int Size { get; set; }
        public int? Height { get; set; }
        public int? Width { get; set; }

        public static AttachmentModel FromAttachment(Attachment attachment) => new AttachmentModel()
        {
            Id = attachment.Id,
            Filename = attachment.Filename,
            Url = attachment.Url,
            ProxyUrl = attachment.ProxyUrl,
            Size = attachment.Size,
            Height = attachment.Height,
            Width = attachment.Width
        };
    }

    public sealed class RoleModel
    {
        public ulong Id { get; set; }
        public Color Color { get; set; }
        public bool IsHoisted { get; set; }
        public bool IsManaged { get; set; }
        public bool IsMentionable { get; set; }
        public string Name { get; set; }
        public ulong Permissions { get; set; }
        public int Position { get; set; }
        public bool IsEveryone { get; set; }

        public static RoleModel FromSocketRole(SocketRole role) => new RoleModel()
        {
            Id = role.Id,
            Color = role.Color,
            IsHoisted = role.IsHoisted,
            IsManaged = role.IsManaged,
            IsMentionable = role.IsMentionable,
            Name = role.Name,
            Permissions = role.Permissions.RawValue,
            Position = role.Position,
            IsEveryone = role.IsEveryone
        };
    }

    private sealed class ChannelModel
    {
        public ulong Id { get; set; }
        public string Name { get; set; }
        public int Position { get; set; }
        public OverwriteModel[] PermissionOverwrites { get; set; }

        public static ChannelModel FromSocketChannel(SocketGuildChannel channel) => new()
        {
            Id = channel.Id,
            Name = channel.Name,
            Position = channel.Position,
            PermissionOverwrites = [.. channel.PermissionOverwrites.Select(OverwriteModel.FromOverwrite)]
        };
    }

    private struct OverwriteModel
    {
        public ulong TargetId { get; set; }
        public PermissionTarget TargetType { get; set; }
        public ulong AllowPermissions { get; set; }
        public ulong DenyPermissions { get; set; }

        public static OverwriteModel FromOverwrite(Overwrite overwrite) => new()
        {
            TargetId = overwrite.TargetId,
            TargetType = overwrite.TargetType,
            AllowPermissions = overwrite.Permissions.AllowValue,
            DenyPermissions = overwrite.Permissions.DenyValue
        };
    }

    public enum EventType
    {
        MessageReceived = 1,
        MessageUpdated,
        MessageDeleted,
        FileReceived,
        DebugMessage,
        ReactionAdded,
        ReactionRemoved,
        ReactionsCleared,
        VoiceStatusUpdated,
        UserBanned,
        UserLeft,
        UserJoined,
        UserIsTyping,
        UserJoinedVoice,
        UserLeftVoice,
        ChannelDestroyed,
        UserUnbanned,
        UserNicknameChanged,
        UserUsernameChanged,
        UserAvatarIdChanged,
        UserActiveClientsChanged,
        UserActivitiesChanged,
        UserRoleAdded,
        UserRoleRemoved,
        GuildAvailable,
        GuildUnavailable,
        JoinedGuild,
        LeftGuild,
        RoleCreated,
        RoleUpdated,
        RoleDeleted,
        ChannelCreated,
        ChannelUpdated,
    }

    private sealed class LegacyLogEvent
    {
        public EventType Type { get; set; }
        public DateTime TimeStamp { get; set; }
        public ulong GuildID { get; set; }
        public ulong ChannelID { get; set; }
        public ulong MessageID { get; set; }
        public ulong PreviousMessageID { get; set; }
        public ulong UserID { get; set; }
        public string Content { get; set; }
        public AttachmentModel Attachment { get; set; }
        public LogEmote Emote { get; set; }
        public string Emoji { get; set; }
        public VoiceStatusUpdateFlags VoiceStatusUpdated { get; set; }
        public LogMessageModel LogMessage { get; set; }
        public RoleModel Role { get; set; }
    }

    [GeneratedRegex(@"https:\/\/cdn\.discordapp\.com\/[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CdnLinksRegex();

    [GeneratedRegex(@"https:\/\/discord\.gift\/[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex GiftMessageRegex();
}
