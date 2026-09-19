using MihuBot.Games.Blackjack;

namespace MihuBot.Discord.Commands;

public sealed class BlackjackCommand(BrowserBlackjackService tables) : CommandBase
{
    public override string Command => "blackjack";
    public override string[] Aliases => ["bj"];

    public override Task ExecuteAsync(CommandContext ctx) =>
        ctx.ReplyAsync(GetLobbyMessage(ctx.Channel.Id, ctx.AuthorId), suppressMentions: true);

    public override Task HandleMessageComponentAsync(SocketMessageComponent component) =>
        component.RespondAsync(GetLobbyMessage(component.Channel.Id, component.User.Id),
            ephemeral: true, allowedMentions: AllowedMentions.None);

    internal string GetLobbyMessage(ulong channelId, ulong authorId)
    {
        (string roomId, string error) = tables.GetOrCreateDiscordLobby(channelId, authorId);
        return error ?? ($"Blackjack is played in the browser. Join this channel's lobby:\n" +
            $"{Constants.PublicBaseUrl}/blackjack/{roomId}\nSign in with Discord to take a seat.");
    }
}
