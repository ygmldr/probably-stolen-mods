namespace PawnMind.Core
{
    /// <summary>
    /// Edge + cooldown gate for a debug hotkey polled from Update.
    /// Pure so the debounce behavior can be tested without Unity.
    /// </summary>
    public sealed class HotkeyLatch
    {
        private readonly double _cooldownSeconds;
        private double _lastFired = double.NegativeInfinity;

        public HotkeyLatch(double cooldownSeconds = 0.35)
        {
            _cooldownSeconds = cooldownSeconds < 0 ? 0 : cooldownSeconds;
        }

        public bool TryFire(bool down, double now)
        {
            if (!down) return false;
            if (now - _lastFired < _cooldownSeconds) return false;

            _lastFired = now;
            return true;
        }
    }
}
