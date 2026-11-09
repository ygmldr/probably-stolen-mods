using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class HotkeyLatchTests
    {
        [Test]
        public static void Latch_Fires_On_Key_Down()
        {
            var latch = new HotkeyLatch(0.5);
            Assert.True(latch.TryFire(true, 1.0), "fires on down");
            Assert.False(latch.TryFire(false, 1.1), "does not fire on up");
        }

        [Test]
        public static void Latch_Ignores_Repeat_Within_Cooldown()
        {
            var latch = new HotkeyLatch(0.5);
            Assert.True(latch.TryFire(true, 1.0), "first press");
            Assert.False(latch.TryFire(true, 1.2), "repeat inside cooldown");
            Assert.True(latch.TryFire(true, 1.6), "fires after cooldown");
        }
    }
}
