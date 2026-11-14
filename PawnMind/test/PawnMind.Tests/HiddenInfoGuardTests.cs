using System;
using System.Collections.Generic;
using System.IO;

namespace PawnMind.Tests
{
    /// <summary>
    /// Static fail-closed guard for the S6A hidden-information audit. The game
    /// layer must not read answer keys that the player cannot see; those reads are
    /// easy to add accidentally while wiring a status tool. This test scans the
    /// project's own source (nothing from a save, a running game or the game
    /// assembly) and fails on the known hidden members.
    /// </summary>
    internal static class HiddenInfoGuardTests
    {
        private static readonly string[] ForbiddenMembers =
        {
            // Appraisal: the true base value is the answer key for appraisals.
            "GetCorrectBaseValue",
            // Wanted clients: stealth-wanted must never be distinguishable.
            "isStealthWanted",
            "stealthWanted",
            "stealth_wanted",
            // Water: the real purity/quality composition, as opposed to the
            // player-visible label (GetCurrentWaterDisplay / GetCurrentItemCondition).
            "GetWaterPurity",
            "GetWaterQualityScore",
            "GetWineContaminantPerMille",
            // ItemFeature.realCondition bypasses IsExposed(); only the public
            // projection (GetCurrentItemCondition display) may be read.
            "realCondition",
            // Customer wallet: the negotiation UI shows the client budget, not
            // the private cash field (dead in Rev13, but do not rely on that).
            "clientCash"
        };

        private static string SourceRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Bridge", "BridgeServer.cs")))
                    return directory.FullName;
                directory = directory.Parent;
            }

            throw new AssertionException(
                "could not locate the PawnMind source root from " + AppContext.BaseDirectory);
        }

        [Test]
        public static void Bridge_Source_Never_Reads_Hidden_Answer_Keys()
        {
            string root = Path.Combine(SourceRoot(), "src");
            var violations = new List<string>();

            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (string member in ForbiddenMembers)
                {
                    if (text.IndexOf(member, StringComparison.Ordinal) >= 0)
                    {
                        violations.Add(Path.GetFileName(file) + " -> " + member);
                    }
                }
            }

            Assert.Equal(0, violations.Count,
                "source reads hidden game members: " + string.Join(", ", violations.ToArray()));
        }

        [Test]
        public static void List_Contacts_Routes_Through_The_Visibility_Gate()
        {
            string phoneService = File.ReadAllText(
                Path.Combine(SourceRoot(), "src", "Bridge", "PhoneService.cs"));
            Assert.Contains(phoneService, "TryResolveIncludeHidden",
                "list_contacts must consult the hidden-contact gate");
            Assert.Contains(phoneService, "hiddenError",
                "the gate error code must be surfaced to the caller");
        }
    }
}
