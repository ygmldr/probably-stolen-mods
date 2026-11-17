using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class MarkingPenService
    {
        public static string SetModeJson(int penId, int mode)
        {
            GameItem pen = GameState.FindItemAnywhere(penId);
            if (pen == null)
                return "{\"ok\":false,\"error\":\"item_not_found\",\"dispatched\":false}";
            if (!SafeTag(pen, "MARKING_PEN_TAG"))
                return "{\"ok\":false,\"error\":\"not_marking_pen\",\"dispatched\":false}";
            if (!MarkingPenMode.IsValid(mode))
                return "{\"ok\":false,\"error\":\"bad_mode\",\"dispatched\":false}";

            pen.ModifyTag("MARKING_PEN_MODE_INT", delegate(TagState state)
            {
                state.Enable().SetInt(mode);
            });

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "dispatched", true },
                { "mode", mode },
                { "color_id", MarkingPenMode.ColorId(mode) },
                { "eraser", mode == MarkingPenMode.Eraser }
            });
        }

        public static string ListMarksJson()
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
                    if (item == null || !AnimalMarkHelper.IsMarked(item)) continue;

                    int color = AnimalMarkHelper.GetColorIndex(item);
                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", item.GetUniqueID() },
                        { "name", item.name },
                        { "identifier", item.identifier },
                        { "container", containers[c] },
                        { "color_index", color },
                        { "color_id", MarkingPenMode.ColorId(color) }
                    });
                }
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "count", rows.Count },
                { "marks", rows }
            });
        }

        private static bool SafeTag(GameItem item, string tag)
        {
            try { return item.IsTag(tag); }
            catch { return false; }
        }
    }
}
