using System;
using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Game
{
    /// <summary>
    /// Read-only faction perk adapter. ReputationUIManager shows the perk list
    /// for each faction; this projects the same fields into Core rows and does
    /// not mutate anything. Reputation is read with GetReputation (the rounded
    /// value the UI uses for thresholds); GetReputationExact stays in the
    /// existing faction response for callers that need it.
    /// </summary>
    internal static class FactionPerkAdapter
    {
        public static Dictionary<string, List<FactionPerkView>> ReadDetails()
        {
            var details = new Dictionary<string, List<FactionPerkView>>(StringComparer.Ordinal);
            try
            {
                if (!PlayerStore.HasInstance) return details;

                List<StoreReputation> reps = PlayerStore.Instance.storeReputations;
                if (reps == null) return details;

                for (int i = 0; i < reps.Count; i++)
                {
                    StoreReputation rep = reps[i];
                    if (rep == null || string.IsNullOrEmpty(rep.factionId)) continue;

                    var raws = new List<FactionPerkRaw>();
                    if (rep.perks != null)
                    {
                        for (int p = 0; p < rep.perks.Count; p++)
                        {
                            StoreReputationPerk perk = rep.perks[p];
                            if (perk == null) continue;
                            raws.Add(new FactionPerkRaw
                            {
                                Id = perk.perkId,
                                Threshold = perk.perkThreshold,
                                Unlocked = perk.isUnlocked,
                                Type = perk.perkType,
                                Icon = perk.iconName,
                                AlwaysActive = perk.isAlwaysActive
                            });
                        }
                    }

                    details[rep.factionId] = FactionPerkRules.Build(raws, SafeReputation(rep));
                }
            }
            catch (Exception)
            {
                // A perk read failure must not hide the rest of the faction list.
            }

            return details;
        }

        private static int SafeReputation(StoreReputation rep)
        {
            try
            {
                return rep.GetReputation();
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
