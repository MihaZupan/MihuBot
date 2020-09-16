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
        private static Logger Logger;

        private static ServiceCollection ServiceCollection;

        private static CompactPrefixTree<CommandBase> _commands = new CompactPrefixTree<CommandBase>(ignoreCase: true);
        private static List<INonCommandHandler> _nonCommandHandlers = new List<INonCommandHandler>();

        internal static readonly TaskCompletionSource<object> BotStopTCS =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static Task DebugAsync(string message) => Logger.DebugAsync(message);

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

            Client = new DiscordSocketClient(
                new DiscordSocketConfig()
                {
                    MessageCacheSize = 1024 * 16,
                    ConnectionTimeout = 30_000
                });

            HttpClient = new HttpClient();

            RedisClient = await ConnectionMultiplexer.ConnectAsync($"{Secrets.RedisDatabaseAddress},password={Secrets.RedisDatabasePassword}");

            ServiceCollection = new ServiceCollection(Client, HttpClient, RedisClient);

            Logger = new Logger(ServiceCollection);

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
                    _nonCommandHandlers.Add(instance);
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

            await Logger.OnShutdownAsync();
        }

        private static async Task Client_ReactionAdded(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            try
            {
                if (!(reaction.Channel is SocketGuildChannel guildChannel) || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
                    return;

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

            if (message.Author.Id == KnownUsers.Miha && message.Attachments.Any())
            {
                Attachment mcFunction = message.Attachments.FirstOrDefault(a => a.Filename.EndsWith(".mcfunction", StringComparison.OrdinalIgnoreCase));

                if (mcFunction != null)
                {
                    string functionsFile = await HttpClient.GetStringAsync(mcFunction.Url);
                    string[] functions = functionsFile
                        .Replace('\r', '\n')
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Where(f => f.Trim().Length > 0)
                        .Select(f => "execute positioned as MihuBot run " + f)
                        .ToArray();

                    await message.ReplyAsync($"Running {functions.Length} commands");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            StringBuilder sb = new StringBuilder();

                            await McCommand.RunMinecraftCommandAsync("gamerule sendCommandFeedback false");

                            for (int i = 0; i < functions.Length; i += 100)
                            {
                                Task<string>[] tasks = functions
                                    .AsMemory(i, Math.Min(100, functions.Length - i))
                                    .ToArray()
                                    .Select(f => McCommand.RunMinecraftCommandAsync(f))
                                    .ToArray();

                                await Task.WhenAll(tasks);

                                foreach (var task in tasks)
                                    sb.AppendLine(task.Result);
                            }

                            await McCommand.RunMinecraftCommandAsync("gamerule sendCommandFeedback true");

                            var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
                            await message.Channel.SendFileAsync(ms, "responses.txt");
                        }
                        catch { }
                    });
                }
            }

            if (message.Author.Id == KnownUsers.MihuBot)
                return;

            if (guildChannel.Guild.Id == Guilds.DDs)
            {
                if (guildChannel.Id == Channels.DDsGeneral)
                    return; // Ignore
            }

            await HandleMessageAsync(message);
        }

        private static async Task HandleMessageAsync(SocketMessage message)
        {
            if (message.Content.Contains('\0'))
                throw new InvalidOperationException("Null in text");

            var content = message.Content.TrimStart();

            if (content.StartsWith('!') || content.StartsWith('/') || content.StartsWith('-'))
            {
                int spaceIndex = content.AsSpan().IndexOfAny(' ', '\r', '\n');

                if (_commands.TryMatchExact(spaceIndex == -1 ? content.AsSpan(1) : content.AsSpan(1, spaceIndex - 1), out var match))
                {
                    var command = match.Value;
                    var context = new CommandContext(ServiceCollection, message);

                    if (command.TryEnter(context, out TimeSpan cooldown, out bool shouldWarn))
                    {
                        await command.ExecuteAsync(context);
                    }
                    else if (shouldWarn)
                    {
                        await context.WarnCooldownAsync(cooldown);
                    }
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
