using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace MihuBot.Helpers
{
    public class CooldownTracker
    {
        public static readonly CooldownTracker NoCooldownTracker = new CooldownTracker(TimeSpan.Zero, 0, false);

        private readonly struct UserTimings
        {
            public readonly long Ticks;
            public readonly byte Tolerance;
            public readonly byte MaxTolerance;
            public readonly bool Warned;

            public UserTimings(long ticks, byte tolerance, byte maxTolerance, bool warned = false)
            {
                Ticks = ticks;
                Tolerance = tolerance;
                MaxTolerance = maxTolerance;
                Warned = warned;
            }

            public UserTimings TryGetNext(long cooldown, long currentTime)
            {
                long earliestValidTime = Ticks + cooldown;

                if (earliestValidTime > currentTime)
                {
                    if (Tolerance > 0)
                    {
                        return new UserTimings(currentTime, (byte)(Tolerance - 1), MaxTolerance);
                    }
                    else
                    {
                        // Cooldown
                        return new UserTimings(Ticks, Tolerance, MaxTolerance, warned: true);
                    }
                }
                else
                {
                    int newTolerance = Tolerance;

                    if (newTolerance < MaxTolerance)
                    {
                        newTolerance += (int)((currentTime - earliestValidTime) / (cooldown * 2));
                        newTolerance = Math.Min(newTolerance, MaxTolerance);
                    }

                    return new UserTimings(currentTime, (byte)newTolerance, MaxTolerance);
                }
            }
        }

        private readonly long _cooldown;
        private readonly int _cooldownTolerance;
        private readonly bool _adminOverride;
        private readonly ConcurrentDictionary<ulong, UserTimings> _timings;

        public CooldownTracker(TimeSpan cooldown, int cooldownTolerance, bool adminOverride = true)
        {
            if (cooldown > TimeSpan.Zero)
            {
                _cooldown = cooldown.Ticks;
                _cooldownTolerance = cooldownTolerance;
                _adminOverride = adminOverride;
                _timings = new ConcurrentDictionary<ulong, UserTimings>();
            }
        }

        public bool TryEnter(MessageContext ctx, out TimeSpan cooldown, out bool warned)
        {
            cooldown = TimeSpan.Zero;
            warned = false;

            if (_timings is null || (_adminOverride && ctx.IsFromAdmin))
                return true;

            long cooldownTicks = _cooldown;
            long currentTicks = Environment.TickCount64;

            UserTimings newValue = _timings.AddOrUpdate(
                ctx.AuthorId,
                (_, state) => new UserTimings(state.Current, state.InitialTolerance, state.InitialTolerance),
                (_, previous, state) => previous.TryGetNext(state.Cooldown, state.Current),
                (Current: currentTicks, Cooldown: cooldownTicks, InitialTolerance: (byte)((ctx.IsFromAdmin ? 5 : 1) * _cooldownTolerance)));

            if (newValue.Ticks == currentTicks)
            {
                return true;
            }

            cooldown = new TimeSpan(cooldownTicks - (currentTicks - newValue.Ticks));
            Debug.Assert(cooldown.Ticks > 0);
            warned = newValue.Warned;
            return false;
        }
    }
}
