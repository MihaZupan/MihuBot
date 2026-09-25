using System.Globalization;

namespace MihuBot.Discord;

internal static class MihuBotDiscordActivitySource
{
    internal static readonly ActivitySource Instance = new("MihuBot.Discord", "1.0.0");

    internal static async Task RunAsync(
        string operation,
        string command,
        ulong? guildId,
        ulong channelId,
        ulong messageId,
        ulong userId,
        Func<Task> action,
        string invokedAs = null,
        ulong? interactionId = null,
        ComponentType? componentType = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = Instance.StartActivity(operation);
        activity?.SetTag("command.name", command);
        activity?.SetTag("command.invoked_as", invokedAs);
        activity?.SetTag("discord.guild.id", guildId?.ToString(CultureInfo.InvariantCulture));
        activity?.SetTag("discord.channel.id", channelId.ToString(CultureInfo.InvariantCulture));
        activity?.SetTag("discord.message.id", messageId.ToString(CultureInfo.InvariantCulture));
        activity?.SetTag("discord.user.id", userId.ToString(CultureInfo.InvariantCulture));
        activity?.SetTag("discord.interaction.id", interactionId?.ToString(CultureInfo.InvariantCulture));
        activity?.SetTag("discord.component.type", componentType?.ToString());

        try
        {
            await action();

            if (cancellationToken.IsCancellationRequested)
            {
                activity?.SetTag("command.outcome", "cancelled");
            }
            else if (activity?.Status != ActivityStatusCode.Error)
            {
                if (activity?.GetTagItem("command.outcome") is null)
                {
                    activity?.SetTag("command.outcome", "completed");
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("command.outcome", "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetTag("command.outcome", "failed");
            activity?.SetTag("error.type", ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
        }
    }
}
