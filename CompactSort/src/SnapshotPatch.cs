using HarmonyLib;

namespace CompactSort
{
    /// <summary>
    /// One read-only snapshot after each in-game sleep. StoreStation.StartDay is the
    /// morning transition; the dump is written next to Player.log.
    /// </summary>
    [HarmonyPatch(typeof(StoreStation), "StartDay")]
    public static class SnapshotPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            SnapshotDumper.Capture("day-start");
        }
    }
}
