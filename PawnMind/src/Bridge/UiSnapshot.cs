using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PawnMind.Bridge
{
    /// <summary>
    /// Reads the currently active UI controls and can invoke a button's own
    /// onClick handler on the Unity main thread. Synthetic OS input does not
    /// reach the game, but invoking the UI callback does.
    /// </summary>
    internal static class UiSnapshot
    {
        private static readonly List<TabletButton> TabletButtons = new List<TabletButton>();
        private static readonly List<Button> PlainButtons = new List<Button>();
        private static string _token = string.Empty;

        public static string BuildJson()
        {
            TabletButtons.Clear();
            PlainButtons.Clear();
            _token = Guid.NewGuid().ToString("N").Substring(0, 8);

            var controls = new List<Dictionary<string, object>>();

            TabletButton[] tablets = UnityEngine.Object.FindObjectsOfType<TabletButton>();
            for (int i = 0; i < tablets.Length; i++)
            {
                TabletButton tablet = tablets[i];
                if (tablet == null || !tablet.isActiveAndEnabled || tablet.label == null) continue;

                TabletButtons.Add(tablet);
                var row = new Dictionary<string, object>();
                row["id"] = "t" + _token + ":" + (TabletButtons.Count - 1);
                row["kind"] = "tablet";
                row["text"] = tablet.label.text;
                row["enabled"] = tablet.Interactable;
                row["path"] = Path(tablet.transform);
                controls.Add(row);
            }

            Button[] buttons = UnityEngine.Object.FindObjectsOfType<Button>();
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.isActiveAndEnabled) continue;
                if (IsTabletButton(button)) continue;

                PlainButtons.Add(button);
                var row = new Dictionary<string, object>();
                row["id"] = "b" + _token + ":" + (PlainButtons.Count - 1);
                row["kind"] = "button";
                row["text"] = Label(button);
                row["enabled"] = button.interactable;
                row["path"] = Path(button.transform);
                controls.Add(row);
            }

            var payload = new Dictionary<string, object>();
            payload["ok"] = true;
            payload["controls"] = controls;
            return JsonConvert.SerializeObject(payload);
        }

        public static ActionResult Press(string controlId)
        {
            if (string.IsNullOrEmpty(controlId)) return ActionResult.Failure("press", "missing_control_id");

            try
            {
                int colon = controlId.IndexOf(':');
                if (colon <= 1) return ActionResult.Failure("press", "stale_snapshot");
                if (controlId.Substring(1, colon - 1) != _token)
                    return ActionResult.Failure("press", "stale_snapshot");

                if (controlId[0] == 't')
                {
                    int index;
                    if (!int.TryParse(controlId.Substring(colon + 1), out index)
                        || index < 0 || index >= TabletButtons.Count)
                        return ActionResult.Failure("press", "unknown_control");

                    TabletButton tablet = TabletButtons[index];
                    if (tablet == null || tablet.button == null)
                        return ActionResult.Failure("press", "no_button");
                    if (!tablet.Interactable)
                        return ActionResult.Failure("press", "disabled");

                    tablet.button.onClick.Invoke();
                    return ActionResult.Success("press");
                }

                if (controlId[0] == 'b')
                {
                    int index;
                    if (!int.TryParse(controlId.Substring(colon + 1), out index)
                        || index < 0 || index >= PlainButtons.Count)
                        return ActionResult.Failure("press", "unknown_control");

                    Button button = PlainButtons[index];
                    if (button == null) return ActionResult.Failure("press", "no_button");
                    if (!button.interactable) return ActionResult.Failure("press", "disabled");

                    button.onClick.Invoke();
                    return ActionResult.Success("press");
                }
            }
            catch (Exception e)
            {
                return ActionResult.Failure("press", "exception:" + e.GetType().Name);
            }

            return ActionResult.Failure("press", "unknown_control");
        }

        private static bool IsTabletButton(Button button)
        {
            for (int i = 0; i < TabletButtons.Count; i++)
            {
                if (TabletButtons[i] != null && TabletButtons[i].button == button) return true;
            }

            return false;
        }

        private static string Label(Button button)
        {
            TextMeshProUGUI text = button.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) return text.text;

            Text legacy = button.GetComponentInChildren<Text>();
            return legacy != null ? legacy.text : string.Empty;
        }

        private static string Path(Transform transform)
        {
            string path = transform.name;
            Transform parent = transform.parent;
            for (int i = 0; i < 3 && parent != null; i++)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }
    }
}
