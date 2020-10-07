using System;

namespace MihuBot.Helpers
{
    public sealed class SimpleCooldownTracker
    {
        private readonly long _cooldown;
        private long _lastTicks;

        public SimpleCooldownTracker(TimeSpan cooldown)
        {
            _cooldown = cooldown.Ticks;
        }

        public bool TryEnter(Func<bool> secondCondition)
        {
            lock (this)
            {
                long currentTime = DateTime.UtcNow.Ticks;

                if (_lastTicks + _cooldown > currentTime || !secondCondition())
                {
                    return false;
                }
                else
                {
                    _lastTicks = currentTime;
                    return true;
                }
            }
        }
    }
}
