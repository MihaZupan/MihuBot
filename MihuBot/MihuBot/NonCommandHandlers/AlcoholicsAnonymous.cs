using MihuBot.Permissions;

namespace MihuBot.NonCommandHandlers;

public sealed class AlcoholicsAnonymous : NonCommandHandler
{
    private static readonly HashSet<ulong> Alcoholics = new HashSet<ulong>()
    {
        KnownUsers.Miha,
        KnownUsers.Jordan,
        KnownUsers.James,
        KnownUsers.Christian,
        KnownUsers.PaulK,
        KnownUsers.Sticky,
        KnownUsers.Ryboh,
        KnownUsers.Joster,
    };

    private readonly IPermissionsService _permissions;

    public AlcoholicsAnonymous(IPermissionsService permissions)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    }

    public override Task HandleAsync(MessageContext ctx)
    {
        if (!ctx.Content.StartsWith("@alcoholics", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        if (ctx.IsFromAdmin ||
            Alcoholics.Contains(ctx.AuthorId) ||
            _permissions.HasPermission("alcoholics", ctx.AuthorId))
        {
            return HandleAsyncCore();
        }

        return Task.CompletedTask;

        async Task HandleAsyncCore()
        {
            var alcoholics = Alcoholics
                .Where(a => a != ctx.AuthorId)
                .ToArray();

            Rng.Shuffle(alcoholics);

            await ctx.ReplyAsync(string.Join(' ', alcoholics.Select(a => MentionUtils.MentionUser(a))), suppressMentions: true);
        }
    }
}
