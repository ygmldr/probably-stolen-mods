using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class ClienteleRulesTests
    {
        private static ClienteleTypeRow Type(string id, string name = null)
        {
            return new ClienteleTypeRow { Id = id, Name = name };
        }

        private static ClientelePredictionRow Prediction(
            string clientId,
            int chance,
            string itemId,
            string clientName = null,
            string itemName = null)
        {
            return new ClientelePredictionRow
            {
                ClientId = clientId,
                ClientName = clientName,
                Chance = chance,
                ItemId = itemId,
                ItemName = itemName
            };
        }

        private static ClienteleRaw Raw()
        {
            return new ClienteleRaw
            {
                Readable = true,
                Types = new List<ClienteleTypeRow>(),
                Predictions = new List<ClientelePredictionRow>(),
                Warnings = new List<string>()
            };
        }

        [Test]
        public static void ClampChance_Is_Bounded()
        {
            Assert.Equal(0, ClienteleRules.ClampChance(-5), "negative chance");
            Assert.Equal(0, ClienteleRules.ClampChance(0), "zero chance");
            Assert.Equal(75, ClienteleRules.ClampChance(75), "normal chance");
            Assert.Equal(100, ClienteleRules.ClampChance(100), "maximum chance");
            Assert.Equal(100, ClienteleRules.ClampChance(180), "over maximum");
        }

        [Test]
        public static void Build_Null_Lists_Do_Not_Throw()
        {
            var raw = new ClienteleRaw { Readable = true, Types = null, Predictions = null };

            Clientele view = ClienteleRules.Build(raw, 3);

            Assert.True(view.Readable, "readable");
            Assert.NotNull(view.Types, "types is never null");
            Assert.NotNull(view.Predictions, "predictions is never null");
            Assert.Equal(0, view.Types.Count, "no types");
            Assert.Equal(0, view.Predictions.Count, "no predictions");
        }

        [Test]
        public static void Build_Cleans_Types()
        {
            ClienteleRaw raw = Raw();
            raw.Types.Add(Type("LUXURY_ITEM", "Luxury"));
            raw.Types.Add(Type("LUXURY_ITEM", "Luxury duplicate"));
            raw.Types.Add(Type(" "));
            raw.Types.Add(null);
            raw.Types.Add(Type("FOOD", null));

            Clientele view = ClienteleRules.Build(raw, 3);

            Assert.Equal(2, view.Types.Count, "duplicates and blanks dropped");
            Assert.Equal("LUXURY_ITEM", view.Types[0].Id, "order preserved");
            Assert.Equal("Luxury", view.Types[0].Name, "name preserved");
            Assert.Equal("FOOD", view.Types[1].Id, "second type");
            Assert.Equal("FOOD", view.Types[1].Name, "missing name falls back to the id");
        }

        [Test]
        public static void Build_Caps_Types()
        {
            ClienteleRaw raw = Raw();
            for (int i = 0; i < 30; i++) raw.Types.Add(Type("TYPE_" + i));

            Clientele view = ClienteleRules.Build(raw, 3);

            Assert.Equal(ClienteleRules.MaxTypes, view.Types.Count, "types capped");
        }

        [Test]
        public static void Build_Sorts_Predictions_By_Chance()
        {
            ClienteleRaw raw = Raw();
            raw.Predictions.Add(Prediction("b", 40, "item_b"));
            raw.Predictions.Add(Prediction("a", 75, "item_a"));
            raw.Predictions.Add(Prediction("c", 40, "item_c"));
            raw.Predictions.Add(Prediction("b", 40, "item_b"));

            Clientele view = ClienteleRules.Build(raw, 3);

            Assert.Equal(3, view.Predictions.Count, "exact duplicates removed");
            Assert.Equal("a", view.Predictions[0].ClientId, "highest chance first");
            Assert.Equal("b", view.Predictions[1].ClientId, "ties sort by client id");
            Assert.Equal("c", view.Predictions[2].ClientId, "ties sort by client id");
            Assert.Equal(40, view.Predictions[1].Chance, "chance preserved");
        }

        [Test]
        public static void Build_Drops_Invalid_And_Clamps_Predictions()
        {
            ClienteleRaw raw = Raw();
            raw.Predictions.Add(null);
            raw.Predictions.Add(Prediction(" ", 10, "item"));
            raw.Predictions.Add(Prediction("ok", 220, "item", null, null));

            Clientele view = ClienteleRules.Build(raw, 3);

            Assert.Equal(1, view.Predictions.Count, "only the valid prediction survives");
            Assert.Equal("ok", view.Predictions[0].ClientId, "client id kept");
            Assert.Equal(100, view.Predictions[0].Chance, "chance clamped");
            Assert.Equal("ok", view.Predictions[0].ClientName, "client name falls back to the id");
            Assert.Equal("item", view.Predictions[0].ItemName, "item name falls back to the item id");
        }

        [Test]
        public static void Build_Caps_Predictions()
        {
            ClienteleRaw raw = Raw();
            for (int i = 0; i < 40; i++) raw.Predictions.Add(Prediction("client_" + i, 50, "item_" + i));

            Clientele view = ClienteleRules.Build(raw, 3);

            Assert.Equal(ClienteleRules.MaxPredictions, view.Predictions.Count, "predictions capped");
        }

        [Test]
        public static void Build_Clamps_Counters()
        {
            ClienteleRaw raw = Raw();
            raw.DiscerningChance = 150;
            raw.DiscerningCooldownOpenings = -4;
            raw.NormalCustomerCount = -2;

            Clientele view = ClienteleRules.Build(raw, 3);

            Assert.Equal(100, view.DiscerningChance, "chance clamped");
            Assert.Equal(0, view.DiscerningCooldownOpenings, "cooldown clamped");
            Assert.Equal(0, view.NormalCustomerCount, "customer count clamped");
        }

        [Test]
        public static void Build_Dedupes_Warnings_And_Caps_Them()
        {
            ClienteleRaw raw = Raw();
            raw.Warnings.Add("types_unavailable");
            raw.Warnings.Add("types_unavailable");
            raw.Warnings.Add(" ");
            for (int i = 0; i < 20; i++) raw.Warnings.Add("warn_" + i);

            Clientele view = ClienteleRules.Build(raw, 3);

            Assert.Equal(ClienteleRules.MaxWarnings, view.Warnings.Count, "warnings capped");
            Assert.Equal("types_unavailable", view.Warnings[0], "first warning kept");
        }

        [Test]
        public static void Build_Unreadable_Is_Fail_Closed()
        {
            var raw = new ClienteleRaw { Readable = false, Error = "not_in_game" };

            Clientele view = ClienteleRules.Build(raw, 3);

            Assert.False(view.Readable, "unreadable");
            Assert.Equal("not_in_game", view.Error, "error preserved");
            Assert.Equal(0, view.Types.Count, "no types");
            Assert.Equal(0, view.Predictions.Count, "no predictions");
        }

        [Test]
        public static void Build_Null_Raw_Is_Unreadable()
        {
            Clientele view = ClienteleRules.Build(null, 3);

            Assert.False(view.Readable, "null raw");
            Assert.Equal(ClienteleErrors.Unavailable, view.Error, "stable error");
        }
        [Test]
        public static void ShouldReuse_Only_Within_The_Same_Day()
        {
            Assert.True(ClienteleRules.ShouldReuse(hasCache: true, cachedDay: 5, currentDay: 5),
                "same day reuses the snapshot");
            Assert.False(ClienteleRules.ShouldReuse(hasCache: true, cachedDay: 5, currentDay: 6),
                "a new day must re-read");
            Assert.False(ClienteleRules.ShouldReuse(hasCache: false, cachedDay: 5, currentDay: 5),
                "no cache means read");
        }

        [Test]
        public static void Build_Propagates_The_Cached_Flag()
        {
            var raw = new ClienteleRaw { Readable = true, Day = 5, Cached = true };

            Clientele view = ClienteleRules.Build(raw, 5);

            Assert.True(view.Cached, "cached flag must survive Build");
        }
    }
}
