using MihuBot.Configuration;
using MihuBot.Discord.Permissions;
using SharpCollections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MihuBot.Discord;

public class MihuBotService : IHostedService
{
    private readonly InitializedDiscordClient _discord;
    private readonly Logger _logger;
    private readonly IPermissionsService _permissions;

    private readonly CompactPrefixTree<CommandBase> _commands = new(ignoreCase: true);
    private readonly List<INonCommandHandler> _nonCommandHandlers = new();

    private readonly RunningDiscordCommands _runningCommands;

    public MihuBotService(IServiceProvider services, InitializedDiscordClient discord, Logger logger, IPermissionsService permissions, IHostApplicationLifetime lifetime)
    {
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _runningCommands = new RunningDiscordCommands(lifetime.ApplicationStopping,
            ex => _logger.DebugLog($"Failure while cancelling running command: {ex}"));

        foreach (var type in Assembly
            .GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract))
        {
            bool isCommand = typeof(CommandBase).IsAssignableFrom(type);

            if (!isCommand && !typeof(NonCommandHandler).IsAssignableFrom(type))
            {
                continue;
            }

            if (OptionalDependencies.GetMissingDependency(services, type) is { } missingDependency)
            {
                Console.WriteLine($"Skipping {type.Name} as {missingDependency.Name} is not available.");
                continue;
            }

            if (isCommand)
            {
                var instance = ActivatorUtilities.CreateInstance(services, type) as CommandBase;
                _commands.Add(instance.Command.ToLowerInvariant(), instance);
                foreach (string alias in instance.Aliases)
                {
                    _commands.Add(alias.ToLowerInvariant(), instance);
                }
                _nonCommandHandlers.Add(instance);
            }
            else
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
            if (reaction.Emote?.Name == Emotes.RedCross.Name && (Constants.Admins.Contains(reaction.UserId) || reaction.UserId == reaction.Message.GetValueOrDefault()?.Author?.Id))
            {
                await _runningCommands.CancelAsync(reaction.MessageId);
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

                var run = _runningCommands.TryStart(message.Id);

                if (run is null)
                {
                    return;
                }

                var context = new CommandContext(_discord, message, match.Key, _logger, _permissions, run.Token);
                bool queued = false;

                try
                {
                    if (command.TryEnter(context, out TimeSpan cooldown, out bool shouldWarn))
                    {
                        _ = Task.Run(async () =>
                        {
                            using (run)
                            {
                                await ObserveCommandAsync(context, async () =>
                                {
                                    run.Token.ThrowIfCancellationRequested();
                                    await MihuBotDiscordActivitySource.RunAsync("ExecuteDiscordCommand", command.Command,
                                        context.Guild.Id, context.Message.Channel.Id, context.Message.Id, context.AuthorId,
                                        () => command.ExecuteAsync(context),
                                        invokedAs: context.Command, cancellationToken: run.Token);
                                });
                            }
                        });
                        queued = true;
                    }
                    else if (shouldWarn)
                    {
                        await context.WarnCooldownAsync(cooldown);
                    }
                }
                finally
                {
                    if (!queued)
                    {
                        run.Dispose();
                    }
                }
            }
        }
        else
        {
            _ = _runningCommands.RunAsync(message.Id, token => HandleNonCommandMessageAsync(message, token));
        }
    }

    private async Task HandleNonCommandMessageAsync(SocketUserMessage message, CancellationToken cancellationToken)
    {
        var messageContext = new MessageContext(_discord, message, _logger, cancellationToken);
        List<Task> pending = null;

        foreach (var handler in _nonCommandHandlers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                Task task = handler.HandleAsync(messageContext);

                if (task.IsCompleted)
                {
                    await task;
                }
                else
                {
                    (pending ??= []).Add(ObserveCommandAsync(messageContext, () => task));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                (pending ??= []).Add(messageContext.DebugAsync(ex));
            }
        }

        if (pending is not null)
        {
            await Task.WhenAll(pending);
        }
    }

    private static async Task ObserveCommandAsync(MessageContext context, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                context.DebugLog($"Error during cancellation: {ex}");
            }
            else
            {
                await context.DebugAsync(ex);
            }
        }
    }

    private async Task HandleMessageComponentAsync(SocketMessageComponent component)
    {
        using var run = _runningCommands.TryStart(component.Message.Id);

        if (run is null)
        {
            return;
        }

        try
        {
            run.Token.ThrowIfCancellationRequested();
            string id = component.Data.CustomId;
            int dashIndex = id.IndexOf('-');

            if (dashIndex > 0 && _commands.TryMatchExact(id.AsSpan(0, dashIndex), out var match))
            {
                _logger.DebugLog($"Processing message component update '{id}'",
                    component.Channel.Guild()?.Id ?? 0,
                    component.Channel.Id,
                    component.Message.Id,
                    component.User.Id);

                await MihuBotDiscordActivitySource.RunAsync("HandleDiscordMessageComponent", match.Value.Command,
                    component.Channel.Guild()?.Id, component.Channel.Id, component.Message.Id, component.User.Id,
                    () => match.Value.HandleMessageComponentAsync(component, run.Token),
                    interactionId: component.Id, componentType: component.Data.Type, cancellationToken: run.Token);
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
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
        _discord.SelectMenuExecuted += HandleMessageComponentAsync;

        _discord.ReactionAdded += Client_ReactionAdded;

        if (OperatingSystem.IsLinux())
        {
            try
            {
                string commit = BuildInfo.GetCommitId();

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
                    await _runningCommands.StopAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.DebugLog("Shutdown deadline reached while waiting for Discord command cleanup.");
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
            await tcs.Task.WaitAsync(cancellationToken);
        }
    }
}
