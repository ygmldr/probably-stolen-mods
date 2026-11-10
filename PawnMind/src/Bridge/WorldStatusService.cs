using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class WorldStatusService
    {
        public static string ExpeditionJson()
        {
            try
            {
                ExpeditionData data = PlayerStore.Instance.expeditionData;
                if (data == null) return "{\"ok\":false,\"error\":\"no_expedition_data\"}";

                var locations = new List<Dictionary<string, object>>();
                for (int i = 0; i < data.expeditionLocations.Count; i++)
                {
                    ExpeditionLocation location = data.expeditionLocations[i];
                    if (location == null) continue;
                    locations.Add(new Dictionary<string, object>
                    {
                        { "id", location.id },
                        { "name", location.displayName },
                        { "danger", location.dangerRating },
                        { "foot_traffic", location.footTraffic },
                        { "legality", location.legality },
                        { "unlocked", location.isUnlocked },
                        { "description", location.description }
                    });
                }

                var hirelings = new List<Dictionary<string, object>>();
                for (int i = 0; i < data.hirelings.Count; i++)
                {
                    Hireling hireling = data.hirelings[i];
                    if (hireling == null) continue;
                    hirelings.Add(new Dictionary<string, object>
                    {
                        { "id", hireling.id },
                        { "name", hireling.displayName },
                        { "health", hireling.health },
                        { "max_health", hireling.maxHealth },
                        { "courage", hireling.courage },
                        { "state", hireling.state.ToString() },
                        { "status", hireling.status.ToString() },
                        { "assigned_location", hireling.assignedLocationId },
                        { "injured", SafeInjured(hireling) },
                        { "can_deploy", ExpeditionRules.CanDeploy(data.isExpeditionUnlocked, true, SafeInjured(hireling)) }
                    });
                }

                var ongoing = new List<Dictionary<string, object>>();
                for (int i = 0; i < data.ongoingExpeditions.Count; i++)
                {
                    Expedition expedition = data.ongoingExpeditions[i];
                    if (expedition == null) continue;
                    ongoing.Add(new Dictionary<string, object>
                    {
                        { "id", expedition.id },
                        { "location", expedition.expeditionLocation == null ? "" : expedition.expeditionLocation.id },
                        { "hireling", expedition.hireling == null ? "" : expedition.hireling.displayName },
                        { "turn", expedition.turn },
                        { "can_continue", SafeContinue(expedition) }
                    });
                }

                return JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "unlocked", data.isExpeditionUnlocked },
                    { "locations", locations },
                    { "hirelings", hirelings },
                    { "ongoing", ongoing }
                });
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"expedition_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string EventsJson()
        {
            try
            {
                StoreEventManager manager = StoreStation.Instance.storeEventManager;
                List<StoreEvent> active = manager.GetActiveEvents();
                var rows = new List<Dictionary<string, object>>();
                for (int i = 0; i < active.Count; i++)
                {
                    StoreEvent ev = active[i];
                    if (ev == null) continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", ev.identifier },
                        { "name", string.IsNullOrEmpty(ev.displayName) ? ev.newsName : ev.displayName },
                        { "type", ev.eventType.ToString() },
                        { "area", ev.eventArea.ToString() },
                        { "current_day", ev.currentDay },
                        { "duration", ev.duration },
                        { "importance", ev.importance }
                    });
                }

                return JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "active", rows },
                    { "future", manager.futurEvents == null ? 0 : manager.futurEvents.Count }
                });
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"events_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string SignsJson()
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
                    if (item == null || item.identifier == null || !item.identifier.StartsWith("sign_", StringComparison.Ordinal)) continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", item.GetUniqueID() },
                        { "name", item.name },
                        { "identifier", item.identifier },
                        { "container", containers[c] }
                    });
                }
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object> { { "ok", true }, { "signs", rows } });
        }

        private static bool SafeInjured(Hireling hireling)
        {
            try { return hireling.IsInjured(); } catch { return false; }
        }

        private static bool SafeContinue(Expedition expedition)
        {
            try { return expedition.CanContinue(); } catch { return false; }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
