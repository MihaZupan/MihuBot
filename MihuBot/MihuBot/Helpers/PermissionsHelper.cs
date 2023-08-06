using System.Security.Claims;

namespace MihuBot.Helpers;

public static class PermissionsHelper
{
    public static bool IsAdmin(this SocketUser user)
    {
        return Constants.Admins.Contains(user.Id);
    }

    public static bool IsAdmin(this ClaimsPrincipal claims)
    {
        return claims.TryGetDiscordUserId(out ulong userId)
            && Constants.Admins.Contains(userId);
    }

    public static bool HasWriteAccess(this SocketGuildChannel channel, ulong userId)
    {
        SocketGuildUser guildUser = channel.Guild.GetUser(userId);
        if (guildUser is null)
            return false;

        var permissions = guildUser.GetPermissions(channel);

        if (channel is ITextChannel)
        {
            return permissions.SendMessages;
        }
        else if (channel is IVoiceChannel)
        {
            return permissions.Connect && permissions.Speak;
        }
        else
        {
            return false;
        }
    }

    public static bool HasReadAccess(this SocketGuildChannel channel, ulong userId)
    {
        SocketGuildUser guildUser = channel.Guild.GetUser(userId);
        if (guildUser is null)
            return false;

        var permissions = guildUser.GetPermissions(channel);

        if (channel is ITextChannel)
        {
            return permissions.ViewChannel;
        }
        else if (channel is IVoiceChannel)
        {
            return permissions.Connect;
        }
        else
        {
            return false;
        }
    }

    public static ulong GetDiscordUserId(this ClaimsPrincipal claims)
    {
        if (claims.TryGetDiscordUserId(out ulong userId))
            return userId;

        return 0;
    }

    public static bool TryGetDiscordUserId(this ClaimsPrincipal claims, out ulong userId)
    {
        var discordIdentity = claims.Identities.FirstOrDefault(i => i.AuthenticationType == "Discord");
        string id = discordIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return ulong.TryParse(id, out userId);
    }

    public static bool TryGetGitHubLogin(this ClaimsPrincipal claims, out string userLogin)
    {
        var gitHubIdentity = claims.Identities.FirstOrDefault(i => i.AuthenticationType == "GitHub");
        userLogin = gitHubIdentity?.FindFirst(ClaimTypes.Name)?.Value;
        return !string.IsNullOrEmpty(userLogin);
    }
}
