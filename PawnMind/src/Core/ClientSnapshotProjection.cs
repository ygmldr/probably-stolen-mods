using System.Collections.Generic;

namespace PawnMind.Core
{
    /// <summary>
    /// The complete set of current-client values the read-only snapshot may read.
    /// Every member is a plain field (or a field getter). StoreClient.CanReport()
    /// is deliberately absent: it calls BargainUIManager.IsCurrentClientCoerced(),
    /// whose first line is Refresh(), which can reset the per-client bargain flags
    /// when the tracked client changes.
    /// </summary>
    public interface IClientSnapshotSource
    {
        string DisplayName { get; }
        string Intent { get; }
        bool UsesBudget { get; }
        int Impatience { get; }
        bool IsWanted { get; }
        int ReportReward { get; }
        bool Reported { get; }
        string WantedPosterId { get; }
        bool BargainAvailable { get; }
        string Faction { get; }
        int Budget { get; }
    }

    /// <summary>
    /// Pure projection for the snapshot's `client` section. It sees only
    /// <see cref="IClientSnapshotSource"/>, so no Refresh()-backed getter can be
    /// reached from here; the fake in SnapshotPurityTests proves a build never
    /// probes an impure source and the source drift test keeps the Bridge adapter
    /// on the same contract.
    /// </summary>
    public static class ClientSnapshotProjection
    {
        public static Dictionary<string, object> Describe(IClientSnapshotSource source)
        {
            if (source == null) return null;

            return new Dictionary<string, object>
            {
                { "name", source.DisplayName },
                { "intent", source.Intent },
                // S6A: the private wallet is not shown by the game; only the
                // negotiation budget is.
                { "uses_budget", source.UsesBudget },
                { "impatience", source.Impatience },
                { "is_wanted", source.IsWanted },
                { "report_reward", source.ReportReward },
                { "reported", source.Reported },
                { "wanted_poster_id", source.WantedPosterId },
                { "bargain_available", source.BargainAvailable },
                { "faction", source.Faction },
                { "budget", source.Budget },
            };
        }
    }
}
