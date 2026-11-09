using PawnMind.Core;
using UnityEngine;

namespace PawnMind.Bridge
{
    /// <summary>
    /// Drives the main-thread HTTP server from Unity's Update loop.
    /// No threads, tasks or coroutines are created.
    /// </summary>
    public sealed class MainThreadPump : MonoBehaviour
    {
        private readonly HotkeyLatch _debugLatch = new HotkeyLatch(0.4);
        private BridgeServer _server;

        public void Initialize(BridgeServer server)
        {
            _server = server;
        }

        private void Update()
        {
            if (_server != null) _server.Tick();

            bool down = Input.GetKeyDown(KeyCode.F7);
            if (!_debugLatch.TryFire(down, Time.unscaledTime)) return;

            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                Diagnostics.DumpState();
            }
            else
            {
                Diagnostics.Pulse();
            }
        }
    }
}
