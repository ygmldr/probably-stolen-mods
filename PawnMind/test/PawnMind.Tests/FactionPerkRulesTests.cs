using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class FactionPerkRulesTests
    {
        private static FactionPerkRaw Perk(
            string id,
            int threshold,
            bool unlocked = false,
            int type = 0,
            string icon = null,
            bool alwaysActive = false)
        {
            return new FactionPerkRaw
            {
                Id = id,
                Threshold = threshold,
                Unlocked = unlocked,
                Type = type,
                Icon = icon,
                AlwaysActive = alwaysActive
            };
        }

        [Test]
        public static void Remaining_Is_Never_Negative()
        {
            Assert.Equal(60, FactionPerkRules.Remaining(100, 40), "below the threshold");
            Assert.Equal(0, FactionPerkRules.Remaining(100, 100), "exactly at the threshold");
            Assert.Equal(0, FactionPerkRules.Remaining(100, 250), "above the threshold");
            Assert.Equal(120, FactionPerkRules.Remaining(100, -20), "negative reputation still owes the full threshold");
            Assert.Equal(0, FactionPerkRules.Remaining(-10, 0), "invalid threshold");
        }

        [Test]
        public static void RemainingToUnlock_Respects_The_Threshold_Direction()
        {
            Assert.Equal(60, FactionPerkRules.RemainingToUnlock(100, 40), "gain reputation");
            Assert.Equal(0, FactionPerkRules.RemainingToUnlock(100, 120), "already unlocked");
            Assert.Equal(40, FactionPerkRules.RemainingToUnlock(-80, -40), "lose reputation");
            Assert.Equal(0, FactionPerkRules.RemainingToUnlock(-80, -100), "already below");

            Assert.Equal("at_or_above", FactionPerkRules.DirectionFor(80), "positive threshold");
            Assert.Equal("at_or_below", FactionPerkRules.DirectionFor(-80), "negative threshold");
        }

        [Test]
        public static void Build_Exposes_Direction_And_Direction_Aware_Distance()
        {
            var perks = new List<FactionPerkRaw>
            {
                Perk("positive", 100),
                Perk("negative", -80)
            };

            List<FactionPerkView> rows = FactionPerkRules.Build(perks, -40);

            Assert.Equal("at_or_below", rows[0].Direction, "negative threshold sorts first");
            Assert.Equal(-80, rows[0].Threshold, "threshold preserved");
            Assert.Equal(0, rows[0].Remaining, "literal formula reads zero while still locked");
            Assert.Equal(40, rows[0].RemainingToUnlock, "direction-aware distance");
            Assert.Equal("at_or_above", rows[1].Direction, "positive threshold direction");
            Assert.Equal(140, rows[1].RemainingToUnlock, "100 - (-40)");
        }

        [Test]
        public static void Build_Sorts_By_Threshold_Then_Id()
        {
            var perks = new List<FactionPerkRaw>
            {
                Perk("zeta", 300),
                Perk("beta", 100),
                Perk("alpha", 100),
                Perk("lowest", 50)
            };

            List<FactionPerkView> rows = FactionPerkRules.Build(perks, 100);

            Assert.Equal(4, rows.Count, "all perks");
            Assert.Equal("lowest", rows[0].Id, "lowest threshold first");
            Assert.Equal("alpha", rows[1].Id, "tie broken by id");
            Assert.Equal("beta", rows[2].Id, "tie broken by id");
            Assert.Equal("zeta", rows[3].Id, "highest threshold last");
        }

        [Test]
        public static void Build_Computes_Remaining_Per_Perk()
        {
            var perks = new List<FactionPerkRaw>
            {
                Perk("one", 40),
                Perk("two", 100, unlocked: true),
                Perk("three", 160)
            };

            List<FactionPerkView> rows = FactionPerkRules.Build(perks, 120);

            Assert.Equal(0, rows[0].Remaining, "already reached");
            Assert.False(rows[0].Unlocked, "unlock flag preserved");
            Assert.Equal(0, rows[1].Remaining, "unlocked perk has no remainder");
            Assert.True(rows[1].Unlocked, "unlock flag preserved on the second perk");
            Assert.Equal(40, rows[2].Remaining, "160 - 120");
        }

        [Test]
        public static void Build_Skips_Invalid_And_Duplicate_Perks()
        {
            var perks = new List<FactionPerkRaw>
            {
                null,
                Perk(" ", 10),
                Perk("ok", 10, type: 2, icon: "icon_a", alwaysActive: true),
                Perk("ok", 99)
            };

            List<FactionPerkView> rows = FactionPerkRules.Build(perks, 0);

            Assert.Equal(1, rows.Count, "only one valid perk");
            Assert.Equal(10, rows[0].Threshold, "first duplicate wins");
            Assert.Equal(2, rows[0].Type, "type preserved");
            Assert.Equal("icon_a", rows[0].Icon, "icon preserved");
            Assert.True(rows[0].AlwaysActive, "always-active preserved");
        }

        [Test]
        public static void Build_Caps_The_List()
        {
            var perks = new List<FactionPerkRaw>();
            for (int i = 0; i < 100; i++) perks.Add(Perk("perk_" + i, i));

            List<FactionPerkView> rows = FactionPerkRules.Build(perks, 0);

            Assert.Equal(FactionPerkRules.MaxPerks, rows.Count, "perk list capped");
        }

        [Test]
        public static void Build_Null_List_Is_Empty()
        {
            List<FactionPerkView> rows = FactionPerkRules.Build(null, 10);

            Assert.NotNull(rows, "never null");
            Assert.Equal(0, rows.Count, "empty");
        }
    }
}
