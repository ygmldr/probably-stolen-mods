using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PawnMind.Bridge
{
    /// <summary>
    /// In-game debugging helpers triggered by F7 / Shift+F7. No threads, tasks
    /// or coroutines; everything runs from the Update loop on the main thread.
    /// </summary>
    internal static class Diagnostics
    {
        public static void Pulse()
        {
            try
            {
                JObject state = JObject.Parse(StateSnapshot.BuildJson());
                JArray legal = state["legal_actions"] as JArray;
                JObject client = state["client"] as JObject;
                string clientName = client == null ? "-" : ((string)client["name"] ?? "-");

                Debug.Log("[PawnMind] debug rev=" + Text(state, "rev")
                    + " day=" + Text(state, "day")
                    + " shop=" + Text(state, "shop_state")
                    + " cash=" + Text(state, "cash")
                    + " client=" + (clientName ?? "-")
                    + " legal=" + (legal == null ? 0 : legal.Count));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawnMind] debug pulse failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static string Text(JObject state, string key)
        {
            JToken token = state[key];
            return token == null ? "-" : token.ToString();
        }

        public static void DumpState()
        {
            try
            {
                string json = StateSnapshot.BuildJson();
                ModHelper.WriteModDataFileText("pawnmind", "debug-state.json", json);
                Debug.Log("[PawnMind] debug state written to ModData/pawnmind/debug-state.json");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawnMind] debug dump failed: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
