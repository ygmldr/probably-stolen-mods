using System;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;
using PawnMind.Game;
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
    private static bool _recordingArmed;
    private static NewGameMenuService _newGameService;
    private static RecordingSaveService _recordingService;
    private static RecordingDispatchGuard _recordingGuard;
    private static FirstWeekService _firstWeekService;
    private static ScreenshotService _screenshotService;
    private static PromptService _promptService;
    private static WindowService _windowService;
    private static NightReturnService _nightReturnService;
    private static ReportService _reportService;
    private static CalendarService _calendarService;
    private static ClienteleService _clienteleService;
    private static TutorialService _tutorialService;
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

            RecordingSettings recordingSettings = RecordingSettings.From(config);
            _recordingArmed = recordingSettings.Error == null;

            // Read-only intel services are created before the recording service
            // because sleep attaches their morning report to its response.
            _reportService = new ReportService(ReportAdapter.Instance);
            ReportService.SetCurrent(_reportService);
            _calendarService = new CalendarService(CalendarAdapter.Instance);
            CalendarService.SetCurrent(_calendarService);
            _clienteleService = new ClienteleService(ClienteleAdapter.Instance);
            _tutorialService = new TutorialService(TutorialAdapter.Instance);

            RecordingSlotPolicy recordingPolicy = new RecordingSlotPolicy(recordingSettings);
            _recordingService = new RecordingSaveService(
                recordingPolicy,
                RecordingSaveAdapter.Instance,
                RecordingSaveAdapter.Instance,
                RecordingSaveAdapter.Instance,
                NewGameMenuAdapter.Instance,
                RecordingSaveAdapter.Instance,
                _reportService);
            _recordingGuard = new RecordingDispatchGuard(_recordingService);

            // S5: one service instance owns the receipt tracker for both /state
            // and the choose_dialogue action, so one payment is counted once.
            _firstWeekService = new FirstWeekService(
                FirstWeekAdapter.Instance,
                FirstWeekAdapter.Instance,
                FirstWeekAdapter.Instance);
            FirstWeekService.SetCurrent(_firstWeekService);

            string modVersion = _manifest == null ? "unknown" : _manifest.ModVersion;
            GameIdentity gameIdentity;
            try
            {
                // OnEnable runs on the Unity main thread. Capture the identity here
                // so HTTP handling only reads cached strings, never Unity objects.
                gameIdentity = GameVersionAdapter.Read();
            }
            catch (Exception)
            {
                gameIdentity = new GameIdentity(null, null);
            }

            // Expected values come only from bridge.conf. They are never filled
            // from the installed identity, so a fresh config locks all writes.
            WriteGateDecision writeGate = GameVersionGate.Evaluate(
                gameIdentity,
                config.ExpectedGameVersion,
                config.ExpectedAssemblyMvid);
            BridgeHealth health = BridgeHealth.From(writeGate);

            Debug.Log("[PawnMind] game identity: "
                + (string.IsNullOrEmpty(gameIdentity.GameVersion) ? "UNKNOWN" : gameIdentity.GameVersion)
                + " / " + (string.IsNullOrEmpty(gameIdentity.AssemblyMvid) ? "UNKNOWN" : gameIdentity.AssemblyMvid)
                + "; write gate: " + writeGate.State
                + (string.IsNullOrEmpty(writeGate.Reason) ? string.Empty : " (" + writeGate.Reason + ")")
                + "; latest: " + GameVersionGate.LatestUnverified);

            if (!writeGate.WriteAllowed)
            {
                Debug.LogWarning("[PawnMind] write actions locked: " + writeGate.Reason
                    + ". Set expected_game_version and expected_assembly_mvid in bridge.conf only after"
                    + " confirming the Steam stable build.");
            }

            _pumpObject = new GameObject("PawnMind.Pump");
            _pumpObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_pumpObject);

            // Evidence capture shares the pump object so it runs on the same
            // main-thread tick as the bridge. Initialization cannot fail the mod.
            _screenshotService = _pumpObject.AddComponent<ScreenshotService>();
            _screenshotService.Initialize();

            // Modal prompts: a native confirmation owns the input while it is
            // open, so the bridge must be able to read it and answer it once.
            _promptService = new PromptService(PromptAdapter.Instance, PromptAdapter.Instance);
            _windowService = new WindowService(WindowAdapter.Instance);
            _nightReturnService = new NightReturnService(PromptAdapter.Instance, PromptAdapter.Instance);

            _router = new ActionRouter(
                new ActionGate(),
                delegate { return StateSnapshot.CurrentRevision; },
                DispatchAction,
                new ActionHistory(100),
                // Handle() runs inside BridgeServer.Tick from MainThreadPump.Update,
                // so refreshing the snapshot here stays on the Unity main thread.
                delegate { StateSnapshot.BuildJson(); return StateSnapshot.CurrentRevision; },
                config.Token,
                delegate { return writeGate; },
                CaptureOutcome,
                delegate(string op) { return ModePolicy.Refusal(op); });

            // S3B menu writes are evaluated by the same S4 slot policy. The
            // default (recording unconfigured) still refuses every write before
            // any native callback; only get_new_game_options is a read.
            _newGameService = new NewGameMenuService(
                NewGameMenuAdapter.Instance,
                NewGameMenuAdapter.Instance,
                delegate
                {
                    RecordingSaveService service = _recordingService;
                    return service == null
                        ? RecordingSaveErrors.ServiceUnavailable
                        : service.CheckMenuWrite();
                });

            _server = new BridgeServer(
                config,
                StateSnapshot.BuildJson,
                _router.Handle,
                delegate(string message) { Debug.LogWarning(message); },
                modVersion,
                delegate { return StateSnapshot.CurrentRevision; },
                delegate { return health; },
                new BridgeLimits(),
                // A dispatched write whose response never reached the client must
                // not stay cached as a success for the retrying agent.
                _router.MarkResponseUndelivered);

            MainThreadPump pump = _pumpObject.AddComponent<MainThreadPump>();
            pump.Initialize(_server);
            _server.Start();

            Debug.Log("[PawnMind] recording policy: "
                + (recordingSettings.Error == null ? "armed" : recordingSettings.Error)
                + "; writes still require the version gate and a bound slot.");

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
        _screenshotService = null;
        _recordingArmed = false;
        _newGameService = null;
        _promptService = null;
        _windowService = null;
        _nightReturnService = null;
        _reportService = null;
        _calendarService = null;
        _clienteleService = null;
        _tutorialService = null;
        ReportService.SetCurrent(null);
        CalendarService.SetCurrent(null);
        _recordingService = null;
        _recordingGuard = null;
        FirstWeekService.SetCurrent(null);
        _firstWeekService = null;
    }

    private static ActionResult DispatchAction(string op, JObject request)
    {
        ActionResult recordingResult;
        if (_recordingGuard != null
            && _recordingGuard.TryHandle(op, request, out recordingResult))
        {
            if (recordingResult.Ok && IsRunChangeOp(op))
            {
                FirstWeekService service = FirstWeekService.Current;
                if (service != null) service.ResetRentReceipts();
            }
            return recordingResult;
        }

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

            case "dismiss_current_client":
                return GameActions.DismissClient();

            case "choose_dialogue":
            {
                FirstWeekService service = FirstWeekService.Current;
                if (service == null)
                {
                    return ActionResult.Failure("choose_dialogue", "not_initialized");
                }
                return service.ChooseDialogue(
                    ReadInt(request, "index", -1), StateSnapshot.CurrentRevision);
            }

            case "end_day":
            {
                FirstWeekService service = FirstWeekService.Current;
                if (service == null)
                {
                    return ActionResult.Failure("end_day", "not_initialized");
                }
                return service.EndDay(
                    StateSnapshot.CurrentRevision, ReadBool(request, "dismiss_waiting", false));
            }

            case "get_new_game_options":
                return NewGameOptions();

            case "get_recording_save_status":
            {
                RecordingSaveService service = RecordingService;
                if (service == null)
                {
                    return ActionResult.Failure(
                        "get_recording_save_status", "not_initialized");
                }
                return ActionResult.SuccessRaw(
                    "get_recording_save_status", service.StatusJson());
            }

            case "open_new_game_menu":
            {
                NewGameMenuService service = NewGameService;
                if (service == null)
                {
                    return ActionResult.Failure("open_new_game_menu", "not_initialized");
                }
                return NewGameResult("open_new_game_menu", service.Open());
            }

            case "select_new_game_option":
            {
                NewGameOptionKind kind;
                if (!TryParseNewGameKind((string)request["kind"], out kind))
                {
                    return ActionResult.Failure("select_new_game_option", "bad_kind");
                }
                NewGameMenuService service = NewGameService;
                if (service == null)
                {
                    return ActionResult.Failure("select_new_game_option", "not_initialized");
                }
                return ActionResult.SuccessRaw(
                    "select_new_game_option",
                    NewGameMenuJson.SelectJson(service.Select(kind, (string)request["id"])));
            }

            case "new_game_next_page":
            {
                NewGamePageRow row;
                if (!TryParseNewGameRow((string)request["row"], out row))
                {
                    return ActionResult.Failure("new_game_next_page", "bad_row");
                }
                NewGameMenuService service = NewGameService;
                if (service == null)
                {
                    return ActionResult.Failure("new_game_next_page", "not_initialized");
                }
                return NewGameResult(
                    "new_game_next_page",
                    service.NextPage(row, ReadInt(request, "direction", 0)));
            }

            case "new_game_continue":
            {
                NewGameMenuService service = NewGameService;
                if (service == null)
                {
                    return ActionResult.Failure("new_game_continue", "not_initialized");
                }
                return NewGameResult("new_game_continue", service.Continue());
            }

            case "focus_new_game_selected_perk":
            {
                NewGameMenuService service = NewGameService;
                if (service == null)
                {
                    return ActionResult.Failure(
                        "focus_new_game_selected_perk", "not_initialized");
                }
                return NewGameResult(
                    "focus_new_game_selected_perk",
                    service.FocusSelectedPerk((string)request["id"]));
            }

            case "blur_new_game_option":
            {
                NewGameMenuService service = NewGameService;
                if (service == null)
                {
                    return ActionResult.Failure("blur_new_game_option", "not_initialized");
                }
                return NewGameResult(
                    "blur_new_game_option",
                    service.BlurOption((string)request["id"]));
            }

            case "capture_screenshot":
            {
                ScreenshotService service = _screenshotService;
                if (service == null)
                {
                    return ActionResult.Failure("capture_screenshot", "screenshot_unavailable");
                }

                long cash;
                int day = ReadDayCash(out cash);
                string reason = ReadString(request, "reason", "manual");
                ScreenshotRequest capture = service.Request(
                    reason, "ok", StateSnapshot.CurrentRevision, day, cash);
                return ActionResult.SuccessRaw(
                    "capture_screenshot",
                    ScreenshotJson.CaptureResult(capture, StateSnapshot.CurrentRevision));
            }

            case "list_screenshots":
            {
                ScreenshotService service = _screenshotService;
                if (service == null)
                {
                    return ActionResult.Failure("list_screenshots", "screenshot_unavailable");
                }

                return ActionResult.SuccessRaw(
                    "list_screenshots",
                    ScreenshotJson.ListResult(service.Ledger, ReadInt(request, "limit", 20)));
            }

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
            {
                NightReturnService service = _nightReturnService;
                if (service == null)
                {
                    return ActionResult.Failure("return_home", "not_initialized");
                }
                return service.ReturnHome();
            }

            case "get_day_report":
                if (_reportService == null)
                {
                    return ActionResult.Failure("get_day_report", "not_initialized");
                }
                return ActionResult.SuccessRaw(
                    "get_day_report", _reportService.Json(DayReportRules.MaxLines));

            case "get_calendar":
                if (_calendarService == null)
                {
                    return ActionResult.Failure("get_calendar", "not_initialized");
                }
                return ActionResult.SuccessRaw(
                    "get_calendar",
                    _calendarService.Json(ReadInt(request, "days", CalendarRules.DefaultDays)));

            case "get_clientele":
                if (_clienteleService == null)
                {
                    return ActionResult.Failure("get_clientele", "not_initialized");
                }
                return ActionResult.SuccessRaw("get_clientele", _clienteleService.Json());

            case "dismiss_tutorial_hints":
                if (_tutorialService == null)
                {
                    return ActionResult.Failure("dismiss_tutorial_hints", "not_initialized");
                }
                return _tutorialService.Dismiss();

            case "get_pending_prompt":
            {
                PromptService service = _promptService;
                if (service == null)
                {
                    return ActionResult.Failure("get_pending_prompt", "not_initialized");
                }
                return service.GetPending();
            }

            case "close_windows":
            {
                WindowService service = _windowService;
                if (service == null)
                {
                    return ActionResult.Failure("close_windows", "not_initialized");
                }
                return service.Close();
            }

            case "answer_prompt":
            {
                PromptService service = _promptService;
                if (service == null)
                {
                    return ActionResult.Failure("answer_prompt", "not_initialized");
                }
                return service.AnswerPrompt((string)request["choice"]);
            }

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

            case "contraband_status":
                return ActionResult.SuccessRaw("contraband_status", ContrabandService.StatusJson());

            case "security_status":
                return ActionResult.SuccessRaw("security_status", SecurityService.StatusJson());

            case "list_services":
                return ActionResult.SuccessRaw("list_services", ServiceStatusService.ListServicesJson());

            case "toggle_service":
                return ActionResult.SuccessRaw("toggle_service", ServiceStatusService.ToggleServiceJson(
                    (string)request["id"],
                    request["on"] == null || (bool)request["on"]));

            case "husbandry_status":
                return ActionResult.SuccessRaw("husbandry_status", ServiceStatusService.HusbandryJson());

            case "debug_unlock_expedition":
                return ActionResult.SuccessRaw("debug_unlock_expedition", ExpeditionService.UnlockJson(_allowTestItems));

            case "dispatch_expedition":
                return ActionResult.SuccessRaw("dispatch_expedition", ExpeditionService.DispatchJson(
                    (string)request["hireling_id"],
                    (string)request["location_id"]));

            case "advance_expedition":
                return ActionResult.SuccessRaw("advance_expedition", ExpeditionService.AdvanceJson(
                    (string)request["expedition_id"]));

            case "expedition_status":
                return ActionResult.SuccessRaw("expedition_status", WorldStatusService.ExpeditionJson());

            case "events_status":
                return ActionResult.SuccessRaw("events_status", WorldStatusService.EventsJson());

            case "list_signs":
                return ActionResult.SuccessRaw("list_signs", WorldStatusService.SignsJson());

            case "deliver_request":
                return ActionResult.SuccessRaw("deliver_request", P9ActionService.DeliverRequestJson(
                    (string)request["id"]));

            case "report_client":
                return ActionResult.SuccessRaw("report_client", P9ActionService.ReportClientJson());

            case "barter_accept":
                return ActionResult.SuccessRaw("barter_accept", P9ActionService.BarterAcceptJson());

            case "arrest_wanted":
                return ActionResult.SuccessRaw("arrest_wanted", P9ActionService.ArrestWantedJson(
                    (string)request["id"]));

            case "list_factions":
                return ActionResult.SuccessRaw("list_factions", PlayerStatusService.FactionsJson());

            case "health_status":
                return ActionResult.SuccessRaw("health_status", PlayerStatusService.HealthJson());

            case "list_requests":
                return ActionResult.SuccessRaw("list_requests", PlayerStatusService.RequestsJson());

            case "wanted_status":
                return ActionResult.SuccessRaw("wanted_status", PlayerStatusService.WantedJson());

            case "barter_status":
                return ActionResult.SuccessRaw("barter_status", PlayerStatusService.BarterJson());

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
                    IsTrue(request["include_hidden"]), _allowTestItems, _recordingArmed));

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

    private static NewGameMenuService NewGameService
    {
        get { return _newGameService; }
    }

    private static RecordingSaveService RecordingService
    {
        get { return _recordingService; }
    }

    private static ActionResult NewGameOptions()
    {
        NewGameMenuService service = NewGameService;
        if (service == null) return ActionResult.Failure("get_new_game_options", "not_initialized");

        try
        {
            return ActionResult.SuccessRaw(
                "get_new_game_options",
                NewGameMenuJson.OptionsJson(service.GetOptions()));
        }
        catch (Exception)
        {
            return ActionResult.Failure("get_new_game_options", "new_game_read_failed");
        }
    }

    private static ActionResult NewGameResult(string op, NewGameMenuActionResult result)
    {
        return ActionResult.SuccessRaw(op, NewGameMenuJson.ActionResultJson(op, result));
    }

    private static bool TryParseNewGameKind(string value, out NewGameOptionKind kind)
    {
        kind = NewGameOptionKind.Scenario;
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "scenario": kind = NewGameOptionKind.Scenario; return true;
            case "perk": kind = NewGameOptionKind.Perk; return true;
            case "difficulty": kind = NewGameOptionKind.Difficulty; return true;
            default: return false;
        }
    }

    private static bool TryParseNewGameRow(string value, out NewGamePageRow row)
    {
        row = NewGamePageRow.Available;
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "available": row = NewGamePageRow.Available; return true;
            case "selected": row = NewGamePageRow.Selected; return true;
            default: return false;
        }
    }

    private static bool IsRunChangeOp(string op)
    {
        return op == "load_game"
            || op == "load_recording_game"
            || op == "new_game"
            || op == "confirm_new_game";
    }

    private static void CaptureOutcome(ActionOutcome outcome)
    {
        ScreenshotService service = _screenshotService;
        if (service == null || outcome == null) return;

        try
        {
            long cash;
            int day = ReadDayCash(out cash);
            string op = string.IsNullOrEmpty(outcome.Op) ? "unknown" : outcome.Op;
            string result = outcome.Ok
                ? "ok"
                : (string.IsNullOrEmpty(outcome.Error) ? "unknown" : outcome.Error);
            service.Request(op, result, outcome.Revision, day, cash);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[PawnMind] evidence hook failed: " + e.GetType().Name);
        }
    }

    private static int ReadDayCash(out long cash)
    {
        cash = 0;
        try
        {
            if (!GameState.IsInGame) return 0;
            cash = GameState.GetCash();
            return GameState.GetDay();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string ReadString(JObject request, string name, string fallback)
    {
        JToken token = request[name];
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            return fallback;

        try
        {
            return token.Type == JTokenType.String ? (string)token : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static bool IsTrue(JToken token)
    {
        return token != null && token.Type == JTokenType.Boolean && (bool)token;
    }

    private static bool ReadBool(JObject request, string name, bool fallback)
    {
        JToken token = request[name];
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            return fallback;

        try
        {
            return token.Type == JTokenType.Boolean ? (bool)token : fallback;
        }
        catch (Exception)
        {
            return fallback;
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
