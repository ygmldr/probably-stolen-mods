using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class GameVersionGateTests
    {
        private const string VersionA = "VERSION 1-Rev1";
        private const string VersionB = "VERSION 2-Rev2";
        private const string MvidA = "11111111-2222-3333-4444-555555555555";
        private const string MvidB = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        private static WriteGateDecision Evaluate(
            string actualVersion,
            string actualMvid,
            string expectedVersion,
            string expectedMvid)
        {
            return GameVersionGate.Evaluate(
                new GameIdentity(actualVersion, actualMvid),
                expectedVersion,
                expectedMvid);
        }

        [Test]
        public static void Gate_Opens_When_Expected_Identity_Matches()
        {
            WriteGateDecision decision = Evaluate(VersionA, MvidA, VersionA, MvidA);

            Assert.True(decision.WriteAllowed, "matching identity must open the write gate");
            Assert.Equal(GameVersionGate.StateOpen, decision.State, "open state");
            Assert.Null(decision.Reason, "open gate has no reason");
            Assert.True(decision.ExpectedConfigured, "both expected values were configured");
            Assert.Equal(VersionA, decision.ActualGameVersion, "actual version is reported");
            Assert.Equal(MvidA, decision.ActualAssemblyMvid, "actual mvid is reported");
        }

        [Test]
        public static void Gate_Accepts_Expected_Mvid_Without_Hyphens_Or_With_Uppercase()
        {
            string compactUpper = MvidA.Replace("-", string.Empty).ToUpperInvariant();
            WriteGateDecision decision = Evaluate(VersionA, compactUpper, VersionA, compactUpper);

            Assert.True(decision.WriteAllowed, "MVID comparison ignores case and separators");
            Assert.Equal(GameVersionGate.StateOpen, decision.State, "open state");
        }

        [Test]
        public static void Gate_Trims_Configured_Identity()
        {
            WriteGateDecision decision = Evaluate(
                VersionA,
                MvidA,
                "  " + VersionA + "  ",
                "  " + MvidA + "  ");

            Assert.True(decision.WriteAllowed, "configured values are trimmed");
            Assert.Equal(VersionA, decision.ExpectedGameVersion, "trimmed expected version");
            Assert.Equal(MvidA, decision.ExpectedAssemblyMvid, "trimmed expected mvid");
        }

        [Test]
        public static void Gate_Locks_When_Expected_Identity_Is_Missing()
        {
            WriteGateDecision bothMissing = Evaluate(VersionA, MvidA, null, null);
            Assert.False(bothMissing.WriteAllowed, "missing expected values must lock writes");
            Assert.Equal(GameVersionGate.StateLocked, bothMissing.State, "locked state");
            Assert.Equal(GameVersionGate.ReasonUnconfigured, bothMissing.Reason, "stable unconfigured code");
            Assert.False(bothMissing.ExpectedConfigured, "nothing was configured");

            WriteGateDecision versionOnly = Evaluate(VersionA, MvidA, VersionA, null);
            Assert.False(versionOnly.WriteAllowed, "missing expected mvid must lock writes");
            Assert.Equal(GameVersionGate.ReasonUnconfigured, versionOnly.Reason, "unconfigured code");

            WriteGateDecision mvidOnly = Evaluate(VersionA, MvidA, null, MvidA);
            Assert.False(mvidOnly.WriteAllowed, "missing expected version must lock writes");
            Assert.Equal(GameVersionGate.ReasonUnconfigured, mvidOnly.Reason, "unconfigured code");
        }

        [Test]
        public static void Gate_Does_Not_Auto_Approve_The_Installed_Identity()
        {
            WriteGateDecision decision = Evaluate(VersionA, MvidA, string.Empty, string.Empty);

            Assert.False(decision.WriteAllowed, "actual identity alone must never open the gate");
            Assert.Equal(GameVersionGate.ReasonUnconfigured, decision.Reason, "unconfigured code");
        }

        [Test]
        public static void Gate_Locks_When_Expected_Mvid_Is_Malformed()
        {
            WriteGateDecision decision = Evaluate(VersionA, MvidA, VersionA, "not-a-guid");

            Assert.False(decision.WriteAllowed, "malformed expected mvid must lock writes");
            Assert.Equal(GameVersionGate.ReasonInvalidExpected, decision.Reason, "stable invalid-expected code");
            Assert.True(decision.ExpectedConfigured, "expected values were present but invalid");
        }

        [Test]
        public static void Gate_Locks_When_Actual_Identity_Is_Unavailable()
        {
            WriteGateDecision noVersion = Evaluate(null, MvidA, VersionA, MvidA);
            Assert.False(noVersion.WriteAllowed, "missing runtime version must lock writes");
            Assert.Equal(GameVersionGate.ReasonIdentityUnavailable, noVersion.Reason, "identity unavailable code");

            WriteGateDecision noMvid = Evaluate(VersionA, null, VersionA, MvidA);
            Assert.False(noMvid.WriteAllowed, "missing runtime mvid must lock writes");
            Assert.Equal(GameVersionGate.ReasonIdentityUnavailable, noMvid.Reason, "identity unavailable code");

            WriteGateDecision unknown = Evaluate("UNKNOWN", "UNKNOWN", VersionA, MvidA);
            Assert.False(unknown.WriteAllowed, "unknown runtime identity must lock writes");
            Assert.Equal(GameVersionGate.ReasonIdentityUnavailable, unknown.Reason, "identity unavailable code");

            WriteGateDecision missingObject = GameVersionGate.Evaluate(null, VersionA, MvidA);
            Assert.False(missingObject.WriteAllowed, "missing runtime identity object must lock writes");
            Assert.Equal(
                GameVersionGate.ReasonIdentityUnavailable,
                missingObject.Reason,
                "missing identity object uses the identity unavailable code");
        }

        [Test]
        public static void Gate_Locks_On_Game_Version_Mismatch()
        {
            WriteGateDecision decision = Evaluate(VersionB, MvidA, VersionA, MvidA);

            Assert.False(decision.WriteAllowed, "version mismatch must lock writes");
            Assert.Equal(GameVersionGate.ReasonVersionMismatch, decision.Reason, "stable version mismatch code");
            Assert.Equal(VersionB, decision.ActualGameVersion, "actual version is reported");
            Assert.Equal(VersionA, decision.ExpectedGameVersion, "expected version is reported");
        }

        [Test]
        public static void Gate_Locks_On_Assembly_Mvid_Change()
        {
            WriteGateDecision decision = Evaluate(VersionA, MvidB, VersionA, MvidA);

            Assert.False(decision.WriteAllowed, "mvid change must lock writes");
            Assert.Equal(GameVersionGate.ReasonMvidMismatch, decision.Reason, "stable mvid mismatch code");
            Assert.Equal(MvidB, decision.ActualAssemblyMvid, "actual mvid is reported");
            Assert.Equal(MvidA, decision.ExpectedAssemblyMvid, "expected mvid is reported");
        }

        [Test]
        public static void Gate_Prefers_Version_Mismatch_When_Both_Differ()
        {
            WriteGateDecision decision = Evaluate(VersionB, MvidB, VersionA, MvidA);

            Assert.Equal(GameVersionGate.ReasonVersionMismatch, decision.Reason, "version is the primary identity");
        }

        [Test]
        public static void Gate_Normalizes_Mvid_Consistently()
        {
            Assert.Equal(
                "11111111222233334444555555555555",
                GameVersionGate.NormalizeMvid(MvidA),
                "hyphenated guid normalizes to 32 lowercase hex chars");
            Assert.Equal(
                "11111111222233334444555555555555",
                GameVersionGate.NormalizeMvid(MvidA.Replace("-", string.Empty).ToUpperInvariant()),
                "compact uppercase guid normalizes");
            Assert.Null(GameVersionGate.NormalizeMvid("not-a-guid"), "non-guid is rejected");
            Assert.Null(GameVersionGate.NormalizeMvid(null), "null is rejected");
        }

        [Test]
        public static void Health_Copies_Gate_Identity_And_Keeps_Latest_Unverified()
        {
            WriteGateDecision gate = Evaluate(VersionA, MvidA, VersionA, MvidA);
            BridgeHealth health = BridgeHealth.From(gate);

            Assert.Equal(VersionA, health.GameVersion, "actual game version");
            Assert.Equal(MvidA, health.GameAssemblyMvid, "actual mvid");
            Assert.Equal(VersionA, health.ExpectedGameVersion, "expected version");
            Assert.Equal(MvidA, health.ExpectedAssemblyMvid, "expected mvid");
            Assert.Equal(GameVersionGate.StateOpen, health.WriteGateState, "gate state");
            Assert.Null(health.WriteGateReason, "open gate reason");
            Assert.Equal(GameVersionGate.LatestUnverified, health.Latest, "latest is never auto-verified");
        }

        [Test]
        public static void Config_Expected_Identity_Opens_Gate_For_Matching_Installed_Build()
        {
            string text = "port=16000\ntoken=" + new string('a', 32)
                + "\nexpected_game_version=" + VersionA
                + "\nexpected_assembly_mvid=" + MvidA + "\n";

            BridgeConfig config;
            string error;
            Assert.True(BridgeConfig.TryParse(text, out config, out error), "parse: " + error);

            WriteGateDecision decision = GameVersionGate.Evaluate(
                new GameIdentity(VersionA, MvidA),
                config.ExpectedGameVersion,
                config.ExpectedAssemblyMvid);

            Assert.True(decision.WriteAllowed, "configured matching identity opens the gate");
        }
    }
}
