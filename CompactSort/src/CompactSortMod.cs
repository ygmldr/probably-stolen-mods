using UnityEngine;

namespace CompactSort
{
    public class CompactSortMod : IMod
    {
        public static bool Enabled = true;
        public static ModManifest Manifest;

        public void Init(ModManifest manifest)
        {
            Manifest = manifest;
            Enabled = true;
            SettingsPatch.Register();
            Debug.Log("[CompactSort] loaded " + manifest.Name + " v" + manifest.ModVersion +
                      " (gap-first sorter; use the separate 'compact sort' menu entry).");
        }

        public void OnEnable()
        {
            Enabled = true;
        }

        public void OnDisable()
        {
            Enabled = false;
        }
    }
}
