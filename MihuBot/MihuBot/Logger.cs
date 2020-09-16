using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MihuBot
{
    public sealed class Logger
    {
        private const string LogsRoot = "logs/";
        private const string FilesRoot = LogsRoot + "files/";

        private readonly ServiceCollection _services;

        private int _fileCounter = 0;

        private static readonly ReadOnlyMemory<byte> NewLineByte = new[] { (byte)'\n' };
        private static readonly char[] TrimChars = new[] { ' ', '\t', '\n', '\r' };
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

        private readonly SemaphoreSlim LogSemaphore = new SemaphoreSlim(1, 1);
        private readonly Channel<LogEvent> LogChannel;
        private readonly StringBuilder LogBuilder = new StringBuilder(4 * 1024 * 1024);
        private Stream JsonLogStream;
        private string JsonLogPath;
        private DateTime LogDate;

        private readonly BlobContainerClient BlobContainerClient;
        private readonly Channel<(string FilePath, bool Delete)> FileArchivingChannel;

        private async Task ChannelReaderTaskAsync()
        {
            while (await LogChannel.Reader.WaitToReadAsync())
            {
                await LogSemaphore.WaitAsync();
                while (LogChannel.Reader.TryRead(out LogEvent logEvent))
                {
                    await JsonSerializer.SerializeAsync(JsonLogStream, logEvent, JsonOptions);
                    await JsonLogStream.WriteAsync(NewLineByte);

                    if (logEvent.Type != EventType.VoiceStatusUpdated && logEvent.Type != EventType.UserIsTyping)
                    {
                        logEvent.ToString(LogBuilder, _services.Discord);
                        LogBuilder.Append('\n');
                    }
                }
                await JsonLogStream.FlushAsync();
                LogSemaphore.Release();

                if (DateTime.UtcNow.Date != LogDate)
                {
                    await SendLogFilesAsync(LogsReportsTextChannel, resetLogFiles: true);
                }
            }
        }

        private async Task FileArchivingTaskAsync()
        {
            await foreach (var (FilePath, Delete) in FileArchivingChannel.Reader.ReadAllAsync())
            {
                try
                {
                    string extension = Path.GetExtension(FilePath)?.ToLowerInvariant();

                    AccessTier accessTier = extension switch
                    {
                        ".txt" => AccessTier.Hot,
                        ".json" => AccessTier.Hot,
                        ".html" => AccessTier.Hot,
                        ".jpg" => AccessTier.Cool,
                        ".jpeg" => AccessTier.Cool,
                        ".png" => AccessTier.Cool,
                        ".gif" => AccessTier.Cool,
                        ".mp3" => AccessTier.Cool,
                        ".wav" => AccessTier.Cool,
                        _ => AccessTier.Archive
                    };

                    string blobName = FilePath
                        .Substring(LogsRoot.Length)
                        .Replace('/', '_')
                        .Replace('\\', '_');

                    BlobClient blobClient = BlobContainerClient.GetBlobClient(blobName);

                    var blobOptions = new BlobUploadOptions() { AccessTier = accessTier };

                    using (FileStream fs = File.OpenRead(FilePath))
                    {
                        await blobClient.UploadAsync(fs, blobOptions);
                    }

                    if (Delete)
                    {
                        File.Delete(FilePath);
                    }

                    await DebugAsync($"Archived {FilePath}", logOnly: true);
                }
                catch (Exception ex)
                {
                    await DebugAsync($"Failed to archive {FilePath}: {ex}", logOnly: true);
                }
            }
        }

        public async Task SendLogFilesAsync(ITextChannel channel, bool resetLogFiles)
        {
            await LogSemaphore.WaitAsync();
            try
            {
                if (JsonLogStream != null)
                {
                    const int SizeLimit = 4 * 1024 * 1024;

                    string fileName = Path.GetFileNameWithoutExtension(JsonLogPath);

                    string readableLog = LogBuilder.ToString();
                    using var readableLogStream = new MemoryStream(Encoding.UTF8.GetBytes(readableLog));
                    if (readableLogStream.Length <= SizeLimit)
                    {
                        await channel.SendFileAsync(readableLogStream, fileName + ".txt");
                    }
                    else
                    {
                        using var brotliStream = new BrotliStream(readableLogStream, CompressionLevel.Optimal);
                        await channel.SendFileAsync(brotliStream, fileName + ".txt.br");
                    }

                    using var jsonFileStream = File.OpenRead(JsonLogPath);
                    if (jsonFileStream.Length <= SizeLimit)
                    {
                        await channel.SendFileAsync(jsonFileStream, fileName + ".json");
                    }
                    else
                    {
                        using var brotliStream = new BrotliStream(jsonFileStream, CompressionLevel.Optimal);
                        await channel.SendFileAsync(brotliStream, fileName + ".json.br");
                    }

                    if (resetLogFiles)
                    {
                        LogBuilder.Length = 0;

                        string textLogPath = Path.ChangeExtension(JsonLogPath, ".txt");
                        await File.WriteAllTextAsync(textLogPath, readableLog);

                        FileArchivingChannel.Writer.TryWrite((textLogPath, Delete: false));
                        FileArchivingChannel.Writer.TryWrite((JsonLogPath, Delete: false));

                        await JsonLogStream.DisposeAsync();
                    }
                }

                if (resetLogFiles)
                {
                    DateTime utcNow = DateTime.UtcNow;
                    LogDate = utcNow.Date;

                    string timeString = DateTimeString(utcNow);

                    JsonLogPath = Path.Combine(LogsRoot, timeString + ".json");
                    JsonLogStream = File.Open(JsonLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
                LogSemaphore.Release();
            }
        }

        public async Task<LogEvent[]> GetLogsAsync(DateTime after, DateTime before, Predicate<LogEvent>[] predicates)
        {
            if (after >= before)
                return Array.Empty<LogEvent>();

            string afterString = DateTimeString(after.Date.Subtract(TimeSpan.FromDays(2)));
            string beforeString = DateTimeString(before.Date.Add(TimeSpan.FromDays(2)));

            string[] files = Directory.GetFiles(LogsRoot)
                .OrderBy(file => file)
                .Where(file =>
                {
                    if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        return false;

                    string name = Path.GetFileNameWithoutExtension(file);
                    return name.CompareTo(afterString) >= 0 && name.CompareTo(beforeString) <= 0;
                })
                .ToArray();

            List<LogEvent> events = new List<LogEvent>();

            await LogSemaphore.WaitAsync();
            try
            {
                foreach (var file in files)
                {
                    using var fs = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);

                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        LogEvent logEvent;
                        try
                        {
                            logEvent = JsonSerializer.Deserialize<LogEvent>(line, JsonOptions);
                        }
                        catch { continue; }

                        if (logEvent.TimeStamp >= after && logEvent.TimeStamp <= before && predicates.All(logEvent))
                        {
                            events.Add(logEvent);
                        }
                    }
                }
            }
            finally
            {
                LogSemaphore.Release();
            }

            return events.ToArray();
        }

        private void Log(LogEvent logEvent) => LogChannel.Writer.TryWrite(logEvent);

        public async Task DebugAsync(string message, bool logOnly = false)
        {
            Log(new LogEvent(message));

            if (logOnly)
                return;

            lock (Console.Out)
                Console.WriteLine("DEBUG: " + message);

            try
            {
                await DebugTextChannel.SendMessageAsync(message);
            }
            catch { }
        }

        private static string DateTimeString(DateTime dateTime) => dateTime.ToString("yyyy-MM-dd_HH-mm-ss");

        public SocketTextChannel DebugTextChannel => _services.Discord.GetTextChannel(Guilds.Mihu, Channels.Debug);
        public SocketTextChannel LogsTextChannel => _services.Discord.GetTextChannel(Guilds.PrivateLogs, Channels.LogText);
        public SocketTextChannel LogsReportsTextChannel => _services.Discord.GetTextChannel(Guilds.PrivateLogs, Channels.LogReports);

        public Logger(ServiceCollection services)
        {
            _services = services;
            services.Logger = this;

            Directory.CreateDirectory(LogsRoot);
            Directory.CreateDirectory(FilesRoot);

            Task createLogStreamsTask = SendLogFilesAsync(channel: null, resetLogFiles: true);
            Debug.Assert(createLogStreamsTask.IsCompletedSuccessfully);
            createLogStreamsTask.GetAwaiter().GetResult();

            LogChannel = Channel.CreateUnbounded<LogEvent>(new UnboundedChannelOptions() { SingleReader = true });
            Task.Run(ChannelReaderTaskAsync);

            BlobContainerClient = new BlobContainerClient(Secrets.AzureStorage.ConnectionString, Secrets.AzureStorage.DiscordContainerName);
            FileArchivingChannel = Channel.CreateUnbounded<(string, bool)>(new UnboundedChannelOptions() { SingleReader = true });
            Task.Run(FileArchivingTaskAsync);

            _ = new Timer(_ => Log(new LogEvent("Keepalive")), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

            services.Discord.JoinedGuild += async guild => await DebugAsync($"Added to {guild.Name} ({guild.Id})");
            services.Discord.MessageReceived += message => MessageReceivedAsync(message);
            services.Discord.MessageUpdated += (cacheable, message, _) => MessageReceivedAsync(message, previousId: cacheable.Id);
            services.Discord.MessageDeleted += (cacheable, channel) => MessageDeletedAsync(cacheable.Id, channel);
            services.Discord.MessagesBulkDeleted += MessagesBulkDeletedAsync;
            services.Discord.ReactionAdded += (_, __, reaction) => ReactionAddedAsync(reaction);
            services.Discord.ReactionRemoved += (_, __, reaction) => ReactionRemovedAsync(reaction);
            services.Discord.ReactionsCleared += (cacheable, channel) => ReactionsClearedAsync(cacheable.Id, channel);
            services.Discord.UserVoiceStateUpdated += UserVoiceStateUpdatedAsync;
            services.Discord.UserBanned += UserBannedAsync;
            services.Discord.UserLeft += UserLeftAsync;
            services.Discord.UserJoined += UserJoinedAsync;
            services.Discord.UserIsTyping += UserIsTypingAsync;
        }

        private Task UserIsTypingAsync(SocketUser user, ISocketMessageChannel channel)
        {
            Log(new LogEvent(EventType.UserIsTyping, channel)
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

        private Task ReactionsClearedAsync(ulong messageId, ISocketMessageChannel channel)
        {
            Log(new LogEvent(EventType.ReactionsCleared, channel.Guild().Id, channel.Id, messageId));
            return Task.CompletedTask;
        }

        private Task ReactionRemovedAsync(SocketReaction reaction)
        {
            Log(new LogEvent(EventType.ReactionRemoved, reaction.Channel.Guild().Id, reaction.Channel.Id, reaction.MessageId)
            {
                UserID = reaction.UserId,
                Emote = reaction.Emote is Emote emote ? LogEmote.FromEmote(emote) : null,
                Emoji = (reaction.Emote as Emoji).Name
            });
            return Task.CompletedTask;
        }

        private Task ReactionAddedAsync(SocketReaction reaction)
        {
            Log(new LogEvent(EventType.ReactionAdded, reaction.Channel.Guild().Id, reaction.Channel.Id, reaction.MessageId)
            {
                UserID = reaction.UserId,
                Emote = reaction.Emote is Emote emote ? LogEmote.FromEmote(emote) : null,
                Emoji = (reaction.Emote as Emoji).Name
            });
            return Task.CompletedTask;
        }

        private Task MessagesBulkDeletedAsync(IReadOnlyCollection<Cacheable<IMessage, ulong>> cacheables, ISocketMessageChannel channel)
        {
            foreach (var cacheable in cacheables)
            {
                Log(new LogEvent(EventType.MessageDeleted, channel, cacheable.Id));
            }

            return Task.CompletedTask;
        }

        private Task MessageDeletedAsync(ulong messageId, ISocketMessageChannel channel)
        {
            Log(new LogEvent(EventType.MessageDeleted, channel, messageId));
            return Task.CompletedTask;
        }

        private async Task MessageReceivedAsync(SocketMessage message, ulong? previousId = null)
        {
            if (!(message is SocketUserMessage userMessage))
                return;

            if (!(userMessage.Channel is SocketGuildChannel channel))
                return;

            if (message.Author.Id == KnownUsers.MihuBot && channel.Guild.Id == Guilds.PrivateLogs && channel.Id == Channels.LogText)
                return;

            if (!string.IsNullOrWhiteSpace(message.Content) && !message.Content.Contains('\0'))
            {
                Log(new LogEvent(previousId is null ? EventType.MessageReceived : EventType.MessageUpdated, userMessage)
                {
                    PreviousMessageID = previousId ?? 0
                });
            }

            if (message.Author.Id != KnownUsers.MihuBot && message.Attachments.Any())
            {
                foreach (var attachment in message.Attachments)
                {
                    Log(new LogEvent(userMessage, attachment));
                }

                await Task.WhenAll(message.Attachments.Select(a => Task.Run(async () =>
                {
                    try
                    {
                        var response = await _services.Http.GetAsync(a.Url, HttpCompletionOption.ResponseHeadersRead);

                        string timeString = DateTimeString((message.EditedTimestamp ?? message.Timestamp).UtcDateTime);
                        string filePath = $"{FilesRoot}{timeString}_{a.Id}_{a.Filename}";

                        using (FileStream fs = File.OpenWrite(filePath))
                        {
                            using Stream stream = await response.Content.ReadAsStreamAsync();
                            await stream.CopyToAsync(fs);
                        }

                        await DebugAsync($"Downloaded {filePath}", logOnly: true);

                        FileArchivingChannel.Writer.TryWrite((filePath, Delete: true));

                        int fileCount = Interlocked.Increment(ref _fileCounter);

                        if (fileCount % 10 == 0)
                        {
                            var drive = DriveInfo.GetDrives()
                                .OrderByDescending(d => d.TotalSize)
                                .First();

                            string message = $"Space available: {drive.AvailableFreeSpace / 1024 / 1024} / {drive.TotalSize / 1024 / 1024} MB";
                            await DebugAsync(message, logOnly: fileCount % 100 != 0);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                })).ToArray());
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

        private static VoiceStatusUpdateFlags GetVoiceUpdateFlags(SocketVoiceState before, SocketVoiceState after)
        {
            return (VoiceStatusUpdateFlags)((int)GetVoiceStatusUpdateFlags(before) >> 16) | GetVoiceStatusUpdateFlags(after);
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
                : this(type, message.Guild().Id, message.Channel.Id, message.Id)
            {
                if (type == EventType.MessageReceived || type == EventType.MessageUpdated)
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
                Attachment = attachment;
            }

            public LogEvent(EventType type, IChannel channel)
                : this(type, (channel as SocketGuildChannel)?.Guild.Id ?? 0)
            {
                ChannelID = channel.Id;
            }

            public LogEvent(EventType type, IChannel channel, ulong messageId)
                : this(type, channel)
            {
                MessageID = messageId;
            }

            public LogEvent(string debugMessage)
                : this(EventType.DebugMessage)
            {
                Content = debugMessage;
            }

            public ulong GuildID { get; set; }
            public ulong ChannelID { get; set; }
            public ulong MessageID { get; set; }
            public ulong PreviousMessageID { get; set; }
            public ulong UserID { get; set; }
            public string Content { get; set; }
            public string Embeds { get; set; }
            public Attachment Attachment { get; set; }
            public LogEmote Emote { get; set; }
            public string Emoji { get; set; }
            public VoiceStatusUpdateFlags VoiceStatusUpdated { get; set; }

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

                if (Type == EventType.MessageReceived ||
                    Type == EventType.MessageUpdated ||
                    Type == EventType.MessageDeleted ||
                    Type == EventType.FileReceived)
                {
                    builder.Append(": ");
                    AppendChannelName(builder, client, GuildID, ChannelID);

                    if (Content != null)
                    {
                        if (UserID != default)
                        {
                            builder.Append(" - ");
                            AppendUsername(builder, client, UserID);
                        }

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
                    else
                    {
                        if (UserID != default)
                        {
                            builder.Append(" - Author ");
                            builder.Append(UserID);
                        }

                        if (Type == EventType.MessageDeleted && PreviousMessageID != default)
                        {
                            builder.Append(" - PreviousID ");
                            builder.Append(PreviousMessageID);
                        }

                        builder.Append(" - Message ");
                        builder.Append(MessageID);

                        if (Type == EventType.FileReceived && Attachment != null)
                        {
                            builder.Append(" - File ");
                            builder.Append(Attachment.Url);
                            builder.Append(" - ");
                            builder.Append(Attachment.Filename);
                        }
                    }
                }
                else if (Type == EventType.DebugMessage && Content != null)
                {
                    builder.Append(": ");
                    builder.Append(Content.NormalizeNewLines().Replace("\n", " <new-line> "));
                }
                else if (Type == EventType.ReactionAdded || Type == EventType.ReactionRemoved || Type == EventType.ReactionsCleared)
                {
                    builder.Append(": ");
                    AppendChannelName(builder, client, GuildID, ChannelID);

                    if (UserID != default)
                    {
                        builder.Append(" - Author ");
                        builder.Append(UserID);
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
                }
                else if (Type == EventType.UserJoined || Type == EventType.UserLeft || Type == EventType.UserBanned)
                {
                    builder.Append(": ");
                    AppendGuildName(builder, client, GuildID);
                    builder.Append(" - ");
                    AppendUsername(builder, client, UserID);
                }
                else if (Type == EventType.UserIsTyping)
                {
                    builder.Append(": ");
                    AppendChannelName(builder, client, GuildID, ChannelID);
                    builder.Append(" - ");
                    AppendUsername(builder, client, UserID);
                }
                else if (Type == EventType.VoiceStatusUpdated || Type == EventType.UserJoinedVoice || Type == EventType.UserLeftVoice)
                {
                    builder.Append(": ");
                    AppendChannelName(builder, client, GuildID, ChannelID);
                    builder.Append(" - ");
                    AppendUsername(builder, client, UserID);
                    builder.Append(" - ");
                    builder.Append(VoiceStatusUpdated);
                }

                static void AppendTwoDigits(StringBuilder builder, int value)
                {
                    if (value < 10) builder.Append('0');
                    builder.Append(value);
                }
                static void AppendChannelName(StringBuilder builder, DiscordSocketClient client, ulong guildId, ulong channelId)
                {
                    string channelName = client.GetGuild(guildId)?.GetChannel(channelId)?.Name;
                    if (channelName is null)
                    {
                        builder.Append(guildId);
                        builder.Append(" - ");
                        builder.Append(channelId);
                    }
                    else
                    {
                        builder.Append(channelName);
                    }
                }
                static void AppendGuildName(StringBuilder builder, DiscordSocketClient client, ulong guildId)
                {
                    string guildName = client.GetGuild(guildId)?.Name;
                    if (guildName is null)
                    {
                        builder.Append(guildId);
                    }
                    else
                    {
                        builder.Append(guildName);
                    }
                }
                static void AppendUsername(StringBuilder builder, DiscordSocketClient client, ulong userId)
                {
                    string username = client.GetUser(userId)?.Username;
                    if (username is null)
                    {
                        builder.Append(userId);
                    }
                    else
                    {
                        builder.Append(username);
                    }
                }
            }
        }
    }
}
