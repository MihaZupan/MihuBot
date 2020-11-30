using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MihuBot.Helpers;
using MihuBot.Permissions;
using SharpCollections.Generic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot
{
    public class MihuBotService : IHostedService
    {
        private readonly DiscordSocketClient _discord;
        private readonly Logger _logger;
        private readonly IPermissionsService _permissions;

        private readonly CompactPrefixTree<CommandBase> _commands = new CompactPrefixTree<CommandBase>(ignoreCase: true);
        private readonly List<INonCommandHandler> _nonCommandHandlers = new List<INonCommandHandler>();

        public MihuBotService(IServiceProvider services, DiscordSocketClient discord, Logger logger, IPermissionsService permissions)
        {
            _discord = discord;
            _logger = logger;
            _permissions = permissions;

            foreach (var type in Assembly
                .GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsPublic && !t.IsAbstract))
            {
                if (typeof(CommandBase).IsAssignableFrom(type))
                {
                    var instance = ActivatorUtilities.CreateInstance(services, type) as CommandBase;
                    _commands.Add(instance.Command.ToLowerInvariant(), instance);
                    foreach (string alias in instance.Aliases)
                    {
                        _commands.Add(alias.ToLowerInvariant(), instance);
                    }
                    _nonCommandHandlers.Add(instance);
                }
                else if (typeof(NonCommandHandler).IsAssignableFrom(type))
                {
                    var instance = ActivatorUtilities.CreateInstance(services, type) as NonCommandHandler;
                    _nonCommandHandlers.Add(instance);
                }
            }
        }

        private async Task Client_ReactionAdded(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            try
            {
                if (reaction.Channel is not SocketGuildChannel guildChannel || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
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
                    if (reactionEmote.Id == Emotes.James.Id) // James emote
                    {
                        var userMessage = reaction.Message;

                        if (userMessage.IsSpecified)
                        {
                            await userMessage.Value.AddReactionsAsync(Emotes.JamesEmotes);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private Task Client_MessageReceived(SocketMessage message)
        {
            if (message is not SocketUserMessage userMessage)
                return Task.CompletedTask;

            if (userMessage.Channel is not SocketGuildChannel guildChannel || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
                return Task.CompletedTask;

            if (message.Author.IsBot)
                return Task.CompletedTask;

            if (guildChannel.Guild.Id == Guilds.DDs)
            {
                if (guildChannel.Id == Channels.DDsGeneral)
                    return Task.CompletedTask; // Ignore
            }
            else if (guildChannel.Guild.Id == Guilds.RetirementHome)
            {
                if (guildChannel.Id != Channels.RetirementHomeWhitelist)
                    return Task.CompletedTask; // Ignore
            }

            if (message.Content.Contains('\0'))
                throw new InvalidOperationException("Null in text");

            return HandleMessageAsync(userMessage);
        }

        private async Task HandleMessageAsync(SocketUserMessage message)
        {
            var content = message.Content.TrimStart();

            if (content.StartsWith('!') || content.StartsWith('/') || content.StartsWith('-'))
            {
                int spaceIndex = content.AsSpan().IndexOfAny(' ', '\r', '\n');

                if (_commands.TryMatchExact(spaceIndex == -1 ? content.AsSpan(1) : content.AsSpan(1, spaceIndex - 1), out var match))
                {
                    if (message.Guild().Id == Guilds.RetirementHome && (match.Key != "whitelist" && match.Key != "mc"))
                        return; // Ignore everything except whitelist/mc for Retirement Home

                    var command = match.Value;
                    var context = new CommandContext(_discord, message, match.Key, _logger, _permissions);

                    if (command.TryEnter(context, out TimeSpan cooldown, out bool shouldWarn))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await command.ExecuteAsync(context);
                            }
                            catch (Exception ex)
                            {
                                await context.DebugAsync(ex);
                            }
                        });
                    }
                    else if (shouldWarn)
                    {
                        await context.WarnCooldownAsync(cooldown);
                    }
                }
            }
            else
            {
                if (message.Guild().Id == Guilds.RetirementHome)
                    return;

                var messageContext = new MessageContext(_discord, message, _logger);

                foreach (var handler in _nonCommandHandlers)
                {
                    try
                    {
                        Task task = handler.HandleAsync(messageContext);

                        if (task.IsCompleted)
                        {
                            if (!task.IsCompletedSuccessfully)
                                await task;
                        }
                        else
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await task;
                                }
                                catch (Exception ex)
                                {
                                    await messageContext.DebugAsync(ex);
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _ = Task.Run(async () => await messageContext.DebugAsync(ex));
                    }
                }
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var onConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _discord.Connected += () => { onConnectedTcs.TrySetResult(); return Task.CompletedTask; };

            var onReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _discord.Ready += () => { onReadyTcs.TrySetResult(); return Task.CompletedTask; };

            _discord.ReactionAdded += Client_ReactionAdded;

            await _discord.LoginAsync(TokenType.Bot, Secrets.Discord.AuthToken);
            await _discord.StartAsync();

            await onConnectedTcs.Task;
            await onReadyTcs.Task;

            foreach (var handler in _nonCommandHandlers)
            {
                if (handler is CommandBase commandBase)
                {
                    await commandBase.InitAsync();
                }
                else if (handler is NonCommandHandler nonCommand)
                {
                    await nonCommand.InitAsync();
                }
                else throw new InvalidOperationException(handler.GetType().FullName);
            }

            _discord.MessageReceived += async message =>
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

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _discord.DownloadUsersAsync(_discord.Guilds);
                    }
                    catch (Exception ex)
                    {
                        _logger.DebugLog(ex.ToString());
                    }
                });

                await _logger.DebugAsync("Beep boop. I'm back!");

                await _discord.SetGameAsync("Quality content", streamUrl: "https://www.youtube.com/watch?v=d1YBv2mWll0", type: ActivityType.Streaming);
            }
        }

        private TaskCompletionSource _stopTcs;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            var tcs = Interlocked.CompareExchange(
                ref _stopTcs,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                null);

            if (tcs is null)
            {
                try
                {
                    try
                    {
                        await _discord.StopAsync();
                    }
                    catch { }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        await _logger.OnShutdownAsync();
                    }
                }
                finally
                {
                    _stopTcs.SetResult();
                }
            }
            else
            {
                await tcs.Task;
            }
        }
    }
}
