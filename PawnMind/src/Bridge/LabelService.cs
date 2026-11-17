using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class LabelService
    {
        public static string ListJson(int itemId)
        {
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null) return "{\"ok\":false,\"error\":\"item_not_found\"}";

            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < item.itemFeatures.Count; i++)
            {
                ItemFeature feature = item.itemFeatures[i];
                if (feature == null || !LabelerHelper.IsEditableLabelFeature(feature)) continue;

                rows.Add(Describe(feature));
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "id", itemId },
                { "name", item.name },
                { "labels", rows }
            });
        }

        public static string SetJson(int itemId, string category, string conditionId)
        {
            GameItem item = GameState.FindItemAnywhere(itemId);
            if (item == null)
                return "{\"ok\":false,\"error\":\"item_not_found\",\"dispatched\":false}";

            ItemFeature target = null;
            bool categoryExists = false;
            for (int i = 0; i < item.itemFeatures.Count; i++)
            {
                ItemFeature feature = item.itemFeatures[i];
                if (feature == null) continue;
                if (!string.Equals(feature.category, category, StringComparison.Ordinal)) continue;

                categoryExists = true;
                if (LabelerHelper.IsEditableLabelFeature(feature))
                {
                    target = feature;
                    break;
                }
            }

            bool editable = target != null;
            bool conditionExists = false;
            ItemCondition chosen = null;
            int chosenIndex = -1;
            if (editable && ItemConditionList.conditionLookup.ContainsKey(category))
            {
                List<ItemCondition> conditions = ItemConditionList.conditionLookup[category];
                for (int i = 0; i < conditions.Count; i++)
                {
                    if (!string.Equals(conditions[i].identifier, conditionId, StringComparison.Ordinal)) continue;
                    conditionExists = true;
                    chosen = conditions[i];
                    chosenIndex = i;
                    break;
                }
            }

            LabelCheck check = LabelValidator.Validate(true, editable, categoryExists, conditionExists);
            if (check != LabelCheck.Ok)
                return "{\"ok\":false,\"error\":\"" + ErrorCode(check) + "\",\"dispatched\":false}";

            target.fakeCondition = chosen;
            target.SetExposed(false);
            WaterFeatureHelper.OnWaterLabelUpdated(target, chosenIndex);

            // R4/F4b: the label write ran; report it with the fresh readback.
            JObject view;
            try
            {
                view = JObject.Parse(ListJson(itemId));
            }
            catch (Exception)
            {
                return "{\"ok\":true,\"dispatched\":true,\"written\":true}";
            }

            view["dispatched"] = true;
            return view.ToString(Formatting.None);
        }

        private static Dictionary<string, object> Describe(ItemFeature feature)
        {
            var row = new Dictionary<string, object>();
            row["category"] = feature.category;

            ItemCondition current = feature.GetCurrentItemCondition();
            row["current_id"] = current == null ? "" : current.identifier;
            row["current_display"] = current == null ? "" : current.display;

            var options = new List<Dictionary<string, object>>();
            if (ItemConditionList.conditionLookup.ContainsKey(feature.category))
            {
                List<ItemCondition> conditions = ItemConditionList.conditionLookup[feature.category];
                for (int i = 0; i < conditions.Count; i++)
                {
                    options.Add(new Dictionary<string, object>
                    {
                        { "id", conditions[i].identifier },
                        { "display", conditions[i].display }
                    });
                }
            }

            row["options"] = options;
            return row;
        }

        private static string ErrorCode(LabelCheck check)
        {
            switch (check)
            {
                case LabelCheck.ItemNotFound: return "item_not_found";
                case LabelCheck.NotEditable: return "not_editable";
                case LabelCheck.CategoryNotFound: return "category_not_found";
                case LabelCheck.ConditionNotFound: return "condition_not_found";
                default: return "ok";
            }
        }
    }
}
