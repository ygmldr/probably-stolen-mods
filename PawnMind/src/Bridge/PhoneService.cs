using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PawnMind.Core;
using PawnMind.Game;

namespace PawnMind.Bridge
{
    internal static class PhoneService
    {
        public static string ListJson(bool includeHidden, bool allowTestItems, bool recordingArmed)
        {
            bool resolvedHidden;
            string hiddenError;
            if (!PhoneDirectory.TryResolveIncludeHidden(
                    includeHidden, allowTestItems, recordingArmed, out resolvedHidden, out hiddenError))
            {
                return "{\"ok\":false,\"error\":\"" + Escape(hiddenError) + "\"}";
            }

            var contacts = new List<PhoneContact>();
            try
            {
                foreach (KeyValuePair<long, StorePhoneClient> pair in PlayerStore.Instance.PhoneClientDict)
                {
                    StorePhoneClient client = pair.Value;
                    if (client == null) continue;

                    contacts.Add(new PhoneContact
                    {
                        Number = pair.Key,
                        Name = SafeName(client),
                        State = client.phoneState.ToString(),
                        Shown = SafeShown(client),
                        Cooldown = client.currentCooldown,
                        AmountSpent = client.amountSpent
                    });
                }
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"phone_list_failed\",\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            List<PhoneContact> visible = PhoneDirectory.Visible(contacts, resolvedHidden);
            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < visible.Count; i++)
            {
                PhoneContact contact = visible[i];
                rows.Add(new Dictionary<string, object>
                {
                    { "number", contact.Number },
                    { "name", contact.Name },
                    { "state", contact.State },
                    { "cooldown", contact.Cooldown },
                    { "amount_spent", contact.AmountSpent },
                    { "shown", contact.Shown }
                });
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "contacts", rows }
            });
        }

        public static string CallJson(long number)
        {
            PhoneUIManager ui = PhoneUIManager.Instance;
            if (ui == null)
                return "{\"ok\":false,\"error\":\"no_phone_ui\",\"dispatched\":false}";
            if (!StorePhoneClient.IsNumberExist(number))
                return "{\"ok\":false,\"error\":\"unknown_number\",\"dispatched\":false}";

            StorePhoneClient client = StorePhoneClient.GetPhoneClientByNumber(number);
            bool answers = PhoneUIManager.WillAnswerCall(number);
            try
            {
                ui.OpenUI();
                ui.AutoCall(number);
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"call_failed\",\"dispatched\":true,"
                    + "\"detail\":\"" + Escape(e.Message) + "\"}";
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "ok", true },
                { "dispatched", true },
                { "number", number },
                { "name", client == null ? "" : SafeName(client) },
                { "will_answer", answers },
                { "state", client == null ? "" : client.phoneState.ToString() }
            });
        }

        public static string StateJson()
        {
            PhoneUIManager ui = PhoneUIManager.Instance;
            if (ui == null) return "{\"ok\":false,\"error\":\"no_phone_ui\"}";

            StorePhoneClient client = ui.currentPhoneClient;
            DialoguePhoneUIManager phoneUi = DialoguePhoneUIManager.Instance;
            var payload = new Dictionary<string, object>
            {
                { "ok", true },
                { "in_call", client != null },
                { "name", client == null ? "" : SafeName(client) },
                { "state", client == null ? "" : client.phoneState.ToString() },
                { "cooldown", client == null ? 0 : client.currentCooldown },
                { "dialogue", PhoneDialogue(phoneUi) }
            };
            return JsonConvert.SerializeObject(payload);
        }

        public static string AdvanceJson()
        {
            DialoguePhoneUIManager ui = DialoguePhoneUIManager.Instance;
            if (ui == null)
                return "{\"ok\":false,\"error\":\"no_phone_dialogue\",\"dispatched\":false}";
            try
            {
                ui.OnAdvancePressed();
                return WithDispatched(StateJson());
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"advance_failed\",\"dispatched\":true,"
                    + "\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        public static string ChooseJson(int index)
        {
            DialoguePhoneUIManager ui = DialoguePhoneUIManager.Instance;
            if (ui == null)
                return "{\"ok\":false,\"error\":\"no_phone_dialogue\",\"dispatched\":false}";
            try
            {
                ui.SelectChoice(index);
                return WithDispatched(StateJson());
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"choose_failed\",\"dispatched\":true,"
                    + "\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        private static Dictionary<string, object> PhoneDialogue(DialoguePhoneUIManager ui)
        {
            if (ui == null || ui.currentDialog == null) return null;

            Dialogue dialogue = ui.currentDialog;
            var rows = new List<Dictionary<string, object>>();
            for (int i = 0; i < dialogue.choices.Count; i++)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "text", dialogue.choices[i].Item2 == null ? "" : dialogue.choices[i].Item2.plainText },
                    { "enabled", dialogue.IsChoiceEnabledAt(i) }
                });
            }

            return new Dictionary<string, object>
            {
                { "title", dialogue.title },
                { "text", dialogue.plainText },
                { "choice", ui.IsChoice() },
                { "ended", ui.IsCurrentDialogEnd() },
                { "choices", rows }
            };
        }

        public static string EndJson()
        {
            PhoneUIManager ui = PhoneUIManager.Instance;
            if (ui == null)
                return "{\"ok\":false,\"error\":\"no_phone_ui\",\"dispatched\":false}";
            try
            {
                ui.OnPhoneDialogEnd();
                ui.CloseUI();
                return "{\"ok\":true,\"dispatched\":true}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"end_call_failed\",\"dispatched\":true,"
                    + "\"detail\":\"" + Escape(e.Message) + "\"}";
            }
        }

        /// <summary>Marks a state read that followed a native call (R4/F4b).</summary>
        private static string WithDispatched(string stateJson)
        {
            try
            {
                JObject view = JObject.Parse(stateJson);
                view["dispatched"] = true;
                return view.ToString(Formatting.None);
            }
            catch (Exception)
            {
                return stateJson;
            }
        }

        private static string SafeName(StorePhoneClient client)
        {
            try { return client.GetLocalizedDisplayName(); }
            catch { return client.displayName ?? ""; }
        }

        private static bool SafeShown(StorePhoneClient client)
        {
            try { return client.ShownInPhoneBook(); }
            catch { return false; }
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
