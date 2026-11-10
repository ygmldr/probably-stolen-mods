using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class ServiceStatusService
    {
        public static string ListServicesJson()
        {
            var rows = new List<Dictionary<string, object>>();
            try
            {
                List<StoreService> services = PlayerStore.Instance.storeService;
                for (int i = 0; i < services.Count; i++)
                {
                    StoreService service = services[i];
                    if (service == null) continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", service.id },
                        { "name", service.displayName },
                        { "cost", service.cost },
                        { "unlocked", service.unlocked },
                        { "using", service.isUsing },
                        { "mandatory", service.isMandatory },
                        { "cooldown", service.currentCooldown }
                    });
                }
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"service_list_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object> { { "ok", true }, { "services", rows } });
        }

        public static string ToggleServiceJson(string id, bool on)
        {
            if (string.IsNullOrEmpty(id)) return "{\"ok\":false,\"error\":\"missing_id\"}";

            StoreService service = null;
            try
            {
                List<StoreService> services = PlayerStore.Instance.storeService;
                for (int i = 0; i < services.Count; i++)
                {
                    if (services[i] != null && services[i].id == id) { service = services[i]; break; }
                }
            }
            catch { }

            ServiceCheck check = ServiceValidator.Validate(
                service != null,
                service != null && service.unlocked,
                service != null && service.isMandatory,
                service != null && service.currentCooldown > 0);
            if (check != ServiceCheck.Ok) return "{\"ok\":false,\"error\":\"" + ErrorCode(check) + "\"}";

            service.isUsing = on;
            return "{\"ok\":true,\"id\":\"" + id + "\",\"using\":" + (on ? "true" : "false") + "}";
        }

        public static string HusbandryJson()
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
                    bool fish = SafeBool(delegate { return AquacultureHelper.IsFish(item); });
                    bool animal = SafeTag(item, "ANIMAL_ITEM_TAG");
                    if (!fish && !animal) continue;

                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", item.GetUniqueID() },
                        { "name", item.name },
                        { "identifier", item.identifier },
                        { "container", containers[c] },
                        { "fish", fish },
                        { "animal", animal },
                        { "dead", SafeTag(item, "ANIMAL_IS_DEAD_TAG") || SafeTag(item, "FISH_IS_DEAD_TAG") },
                        { "health", SafeFloat(item, "ANIMAL_HEALTH_TAG") }
                    });
                }
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object> { { "ok", true }, { "animals", rows } });
        }

        private static bool SafeTag(GameItem item, string tag)
        {
            try { return item.IsTag(tag); } catch { return false; }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); } catch { return false; }
        }

        private static float SafeFloat(GameItem item, string tag)
        {
            try { return item.GetTagReadonly(tag).GetFloat(); } catch { return 0f; }
        }

        private static string ErrorCode(ServiceCheck check)
        {
            switch (check)
            {
                case ServiceCheck.NotFound: return "service_not_found";
                case ServiceCheck.Locked: return "service_locked";
                case ServiceCheck.Mandatory: return "service_mandatory";
                case ServiceCheck.Cooldown: return "service_cooldown";
                default: return "ok";
            }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
