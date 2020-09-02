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
        private string CurrentFilesDirectory;

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

        private async Task ChannelReaderTaskAsync()
        {
            while (await LogChannel.Reader.WaitToReadAsync())
            {
                await LogSemaphore.WaitAsync();
                while (LogChannel.Reader.TryRead(out LogEvent logEvent))
                {
                    await JsonSerializer.SerializeAsync(JsonLogStream, logEvent, JsonOptions);
                    await JsonLogStream.WriteAsync(NewLineByte);
                    logEvent.ToString(LogBuilder, _services.Discord);
                    LogBuilder.Append('\n');
                }
                await JsonLogStream.FlushAsync();
                LogSemaphore.Release();

                if (DateTime.UtcNow.Date != LogDate)
                {
                    await SendLogFilesAsync(LogsReportsTextChannel, resetLogFiles: true);
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
                        await File.WriteAllTextAsync(Path.ChangeExtension(JsonLogPath, ".txt"), readableLog);

                        await JsonLogStream.DisposeAsync();
                    }
                }

                if (resetLogFiles)
                {
                    DateTime utcNow = DateTime.UtcNow;
                    LogDate = utcNow.Date;

                    string timeString = utcNow.ToString("yyyy-MM-dd_HH-mm-ss");

                    JsonLogPath = Path.Combine(LogsRoot, timeString + ".json");
                    JsonLogStream = File.Open(JsonLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);

                    var newFilesDirectory = FilesRoot + timeString + "/";
                    Directory.CreateDirectory(newFilesDirectory);
                    CurrentFilesDirectory = newFilesDirectory;
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

        public SocketTextChannel DebugTextChannel => _services.Discord.GetGuild(Guilds.Mihu).GetTextChannel(719903263297896538ul);
        public SocketTextChannel LogsTextChannel => _services.Discord.GetGuild(Guilds.PrivateLogs).GetTextChannel(Constants.LogTextChannelID);
        public SocketTextChannel LogsReportsTextChannel => _services.Discord.GetGuild(Guilds.PrivateLogs).GetTextChannel(Constants.LogReportsChannelID);

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

            _ = new Timer(_ => Log(new LogEvent("Keepalive")), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

            services.Discord.JoinedGuild += async guild => await DebugAsync($"Added to {guild.Name} ({guild.Id})");
            services.Discord.MessageReceived += message => MessageReceivedAsync(message);
            services.Discord.MessageUpdated += (cacheable, message, _) => MessageReceivedAsync(message, previousId: cacheable.Id);
            services.Discord.MessageDeleted += (cacheable, channel) => MessageDeletedAsync(cacheable.Id, channel);
            services.Discord.MessagesBulkDeleted += MessagesBulkDeletedAsync;
            services.Discord.ReactionAdded += (_, __, reaction) => ReactionAddedAsync(reaction);
            services.Discord.ReactionRemoved += (_, __, reaction) => ReactionRemovedAsync(reaction);
            services.Discord.ReactionsCleared += (cacheable, channel) => ReactionsClearedAsync(cacheable.Id, channel);
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
                AuthorID = reaction.UserId,
                Emote = reaction.Emote as Emote,
                Emoji = reaction.Emote as Emoji
            });
            return Task.CompletedTask;
        }

        private Task ReactionAddedAsync(SocketReaction reaction)
        {
            Log(new LogEvent(EventType.ReactionAdded, reaction.Channel.Guild().Id, reaction.Channel.Id, reaction.MessageId)
            {
                AuthorID = reaction.UserId,
                Emote = reaction.Emote as Emote,
                Emoji = reaction.Emote as Emoji
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

            if (message.Author.Id == KnownUsers.MihuBot && channel.Guild.Id == Guilds.PrivateLogs && channel.Id == Constants.LogTextChannelID)
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

                        using FileStream fs = File.OpenWrite(CurrentFilesDirectory + a.Id + "_" + a.Filename);
                        using Stream stream = await response.Content.ReadAsStreamAsync();
                        await stream.CopyToAsync(fs);

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


        private enum EventType
        {
            MessageReceived = 1,
            MessageUpdated,
            MessageDeleted,
            FileReceived,
            DebugMessage,
            ReactionAdded,
            ReactionRemoved,
            ReactionsCleared,
        }

        private sealed class LogEvent
        {
            public EventType Type { get; private set; }
            public DateTime TimeStamp { get; private set; }

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

                AuthorID = message.Author.Id;
            }

            public LogEvent(SocketUserMessage message, Attachment attachment)
                : this(EventType.FileReceived, message)
            {
                Attachment = attachment;
            }

            public LogEvent(EventType type, ISocketMessageChannel channel, ulong messageId)
                : this(type, (channel as SocketGuildChannel)?.Guild.Id ?? 0, channel.Id, messageId)
            { }

            public LogEvent(string debugMessage)
                : this(EventType.DebugMessage)
            {
                Content = debugMessage;
            }

            public ulong GuildID { get; set; }
            public ulong ChannelID { get; set; }
            public ulong MessageID { get; set; }
            public ulong PreviousMessageID { get; set; }
            public ulong AuthorID { get; set; }
            public string Content { get; set; }
            public string Embeds { get; set; }
            public Attachment Attachment { get; set; }
            public Emote Emote { get; set; }
            public Emoji Emoji { get; set; }

            public void ToString(StringBuilder builder, DiscordSocketClient client)
            {
                builder.Append(TimeStamp.Year);
                builder.Append('-');

                if (TimeStamp.Month < 10) builder.Append('0');
                builder.Append(TimeStamp.Month);
                builder.Append('-');

                if (TimeStamp.Day < 10) builder.Append('0');
                builder.Append(TimeStamp.Day);
                builder.Append('_');

                if (TimeStamp.Hour < 10) builder.Append('0');
                builder.Append(TimeStamp.Hour);
                builder.Append('-');

                if (TimeStamp.Minute < 10) builder.Append('0');
                builder.Append(TimeStamp.Minute);
                builder.Append('-');

                if (TimeStamp.Second < 10) builder.Append('0');
                builder.Append(TimeStamp.Second);
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
                        if (AuthorID != default)
                        {
                            builder.Append(" - ");
                            builder.Append(client.GetUser(AuthorID).Username);
                        }

                        builder.Append(" - ");
                        if (Content.AsSpan().IndexOfAny('\n', '\r') == -1)
                        {
                            builder.Append(Content);
                        }
                        else
                        {
                            builder.Append(Content.Replace("\r\n", "\n").Replace("\n", " <new-line> "));
                        }
                    }
                    else
                    {
                        if (AuthorID != default)
                        {
                            builder.Append(" - Author ");
                            builder.Append(AuthorID);
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
                    builder.Append(Content.Replace("\r\n", "\n").Replace("\n", " <new-line> "));
                }
                else if (Type == EventType.ReactionAdded || Type == EventType.ReactionRemoved || Type == EventType.ReactionsCleared)
                {
                    builder.Append(": ");
                    AppendChannelName(builder, client, GuildID, ChannelID);

                    if (AuthorID != default)
                    {
                        builder.Append(" - Author ");
                        builder.Append(AuthorID);
                    }

                    if (Emoji != null)
                    {
                        builder.Append(" - Emoji ");
                        builder.Append(Emoji.Name);
                    }

                    if (Emote != null)
                    {
                        builder.Append(" - Emote ");
                        builder.Append(Emote.Name);
                        builder.Append(' ');
                        builder.Append(Emote.Url);
                    }
                }

                static void AppendChannelName(StringBuilder builder, DiscordSocketClient client, ulong guildId, ulong channelId)
                {
                    string channelName = client.GetGuild(guildId)?.GetTextChannel(channelId)?.Name;
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
            }
        }
    }
}
