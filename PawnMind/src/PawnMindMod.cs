using System;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;
using UnityEngine;

/// <summary>
/// M0 entry point: start a localhost HTTP bridge and answer /health and /state.
/// Gameplay actions arrive in M1.
/// </summary>
public sealed class PawnMindMod : IMod
{
    private const string ModId = "pawnmind";
    private const string ConfigFile = "bridge.conf";

    private static ModManifest _manifest;
    private BridgeServer _server;
    private ActionRouter _router;
    private GameObject _pumpObject;

    public void Init(ModManifest manifest)
    {
        _manifest = manifest;
        Debug.Log("[PawnMind] loaded " + manifest.Name + " v" + manifest.ModVersion);
    }

    public void OnEnable()
    {
        try
        {
            BridgeConfig config = LoadOrCreateConfig();

            _pumpObject = new GameObject("PawnMind.Pump");
            _pumpObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_pumpObject);

            _router = new ActionRouter(
                new ActionGate(),
                delegate { return StateSnapshot.CurrentRevision; },
                DispatchAction,
                new ActionHistory(100));

            _server = new BridgeServer(
                config,
                StateSnapshot.BuildJson,
                _router.Handle,
                delegate(string message) { Debug.LogWarning(message); },
                _manifest == null ? "unknown" : _manifest.ModVersion);

            MainThreadPump pump = _pumpObject.AddComponent<MainThreadPump>();
            pump.Initialize(_server);
            _server.Start();

            Debug.Log("[PawnMind] listening on http://127.0.0.1:" + config.Port
                + "/ (config: ModData/" + ModId + "/" + ConfigFile + ")");
        }
        catch (Exception e)
        {
            Debug.LogError("[PawnMind] failed to start bridge: " + e);
        }
    }

    public void OnDisable()
    {
        try
        {
            if (_server != null) _server.Stop();
        }
        catch
        {
        }

        _server = null;

        try
        {
            if (_pumpObject != null) UnityEngine.Object.Destroy(_pumpObject);
        }
        catch
        {
        }

        _pumpObject = null;
    }

    private static ActionResult DispatchAction(string op, JObject request)
    {
        if (op == "act")
        {
            string inner = (string)request["action"];
            if (string.IsNullOrEmpty(inner) || inner == "act")
                return ActionResult.Failure("act", "missing_action");

            JObject args = request["args"] as JObject;
            return DispatchAction(inner, args ?? new JObject());
        }

        switch (op)
        {
            case "open_shop":
                return GameActions.OpenShop();

            case "call_next_customer":
                return GameActions.CallNextCustomer();

            case "advance_dialogue":
                return GameActions.AdvanceDialogue();

            case "choose_dialogue":
                return GameActions.ChooseDialogue(ReadInt(request, "index", -1));

            case "end_day":
                return GameActions.EndDay();

            case "new_game":
                return GameActions.NewGame();

            case "load_game":
                return GameActions.LoadGame(ReadInt(request, "slot", -1));

            case "sleep":
                return GameActions.Sleep();

            case "debug_pulse":
                Diagnostics.Pulse();
                return ActionResult.Success("debug_pulse");

            case "debug_dump":
                Diagnostics.DumpState();
                return ActionResult.Success("debug_dump");

            case "list_containers":
                return ActionResult.SuccessRaw("list_containers", InventoryService.ListContainersJson());

            case "list_inventory":
                return ActionResult.SuccessRaw("list_inventory", InventoryService.ListJson(
                    (string)request["container"],
                    (string)request["search"],
                    ReadInt(request, "page", 1),
                    ReadInt(request, "page_size", 20)));

            case "move_item":
                int turns = ReadInt(request, "turns", -1);
                if (turns < 0)
                {
                    turns = request["rotate"] != null && (bool)request["rotate"] ? 1 : 0;
                }

                return GameActions.MoveItem(
                    ReadInt(request, "item_id", -1),
                    (string)request["target"],
                    ReadInt(request, "x", -1),
                    ReadInt(request, "y", -1),
                    turns);

            case "get_layout":
                return ActionResult.SuccessRaw("get_layout", InventoryService.LayoutJson((string)request["container"]));

            case "get_item":
                return ActionResult.SuccessRaw("get_item", InventoryService.GetItemJson(
                    ReadInt(request, "item_id", -1)));

            case "get_ui_snapshot":
                return ActionResult.SuccessRaw("get_ui_snapshot", UiSnapshot.BuildJson());

            case "press":
                return UiSnapshot.Press((string)request["control_id"]);

            case "save_game":
                return GameActions.SaveGame();

            case "confirm_trade":
                return GameActions.ConfirmTrade(
                    ReadInt(request, "item_id", -1),
                    (string)request["side"]);

            case "bargain":
                return GameActions.Bargain(
                    (string)request["direction"],
                    ReadInt(request, "percent", 0));

            default:
                return ActionResult.Failure(op, "unknown_op");
        }
    }

    private static int ReadInt(JObject request, string name, int fallback)
    {
        JToken token = request[name];
        if (token == null) return fallback;

        try
        {
            return (int)token;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static BridgeConfig LoadOrCreateConfig()
    {
        string text = null;
        try
        {
            text = ModHelper.ReadModDataFileText(ModId, ConfigFile);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[PawnMind] could not read bridge config: " + e.Message);
        }

        if (!string.IsNullOrEmpty(text))
        {
            BridgeConfig parsed;
            string error;
            if (BridgeConfig.TryParse(text, out parsed, out error)) return parsed;
            Debug.LogWarning("[PawnMind] invalid bridge config, regenerating: " + error);
        }

        string token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        BridgeConfig config = BridgeConfig.Create(BridgeConfig.DefaultPort, token);

        try
        {
            ModHelper.WriteModDataFileText(ModId, ConfigFile, config.Serialize());
        }
        catch (Exception e)
        {
            Debug.LogError("[PawnMind] could not persist bridge config: " + e.Message);
        }

        return config;
    }
}
