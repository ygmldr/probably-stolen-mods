using System;
using System.Text;

namespace PawnMind.Core
{
    /// <summary>
    /// Runtime identity of the loaded game assembly. The game layer fills this from
    /// confirmed public metadata only (PlayerStore.VERSION_NUMBER and the module
    /// MVID); no file access and no save access.
    /// </summary>
    public sealed class GameIdentity
    {
        public string GameVersion { get; private set; }
        public string AssemblyMvid { get; private set; }

        public GameIdentity(string gameVersion, string assemblyMvid)
        {
            GameVersion = gameVersion;
            AssemblyMvid = assemblyMvid;
        }
    }

    /// <summary>
    /// Result of comparing the runtime game identity with the identity explicitly
    /// configured by the user for recording. A missing or malformed expectation
    /// always locks writes; the installed identity is never auto-approved.
    /// </summary>
    public sealed class WriteGateDecision
    {
        public bool WriteAllowed { get; private set; }
        public string State { get; private set; }
        public string Reason { get; private set; }
        public string ActualGameVersion { get; private set; }
        public string ActualAssemblyMvid { get; private set; }
        public string ExpectedGameVersion { get; private set; }
        public string ExpectedAssemblyMvid { get; private set; }
        public bool ExpectedConfigured { get; private set; }

        private WriteGateDecision()
        {
        }

        public static WriteGateDecision Allow(
            string actualGameVersion,
            string actualAssemblyMvid,
            string expectedGameVersion,
            string expectedAssemblyMvid)
        {
            return new WriteGateDecision
            {
                WriteAllowed = true,
                State = GameVersionGate.StateOpen,
                Reason = null,
                ActualGameVersion = actualGameVersion,
                ActualAssemblyMvid = actualAssemblyMvid,
                ExpectedGameVersion = expectedGameVersion,
                ExpectedAssemblyMvid = expectedAssemblyMvid,
                ExpectedConfigured = true
            };
        }

        public static WriteGateDecision Locked(
            string reason,
            string actualGameVersion = null,
            string actualAssemblyMvid = null,
            string expectedGameVersion = null,
            string expectedAssemblyMvid = null,
            bool expectedConfigured = false)
        {
            return new WriteGateDecision
            {
                WriteAllowed = false,
                State = GameVersionGate.StateLocked,
                Reason = string.IsNullOrEmpty(reason) ? GameVersionGate.ReasonUnconfigured : reason,
                ActualGameVersion = actualGameVersion,
                ActualAssemblyMvid = actualAssemblyMvid,
                ExpectedGameVersion = expectedGameVersion,
                ExpectedAssemblyMvid = expectedAssemblyMvid,
                ExpectedConfigured = expectedConfigured
            };
        }
    }

    /// <summary>
    /// Pure comparison of the runtime game identity against the user-confirmed
    /// expectation. This is intentionally independent of Steam and of the
    /// installed build: an empty expectation means "recording writes disabled".
    /// </summary>
    public static class GameVersionGate
    {
        public const string StateOpen = "open";
        public const string StateLocked = "locked";

        public const string ReasonUnconfigured = "version_gate_unconfigured";
        public const string ReasonInvalidExpected = "version_gate_invalid";
        public const string ReasonIdentityUnavailable = "version_identity_unavailable";
        public const string ReasonVersionMismatch = "game_version_mismatch";
        public const string ReasonMvidMismatch = "game_assembly_mvid_mismatch";

        /// <summary>The code cannot verify the Steam stable branch; health always says so.</summary>
        public const string LatestUnverified = "UNVERIFIED";

        public static WriteGateDecision Evaluate(
            GameIdentity actual,
            string expectedGameVersion,
            string expectedAssemblyMvid)
        {
            string actualVersion = Clean(actual == null ? null : actual.GameVersion);
            string actualMvid = Clean(actual == null ? null : actual.AssemblyMvid);
            string expectedVersion = Clean(expectedGameVersion);
            string expectedMvid = Clean(expectedAssemblyMvid);
            bool configured = expectedVersion.Length > 0 && expectedMvid.Length > 0;

            if (!configured)
            {
                return WriteGateDecision.Locked(
                    ReasonUnconfigured,
                    actualVersion,
                    actualMvid,
                    expectedVersion,
                    expectedMvid,
                    false);
            }

            string normalizedExpectedMvid = NormalizeMvid(expectedMvid);
            if (normalizedExpectedMvid == null)
            {
                return WriteGateDecision.Locked(
                    ReasonInvalidExpected,
                    actualVersion,
                    actualMvid,
                    expectedVersion,
                    expectedMvid,
                    true);
            }

            string normalizedActualMvid = NormalizeMvid(actualMvid);
            if (actualVersion.Length == 0 || normalizedActualMvid == null)
            {
                return WriteGateDecision.Locked(
                    ReasonIdentityUnavailable,
                    actualVersion,
                    actualMvid,
                    expectedVersion,
                    expectedMvid,
                    true);
            }

            if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
            {
                return WriteGateDecision.Locked(
                    ReasonVersionMismatch,
                    actualVersion,
                    actualMvid,
                    expectedVersion,
                    expectedMvid,
                    true);
            }

            if (!string.Equals(normalizedActualMvid, normalizedExpectedMvid, StringComparison.Ordinal))
            {
                return WriteGateDecision.Locked(
                    ReasonMvidMismatch,
                    actualVersion,
                    actualMvid,
                    expectedVersion,
                    expectedMvid,
                    true);
            }

            return WriteGateDecision.Allow(actualVersion, actualMvid, expectedVersion, expectedMvid);
        }

        /// <summary>
        /// Accepts the GUID forms users normally copy (hyphenated or compact,
        /// upper or lower case) and returns 32 lowercase hex chars.
        /// </summary>
        public static string NormalizeMvid(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            var builder = new StringBuilder(32);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '-') continue;
                if (!IsHex(c)) return null;
                builder.Append(char.ToLowerInvariant(c));
            }

            return builder.Length == 32 ? builder.ToString() : null;
        }

        private static string Clean(string value)
        {
            return value == null ? string.Empty : value.Trim();
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
        }
    }
}
