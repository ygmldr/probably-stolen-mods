using System;
using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Game
{
    /// <summary>
    /// Read-only clientele adapter. Mirrors ClienteleUIManager.RefreshUI:
    /// ShowcaseHelper.GetDisplayCaseType / GetDiscerningShopperChance /
    /// GetDiscerningShopperCooldownOpenings / GetDisplayCasePrediction and
    /// StoreClientManager.GetNormalCustomerCount. Only player-visible names are
    /// resolved (StoreClientListDict / TypeHelper / GeneralHelper).
    ///
    /// Note: GetDisplayCasePrediction and GetNormalCustomerCount call the game
    /// RNG/caches exactly like opening the panel does; the values are the same
    /// ones the panel would show, but repeated reads can differ where the game
    /// itself rolls again.
    /// </summary>
    internal sealed class ClienteleAdapter : IClientelePort
    {
        public static readonly ClienteleAdapter Instance = new ClienteleAdapter();

        // Reading clientele is not side-effect free: GetNormalCustomerCount rolls
        // RNG and GetDisplayCasePrediction caches a random supplier pick. Reuse one
        // snapshot per game day so repeated lookups cannot keep advancing the RNG.
        private static int _cachedDay = int.MinValue;
        private static ClienteleRaw _cachedRaw;

        private static ClienteleRaw CopyWithCached(ClienteleRaw source)
        {
            return new ClienteleRaw
            {
                Readable = source.Readable,
                Error = source.Error,
                Day = source.Day,
                Types = source.Types,
                DiscerningChance = source.DiscerningChance,
                DiscerningCooldownOpenings = source.DiscerningCooldownOpenings,
                Predictions = source.Predictions,
                NormalCustomerCount = source.NormalCustomerCount,
                Warnings = source.Warnings,
                Cached = true
            };
        }

        public ClienteleRaw ReadClientele()
        {
            var raw = new ClienteleRaw();
            try
            {
                if (!PlayerStore.HasInstance)
                {
                    raw.Error = "not_in_game";
                    return raw;
                }

                PlayerStore store = PlayerStore.Instance;
                if (store == null)
                {
                    raw.Error = ClienteleErrors.Unavailable;
                    return raw;
                }

                raw.Readable = true;
                raw.Day = GameState.GetDay();

                if (ClienteleRules.ShouldReuse(_cachedRaw != null, _cachedDay, raw.Day))
                {
                    return CopyWithCached(_cachedRaw);
                }

                ReadTypes(raw);
                ReadDiscerning(raw);
                ReadPredictions(raw);
                ReadNormalCustomers(raw, store);

                if (raw.Readable)
                {
                    _cachedDay = raw.Day;
                    _cachedRaw = raw;
                }
            }
            catch (Exception)
            {
                raw = new ClienteleRaw
                {
                    Readable = false,
                    Error = ClienteleErrors.Unavailable
                };
            }

            return raw;
        }

        private static void ReadTypes(ClienteleRaw raw)
        {
            try
            {
                List<string> types = ShowcaseHelper.GetDisplayCaseType();
                if (types == null) return;

                for (int i = 0; i < types.Count; i++)
                {
                    string id = types[i];
                    if (string.IsNullOrEmpty(id)) continue;
                    raw.Types.Add(new ClienteleTypeRow
                    {
                        Id = id,
                        Name = SafeTypeName(id)
                    });
                }
            }
            catch (Exception)
            {
                raw.Warnings.Add("types_unavailable");
            }
        }

        private static void ReadDiscerning(ClienteleRaw raw)
        {
            try
            {
                raw.DiscerningChance = ShowcaseHelper.GetDiscerningShopperChance();
            }
            catch (Exception)
            {
                raw.Warnings.Add("discerning_chance_unavailable");
            }

            try
            {
                raw.DiscerningCooldownOpenings =
                    ShowcaseHelper.GetDiscerningShopperCooldownOpenings();
            }
            catch (Exception)
            {
                raw.Warnings.Add("discerning_cooldown_unavailable");
            }
        }

        private static void ReadPredictions(ClienteleRaw raw)
        {
            try
            {
                List<(string, int, string)> prediction = ShowcaseHelper.GetDisplayCasePrediction();
                if (prediction == null) return;

                for (int i = 0; i < prediction.Count; i++)
                {
                    // Verified against ShowcaseHelper and ClienteleUIManager:
                    // Item1 = client identifier, Item2 = chance percent,
                    // Item3 = attracted item identifier.
                    string clientId = prediction[i].Item1;
                    if (string.IsNullOrEmpty(clientId)) continue;

                    string itemId = prediction[i].Item3;
                    raw.Predictions.Add(new ClientelePredictionRow
                    {
                        ClientId = clientId,
                        ClientName = SafeClientName(clientId),
                        Chance = prediction[i].Item2,
                        ItemId = itemId,
                        ItemName = SafeItemName(itemId)
                    });
                }
            }
            catch (Exception)
            {
                raw.Warnings.Add("prediction_unavailable");
            }
        }

        private static void ReadNormalCustomers(ClienteleRaw raw, PlayerStore store)
        {
            try
            {
                StoreClientManager manager = store.storeClientManager;
                raw.NormalCustomerCount = manager == null ? 0 : manager.GetNormalCustomerCount();
            }
            catch (Exception)
            {
                raw.Warnings.Add("normal_customer_count_unavailable");
            }
        }

        private static string SafeTypeName(string id)
        {
            try
            {
                string name = TypeHelper.GetTypeDisplayName(id);
                return string.IsNullOrEmpty(name) ? id : name;
            }
            catch (Exception)
            {
                return id;
            }
        }

        private static string SafeClientName(string id)
        {
            try
            {
                string name = StoreClientListDict.GetClientDisplayName(id);
                return string.IsNullOrEmpty(name) ? id : name;
            }
            catch (Exception)
            {
                return id;
            }
        }

        private static string SafeItemName(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            try
            {
                string name = GeneralHelper.GetDisplayNameFromID(id);
                return string.IsNullOrEmpty(name) ? id : name;
            }
            catch (Exception)
            {
                return id;
            }
        }
    }
}
