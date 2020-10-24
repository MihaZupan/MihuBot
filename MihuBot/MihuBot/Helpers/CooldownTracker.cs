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
            public readonly short Tolerance;
            public readonly short MaxTolerance;

            public UserTimings(long ticks, short tolerance, short maxTolerance)
            {
                Ticks = ticks;
                Tolerance = tolerance;
                MaxTolerance = maxTolerance;
            }

            public UserTimings TryGetNext(long cooldown, long currentTime)
            {
                long earliestValidTime = Ticks + cooldown;

                if (earliestValidTime > currentTime)
                {
                    short newTolerance = (short)(Math.Max(-1000, Tolerance - 1));
                    return new UserTimings(Tolerance > 0 ? currentTime : Ticks, newTolerance, MaxTolerance);
                }
                else
                {
                    int newTolerance = Tolerance;

                    if (newTolerance < MaxTolerance)
                    {
                        newTolerance += (int)((currentTime - earliestValidTime) / (cooldown * 2));
                        newTolerance = Math.Min(newTolerance, MaxTolerance);
                        newTolerance = Math.Max(newTolerance, 0);
                    }

                    return new UserTimings(currentTime, (short)newTolerance, MaxTolerance);
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

        public bool TryPeek(ulong userId)
        {
            return _timings is null
                || (_adminOverride && Constants.Admins.Contains(userId))
                || !_timings.TryGetValue(userId, out UserTimings timings)
                || timings.Ticks != timings.TryGetNext(_cooldown, DateTime.UtcNow.Ticks).Ticks;
        }

        public bool TryEnter(ulong userId) => TryEnter(userId, out _, out _);

        public bool TryEnter(ulong userId, out TimeSpan cooldown, out bool shouldWarn)
        {
            cooldown = TimeSpan.Zero;
            shouldWarn = false;

            if (_timings is null)
                return true;

            bool isAdmin = Constants.Admins.Contains(userId);
            if (isAdmin && _adminOverride)
                return true;

            long cooldownTicks = _cooldown;
            long currentTicks = DateTime.UtcNow.Ticks;

            UserTimings newValue = _timings.AddOrUpdate(
                userId,
                (_, state) => new UserTimings(state.Current, state.InitialTolerance, state.InitialTolerance),
                (_, previous, state) => previous.TryGetNext(state.Cooldown, state.Current),
                (Current: currentTicks, Cooldown: cooldownTicks, InitialTolerance: (short)((isAdmin ? 5 : 1) * _cooldownTolerance)));

            if (newValue.Ticks == currentTicks)
            {
                return true;
            }

            cooldown = new TimeSpan(cooldownTicks - (currentTicks - newValue.Ticks));
            Debug.Assert(cooldown.Ticks > 0);
            shouldWarn = newValue.Tolerance == -1;
            return false;
        }
    }
}
