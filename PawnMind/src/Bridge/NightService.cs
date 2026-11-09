using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class NightService
    {
        public static string ListLocationsJson()
        {
            var rows = new List<Dictionary<string, object>>();
            try
            {
                List<AfterhourLocation> locations = AfterhourLocation.GetDiscovered();
                for (int i = 0; i < locations.Count; i++)
                {
                    AfterhourLocation location = locations[i];
                    if (location == null) continue;

                    var row = new Dictionary<string, object>();
                    row["id"] = location.Id;
                    row["title"] = location.GetTitle();
                    row["available"] = location.IsAvailable();
                    row["current"] = GameState.CurrentLocationId == location.Id;
                    row["visits"] = Visits(location.Id);
                    rows.Add(row);
                }
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"location_list_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "outside", GameState.IsOutside },
                { "location", GameState.CurrentLocationId },
                { "locations", rows }
            });
        }

        public static string GoOutJson(string id)
        {
            AfterhourLocation location = AfterhourLocation.Get(id);
            NightCheck check = NightValidator.Validate(new NightFlags
            {
                Known = location != null,
                Discovered = location != null && location.IsDiscovered(),
                Available = location != null && location.IsAvailable(),
                Outside = GameState.IsOutside
            });
            if (check != NightCheck.Ok) return "{\"ok\":false,\"error\":\"" + ErrorCode(check) + "\"}";

            try
            {
                if (!GameState.IsOutside)
                {
                    MapUIManager.Instance.OpenUI();
                }

                MapUIManager.Instance.Travel(location);
                return "{\"ok\":true,\"location\":\"" + location.Id + "\"}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"go_out_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string ScavengeJson()
        {
            if (GameState.CurrentLocationId != "dumping_ground")
                return "{\"ok\":false,\"error\":\"not_at_dumping_ground\"}";

            bool canScavenge;
            try { canScavenge = ScavHelper.CanScavenge(); }
            catch (Exception e) { return "{\"ok\":false,\"error\":\"scav_check_failed\",\"detail\":\"" + Escape(e.Message) + "\"}"; }
            if (!canScavenge) return "{\"ok\":false,\"error\":\"no_attempts_left\"}";

            int before = AfterhourCount();
            try
            {
                ScavHelper.ScavengeDumpingGrounds();
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"scavenge_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            int after = AfterhourCount();
            var gained = new List<Dictionary<string, object>>();
            try
            {
                List<GameItem> items = EmporiumEntry.Instance.afterhourInventory.childItems;
                for (int i = Math.Max(0, before); i < items.Count; i++)
                {
                    var row = new Dictionary<string, object>();
                    row["id"] = items[i].GetUniqueID();
                    row["name"] = items[i].name;
                    row["identifier"] = items[i].identifier;
                    gained.Add(row);
                }
            }
            catch
            {
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "gained_count", Math.Max(0, after - before) },
                { "items", gained },
                { "time_left", ScavHelper.GetScavTimeLeft() },
                { "minor_wound_chance", ScavHelper.GetMinorWoundChance() },
                { "major_wound_chance", ScavHelper.GetMajorWoundChance() }
            });
        }

        public static string ReturnHomeJson()
        {
            if (!GameState.IsOutside) return "{\"ok\":false,\"error\":\"not_outside\"}";

            int itemsOnGround = AfterhourCount();
            try
            {
                MapUIManager.Instance.Return();
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"return_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "return_requested", true },
                { "items_on_ground", itemsOnGround },
                { "note", itemsOnGround > 0 ? "items on the ground may block the return" : "return is asynchronous; poll /state.outside" }
            });
        }

        private static int AfterhourCount()
        {
            try
            {
                return EmporiumEntry.Instance.afterhourInventory.childItems.Count;
            }
            catch
            {
                return 0;
            }
        }

        private static int Visits(string id)
        {
            try
            {
                return PlayerStore.Instance.GetAfterhourState(id).visits;
            }
            catch
            {
                return 0;
            }
        }

        private static string ErrorCode(NightCheck check)
        {
            switch (check)
            {
                case NightCheck.Unknown: return "unknown_location";
                case NightCheck.Undiscovered: return "undiscovered_location";
                case NightCheck.Unavailable: return "location_unavailable";
                case NightCheck.AlreadyOutside: return "already_outside";
                default: return "ok";
            }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
