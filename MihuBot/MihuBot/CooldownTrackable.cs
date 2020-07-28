using MihuBot.Helpers;
using System;

namespace MihuBot
{
    public abstract class CooldownTrackable
    {
        private readonly CooldownTracker _cooldownTracker;

        protected virtual CooldownTracker Cooldown { get => CooldownTracker.NoCooldownTracker; }

        public CooldownTrackable()
        {
            _cooldownTracker = Cooldown;
        }

        public bool TryEnter(MessageContext ctx, out TimeSpan cooldown, out bool warned) =>
            _cooldownTracker.TryEnter(ctx, out cooldown, out warned);
    }
}
