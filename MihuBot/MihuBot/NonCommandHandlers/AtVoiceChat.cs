using MihuBot.Permissions;

namespace MihuBot.NonCommandHandlers
{
    public sealed class AtVoiceChat : NonCommandHandler
    {
        private readonly IPermissionsService _permissions;

        public AtVoiceChat(IPermissionsService permissionsService)
        {
            _permissions = permissionsService ?? throw new ArgumentNullException(nameof(permissionsService));
        }

        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.StartsWith('@') &&
                ulong.TryParse(ctx.Content.AsSpan(1), out ulong id) &&
                _permissions.HasPermission(nameof(AtVoiceChat), ctx.AuthorId) &&
                ctx.Guild.VoiceChannels.TryGetFirst(id, out var vc))
            {
                return HandleAsyncCore();
            }

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                if (vc.Users.Count > 0)
                {
                    string message = string.Join(' ', vc.Users.Select(u => MentionUtils.MentionUser(u.Id)));
                    await ctx.ReplyAsync(message);
                }
            }
        }
    }
}
