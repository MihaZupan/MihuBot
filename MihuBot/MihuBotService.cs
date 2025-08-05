using MihuBot.Permissions;
using SharpCollections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MihuBot;

public class MihuBotService : IHostedService
{
    private readonly InitializedDiscordClient _discord;
    private readonly Logger _logger;
    private readonly IPermissionsService _permissions;

    private readonly CompactPrefixTree<CommandBase> _commands = new(ignoreCase: true);
    private readonly List<INonCommandHandler> _nonCommandHandlers = new();

    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _runningCommands = [];

    public MihuBotService(IServiceProvider services, InitializedDiscordClient discord, Logger logger, IPermissionsService permissions)
    {
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));

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

    private async Task Client_ReactionAdded(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        try
        {
            if (reaction.Emote is Emote reactionEmote)
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

            if (reaction.Emote?.Name == Emotes.RedCross.Name && (Constants.Admins.Contains(reaction.UserId) || reaction.UserId == reaction.Message.GetValueOrDefault()?.Author?.Id))
            {
                if (_runningCommands.TryRemove(reaction.MessageId, out CancellationTokenSource cts))
                {
                    TryCancelRunningCommand(cts);
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

        if (userMessage.Channel is not SocketGuildChannel guildChannel)
            return Task.CompletedTask;

        if (message.Author.IsBot)
            return Task.CompletedTask;

        if (guildChannel.Guild.Id == Guilds.RetirementHome)
            return Task.CompletedTask; // Ignore

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
                CommandBase command = match.Value;

                var cts = new CancellationTokenSource();
                var context = new CommandContext(_discord, message, match.Key, _logger, _permissions, cts.Token);

                if (command.TryEnter(context, out TimeSpan cooldown, out bool shouldWarn))
                {
                    _ = Task.Run(async () =>
                    {
                        _runningCommands.TryAdd(context.Message.Id, cts);
                        try
                        {
                            await command.ExecuteAsync(context);
                        }
                        catch (Exception ex)
                        {
                            if (cts.Token.IsCancellationRequested)
                            {
                                context.DebugLog($"Error during cancellation: {ex}");
                            }
                            else
                            {
                                await context.DebugAsync(ex);
                            }
                        }
                        finally
                        {
                            _runningCommands.TryRemove(context.Message.Id, out _);
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
            var cts = new CancellationTokenSource();
            var messageContext = new MessageContext(_discord, message, _logger, cts.Token);

            foreach (var handler in _nonCommandHandlers)
            {
                try
                {
                    Task task = handler.HandleAsync(messageContext);

                    if (task.IsCompleted)
                    {
                        await task;
                    }
                    else
                    {
                        _ = Task.Run(async () =>
                        {
                            _runningCommands.TryAdd(messageContext.Message.Id, cts);
                            try
                            {
                                await task;
                            }
                            catch (Exception ex)
                            {
                                if (cts.Token.IsCancellationRequested)
                                {
                                    messageContext.DebugLog($"Error during cancellation: {ex}");
                                }
                                else
                                {
                                    await messageContext.DebugAsync(ex);
                                }
                            }
                            finally
                            {
                                _runningCommands.TryRemove(messageContext.Message.Id, out _);
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

    private async Task HandleMessageComponentAsync(SocketMessageComponent component)
    {
        try
        {
            string id = component.Data.CustomId;
            int dashIndex = id.IndexOf('-');

            if (dashIndex > 0 && _commands.TryMatchExact(id.AsSpan(0, dashIndex), out var match))
            {
                _logger.DebugLog($"Processing message component update '{id}'",
                    component.Channel.Guild()?.Id ?? 0,
                    component.Channel.Id,
                    component.Message.Id,
                    component.User.Id);

                await match.Value.HandleMessageComponentAsync(component);
            }
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync(ex.ToString());
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _discord.EnsureInitializedAsync();

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

        _discord.ButtonExecuted += HandleMessageComponentAsync;

        _discord.ReactionAdded += Client_ReactionAdded;

        if (OperatingSystem.IsLinux())
        {
            try
            {
                string commit = Helpers.Helpers.GetCommitId();

                var embed = new EmbedBuilder()
                    .WithTitle("Started")
                    .AddField("RID", $"`{RuntimeInformation.RuntimeIdentifier}`")
                    .AddField("Version", $"`{RuntimeInformation.FrameworkDescription}`")
                    .AddField("Build", commit is null ? "local" : $"[`{commit.AsSpan(0, 6)}`](https://github.com/MihaZupan/MihuBot/commit/{commit})")
                    .AddField("WorkDir", $"`{Environment.CurrentDirectory}`")
                    .AddField("Machine", $"`{Environment.MachineName}`")
                    .Build();

                await _discord.GetTextChannel(Channels.Debug).TrySendMessageAsync(embed: embed);
            }
            catch { }
        }
    }

    private TaskCompletionSource _stopTcs;

    private void TryCancelRunningCommand(CancellationTokenSource cts)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync("Failure while cancelling running command", ex);
            }
        });
    }

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
                foreach ((_, CancellationTokenSource cts) in _runningCommands)
                {
                    TryCancelRunningCommand(cts);
                }

                if (!_runningCommands.IsEmpty)
                {
                    Stopwatch s = Stopwatch.StartNew();

                    while (s.Elapsed.TotalSeconds < 3 && !_runningCommands.IsEmpty)
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                }

                try
                {
                    await _discord.StopAsync();
                }
                catch { }

                if (OperatingSystem.IsLinux())
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
