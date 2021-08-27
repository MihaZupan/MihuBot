using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace MihuBot
{
    public delegate bool RosBytePredicate(ReadOnlySpan<byte> chars);

    public sealed class Logger
    {
        public readonly LoggerOptions Options;

        private readonly HttpClient _http;

        private DiscordSocketClient Discord => Options.Discord;

        private int _fileCounter = 0;

        private static readonly ReadOnlyMemory<byte> NewLineByte = new[] { (byte)'\n' };
        private static readonly char[] TrimChars = new[] { ' ', '\t', '\n', '\r' };
        internal static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        private readonly SemaphoreSlim LogSemaphore = new(1, 1);
        private readonly Channel<LogEvent> LogChannel;
        private BrotliStream JsonLogStream;
        private string JsonLogPath;
        private DateTime LogDate;

        private readonly Channel<(string FileName, string FilePath, SocketMessage Message, bool Delete)> FileArchivingChannel;
        private readonly BlobContainerClient BlobContainerClient;
        private readonly ConcurrentDictionary<string, TaskCompletionSource> FileArchivingCompletions = new();

        private static readonly FileBackedHashSet _cdnLinksHashSet = new("CdnLinks.txt", StringComparer.OrdinalIgnoreCase);
        private static readonly Regex _cdnLinksRegex = new(
            @"https:\/\/cdn\.discordapp\.com\/[^\s]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const ulong IgnoredListChannelId = 806065691689746432ul;
        private static ConcurrentDictionary<ulong, bool> _ignoredGuildsAndChannels = new();

        public async Task OnShutdownAsync()
        {
            try
            {
                Task uploadTask = await ResetLogFileAsync();
                await Task.WhenAny(uploadTask, Task.Delay(TimeSpan.FromSeconds(15)));
            }
            catch { }
        }

        private async Task ChannelReaderTaskAsync()
        {
            const int QueueSize = 100_000;
            List<LogEvent> events = new(128);
            while (await LogChannel.Reader.WaitToReadAsync())
            {
                while (events.Count < QueueSize && LogChannel.Reader.TryRead(out LogEvent logEvent))
                {
                    events.Add(logEvent);
                }

                await LogSemaphore.WaitAsync();
                try
                {
                    try
                    {
                        foreach (LogEvent logEvent in events)
                        {
                            try
                            {
                                await JsonSerializer.SerializeAsync(JsonLogStream, logEvent, JsonOptions);
                            }
                            catch (Exception ex)
                            {
                                try
                                {
                                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(logEvent);
                                    await JsonLogStream.WriteAsync(NewLineByte);
                                    await JsonLogStream.WriteAsync(Encoding.UTF8.GetBytes(json));

                                    DebugLog(json + ": " + ex.ToString());
                                }
                                catch { }
                            }
                            finally
                            {
                                await JsonLogStream.WriteAsync(NewLineByte);
                            }
                        }
                    }
                    finally
                    {
                        await JsonLogStream.FlushAsync();
                        await JsonLogStream.BaseStream.FlushAsync();
                    }
                }
                finally
                {
                    LogSemaphore.Release();
                }

                events.Clear();

                if (DateTime.UtcNow.Subtract(LogDate) >= TimeSpan.FromHours(1))
                {
                    await ResetLogFileAsync();
                }
            }
        }

        private async Task FileArchivingTaskAsync()
        {
            await foreach (var (FileName, FilePath, Message, Delete) in FileArchivingChannel.Reader.ReadAllAsync())
            {
                try
                {
                    string extension = Path.GetExtension(FilePath)?.ToLowerInvariant();

                    bool isImage =
                        extension == ".jpg" ||
                        extension == ".jpeg" ||
                        extension == ".png" ||
                        extension == ".gif";

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

                    if (accessTier == AccessTier.Archive && Message?.Guild()?.Id == Guilds.DDs)
                    {
                        accessTier = AccessTier.Cool;
                    }

                    string blobName = FilePath
                        .Substring(Options.LogsRoot.Length)
                        .Replace('/', '_')
                        .Replace('\\', '_');

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

                        if (isImage)
                        {
                            embed.WithImageUrl(blobClient.Uri.AbsoluteUri);
                        }

                        await Options.LogsFilesTextChannel.SendMessageAsync(embed: embed.Build());
                    }

                    DebugLog($"Archived {FilePath}");
                }
                catch (Exception ex)
                {
                    DebugLog($"Failed to archive {FilePath}: {ex}");
                }
                finally
                {
                    if (FileArchivingCompletions.TryRemove(FilePath, out var tcs))
                        tcs.TrySetResult();
                }
            }
        }

        public async Task<Task> ResetLogFileAsync()
        {
            TaskCompletionSource tcs = null;
            await LogSemaphore.WaitAsync();
            try
            {
                if (JsonLogStream != null)
                {
                    await JsonLogStream.FlushAsync();
                    await JsonLogStream.BaseStream.FlushAsync();

                    await JsonLogStream.DisposeAsync();

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        tcs = new TaskCompletionSource();
                        if (!FileArchivingCompletions.TryAdd(JsonLogPath, tcs))
                            tcs.TrySetResult();

                        FileArchivingChannel.Writer.TryWrite((Path.GetFileName(JsonLogPath), JsonLogPath, Message: null, Delete: false));
                    }
                }

                LogDate = DateTime.UtcNow;

                JsonLogPath = Path.Combine(Options.LogsRoot, Options.LogPrefix + LogDate.ToISODateTime() + ".json.br");

                Stream fileStream = File.Open(JsonLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);

                JsonLogStream = new BrotliStream(fileStream, (CompressionLevel)4);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
                LogSemaphore.Release();
            }

            return tcs?.Task ?? Task.CompletedTask;
        }

        public async Task<(LogEvent[] Logs, Exception[] ParsingErrors)> GetLogsAsync(DateTime after, DateTime before, Predicate<LogEvent> predicate, RosBytePredicate rawJsonPredicate = null)
        {
            if (after >= before)
                return (Array.Empty<LogEvent>(), Array.Empty<Exception>());

            List<LogEvent> events = new();
            List<Exception> parsingErrors = new();
            rawJsonPredicate ??= delegate { return true; };

            await LogSemaphore.WaitAsync();
            try
            {
                foreach (var file in GetLogFilesForDateRange(after, before))
                {
                    try
                    {
                        await using Stream fileStream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        await using Stream stream = file.EndsWith(".br", StringComparison.OrdinalIgnoreCase)
                            ? new BrotliStream(fileStream, CompressionMode.Decompress)
                            : fileStream;

                        PipeReader pipeReader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 32 * 1024));

                        while (true)
                        {
                            ReadResult result = await pipeReader.ReadAsync();
                            ReadOnlySequence<byte> buffer = result.Buffer;

                            SequencePosition position = Consume(buffer, events, parsingErrors, after, before, predicate, rawJsonPredicate);

                            if (result.IsCompleted)
                                break;

                            pipeReader.AdvanceTo(position, buffer.End);
                        }

                        await pipeReader.CompleteAsync();
                    }
                    catch (Exception ex)
                    {
                        parsingErrors.Add(new Exception($"Exception when parsing {file}", ex));
                    }
                }
            }
            finally
            {
                LogSemaphore.Release();
            }

            return (events.ToArray(), parsingErrors.ToArray());

            string[] GetLogFilesForDateRange(DateTime after, DateTime before)
            {
                string afterString = after.Subtract(TimeSpan.FromHours(2)).ToISODateTime();
                string beforeString = before.Add(TimeSpan.FromHours(2)).ToISODateTime();

                string[] files = Directory.GetFiles(Options.LogsRoot)
                    .Where(file =>
                        file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".json.br", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file)
                    .ToArray();

                int startIndex = 0, endIndex;
                for (endIndex = 0; endIndex < files.Length; endIndex++)
                {
                    string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(files[endIndex]));
                    name = name.Substring(Options.LogPrefix.Length);

                    if (name.CompareTo(afterString) <= 0)
                    {
                        startIndex = endIndex;
                    }

                    if (name.CompareTo(beforeString) > 0)
                    {
                        break;
                    }
                }

                return files.AsSpan(startIndex, endIndex - startIndex).ToArray();
            }

            static SequencePosition Consume(ReadOnlySequence<byte> buffer, List<LogEvent> events, List<Exception> parsingErrors, DateTime after, DateTime before, Predicate<LogEvent> predicate, RosBytePredicate rawJsonPredicate)
            {
                Debug.Assert((int)Enum.GetValues<EventType>().Max() < 100);

                var reader = new SequenceReader<byte>(buffer);
                while (reader.TryReadTo(out ReadOnlySpan<byte> span, (byte)'\n'))
                {
                    if (!rawJsonPredicate(span))
                    {
                        continue;
                    }

                    LogEvent logEvent;
                    try
                    {
                        logEvent = JsonSerializer.Deserialize<LogEvent>(span, JsonOptions);
                    }
                    catch (Exception ex)
                    {
                        parsingErrors.Add(ex);
                        continue;
                    }

                    if (logEvent.TimeStamp < after || logEvent.TimeStamp > before)
                    {
                        continue;
                    }

                    if (predicate(logEvent))
                    {
                        events.Add(logEvent);
                    }
                }
                return reader.Position;
            }
        }

        private void Log(LogEvent logEvent) => LogChannel.Writer.TryWrite(logEvent);

        public void DebugLog(string debugMessage, SocketUserMessage message) =>
            DebugLog(debugMessage, message?.Guild()?.Id ?? 0, message?.Channel.Id ?? 0, message?.Id ?? 0, message?.Author.Id ?? 0);

        public void DebugLog(string debugMessage, ulong guildId = 0, ulong channelId = 0, ulong messageId = 0, ulong authorId = 0)
        {
            Log(new LogEvent(EventType.DebugMessage, guildId, channelId, messageId)
            {
                Content = debugMessage,
                UserID = authorId
            });
        }

        public async Task DebugAsync(string debugMessage, SocketUserMessage message = null)
        {
            DebugLog(debugMessage, message);

            lock (Console.Out)
                Console.WriteLine("DEBUG: " + debugMessage);

            try
            {
                if (debugMessage.Length >= 2000)
                    debugMessage = debugMessage.Substring(0, 1995) + " ...";

                await Options.DebugTextChannel.SendMessageAsync(debugMessage);
            }
            catch { }
        }

        private bool ShouldLogAttachments(SocketUserMessage message)
        {
            if (message.Guild()?.Id is ulong guildId && _ignoredGuildsAndChannels.ContainsKey(guildId))
                return false;

            if (_ignoredGuildsAndChannels.ContainsKey(message.Channel.Id))
                return false;

            return Options.ShouldLogAttachments(message);
        }

        public Logger(HttpClient httpClient, LoggerOptions options, IConfiguration configuration)
        {
            _http = httpClient;
            Options = options;

            BlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString"],
                "discord");

            Directory.CreateDirectory(Options.LogsRoot);
            Directory.CreateDirectory(Options.FilesRoot);

            Task createLogStreamsTask = ResetLogFileAsync();
            Debug.Assert(createLogStreamsTask.IsCompletedSuccessfully);
            createLogStreamsTask.GetAwaiter().GetResult();

            LogChannel = Channel.CreateUnbounded<LogEvent>(new UnboundedChannelOptions() { SingleReader = true });
            FileArchivingChannel = Channel.CreateUnbounded<(string, string, SocketMessage, bool)>(new UnboundedChannelOptions() { SingleReader = true });

            Task.Run(ChannelReaderTaskAsync);
            Task.Run(FileArchivingTaskAsync);

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

            /*
GuildUpdated
VoiceServerUpdated
RecipientRemoved
RecipientAdded
            */
        }

        private Task ChannelUpdatedAsync(SocketChannel beforeChannel, SocketChannel afterChannel)
        {
            if (beforeChannel is SocketGuildChannel before && afterChannel is SocketGuildChannel after)
            {
                Log(new LogEvent(EventType.ChannelUpdated, after)
                {
                    Content = $"{JsonSerializer.Serialize(ChannelModel.FromSocketChannel(before), JsonOptions)} => {JsonSerializer.Serialize(ChannelModel.FromSocketChannel(after), JsonOptions)}"
                });

                if (Discord.CurrentUser.Id == KnownUsers.Miha &&
                    !before.PermissionOverwrites.OverwritesEqual(after.PermissionOverwrites))
                {
                    Task.Run(async () => await LogPermissionsChangedAsync(before, after));
                }
            }
            return Task.CompletedTask;
        }

        private Task ChannelCreatedAsync(SocketChannel channel)
        {
            if (channel is SocketGuildChannel guildChannel)
            {
                Log(new LogEvent(EventType.ChannelCreated, guildChannel)
                {
                    Content = JsonSerializer.Serialize(ChannelModel.FromSocketChannel(guildChannel), JsonOptions)
                });

                if (Discord.CurrentUser.Id == KnownUsers.Miha)
                {
                    Task.Run(async () => await LogPermissionsChangedAsync(null, guildChannel));
                }
            }
            return Task.CompletedTask;
        }

        private Task ChannelDestroyedAsync(SocketChannel channel)
        {
            if (channel is SocketGuildChannel guildChannel)
            {
                Log(new LogEvent(EventType.ChannelDestroyed, guildChannel)
                {
                    Content = JsonSerializer.Serialize(ChannelModel.FromSocketChannel(guildChannel), JsonOptions)
                });
            }
            return Task.CompletedTask;
        }

        private Task RoleCreatedAsync(SocketRole role)
        {
            Log(new LogEvent(EventType.RoleCreated, role.Guild.Id)
            {
                Role = RoleModel.FromSocketRole(role)
            });
            return Task.CompletedTask;
        }

        private Task RoleDeletedAsync(SocketRole role)
        {
            Log(new LogEvent(EventType.RoleDeleted, role.Guild.Id)
            {
                Role = RoleModel.FromSocketRole(role)
            });
            return Task.CompletedTask;
        }

        private Task RoleUpdatedAsync(SocketRole before, SocketRole after)
        {
            Log(new LogEvent(EventType.RoleUpdated, after.Guild.Id)
            {
                Role = RoleModel.FromSocketRole(after)
            });
            return Task.CompletedTask;
        }

        private async Task JoinedGuildAsync(SocketGuild guild)
        {
            Log(new LogEvent(EventType.JoinedGuild, guild.Id));
            await DebugAsync($"Added to {guild.Name} ({guild.Id})");
        }

        private async Task LeftGuildAsync(SocketGuild guild)
        {
            Log(new LogEvent(EventType.LeftGuild, guild.Id));
            await DebugAsync($"Left {guild.Name} ({guild.Id})");
        }

        private Task GuildUnavailableAsync(SocketGuild guild)
        {
            Log(new LogEvent(EventType.GuildUnavailable, guild.Id));
            return Task.CompletedTask;
        }

        private Task GuildAvailableAsync(SocketGuild guild)
        {
            Log(new LogEvent(EventType.GuildAvailable, guild.Id));
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
                    Log(new LogEvent(EventType.UserNicknameChanged, guildId)
                    {
                        UserID = after.Id,
                        Content = after.Nickname
                    });
                }

                if (before is null || !before.Roles.SequenceIdEquals(after.Roles))
                {
                    if (before != null)
                    {
                        foreach (SocketRole beforeRole in before.Roles)
                        {
                            if (!after.Roles.Any(beforeRole.Id))
                            {
                                Log(new LogEvent(EventType.UserRoleRemoved, guildId)
                                {
                                    UserID = afterUser.Id,
                                    Role = RoleModel.FromSocketRole(beforeRole)
                                });
                            }
                        }
                    }

                    foreach (SocketRole afterRole in after.Roles)
                    {
                        if (before is null || !before.Roles.Any(afterRole.Id))
                        {
                            Log(new LogEvent(EventType.UserRoleAdded, guildId)
                            {
                                UserID = afterUser.Id,
                                Role = RoleModel.FromSocketRole(afterRole)
                            });
                        }
                    }
                }
            }

            if (beforeUser is null || beforeUser.Username != afterUser.Username || beforeUser.DiscriminatorValue != afterUser.DiscriminatorValue)
            {
                Log(new LogEvent(EventType.UserUsernameOrDiscriminatorChanged, guildId)
                {
                    UserID = afterUser.Id,
                    Content = $"{afterUser.Username}#{afterUser.Discriminator}"
                });
            }

            if (beforeUser?.AvatarId != afterUser.AvatarId)
            {
                Log(new LogEvent(EventType.UserAvatarIdChanged, guildId)
                {
                    UserID = afterUser.Id,
                    Content = afterUser.AvatarId
                });
            }

            if (beforeUser is null ? afterUser.ActiveClients.Count != 0 : !beforeUser.ActiveClients.SetEquals(afterUser.ActiveClients))
            {
                Log(new LogEvent(EventType.UserActiveClientsChanged, guildId)
                {
                    UserID = afterUser.Id,
                    Content = string.Join(' ', afterUser.ActiveClients)
                });
            }

            if (beforeUser is null ? afterUser.Activities.Count != 0 : !beforeUser.Activities.SequenceEqual(afterUser.Activities))
            {
                Log(new LogEvent(EventType.UserActivitiesChanged, guildId)
                {
                    UserID = afterUser.Id,
                    Content = JsonSerializer.Serialize(afterUser.Activities, JsonOptions)
                });
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
            Log(new LogEvent(EventType.DebugMessage)
            {
                LogMessage = LogMessageModel.FromLogMessage(logMessage)
            });
            return Task.CompletedTask;
        }

        private Task UserIsTypingAsync(Cacheable<IUser, ulong> user, Cacheable<IMessageChannel, ulong> channel)
        {
            Log(new LogEvent(EventType.UserIsTyping, guildId: 0, channelId: channel.Id, messageId: 0)
            {
                UserID = user.Id
            });
            return Task.CompletedTask;
        }

        private Task UserJoinedAsync(SocketGuildUser user)
        {
            Log(new LogEvent(EventType.UserJoined, user.Guild.Id)
            {
                UserID = user.Id
            });
            return Task.CompletedTask;
        }

        private Task UserLeftAsync(SocketGuildUser user)
        {
            Log(new LogEvent(EventType.UserLeft, user.Guild.Id)
            {
                UserID = user.Id
            });
            return Task.CompletedTask;
        }

        private Task UserBannedAsync(SocketUser user, SocketGuild guild)
        {
            Log(new LogEvent(EventType.UserBanned, guild.Id)
            {
                UserID = user.Id
            });
            return Task.CompletedTask;
        }

        private Task UserUnbannedAsync(SocketUser user, SocketGuild guild)
        {
            Log(new LogEvent(EventType.UserUnbanned, guild.Id)
            {
                UserID = user.Id
            });
            return Task.CompletedTask;
        }

        private Task UserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
        {
            if (before.VoiceChannel == after.VoiceChannel)
            {
                Log(new LogEvent(EventType.VoiceStatusUpdated, before.VoiceChannel)
                {
                    UserID = user.Id,
                    VoiceStatusUpdated = (VoiceStatusUpdateFlags)((int)GetVoiceStatusUpdateFlags(before) >> 16) | GetVoiceStatusUpdateFlags(after)
                });
                return Task.CompletedTask;
            }

            bool left = after.VoiceChannel is null;
            bool joined = before.VoiceChannel is null;

            if (!left && !joined)
                left = joined = true; // Switched calls

            if (left)
            {
                Log(new LogEvent(EventType.UserLeftVoice, before.VoiceChannel)
                {
                    UserID = user.Id,
                    VoiceStatusUpdated = GetVoiceStatusUpdateFlags(before)
                });
            }

            if (joined)
            {
                Log(new LogEvent(EventType.UserJoinedVoice, after.VoiceChannel)
                {
                    UserID = user.Id,
                    VoiceStatusUpdated = GetVoiceStatusUpdateFlags(after)
                });
            }

            return Task.CompletedTask;
        }

        private Task ReactionsClearedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
        {
            Log(new LogEvent(EventType.ReactionsCleared, guildId: 0, channel.Id, message.Id));
            return Task.CompletedTask;
        }

        private Task ReactionRemovedAsync(SocketReaction reaction)
        {
            Log(new LogEvent(EventType.ReactionRemoved, reaction.Channel.Guild()?.Id ?? 0, reaction.Channel.Id, reaction.MessageId)
            {
                UserID = reaction.UserId,
                Emote = reaction.Emote is Emote emote ? LogEmote.FromEmote(emote) : null,
                Emoji = (reaction.Emote as Emoji)?.Name
            });
            return Task.CompletedTask;
        }

        private Task ReactionAddedAsync(SocketReaction reaction)
        {
            Log(new LogEvent(EventType.ReactionAdded, reaction.Channel.Guild()?.Id ?? 0, reaction.Channel.Id, reaction.MessageId)
            {
                UserID = reaction.UserId,
                Emote = reaction.Emote is Emote emote ? LogEmote.FromEmote(emote) : null,
                Emoji = (reaction.Emote as Emoji)?.Name
            });
            return Task.CompletedTask;
        }

        private Task MessagesBulkDeletedAsync(IReadOnlyCollection<Cacheable<IMessage, ulong>> cacheables, Cacheable<IMessageChannel, ulong> channel)
        {
            foreach (var cacheable in cacheables)
            {
                Log(new LogEvent(EventType.MessageDeleted, guildId: 0, channel.Id, cacheable.Id));
            }

            return Task.CompletedTask;
        }

        private Task MessageDeletedAsync(Cacheable<IMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
        {
            Log(new LogEvent(EventType.MessageDeleted, guildId: 0, channel.Id, message.Id));
            return Task.CompletedTask;
        }

        private Task MessageReceivedAsync(SocketMessage message, ulong? previousId = null)
        {
            if (message is not SocketUserMessage userMessage)
                return Task.CompletedTask;

            ulong channelId = message.Channel.Id;

            if (channelId == Channels.LogText || channelId == 750706839431413870ul || channelId == Channels.Files)
                return Task.CompletedTask;

            bool isRetirementHome = message.Guild()?.Id == Guilds.RetirementHome;

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                Log(new LogEvent(previousId is null ? EventType.MessageReceived : EventType.MessageUpdated, userMessage)
                {
                    PreviousMessageID = previousId ?? 0
                });
            }

            if (message.Author.Id != KnownUsers.MihuBot)
            {
                const string CdnLinkPrefix = "https://cdn.discordapp.com/";

                foreach (var attachment in message.Attachments)
                {
                    Log(new LogEvent(userMessage, attachment));

                    if (!ShouldLogAttachments(userMessage))
                        break;

                    if (isRetirementHome)
                    {
                        if (attachment.Size > 1024 * 1024 * 8) // 8 MB
                            continue;
                    }

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

                if (!isRetirementHome &&
                    message.Content.Contains(CdnLinkPrefix, StringComparison.OrdinalIgnoreCase) &&
                    ShouldLogAttachments(userMessage))
                {
                    _ = Task.Run(() =>
                    {
                        foreach (Match match in _cdnLinksRegex.Matches(message.Content))
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
                    });
                }
            }

            if (channelId == IgnoredListChannelId || (_ignoredGuildsAndChannels.IsEmpty && _ignoredGuildsAndChannels.TryAdd(ulong.MaxValue, true)))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var ignored = new ConcurrentDictionary<ulong, bool>();
                        _ignoredGuildsAndChannels.TryAdd(ulong.MaxValue, true);

                        var channel = Discord.GetTextChannel(IgnoredListChannelId);
                        foreach (var message in await channel.DangerousGetAllMessagesAsync(this, auditReason: null))
                        {
                            foreach (var part in message.Content.NormalizeNewLines().Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (ulong.TryParse(part, out ulong id))
                                {
                                    ignored.TryAdd(id, true);
                                }
                            }
                        }

                        _ignoredGuildsAndChannels = ignored;
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

                FileArchivingChannel.Writer.TryWrite((fileName, filePath, message, Delete: true));

                int fileCount = Interlocked.Increment(ref _fileCounter);

                if (fileCount % 20 == 0)
                {
                    var drive = DriveInfo.GetDrives()
                        .OrderByDescending(d => d.TotalSize)
                        .First();

                    string spaceMessage = $"Space available: {drive.AvailableFreeSpace / 1024 / 1024} / {drive.TotalSize / 1024 / 1024} MB";

                    if (fileCount % 100 == 0) await DebugAsync(spaceMessage, message);
                    else DebugLog(spaceMessage, message);
                }
            }
            catch (Exception ex)
            {
                DebugLog(ex.ToString(), message);
            }
        }

        private async Task LogPermissionsChangedAsync(SocketGuildChannel before, SocketGuildChannel after)
        {
            try
            {
                if (after.Guild.Id != Guilds.DDs && after.Guild.Id != Guilds.ComfyCove)
                {
                    return;
                }

                bool updated = before is not null;

                var embedBuilder = new EmbedBuilder()
                    .WithTitle($"Channel *{after.Name}* {(updated ? "updated" : "created")}")
                    .WithUrl(after.GetJumpUrl())
                    .WithColor(0x00, 0x42, 0xFF)
                    .WithThumbnailUrl(after.Guild.IconUrl);

                Overwrite[] overwrites = after.PermissionOverwrites.ToArray();
                if (updated && overwrites.Length > 25)
                {
                    Overwrite[] overwritesBefore = before.PermissionOverwrites.ToArray();
                    if (overwritesBefore.Length != 0)
                    {
                        overwrites = overwrites
                            .Where(o => !overwritesBefore.Any(ob => o.OverwriteEquals(ob)))
                            .ToArray();
                    }
                }

                for (int i = 0; i < Math.Min(25, overwrites.Length); i++)
                {
                    Overwrite overwrite = overwrites[i];

                    string name = overwrite.TargetType == PermissionTarget.User
                        ? after.Guild.GetUser(overwrite.TargetId)?.Username ?? $"User {overwrite.TargetId}"
                        : after.Guild.GetRole(overwrite.TargetId)?.Name ?? $"Role {overwrite.TargetId}";

                    string allow = string.Join('\n', overwrite.Permissions.ToAllowList());
                    string deny = string.Join('\n', overwrite.Permissions.ToDenyList());

                    string value = $"{(allow.Length == 0 ? "" : $"{Emotes.Checkmark}\n")}{allow}";
                    if (allow.Length != 0 && deny.Length != 0) value += "\n";
                    value += $"{(deny.Length == 0 ? "" : $"{Emotes.RedCross}\n")}{deny}";

                    if (value.Length != 0)
                    {
                        embedBuilder.AddField(name, value, inline: true);
                    }
                }

                await Options.LogsTextChannel.SendMessageAsync(embed: embedBuilder.Build());
            }
            catch (Exception ex)
            {
                await DebugAsync(ex.ToString());
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
                PermissionOverwrites = channel.PermissionOverwrites.Select(o => OverwriteModel.FromOverwrite(o)).ToArray()
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
            UserUsernameOrDiscriminatorChanged,
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

        public sealed class LogEvent
        {
            public EventType Type { get; set; }
            public DateTime TimeStamp { get; set; }

            public LogEvent() { }

            public LogEvent(EventType type)
            {
                Type = type;
                TimeStamp = DateTime.UtcNow;
            }

            public LogEvent(EventType type, ulong guildId)
                : this(type)
            {
                GuildID = guildId;
            }

            public LogEvent(EventType type, ulong guildId, ulong channelId, ulong messageId)
                : this(type, guildId)
            {
                ChannelID = channelId;
                MessageID = messageId;
            }

            public LogEvent(EventType type, SocketUserMessage message)
                : this(type, message.Guild()?.Id ?? 0, message.Channel.Id, message.Id)
            {
                if (type == EventType.MessageReceived || type == EventType.MessageUpdated || type == EventType.FileReceived)
                {
                    Content = message.Content?.Trim(TrimChars);

                    if (message.Embeds.Any())
                    {
                        Embeds = JsonSerializer.Serialize(message.Embeds, JsonOptions);
                    }
                }

                UserID = message.Author.Id;
            }

            public LogEvent(SocketUserMessage message, Attachment attachment)
                : this(EventType.FileReceived, message)
            {
                Attachment = AttachmentModel.FromAttachment(attachment);
            }

            public LogEvent(EventType type, IChannel channel)
                : this(type, (channel as SocketGuildChannel)?.Guild.Id ?? 0)
            {
                ChannelID = channel.Id;
            }

            public ulong GuildID { get; set; }
            public ulong ChannelID { get; set; }
            public ulong MessageID { get; set; }
            public ulong PreviousMessageID { get; set; }
            public ulong UserID { get; set; }
            public string Content { get; set; }
            public string Embeds { get; set; }
            public AttachmentModel Attachment { get; set; }
            public LogEmote Emote { get; set; }
            public string Emoji { get; set; }
            public VoiceStatusUpdateFlags VoiceStatusUpdated { get; set; }
            public LogMessageModel LogMessage { get; set; }
            public RoleModel Role { get; set; }

            public void ToString(StringBuilder builder, DiscordSocketClient client)
            {
                builder.Append(TimeStamp.Year);
                builder.Append('-');

                AppendTwoDigits(builder, TimeStamp.Month);
                builder.Append('-');

                AppendTwoDigits(builder, TimeStamp.Day);
                builder.Append('_');

                AppendTwoDigits(builder, TimeStamp.Hour);
                builder.Append('-');

                AppendTwoDigits(builder, TimeStamp.Minute);
                builder.Append('-');

                AppendTwoDigits(builder, TimeStamp.Second);
                builder.Append(' ');

                builder.Append(Type.ToString());

                SocketGuild guild = null;
                if (GuildID != 0)
                {
                    builder.Append(": ");

                    guild = client.GetGuild(GuildID);
                    if (guild is null)
                    {
                        builder.Append(GuildID);
                    }
                    else
                    {
                        builder.Append(guild.Name);
                    }
                }

                if (ChannelID != 0)
                {
                    builder.Append(GuildID == 0 ? ": " : " - ");

                    SocketGuildChannel channel = guild?.GetChannel(ChannelID);
                    if (channel is null)
                    {
                        builder.Append(ChannelID);
                    }
                    else
                    {
                        builder.Append(channel.Name);
                    }
                }

                if (UserID != 0)
                {
                    builder.Append(" - ");
                    string username = client.GetUser(UserID)?.Username;
                    if (username is null)
                    {
                        builder.Append(UserID);
                    }
                    else
                    {
                        builder.Append(username);
                    }
                }

                if (PreviousMessageID != 0)
                {
                    builder.Append(" - PreviousID ");
                    builder.Append(PreviousMessageID);
                }

                if (Content != null)
                {
                    builder.Append(" - ");
                    if (Content.AsSpan().IndexOfAny('\n', '\r') == -1)
                    {
                        builder.Append(Content);
                    }
                    else
                    {
                        builder.Append(Content.NormalizeNewLines().Replace("\n", " <new-line> "));
                    }
                }

                if (Attachment != null)
                {
                    builder.Append(" - File ");
                    builder.Append(Attachment.Url);
                    builder.Append(" - ");
                    builder.Append(Attachment.Filename);
                }

                if (LogMessage != null)
                {
                    builder.Append(": ");
                    builder.Append(JsonSerializer.Serialize(LogMessage, JsonOptions));
                }

                if (Emoji != null)
                {
                    builder.Append(" - Emoji ");
                    builder.Append(Emoji);
                }

                if (Emote != null)
                {
                    builder.Append(" - Emote ");
                    builder.Append(Emote.Name);
                    builder.Append(' ');
                    builder.Append(Emote.Url);
                }

                if (Role != null)
                {
                    builder.Append(": ");
                    builder.Append(JsonSerializer.Serialize(Role, JsonOptions));
                }

                static void AppendTwoDigits(StringBuilder builder, int value)
                {
                    if (value < 10) builder.Append('0');
                    builder.Append(value);
                }
            }
        }
    }
}
