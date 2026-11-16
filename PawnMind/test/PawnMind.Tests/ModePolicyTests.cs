using System.Collections.Generic;
using PawnMind.Core;
using static PawnMind.Tests.Assert;

namespace PawnMind.Tests
{
    /// <summary>
    /// Unknown operations are denied in every non-test mode; the allowlist is the
    /// only way in, and it is the same policy used by the pre-snapshot check.
    /// </summary>
    internal static class ModePolicyTests
    {
        [Test]
        public static void Test_Mode_Allows_Everything()
        {
            ModePolicy.Configure(RunMode.Test, null);
            Equal(null, ModePolicy.Refusal("confirm_trade"), "test mode");
            Equal(null, ModePolicy.Refusal("anything_unknown"), "test mode is permissive by design");
        }

        [Test]
        public static void Play_Mode_Denies_Unknown_Operations()
        {
            ModePolicy.Configure(RunMode.Play, new List<string> { "get_state", "confirm_trade" });
            Equal(null, ModePolicy.Refusal("confirm_trade"), "listed");
            Equal(ModePolicy.Forbidden, ModePolicy.Refusal("save_game"), "unlisted");
            Equal(ModePolicy.Forbidden, ModePolicy.Refusal("brand_new_op"), "unknown");
            Equal(ModePolicy.Forbidden, ModePolicy.Refusal(null), "empty");
        }

        [Test]
        public static void Bootstrap_Mode_Is_Narrower_Than_Play()
        {
            ModePolicy.Configure(RunMode.Bootstrap, new List<string> { "get_new_game_options" });
            Equal(null, ModePolicy.Refusal("get_new_game_options"), "menu only");
            Equal(ModePolicy.Forbidden, ModePolicy.Refusal("get_state"), "no snapshot reads");
        }

        [Test]
        public static void Reconfiguring_Replaces_The_Allowlist()
        {
            ModePolicy.Configure(RunMode.Play, new List<string> { "a" });
            ModePolicy.Configure(RunMode.Play, new List<string> { "b" });
            Equal(ModePolicy.Forbidden, ModePolicy.Refusal("a"), "old entry dropped");
            Equal(null, ModePolicy.Refusal("b"), "new entry");
            ModePolicy.Configure(RunMode.Test, null);
        }
    }
}
