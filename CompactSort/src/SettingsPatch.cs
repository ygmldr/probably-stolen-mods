using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CompactSort
{
    internal static class CompactSortSettings
    {
        private const string Key = "CompactSort.TimeBudgetMs";
        private const int DefaultBudget = 350;
        private static int _value = -1;

        public static int TimeBudgetMs
        {
            get
            {
                if (_value < 0)
                {
                    _value = Mathf.Clamp(
                        Mathf.RoundToInt(PlayerPrefs.GetFloat(Key, DefaultBudget)),
                        80, 2000);
                }
                return _value;
            }
            set
            {
                _value = Mathf.Clamp(value, 80, 2000);
                PlayerPrefs.SetFloat(Key, _value);
                PlayerPrefs.Save();
            }
        }
    }

    internal static class KeyChoices
    {
        public static readonly List<KeyCode> Keys = BuildKeys();
        public static readonly List<string> Labels = BuildLabels();

        private static List<KeyCode> BuildKeys()
        {
            var keys = new List<KeyCode>();
            for (int i = (int)KeyCode.A; i <= (int)KeyCode.Z; i++) keys.Add((KeyCode)i);
            for (int i = (int)KeyCode.Alpha0; i <= (int)KeyCode.Alpha9; i++) keys.Add((KeyCode)i);
            for (int i = (int)KeyCode.F1; i <= (int)KeyCode.F12; i++) keys.Add((KeyCode)i);
            return keys;
        }

        private static List<string> BuildLabels()
        {
            var labels = new List<string>();
            for (int i = 0; i < Keys.Count; i++)
            {
                var key = Keys[i];
                if (key >= KeyCode.A && key <= KeyCode.Z)
                    labels.Add(((char)('A' + (key - KeyCode.A))).ToString());
                else if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                    labels.Add(((char)('0' + (key - KeyCode.Alpha0))).ToString());
                else
                    labels.Add("F" + (key - KeyCode.F1 + 1));
            }
            return labels;
        }

        public static int IndexOf(KeyCode key)
        {
            int index = Keys.IndexOf(key);
            return index >= 0 ? index : 0;
        }

        public static KeyCode At(int index)
        {
            if (index < 0 || index >= Keys.Count) return Keys[0];
            return Keys[index];
        }

        public static bool Contains(KeyCode key)
        {
            return Keys.Contains(key);
        }
    }

    internal static class HotkeySettings
    {
        private const string ModifierKey = "CompactSort.Modifier";
        private const string LockKeyPref = "CompactSort.LockKey";

        private static int _modifier = -1;
        private static KeyCode _lock = KeyCode.None;

        public static IList<string> ModifierLabels
        {
            get { return new List<string> { "Ctrl", "Alt", "无" }; }
        }

        public static IList<string> KeyLabels
        {
            get { return KeyChoices.Labels; }
        }

        public static int ModifierIndex
        {
            get
            {
                if (_modifier < 0)
                    _modifier = Mathf.Clamp(PlayerPrefs.GetInt(ModifierKey, 0), 0, 2);
                return _modifier;
            }
            set
            {
                _modifier = Mathf.Clamp(value, 0, 2);
                PlayerPrefs.SetInt(ModifierKey, _modifier);
                PlayerPrefs.Save();
            }
        }

        public static KeyCode LockKey
        {
            get
            {
                if (_lock == KeyCode.None) _lock = LoadKey(LockKeyPref, KeyCode.Q);
                return _lock;
            }
            set
            {
                _lock = KeyChoices.Contains(value) ? value : KeyCode.Q;
                PlayerPrefs.SetInt(LockKeyPref, (int)_lock);
                PlayerPrefs.Save();
            }
        }

        public static int LockKeyIndex
        {
            get { return KeyChoices.IndexOf(LockKey); }
            set { LockKey = KeyChoices.At(value); }
        }

        private static KeyCode LoadKey(string key, KeyCode fallback)
        {
            int value = PlayerPrefs.GetInt(key, (int)fallback);
            var code = (KeyCode)value;
            return KeyChoices.Contains(code) ? code : fallback;
        }

        public static bool ModifierDown
        {
            get
            {
                switch (ModifierIndex)
                {
                    case 0:
                        return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                    case 1:
                        return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                    default:
                        return true;
                }
            }
        }

        public static bool LockPressed
        {
            get { return ModifierDown && Input.GetKeyDown(LockKey); }
        }
    }

    /// <summary>Adds a native slider to the game's Gameplay settings section.</summary>
    [HarmonyPatch(typeof(GameOptionRegistry), "EnsureVanilla")]
    public static class SettingsPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Register();
        }

        public static void Register()
        {
            try
            {
                GameOptionRegistry.Add(GameOptionRegistry.SECTION_GAMEPLAY,
                    GameOptionRegistry.Slider(
                        "compact_sort_budget_ms",
                        () => "紧凑整理耗时上限",
                        () => CompactSortSettings.TimeBudgetMs,
                        delegate(float v) { CompactSortSettings.TimeBudgetMs = Mathf.RoundToInt(v); },
                        80f, 1000f, true,
                        () => "越高越可能找到更紧的布局，但每次整理卡顿更久。默认 350ms；建议 150–500ms。",
                        v => Mathf.RoundToInt(v) + " ms"));

                GameOptionRegistry.GetOrAddSection("compact_sort_keys", () => "紧凑整理锁定键");
                GameOptionRegistry.Add("compact_sort_keys",
                    GameOptionRegistry.Dropdown(
                        "compact_sort_modifier",
                        () => "快捷键修饰键",
                        () => HotkeySettings.ModifierLabels,
                        () => HotkeySettings.ModifierIndex,
                        i => HotkeySettings.ModifierIndex = i,
                        () => "Ctrl / Alt / 无；仅用于锁定排序。"));
                GameOptionRegistry.Add("compact_sort_keys",
                    GameOptionRegistry.Dropdown(
                        "compact_sort_lock_key",
                        () => "锁定排序按键",
                        () => HotkeySettings.KeyLabels,
                        () => HotkeySettings.LockKeyIndex,
                        i => HotkeySettings.LockKeyIndex = i,
                        () => "鼠标悬停物品时触发；默认 Ctrl+Q。"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CompactSort] could not add time-budget slider: " + e.Message);
            }
        }
    }
}
