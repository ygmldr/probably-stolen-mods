using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// The state snapshot is a read path: it must not call game getters that
    /// mutate. BargainUIManager's offered/used flags all start with Refresh(),
    /// which resets the per-client offer state when the tracked client changes —
    /// so the snapshot only ever exposes whether the panel is open.
    /// </summary>
    public static class SnapshotRules
    {
        /// <summary>Keys that may only be produced by a Refresh()-calling getter.</summary>
        public static readonly string[] ImpureKeys =
        {
            "can_sell_discount",
            "can_sell_markup",
            "can_buy_markup",
            "can_buy_discount",
            "sell_discount_used",
            "sell_markup_used",
            "buy_markup_used",
            "buy_discount_used",
            "can_report",
        };

        /// <summary>The whole bargain section a snapshot may contain.</summary>
        public static Dictionary<string, object> Bargain(bool open)
        {
            return new Dictionary<string, object> { { "open", open } };
        }

        /// <summary>Keys present in a snapshot that a pure read must never emit.</summary>
        public static List<string> Violations(IEnumerable<string> keys)
        {
            var found = new List<string>();
            if (keys == null) return found;

            foreach (string key in keys)
            {
                for (int i = 0; i < ImpureKeys.Length; i++)
                {
                    if (ImpureKeys[i] == key)
                    {
                        found.Add(key);
                        break;
                    }
                }
            }

            return found;
        }
    }
}
