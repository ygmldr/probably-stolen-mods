using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class SecurityService
    {
        public static string StatusJson()
        {
            SecData sec = null;
            try { sec = PlayerStore.Instance.secData; } catch { }
            if (sec == null) return "{\"ok\":false,\"error\":\"no_security_data\"}";

            var crimes = new List<Dictionary<string, object>>();
            int count = Math.Min(sec.crimeTypes == null ? 0 : sec.crimeTypes.Count,
                sec.crimeAmount == null ? 0 : sec.crimeAmount.Count);
            for (int i = 0; i < count; i++)
            {
                crimes.Add(new Dictionary<string, object>
                {
                    { "type", sec.crimeTypes[i] },
                    { "amount", sec.crimeAmount[i] },
                    { "total", sec.crimeAmountTotal != null && i < sec.crimeAmountTotal.Count ? sec.crimeAmountTotal[i] : 0 }
                });
            }

            GameItem inspecting = null;
            try { inspecting = InspectionUIManager.Instance == null ? null : InspectionUIManager.Instance.currentInspectingItem; } catch { }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "evidence_level", sec.evidenceLevel },
                { "under_investigation", SecurityRules.IsUnderInvestigation(sec.evidenceLevel) },
                { "recommended_action", sec.recommendedAction },
                { "case_officer", sec.assignedCaseOfficer },
                { "dirty_water", sec.dirtyWaterTracker },
                { "mislabeled_water", sec.mislabeledWaterTracker },
                { "bad_chem_fraud", sec.badChemTrackerFraud },
                { "bad_chem_endangerment", sec.badChemTrackerEndangerment },
                { "bribe_tracker", sec.bribeTracker },
                { "crimes", crimes },
                { "inspecting", inspecting == null ? null : inspecting.name }
            });
        }
    }
}
