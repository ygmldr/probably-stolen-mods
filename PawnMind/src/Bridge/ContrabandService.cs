using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class ContrabandService
    {
        public static string StatusJson()
        {
            var rows = new List<Dictionary<string, object>>();
            List<string> containers = GameState.GetContainerIds();
            for (int c = 0; c < containers.Count; c++)
            {
                GameInventory inventory = GameState.GetContainer(containers[c]);
                if (inventory == null) continue;
                List<GameItem> items = inventory.childItems;
                for (int i = 0; i < items.Count; i++)
                {
                    GameItem item = items[i];
                    if (item == null) continue;

                    int level = 0;
                    bool stolen = false;
                    bool contraband = false;
                    try { level = ContrabandHelper.GetContrabandLevelInInt(item); } catch { }
                    try { contraband = ContrabandHelper.IsContraband(item); } catch { }
                    try { stolen = StolenHelper.IsStolenItem(item); } catch { }
                    if (!contraband && !stolen) continue;

                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", item.GetUniqueID() },
                        { "name", item.name },
                        { "identifier", item.identifier },
                        { "container", containers[c] },
                        { "contraband", contraband },
                        { "level", level },
                        { "stolen", stolen },
                        { "high_risk", ContrabandRules.IsHighRisk(level, stolen) }
                    });
                }
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "count", rows.Count },
                { "items", rows }
            });
        }
    }
}
