using System.Collections.Generic;
using PawnMind.Core;
using static PawnMind.Tests.Assert;

namespace PawnMind.Tests
{
    /// <summary>
    /// The run mode is the outermost gate: a missing, malformed or unauthorised
    /// configuration must never fall back to a permissive mode. Only an explicit
    /// "run_mode=test" plus "allow_test_mode=true" reaches Test.
    /// </summary>
    internal static class RunModeTests
    {
        [Test]
        public static void Missing_Run_Mode_Fails_Closed()
        {
            RunModePlan plan = RunModeBinding.Evaluate(null, false);
            Equal(RunMode.Locked, plan.Mode, "no value");
            Equal(RunModeBinding.Missing, plan.Error, "stable error code");
            Equal(0, plan.Allow.Count, "nothing allowed");

            plan = RunModeBinding.Evaluate("   ", false);
            Equal(RunMode.Locked, plan.Mode, "blank value");
            Equal(RunModeBinding.Missing, plan.Error, "blank reports missing");
        }

        [Test]
        public static void Invalid_Run_Mode_Fails_Closed()
        {
            RunModePlan plan = RunModeBinding.Evaluate("production", false);
            Equal(RunMode.Locked, plan.Mode, "unknown word");
            Equal(RunModeBinding.Invalid, plan.Error, "stable error code");
            Equal(0, plan.Allow.Count, "nothing allowed");
        }

        [Test]
        public static void Test_Mode_Requires_Explicit_Acknowledgement()
        {
            RunModePlan plan = RunModeBinding.Evaluate("test", false);
            Equal(RunMode.Locked, plan.Mode, "test without ack stays locked");
            Equal(RunModeBinding.TestNotAuthorized, plan.Error, "stable error code");
        }

        [Test]
        public static void Acknowledged_Test_Mode_Is_Permissive()
        {
            RunModePlan plan = RunModeBinding.Evaluate("TEST", true);
            Equal(RunMode.Test, plan.Mode, "case insensitive");
            Equal(null, plan.Error, "no error");

            ModePolicy.Configure(plan.Mode, plan.Allow);
            Equal(null, ModePolicy.Refusal("anything_unknown"), "test stays permissive");
        }

        [Test]
        public static void Bootstrap_Plan_Excludes_Snapshot_Readers()
        {
            RunModePlan plan = RunModeBinding.Evaluate("bootstrap", false);
            Equal(RunMode.Bootstrap, plan.Mode, "bootstrap");
            Equal(null, plan.Error, "no error");
            True(plan.Allow.Count > 0, "menu operations stay available");

            for (int i = 0; i < plan.Allow.Count; i++)
            {
                False(PolicyCatalog.IsSnapshotReader(plan.Allow[i]),
                    "bootstrap must not allow snapshot reader " + plan.Allow[i]);
            }

            ModePolicy.Configure(plan.Mode, plan.Allow);
            Equal(ModePolicy.Forbidden, ModePolicy.Refusal("get_state"), "no snapshot reads");
        }

        [Test]
        public static void Play_Mode_Stays_Closed_Without_Live_Evidence()
        {
            RunModePlan plan = RunModeBinding.Evaluate("play", false);
            Equal(RunMode.Play, plan.Mode, "play");
            Equal(null, plan.Error, "no error");
            // Play never inherits the candidate allowlist: only tools with reviewed
            // live evidence qualify, and none has any yet.
            Equal(0, plan.Allow.Count, "no tool has live evidence yet");

            ModePolicy.Configure(plan.Mode, plan.Allow);
            Equal(ModePolicy.Forbidden, ModePolicy.Refusal("get_state"), "no silent downgrade");
            Equal(ModePolicy.Forbidden, ModePolicy.Refusal("save_game"), "writes stay closed");
        }

        [Test]
        public static void Play_Mode_Allows_A_Reviewed_Operation()
        {
            ModePolicy.Configure(RunMode.Play, new List<string> { "get_state", "confirm_trade" });
            Equal(null, ModePolicy.Refusal("confirm_trade"), "listed");
            Equal(ModePolicy.Forbidden, ModePolicy.Refusal("save_game"), "unlisted");
        }

        [Test]
        public static void Locked_Mode_Denies_Everything()
        {
            ModePolicy.Configure(RunMode.Locked, new List<string> { "get_state" });
            Equal(ModePolicy.Locked, ModePolicy.Refusal("get_state"), "even a listed operation");
            Equal(ModePolicy.Locked, ModePolicy.Refusal("ping"), "unknown");
        }

        [Test]
        public static void Default_Mode_Is_Locked()
        {
            ModePolicy.Reset();
            Equal(RunMode.Locked, ModePolicy.Mode, "no implicit Test");
            Equal(ModePolicy.Locked, ModePolicy.Refusal("get_state"), "deny by default");
        }

        [Test]
        public static void Catalog_Matches_The_Policy_File()
        {
            Equal("2", PolicyCatalog.Schema, "schema");
            Equal(64, PolicyCatalog.Sha256.Length, "sha256 hex length");
            Equal(6, PolicyCatalog.SnapshotReaders.Length, "six snapshot readers");
            True(PolicyCatalog.BootstrapOps.Length > 0, "bootstrap entries");
            Equal(0, PolicyCatalog.PlayOps.Length, "no live evidence yet");
        }

        [Test]
        public static void BridgeConfig_Parses_And_Roundtrips_The_Mode()
        {
            const string token = "0123456789abcdef0123456789abcdef";
            BridgeConfig parsed;
            string error;

            True(BridgeConfig.TryParse(
                "port=15528\ntoken=" + token + "\nrun_mode=play\nallow_test_mode=true",
                out parsed, out error), "parse explicit mode");
            Equal("play", parsed.RunMode, "raw value kept");
            True(parsed.AllowTestMode, "second switch");

            True(BridgeConfig.TryParse(
                "port=15528\ntoken=" + token, out parsed, out error), "parse without mode");
            Equal(string.Empty, parsed.RunMode, "missing stays missing");
            False(parsed.AllowTestMode, "default off");

            // A typo must survive parsing so the binding can report it instead of
            // the loader silently rewriting the file.
            True(BridgeConfig.TryParse(
                "port=15528\ntoken=" + token + "\nrun_mode=prod", out parsed, out error), "parse typo");
            Equal("prod", parsed.RunMode, "typo is preserved");

            string text = parsed.Serialize();
            True(text.Contains("run_mode=prod") && text.Contains("allow_test_mode=false"),
                "serialize keeps the mode line");
        }
    }
}
