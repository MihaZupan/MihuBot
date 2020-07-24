using Discord;
using Discord.WebSocket;
using MihuBot.Commands;
using MihuBot.Helpers;
using SharpCollections.Generic;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot
{
    class Program
    {
        private static DiscordSocketClient Client;
        private static HttpClient HttpClient;
        private static ConnectionMultiplexer RedisClient;

        private static ServiceCollection ServiceCollection;

        private static readonly string LogsRoot = "logs/";
        private static readonly string FilesRoot = LogsRoot + "files/";

        private static int _fileCounter = 0;
        private static readonly SemaphoreSlim LogSemaphore = new SemaphoreSlim(1, 1);
        private static StreamWriter LogWriter;

        private static CompactPrefixTree<CommandBase> _commands = new CompactPrefixTree<CommandBase>(ignoreCase: true);
        private static List<NonCommandHandler> _nonCommandHandlers = new List<NonCommandHandler>();

        private static async Task LogAsync(string content)
        {
            await LogSemaphore.WaitAsync();
            await LogWriter.WriteLineAsync(DateTime.UtcNow.Ticks + "_" + content);
            await LogWriter.FlushAsync();
            LogSemaphore.Release();
        }

        internal static readonly TaskCompletionSource<object> BotStopTCS =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static SocketTextChannel DebugTextChannel => Client.GetGuild(566925785563136020ul).GetTextChannel(719903263297896538ul);
        internal static async Task DebugAsync(string message)
        {
            lock (Console.Out)
                Console.WriteLine("DEBUG: " + message);

            try
            {
                await DebugTextChannel.SendMessageAsync(message);
            }
            catch { }
        }

        static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "start")
            {
                await StartUpdateAsync(null);
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Console.WriteLine(e.ExceptionObject);
            };

            Directory.CreateDirectory(LogsRoot);
            Directory.CreateDirectory(FilesRoot);

            LogWriter = new StreamWriter(LogsRoot + DateTime.UtcNow.Ticks + ".txt");


            Client = new DiscordSocketClient(
                new DiscordSocketConfig()
                {
                    MessageCacheSize = 1024 * 16,
                    ConnectionTimeout = 30_000
                });

            HttpClient = new HttpClient();

            RedisClient = await ConnectionMultiplexer.ConnectAsync($"{Secrets.RedisDatabaseAddress},password={Secrets.RedisDatabasePassword}");

            ServiceCollection = new ServiceCollection(Client, HttpClient, RedisClient);


            foreach (var type in Assembly
                .GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsPublic && !t.IsAbstract))
            {
                if (typeof(CommandBase).IsAssignableFrom(type))
                {
                    var instance = Activator.CreateInstance(type) as CommandBase;
                    await instance.InitAsync(ServiceCollection);
                    _commands.Add(instance.Command, instance);
                    foreach (string alias in instance.Aliases)
                    {
                        _commands.Add(alias, instance);
                    }
                }
                else if (typeof(NonCommandHandler).IsAssignableFrom(type))
                {
                    var instance = Activator.CreateInstance(type) as NonCommandHandler;
                    await instance.InitAsync(ServiceCollection);
                    _nonCommandHandlers.Add(instance);
                }
            }

            TaskCompletionSource<object> onConnectedTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Client.Connected += () => { onConnectedTcs.TrySetResult(null); return Task.CompletedTask; };

            TaskCompletionSource<object> onReadyTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Client.Ready += () => { onReadyTcs.TrySetResult(null); return Task.CompletedTask; };

            Client.MessageReceived += async message =>
            {
                try
                {
                    await Client_MessageReceived(message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            };

            Client.ReactionAdded += Client_ReactionAdded;
            Client.MessageUpdated += Client_MessageUpdated;

            Client.JoinedGuild += Client_JoinedGuild;

            await Client.LoginAsync(TokenType.Bot, Secrets.AuthToken);
            await Client.StartAsync();

            await onConnectedTcs.Task;
            await onReadyTcs.Task;

            await DebugAsync("Beep boop. I'm back!");

            await Client.SetGameAsync("Quality content", streamUrl: "https://www.youtube.com/watch?v=d1YBv2mWll0", type: ActivityType.Streaming);

            await BotStopTCS.Task;

            try
            {
                await Client.StopAsync();
            }
            catch { }
        }

        private static async Task Client_JoinedGuild(SocketGuild guild)
        {
            await DebugAsync($"Added to {guild.Name} ({guild.Id})");
        }

        private static async Task Client_MessageUpdated(Cacheable<IMessage, ulong> _, SocketMessage message, ISocketMessageChannel channel)
        {
            if (!(message is SocketUserMessage userMessage))
                return;

            try
            {
                if (!(userMessage.Channel is SocketGuildChannel guildChannel) || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
                    return;

                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    await LogAsync(message.Channel.Name + "_Updated_" + message.Author.Username + ": " + message.Content);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private static async Task Client_ReactionAdded(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            try
            {
                if (!(reaction.Channel is SocketGuildChannel guildChannel) || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
                    return;

                //if (reaction.UserId == KnownUsers.Miha)
                //{
                //    Console.WriteLine($"Reaction: {reaction.Emote.GetType().Name} - {reaction.Emote.Name} - {reaction.Emote}");
                //}

                if (reaction.Emote.Name.Equals("yesw", StringComparison.OrdinalIgnoreCase))
                {
                    if (reaction.User.IsSpecified && reaction.User.Value.Id == KnownUsers.MihuBot)
                        return;

                    var userMessage = reaction.Message;

                    if (userMessage.IsSpecified)
                    {
                        await userMessage.Value.AddReactionAsync(Emotes.YesW);
                    }
                }
                else if (reaction.Emote is Emote reactionEmote)
                {
                    if (reactionEmote.Id == 685587814330794040ul) // James emote
                    {
                        var userMessage = reaction.Message;

                        if (userMessage.IsSpecified)
                        {
                            foreach (var emote in Emotes.JamesEmotes)
                            {
                                await userMessage.Value.AddReactionAsync(emote);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private static async Task Client_MessageReceived(SocketMessage message)
        {
            if (!(message is SocketUserMessage userMessage))
                return;

            if (!(userMessage.Channel is SocketGuildChannel guildChannel) || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
                return;

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                await LogAsync(message.Channel.Name + "_" + message.Author.Username + ": " + message.Content);
            }

            if (message.Author.Id != KnownUsers.MihuBot && message.Attachments.Any())
            {
                await Task.WhenAll(message.Attachments.Select(a => Task.Run(async () => {
                    try
                    {
                        var response = await HttpClient.GetAsync(a.Url, HttpCompletionOption.ResponseHeadersRead);

                        using FileStream fs = File.OpenWrite(FilesRoot + a.Id + "_" + a.Filename);
                        using Stream stream = await response.Content.ReadAsStreamAsync();
                        await stream.CopyToAsync(fs);

                        if (Interlocked.Increment(ref _fileCounter) % 10 == 0)
                        {
                            var drive = DriveInfo.GetDrives().Where(d => d.TotalSize > 16 * 1024 * 1024 * 1024L /* 16 GB */).Single();
                            if (drive.AvailableFreeSpace < 16 * 1024 * 1024 * 1024L)
                            {
                                await DebugAsync($"Space available: {(int)(drive.AvailableFreeSpace / 1024 / 1024)} MB");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                })).ToArray());

                if (message.Author.Id == KnownUsers.Miha)
                {
                    Attachment mcFunction = message.Attachments.FirstOrDefault(a => a.Filename.EndsWith(".mcfunction", StringComparison.OrdinalIgnoreCase));

                    if (mcFunction != null)
                    {
                        string functionsFile = await HttpClient.GetStringAsync(mcFunction.Url);
                        string[] functions = functionsFile
                            .Replace('\r', '\n')
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Where(f => f.Trim().Length > 0)
                            .ToArray();

                        await message.ReplyAsync($"Running {functions.Length} commands");

                        _ = Task.Run(async () =>
                        {
                            StringBuilder responses = new StringBuilder();
                            foreach (var function in functions)
                            {
                                string response = await McCommand.RunMinecraftCommandAsync("execute positioned as TuboGaming run " + function);
                                responses.AppendLine(response);
                            }

                            var ms = new MemoryStream(Encoding.UTF8.GetBytes(responses.ToString()));
                            await message.Channel.SendFileAsync(ms, "responses.txt");
                        });
                    }
                }
            }

            await HandleMessageAsync(message);
        }

        private static async Task HandleMessageAsync(SocketMessage message)
        {
            if (message.Content.Contains('\0'))
                throw new InvalidOperationException("Null in text");

            if (message.Author.Id == KnownUsers.MihuBot)
                return;

            var content = message.Content.TrimStart();

            if (content.StartsWith('!') || content.StartsWith('/'))
            {
                int spaceIndex = content.IndexOf(' ');

                if (_commands.TryMatchExact(spaceIndex == -1 ? content.AsSpan(1) : content.AsSpan(1, spaceIndex - 1), out var match))
                {
                    var command = match.Value;
                    await command.ExecuteAsync(new CommandContext(ServiceCollection, message));
                }
            }
            else
            {
                var messageContext = new MessageContext(ServiceCollection, message);
                foreach (var handler in _nonCommandHandlers)
                {
                    await handler.HandleAsync(messageContext);
                }
            }
        }


        private static int _updating = 0;

        internal static async Task StartUpdateAsync(SocketMessage message)
        {
            if (message != null)
            {
                Debug.Assert(message.AuthorIsAdmin());
                Debug.Assert(message.Content.Contains("update", StringComparison.OrdinalIgnoreCase));

                if (Interlocked.Exchange(ref _updating, 1) != 0)
                    return;

                await Task.WhenAll(
                    message.Guild().Id == Guilds.Mihu ? Task.CompletedTask : message.ReplyAsync("Updating ..."),
                    DebugAsync("Updating ..."));

                await Client.StopAsync();
            }

            using Process updateProcess = new Process();
            updateProcess.StartInfo.FileName = "/bin/bash";
            updateProcess.StartInfo.Arguments = "update.sh";
            updateProcess.StartInfo.UseShellExecute = false;
            updateProcess.StartInfo.WorkingDirectory = Environment.CurrentDirectory;
            updateProcess.Start();

            BotStopTCS.TrySetResult(null);
        }
    }
}
