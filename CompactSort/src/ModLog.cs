using System;

namespace CompactSort
{
    /// <summary>
    /// Thin logging seam. Runtime code keeps using Unity's console through the default
    /// delegates; tests can replace them with a no-op sink and assert on collected lines
    /// without starting Unity.
    /// </summary>
    internal static class ModLog
    {
        internal static Action<string> Info = UnityEngine.Debug.Log;
        internal static Action<string> Warn = UnityEngine.Debug.LogWarning;
        internal static Action<string> Error = UnityEngine.Debug.LogError;

        internal static void I(string message)
        {
            var sink = Info;
            if (sink != null) sink(message);
        }

        internal static void W(string message)
        {
            var sink = Warn;
            if (sink != null) sink(message);
        }

        internal static void E(string message)
        {
            var sink = Error;
            if (sink != null) sink(message);
        }
    }
}
