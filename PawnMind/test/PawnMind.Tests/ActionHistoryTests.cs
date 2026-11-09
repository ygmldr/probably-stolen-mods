using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ActionHistoryTests
    {
        [Test]
        public static void History_Keeps_Only_Capacity()
        {
            var history = new ActionHistory(2);
            history.Add("a", "{}", true, null, 1);
            history.Add("b", "{}", true, null, 2);
            history.Add("c", "{}", true, null, 3);

            string json = history.ToJson(10);
            Assert.Contains(json, "\"op\":\"c\"", "newest kept");
            Assert.Contains(json, "\"op\":\"b\"", "second kept");
            Assert.False(json.Contains("\"op\":\"a\""), "oldest evicted");
        }

        [Test]
        public static void History_Serializes_Error_And_Rev()
        {
            var history = new ActionHistory();
            history.Add("open_shop", "{}", false, "not_in_game", 7);

            string json = history.ToJson(10);
            Assert.Contains(json, "\"ok\":false", "failure flag");
            Assert.Contains(json, "not_in_game", "error code");
            Assert.Contains(json, "\"rev\":7", "revision");
        }

        [Test]
        public static void History_Clamps_Limit()
        {
            var history = new ActionHistory();
            for (int i = 0; i < 10; i++) history.Add("op" + i, "{}", true, null, i);

            string json = history.ToJson(3);
            Assert.Contains(json, "op9", "latest");
            Assert.False(json.Contains("op6"), "limit respected");
        }
    }
}
