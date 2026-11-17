using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class InteractionService
    {
        public static string ListJson(int itemId)
        {
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null) return "{\"ok\":false,\"error\":\"item_not_found\"}";

            bool owned = GameState.IsOwned(item);
            bool canActivate = Safe(item.CanActivateSlotItem);
            bool canToggle = Safe(item.CanToggleSlotItem);
            bool canTarget = item.canThisTargetItemFunc != null || item.mayThisTargetItemFunc != null;

            List<ItemInteraction> actions = InteractionCatalog.Available(new InteractionFlags
            {
                Owned = owned,
                CanActivate = canActivate,
                CanToggle = canToggle,
                CanTarget = canTarget
            });

            var names = new List<string>();
            for (int i = 0; i < actions.Count; i++) names.Add(actions[i].ToString().ToLowerInvariant());

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "id", itemId },
                { "name", item.name },
                { "owned", owned },
                { "can_activate", canActivate },
                { "can_toggle", canToggle },
                { "can_target", canTarget },
                { "actions", names }
            });
        }

        public static ActionResult Activate(int itemId)
        {
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null) return ActionResult.Failure("activate_item", "item_not_found");
            if (!GameState.IsOwned(item)) return ActionResult.Failure("activate_item", "not_owned");
            if (!Safe(item.CanActivateSlotItem)) return ActionResult.Failure("activate_item", "cannot_activate");

            try
            {
                item.ActivateSlotItem();
                return ActionResult.Success("activate_item");
            }
            catch (Exception e)
            {
                return ActionResult.Failed("activate_item", "activation_failed: " + e.Message);
            }
        }

        public static ActionResult Toggle(int itemId)
        {
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null) return ActionResult.Failure("toggle_item", "item_not_found");
            if (!GameState.IsOwned(item)) return ActionResult.Failure("toggle_item", "not_owned");
            if (!Safe(item.CanToggleSlotItem)) return ActionResult.Failure("toggle_item", "cannot_toggle");

            try
            {
                item.ToggleSlotItem();
                return ActionResult.Success("toggle_item");
            }
            catch (Exception e)
            {
                return ActionResult.Failed("toggle_item", "toggle_failed: " + e.Message);
            }
        }

        public static ActionResult Use(int toolId, int targetId)
        {
            GameItem tool = GameState.FindItemAnywhere(toolId);
            GameItem target = GameState.FindItemAnywhere(targetId);
            if (tool == null) return ActionResult.Failure("use_tool", "tool_not_found");
            if (target == null) return ActionResult.Failure("use_tool", "target_not_found");
            if (!GameState.IsOwned(tool)) return ActionResult.Failure("use_tool", "tool_not_owned");
            if (!Safe(delegate { return tool.CanTarget(target); }))
                return ActionResult.Failure("use_tool", "cannot_target");

            try
            {
                tool.Target(target);
                return ActionResult.Success("use_tool");
            }
            catch (Exception e)
            {
                return ActionResult.Failed("use_tool", "use_failed: " + e.Message);
            }
        }

        private static bool Safe(Func<bool> check)
        {
            try
            {
                return check();
            }
            catch
            {
                return false;
            }
        }
    }
}
