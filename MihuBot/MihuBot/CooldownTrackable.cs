using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot
{
    public abstract class CooldownTrackable
    {
        private readonly CooldownTracker _cooldownTracker;


        protected virtual int CooldownToleranceCount => 1;
        protected virtual TimeSpan Cooldown => TimeSpan.Zero;

        public CooldownTrackable()
        {
            var cooldown = Cooldown;

            _cooldownTracker = cooldown > TimeSpan.Zero
                ? new CooldownTracker(cooldown, CooldownToleranceCount, adminOverride: true)
                : CooldownTracker.NoCooldownTracker;
        }

        public bool TryEnter(MessageContext ctx) =>
            TryEnter(ctx, out _, out _);

        public bool TryEnter(MessageContext ctx, out TimeSpan cooldown, out bool shouldWarn) =>
            _cooldownTracker.TryEnter(ctx, out cooldown, out shouldWarn);

        public async Task<bool> TryEnterOrWarnAsync(MessageContext ctx)
        {
            if (TryEnter(ctx, out var cooldown, out bool shouldWarn))
                return true;

            if (shouldWarn)
            {
                await ctx.WarnCooldownAsync(cooldown);
            }

            return false;
        }
    }
}
