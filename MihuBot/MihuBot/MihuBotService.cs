using MihuBot.Configuration;
using MihuBot.Permissions;
using SharpCollections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MihuBot;

public class MihuBotService : IHostedService
{
    private readonly IConfigurationService _configuration;
    private readonly HttpClient _http;
    private readonly InitializedDiscordClient _discord;
    private readonly Logger _logger;
    private readonly IPermissionsService _permissions;

    private readonly CompactPrefixTree<CommandBase> _commands = new(ignoreCase: true);
    private readonly List<INonCommandHandler> _nonCommandHandlers = new();

    public MihuBotService(IServiceProvider services, IConfigurationService configuration, HttpClient http, InitializedDiscordClient discord, Logger logger, IPermissionsService permissions)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _http = http ?? throw new ArgumentNullException(nameof(http));
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
                if (message.Channel is IDMChannel &&
                    (!string.IsNullOrWhiteSpace(message.Content) || message.Attachments.Any()) &&
                    _configuration.TryGet(null, "SecretSantaRole", out string roleIdString) &&
                    ulong.TryParse(roleIdString, out ulong roleId) &&
                    _configuration.TryGet(null, "SecretSantaChannel", out string channelIdString) &&
                    ulong.TryParse(channelIdString, out ulong channelId) &&
                    _discord.GetGuild(Guilds.TheBoys).GetUser(message.Author.Id).Roles.Any(r => r.Id == roleId) &&
                    _discord.GetTextChannel(channelId) is { } channel &&
                    _configuration.TryGet(null, "SecretSantaPrefix", out string messagePrefix))
                {
                    if (!string.IsNullOrEmpty(messagePrefix)) messagePrefix += " ";
                    string content = $"{messagePrefix}{message.Content}";

                    if (message.Attachments.FirstOrDefault() is { } attachment)
                    {
                        using var s = await _http.GetStreamAsync(attachment.Url);
                        await channel.SendFileAsync(s, attachment.Filename, content);
                    }
                    else
                    {
                        await channel.SendMessageAsync(content);
                    }
                    return;
                }

                await Client_MessageReceived(message);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        };

        _discord.ButtonExecuted += HandleMessageComponentAsync;

        _discord.ReactionAdded += Client_ReactionAdded;

        try
        {
            await _discord.GetTextChannel(Channels.Debug).TrySendMessageAsync("Started");
        }
        catch { }
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
