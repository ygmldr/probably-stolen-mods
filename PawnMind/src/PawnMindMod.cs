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
    private static bool _allowTestItems;
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
            _allowTestItems = config.AllowTestItems;

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
                _manifest == null ? "unknown" : _manifest.ModVersion,
                delegate { return StateSnapshot.CurrentRevision; });

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

            case "list_locations":
                return ActionResult.SuccessRaw("list_locations", NightService.ListLocationsJson());

            case "go_out":
                return ActionResult.SuccessRaw("go_out", NightService.GoOutJson((string)request["location"]));

            case "scavenge":
                return ActionResult.SuccessRaw("scavenge", NightService.ScavengeJson());

            case "return_home":
                return ActionResult.SuccessRaw("return_home", NightService.ReturnHomeJson());

            case "buy_wholesale":
                return GameActions.BuyWholesale();

            case "commissary_buy":
                return GameActions.BuyCommissary((string)request["identifier"]);

            case "list_goods":
                return ActionResult.SuccessRaw("list_goods", GoodsService.ListJson((string)request["scope"]));

            case "debug_fill_water":
                return ActionResult.SuccessRaw("debug_fill_water", DebugService.FillWaterJson(
                    _allowTestItems,
                    ReadInt(request, "item_id", -1)));

            case "debug_grant":
                return ActionResult.SuccessRaw("debug_grant", DebugService.GrantJson(
                    _allowTestItems,
                    (string)request["identifier"],
                    ReadInt(request, "count", 1)));

            case "list_wine":
                return ActionResult.SuccessRaw("list_wine", WineService.ListJson());

            case "wine_add_water":
                return ActionResult.SuccessRaw("wine_add_water", WineService.AddWaterJson(
                    ReadInt(request, "item_id", -1),
                    ReadInt(request, "ml", 750)));

            case "wine_add_ingredient":
                return ActionResult.SuccessRaw("wine_add_ingredient", WineService.AddIngredientJson(
                    ReadInt(request, "bottle_id", -1),
                    ReadInt(request, "item_id", -1)));

            case "wine_status":
                return ActionResult.SuccessRaw("wine_status", WineService.StatusJson(
                    ReadInt(request, "item_id", -1)));

            case "start_fermentation":
                return ActionResult.SuccessRaw("start_fermentation", WineService.StartJson(
                    ReadInt(request, "item_id", -1)));

            case "list_machines":
                return ActionResult.SuccessRaw("list_machines", MachineService.ListJson());

            case "machine_grow_status":
                return ActionResult.SuccessRaw("machine_grow_status", MachineService.GrowStatusJson(
                    ReadInt(request, "machine_id", -1)));

            case "machine_status":
                return ActionResult.SuccessRaw("machine_status", MachineService.StatusJson(
                    ReadInt(request, "machine_id", -1)));

            case "machine_insert":
                return ActionResult.SuccessRaw("machine_insert", MachineService.InsertJson(
                    ReadInt(request, "machine_id", -1),
                    ReadInt(request, "slot", -1),
                    ReadInt(request, "item_id", -1)));

            case "machine_collect":
                return ActionResult.SuccessRaw("machine_collect", MachineService.CollectJson(
                    ReadInt(request, "machine_id", -1),
                    ReadInt(request, "slot", -1)));

            case "list_upgrades":
                return ActionResult.SuccessRaw("list_upgrades", UpgradeService.ListJson());

            case "buy_upgrade":
                return ActionResult.SuccessRaw("buy_upgrade", UpgradeService.BuyJson(
                    (string)request["id"]));

            case "lottery_status":
                return ActionResult.SuccessRaw("lottery_status", LotteryService.StatusJson());

            case "list_tickets":
                return ActionResult.SuccessRaw("list_tickets", LotteryService.TicketsJson());

            case "inspect_ticket":
                return ActionResult.SuccessRaw("inspect_ticket", LotteryService.InspectJson(
                    ReadInt(request, "item_id", -1)));

            case "redeem_ticket":
                return ActionResult.SuccessRaw("redeem_ticket", LotteryService.RedeemJson(
                    ReadInt(request, "item_id", -1)));

            case "list_contacts":
                return ActionResult.SuccessRaw("list_contacts", PhoneService.ListJson(
                    request["include_hidden"] != null && (bool)request["include_hidden"]));

            case "call":
                return ActionResult.SuccessRaw("call", PhoneService.CallJson(
                    (long)request["number"]));

            case "phone_state":
                return ActionResult.SuccessRaw("phone_state", PhoneService.StateJson());

            case "phone_advance":
                return ActionResult.SuccessRaw("phone_advance", PhoneService.AdvanceJson());

            case "phone_choose":
                return ActionResult.SuccessRaw("phone_choose", PhoneService.ChooseJson(
                    ReadInt(request, "index", -1)));

            case "end_call":
                return ActionResult.SuccessRaw("end_call", PhoneService.EndJson());

            case "list_labels":
                return ActionResult.SuccessRaw("list_labels", LabelService.ListJson(
                    ReadInt(request, "item_id", -1)));

            case "set_label":
                return ActionResult.SuccessRaw("set_label", LabelService.SetJson(
                    ReadInt(request, "item_id", -1),
                    (string)request["category"],
                    (string)request["condition_id"]));

            case "list_analyzers":
                return ActionResult.SuccessRaw("list_analyzers", AnalyzerService.ListJson());

            case "analyze_item":
                return ActionResult.SuccessRaw("analyze_item", AnalyzerService.AnalyzeJson(
                    ReadInt(request, "analyzer_id", -1),
                    ReadInt(request, "target_id", -1)));

            case "set_pen_mode":
                return ActionResult.SuccessRaw("set_pen_mode", MarkingPenService.SetModeJson(
                    ReadInt(request, "pen_id", -1),
                    ReadInt(request, "mode", -1)));

            case "list_marks":
                return ActionResult.SuccessRaw("list_marks", MarkingPenService.ListMarksJson());

            case "list_interactions":
                return ActionResult.SuccessRaw("list_interactions", InteractionService.ListJson(
                    ReadInt(request, "item_id", -1)));

            case "activate_item":
                return InteractionService.Activate(ReadInt(request, "item_id", -1));

            case "toggle_item":
                return InteractionService.Toggle(ReadInt(request, "item_id", -1));

            case "use_tool":
                return InteractionService.Use(
                    ReadInt(request, "tool_id", -1),
                    ReadInt(request, "target_id", -1));

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

            case "list_books":
                return ActionResult.SuccessRaw("list_books", BookService.ListJson());

            case "read_book":
                return ActionResult.SuccessRaw("read_book", BookService.ReadJson(
                    (string)request["book_id"], ReadInt(request, "page", -1)));

            case "read_item":
                return ActionResult.SuccessRaw("read_item", BookService.ReadItemJson(
                    ReadInt(request, "item_id", -1)));

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

            case "bargain_preview":
                return ActionResult.SuccessRaw("bargain_preview", BargainService.PreviewJson(
                    (string)request["direction"], ReadInt(request, "percent", 0)));

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
