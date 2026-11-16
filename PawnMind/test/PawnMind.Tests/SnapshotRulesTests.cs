using System.Collections.Generic;
using PawnMind.Core;
using static PawnMind.Tests.Assert;

namespace PawnMind.Tests
{
    /// <summary>
    /// The snapshot is read on every action; it must not touch getters that call
    /// BargainUIManager.Refresh().
    /// </summary>
    internal static class SnapshotRulesTests
    {
        [Test]
        public static void Bargain_Section_Only_Carries_Open()
        {
            Dictionary<string, object> snapshot = SnapshotRules.Bargain(true);
            Equal(1, snapshot.Count, "one key");
            True(snapshot.ContainsKey("open"), "open");
            Equal(0, SnapshotRules.Violations(snapshot.Keys).Count, "pure");
        }

        [Test]
        public static void Refresh_Backed_Keys_Are_Rejected()
        {
            foreach (string key in SnapshotRules.ImpureKeys)
            {
                Equal(1, SnapshotRules.Violations(new[] { key }).Count, key);
            }
        }

        [Test]
        public static void Ordinary_Keys_Pass()
        {
            Equal(0, SnapshotRules.Violations(new[] { "rev", "day", "cash", "bargain" }).Count,
                "no false positives");
        }
    }
}
