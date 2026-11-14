using System;

namespace PawnMind.Core
{
    /// <summary>
    /// Read-only health payload. It intentionally contains no token, save data or
    /// hidden fields. "latest" remains UNVERIFIED because this code cannot check
    /// the Steam stable branch; an S8 user must confirm it manually.
    /// </summary>
    public sealed class BridgeHealth
    {
        public string GameVersion { get; set; }
        public string GameAssemblyMvid { get; set; }
        public string ExpectedGameVersion { get; set; }
        public string ExpectedAssemblyMvid { get; set; }
        public string WriteGateState { get; set; }
        public string WriteGateReason { get; set; }

        /// <summary>Always UNVERIFIED; this code cannot check the Steam stable branch.</summary>
        public string Latest
        {
            get { return GameVersionGate.LatestUnverified; }
        }

        public static BridgeHealth From(WriteGateDecision gate)
        {
            if (gate == null) throw new ArgumentNullException("gate");

            return new BridgeHealth
            {
                GameVersion = gate.ActualGameVersion,
                GameAssemblyMvid = gate.ActualAssemblyMvid,
                ExpectedGameVersion = gate.ExpectedGameVersion,
                ExpectedAssemblyMvid = gate.ExpectedAssemblyMvid,
                WriteGateState = gate.State,
                WriteGateReason = gate.Reason
            };
        }
    }
}
