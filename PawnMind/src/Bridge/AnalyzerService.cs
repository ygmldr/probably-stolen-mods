using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;
using TMPro;

namespace PawnMind.Bridge
{
    internal static class AnalyzerService
    {
        public static string ListJson()
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
                    if (item == null || !GameState.IsOwned(item)) continue;

                    AnalyzerKind kind = AnalyzerKinds.FromIdentifier(item.identifier);
                    if (kind == AnalyzerKind.None) continue;

                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", item.GetUniqueID() },
                        { "name", item.name },
                        { "identifier", item.identifier },
                        { "kind", kind.ToString().ToLowerInvariant() },
                        { "container", containers[c] },
                        { "can_target", HasTargetHooks(item) }
                    });
                }
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "analyzers", rows }
            });
        }

        public static string AnalyzeJson(int analyzerId, int targetId)
        {
            GameItem analyzer = GameState.FindItemAnywhere(analyzerId);
            if (analyzer == null)
                return "{\"ok\":false,\"error\":\"analyzer_not_found\",\"dispatched\":false}";

            AnalyzerKind kind = AnalyzerKinds.FromIdentifier(analyzer.identifier);
            if (kind == AnalyzerKind.None)
                return "{\"ok\":false,\"error\":\"not_an_analyzer\",\"dispatched\":false}";
            if (kind == AnalyzerKind.Gene)
            {
                bool scanning = false;
                try { scanning = PlayerStore.Instance.isGeneScannerScanning; } catch { }
                // The gene scanner reports a toggle; no native call is issued.
                return JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "dispatched", false },
                    { "kind", "gene" },
                    { "scanning", scanning },
                    { "note", "gene scanner is a toggle; gene details stay in visible item text" }
                });
            }

            GameItem target = GameState.FindItemAnywhere(targetId);
            if (target == null)
                return "{\"ok\":false,\"error\":\"target_not_found\",\"dispatched\":false}";

            if (!Safe(delegate { return analyzer.CanTarget(target); }))
                return "{\"ok\":false,\"error\":\"cannot_target\",\"dispatched\":false}";

            try
            {
                analyzer.Target(target);
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"analyze_failed\",\"dispatched\":true,"
                    + "\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            var payload = new Dictionary<string, object>();
            payload["ok"] = true;
            // R4/F4b: the analyzer was targeted from here on.
            payload["dispatched"] = true;
            payload["kind"] = kind.ToString().ToLowerInvariant();
            payload["analyzer"] = analyzer.name;
            payload["target"] = target.name;

            try
            {
                if (kind == AnalyzerKind.Water) AddWater(payload, target);
                else if (kind == AnalyzerKind.Chem) AddChem(payload);
                else if (kind == AnalyzerKind.Plant) AddPlant(payload, target);
            }
            catch (Exception e)
            {
                payload["warnings"] = "read_failed: " + e.Message;
            }

            return JsonConvert.SerializeObject(payload);
        }

        private static void AddWater(Dictionary<string, object> payload, GameItem target)
        {
            WaterScannerUIManager ui = WaterScannerUIManager.Instance;
            payload["tested"] = SafeText(delegate { return WaterScannerUIManager.GetTestedString(target); });
            payload["labeled"] = SafeText(delegate { return WaterScannerUIManager.GetLabeledString(target); });
            payload["contaminant"] = Text(ui == null ? null : ui.contaminantTMP);
            payload["can_confront"] = SafeBool(delegate { return WaterScannerUIManager.CanConfront(target); });
            payload["can_match"] = SafeBool(delegate { return WaterScannerUIManager.CanMatch(target); });
        }

        private static void AddChem(Dictionary<string, object> payload)
        {
            ChemScannerUIManager ui = ChemScannerUIManager.Instance;
            if (ui != null)
            {
                try { ui.RefreshUI(); } catch { }
                try { ui.RefreshChemMode(); } catch { }
            }
            payload["tested"] = Text(ui == null ? null : ui.testedTMP);
            payload["labeled"] = Text(ui == null ? null : ui.labelTMP);
            payload["contaminant"] = Text(ui == null ? null : ui.contaminantTMP);
            payload["total_volume"] = Text(ui == null ? null : ui.totalVolumeTMP);
        }

        private static void AddPlant(Dictionary<string, object> payload, GameItem target)
        {
            PlantAnalyserUIManager ui = PlantAnalyserUIManager.Instance;
            payload["title"] = Text(ui == null ? null : ui.titleTMP);
            payload["body"] = Text(ui == null ? null : ui.bodyTMP);
            payload["nutrient_range"] = Text(ui == null ? null : ui.nutrimentRangeTMP);
            payload["nutrient_use"] = Text(ui == null ? null : ui.nutrientUseTMP);
            payload["contaminant_sensitivity"] = Text(ui == null ? null : ui.contaminantSensitivityTMP);
            payload["water_use"] = Text(ui == null ? null : ui.waterUsageTMP);
            payload["body2"] = Text(ui == null ? null : ui.body2TMP);
            payload["contaminant_sensitivity_value"] = ui == null ? "" : ui.GetContaminantSentitivity(target);
        }

        private static string Text(TextMeshProUGUI label)
        {
            return label == null || label.text == null ? "" : label.text;
        }

        private static bool HasTargetHooks(GameItem item)
        {
            return item.onItemTargetItemFunc != null || item.mayItemTargetItemFunc != null
                || item.canItemTargetItemFunc != null || item.onThisTargetItemFunc != null
                || item.mayThisTargetItemFunc != null || item.canThisTargetItemFunc != null;
        }

        private static string SafeText(Func<string> read)
        {
            try { return read() ?? ""; }
            catch { return ""; }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); }
            catch { return false; }
        }

        private static bool Safe(Func<bool> check)
        {
            try { return check(); }
            catch { return false; }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
