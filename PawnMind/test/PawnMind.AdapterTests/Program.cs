using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace PawnMind.AdapterTests
{
    /// <summary>
    /// Contract test against the installed game assembly. It does not run the mod:
    /// it fails when a member the bridge relies on disappears or changes shape.
    ///
    /// The target Managed directory can be set with PAWNMIND_GAME_MANAGED, or the
    /// assembly itself with PAWNMIND_GAME_ASSEMBLY. REQUIRE_GAME=1 (exported by
    /// test.sh as PAWNMIND_REQUIRE_GAME=1) turns a missing DLL into a hard failure.
    /// </summary>
    internal static class Program
    {
        private const string DefaultGameAssembly =
            @"D:\Program Files\steam\steamapps\common\Probably Stolen\Probably Stolen_Data\Managed\Assembly-CSharp.dll";

        private const BindingFlags AllMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static int _failures;
        private static int _checks;

        private static int Main(string[] args)
        {
            if (HasArg(args, "--contract-self-test"))
            {
                return RunContractSelfTest();
            }

            string assemblyPath = ResolveGameAssembly(args);
            string requireOption = GetOptionValue(args, "--pawnmind-require-game");
            bool requireGame = requireOption != null
                ? IsTruthy(requireOption)
                : (HasArg(args, "--pawnmind-require-game")
                    || IsTruthy(Environment.GetEnvironmentVariable("PAWNMIND_REQUIRE_GAME")));

            if (!File.Exists(assemblyPath))
            {
                if (requireGame)
                {
                    Console.WriteLine("FAIL adapter tests: Assembly-CSharp.dll not found at");
                    Console.WriteLine("  " + assemblyPath);
                    Console.WriteLine("REQUIRE_GAME=1: refusing to skip game contract checks");
                    return 2;
                }

                Console.WriteLine("SKIP adapter tests: Assembly-CSharp.dll not found at");
                Console.WriteLine("  " + assemblyPath);
                return 0;
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(assemblyPath);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL adapter tests: cannot load game assembly");
                Console.WriteLine("  " + assemblyPath);
                Console.WriteLine("  " + e.GetType().Name + ": " + e.Message);
                return 2;
            }

            PrintGameIdentity(assemblyPath, assembly, requireGame);
            RegisterContracts(assembly);
            return EvaluateContractRun();
        }

        private static int EvaluateContractRun()
        {
            Console.WriteLine();
            Console.WriteLine("contract checks: " + _checks);
            if (_checks == 0)
            {
                Console.WriteLine("FAIL adapter contract: 0 checks executed");
                return 1;
            }

            Console.WriteLine(_failures == 0 ? "adapter contract OK" : "adapter failures: " + _failures);
            return _failures == 0 ? 0 : 1;
        }

        private static void RegisterContracts(Assembly assembly)
        {
            CheckType(assembly, "PlayerStore");
            CheckMethod(assembly, "PlayerStore", "GetCash");
            CheckMethod(assembly, "PlayerStore", "GetCurrentlyNegociatedItem");
            CheckMethod(assembly, "PlayerStore", "TryCallNextClient");
            CheckMethod(assembly, "PlayerStore", "CanNextClient");
            CheckMethod(assembly, "PlayerStore", "DismissCurrentClient");
            CheckMethod(assembly, "PlayerStore", "OpenShutter");
            CheckMethod(assembly, "PlayerStore", "BeginDay");
            CheckMethod(assembly, "PlayerStore", "EndDay");
            CheckMethod(assembly, "PlayerStore", "SaveGame");
            CheckField(assembly, "PlayerStore", "currentClientInstance");
            CheckField(assembly, "PlayerStore", "currentNegociatedItem");
            CheckField(assembly, "PlayerStore", "dayUntilRent");

            CheckType(assembly, "StoreClientInstance");
            CheckField(assembly, "StoreClientInstance", "storeClient");
            CheckType(assembly, "StoreClient");
            CheckField(assembly, "StoreClient", "displayName");
            CheckField(assembly, "StoreClient", "clientCash");
            CheckField(assembly, "StoreClient", "useClientBudget");
            CheckField(assembly, "StoreClient", "clientIntent");
            CheckField(assembly, "StoreClient", "clientImpatience");
            CheckField(assembly, "StoreClient", "isBargainAvailable");
            CheckField(assembly, "StoreClient", "isWanted");
            CheckField(assembly, "StoreClient", "isAug");

            CheckType(assembly, "BargainUIManager");
            CheckMethod(assembly, "BargainUIManager", "OfferDiscount");
            CheckMethod(assembly, "BargainUIManager", "OfferMarkup");
            CheckMethod(assembly, "BargainUIManager", "OfferBuyingMarkup");
            CheckMethod(assembly, "BargainUIManager", "DemandBuyingDiscount");
            CheckMethod(assembly, "BargainUIManager", "GetBargainSuccessChance");
            CheckMethod(assembly, "BargainUIManager", "GetBargainLeaveChance");
            CheckMethod(assembly, "BargainUIManager", "GetPreBargainValue");

            CheckType(assembly, "GameItem");
            CheckMethod(assembly, "GameItem", "GetCurrentValue");
            CheckMethod(assembly, "GameItem", "GetNegociatedValue");
            CheckProperty(assembly, "GameItem", "name");
            CheckProperty(assembly, "GameItem", "identifier");
            CheckProperty(assembly, "GameItem", "unitValue");
            CheckProperty(assembly, "GameItem", "unitCount");

            CheckType(assembly, "EmporiumEntry");
            CheckProperty(assembly, "EmporiumEntry", "backInvinvElement");
            CheckMethod(assembly, "GameItem", "GetUniqueID");
            CheckMethod(assembly, "GeneralHelper", "IsItemOwned");
            CheckType(assembly, "NegociationUIManager");
            CheckMethod(assembly, "NegociationUIManager", "UpdateUI");
            CheckMethod(assembly, "PlayerStore", "CanBuyItemFromTable");
            CheckMethod(assembly, "PlayerStore", "BuyItemFromTable");
            CheckMethod(assembly, "PlayerStore", "CanSellItemFromTable");
            CheckMethod(assembly, "PlayerStore", "SellItemFromTable");

            // R4-WIN: close_windows also closes CustomUIManager windows (ESC parity).
            CheckType(assembly, "CustomUIManager");
            CheckMethod(assembly, "CustomUIManager", "CloseAllWindows");
            CheckMethod(assembly, "CustomUIManager", "GetLayerRoot");

            CheckType(assembly, "EndOfDayUIManager");
            CheckMethod(assembly, "EndOfDayUIManager", "OnEndDayButtonClick");

            CheckType(assembly, "SaveFiles");
            CheckMethod(assembly, "SaveFiles", "IndexLoad");
            CheckMethod(assembly, "SaveUIManager", "GetPreview");
            CheckType(assembly, "SavePreviewData");
            CheckField(assembly, "NewGameData", "saveSlot");
            CheckField(assembly, "NewGameData", "isLoadFromMainMenu");
            CheckMethod(assembly, "MainMenuUIController", "StartNewGameClick");
            CheckField(assembly, "EmporiumEntry", "afterhourInventory");
            CheckProperty(assembly, "EmporiumEntry", "trashcanInvElement");
            CheckMethod(assembly, "ItemPinHelper", "IsPinned");
            CheckMethod(assembly, "GameInventory", "TryAcceptAll");
            CheckMethod(assembly, "GridShapeBuilder", "SetOrientation");
            CheckMethod(assembly, "GridShapeBuilder", "SetPosition");
            CheckMethod(assembly, "GridShapeBuilder", "SetTransform");
            CheckMethod(assembly, "GameGridInventory", "UncheckedAccept");
            CheckProperty(assembly, "GameGridInventory", "inventoryShape");
            CheckMethod(assembly, "GameInventory", "IsInsertLocked");
            CheckProperty(assembly, "GameItem", "parentInventory");
            CheckProperty(assembly, "GameItem", "modifiedShape");
            CheckMethod(assembly, "StoreBookData", "GetBookById");
            CheckMethod(assembly, "PaperBookRegistry", "Resolve");
            CheckProperty(assembly, "PaperBookRegistry", "All");
            CheckMethod(assembly, "GameItem", "IsTag");
            CheckMethod(assembly, "GameItem", "CanActivateSlotItem");
            CheckMethod(assembly, "GameItem", "ActivateSlotItem");
            CheckMethod(assembly, "GameItem", "CanToggleSlotItem");
            CheckMethod(assembly, "GameItem", "ToggleSlotItem");
            CheckMethod(assembly, "GameItem", "CanTarget");
            CheckMethod(assembly, "GameItem", "Target");
            CheckMethod(assembly, "AfterhourLocation", "Get");
            CheckMethod(assembly, "AfterhourLocation", "GetDiscovered");
            CheckMethod(assembly, "MapUIManager", "OpenUI");
            CheckMethod(assembly, "MapUIManager", "Travel");
            CheckMethod(assembly, "MapUIManager", "Return");
            CheckMethod(assembly, "ScavHelper", "ScavengeDumpingGrounds");
            CheckMethod(assembly, "ScavHelper", "CanScavenge");
            CheckProperty(assembly, "MapUIManager", "IsOutside");
            CheckProperty(assembly, "MapUIManager", "Current");
            CheckField(assembly, "PlayerStore", "commissaryData");
            CheckMethod(assembly, "CommissaryUIManager", "CanBuy");
            CheckMethod(assembly, "MachineHelper", "GetBodyStorage");
            CheckMethod(assembly, "MachineHelper", "GetSlot");
            CheckMethod(assembly, "MachineProgressHelper", "GetMachineState");
            CheckMethod(assembly, "ContrabandHelper", "GetContrabandLevelInInt");
            CheckMethod(assembly, "ContrabandHelper", "IsContraband");
            CheckMethod(assembly, "StolenHelper", "IsStolenItem");
            CheckField(assembly, "PlayerStore", "secData");
            CheckField(assembly, "SecData", "evidenceLevel");
            CheckField(assembly, "SecData", "crimeTypes");
            CheckMethod(assembly, "StoreService", "FindServiceByID");
            CheckField(assembly, "PlayerStore", "storeService");
            CheckMethod(assembly, "ExpeditionData", "UnlockExpedition");
            CheckMethod(assembly, "ExpeditionData", "CreateExpedition");
            CheckMethod(assembly, "Expedition", "ContinueExpedition");
            CheckMethod(assembly, "Expedition", "CanContinue");
            CheckMethod(assembly, "Hireling", "IsInjured");
            CheckMethod(assembly, "StoreEventManager", "GetActiveEvents");
            CheckField(assembly, "PlayerStore", "expeditionData");
            CheckMethod(assembly, "StoreClient", "CanReport");
            CheckMethod(assembly, "PlayerStore", "OnCustomerReported");
            CheckMethod(assembly, "StoreRequest", "FillRequest");
            CheckMethod(assembly, "BarterOffer", "OnBarterAccepted");
            CheckMethod(assembly, "WantedUIManager", "OnArrested");
            CheckMethod(assembly, "StoreReputation", "GetReputationExact");
            CheckMethod(assembly, "HealthData", "IsSeriouslyWounded");

            // Intel reads: morning report, calendar, clientele, faction perks.
            CheckField(assembly, "PlayerStore", "nightLogs",
                "System.Collections.Generic.List<System.String>", false);
            CheckField(assembly, "PlayerStore", "tutorialEnable", "System.Boolean", false);
            CheckType(assembly, "StoreUIManager");
            CheckProperty(assembly, "StoreUIManager", "Instance");
            CheckField(assembly, "StoreUIManager", "tutorialGroup", "UnityEngine.GameObject", false);
            CheckType(assembly, "TutorialUIManager");
            CheckProperty(assembly, "TutorialUIManager", "Instance");
            CheckMethod(assembly, "TutorialUIManager", "CloseAllTutorial");
            CheckType(assembly, "StartOfDayUIManager");
            CheckProperty(assembly, "StartOfDayUIManager", "Instance");
            CheckField(assembly, "StartOfDayUIManager", "UIPanel", "UnityEngine.GameObject", false);
            CheckField(assembly, "StartOfDayUIManager", "contentGroupObject", "UnityEngine.GameObject", false);
            CheckField(assembly, "StoreStation", "storeEventManager", "StoreEventManager", false);
            CheckMethod(assembly, "StoreEventManager", "GetActiveEventOnDay",
                "System.Collections.Generic.List<StoreEvent>", false,
                "System.Int32", "System.Boolean", "System.Boolean");
            CheckType(assembly, "StoreEvent");
            CheckField(assembly, "StoreEvent", "identifier", "System.String", false);
            CheckField(assembly, "StoreEvent", "displayName", "System.String", false);
            CheckField(assembly, "StoreEvent", "newsName", "System.String", false);
            CheckField(assembly, "StoreEvent", "eventType", "StoreEvent.EventType", false);
            CheckField(assembly, "StoreEvent", "eventArea", "StoreEvent.EventArea", false);
            CheckField(assembly, "StoreEvent", "startDay", "System.Int32", false);
            CheckField(assembly, "StoreEvent", "currentDay", "System.Int32", false);
            CheckField(assembly, "StoreEvent", "totalDuration", "System.Int32", false);
            CheckField(assembly, "StoreEvent", "importance", "System.Int32", false);
            CheckField(assembly, "StoreEvent", "IsKnown", "System.Boolean", false);
            CheckMethod(assembly, "PlayerStore", "GetRentDayCounter", "System.Int32", false);
            CheckField(assembly, "PlayerStore", "rentValue", "System.Int32", false);
            CheckField(assembly, "PlayerStore", "temporaryRent", "System.Int32", false);
            CheckField(assembly, "PlayerStore", "IsPropertyPaid", "System.Boolean", false);
            CheckField(assembly, "PlayerStore", "isPayingMortgage", "System.Boolean", false);
            CheckField(assembly, "PlayerStore", "mortgageWeeklyPayment", "System.Int32", false);
            CheckMethod(assembly, "PlayerStore", "IsPayingRent", "System.Boolean", false);
            CheckMethod(assembly, "ShowcaseHelper", "GetDisplayCaseType",
                "System.Collections.Generic.List<System.String>", true);
            CheckMethod(assembly, "ShowcaseHelper", "GetDiscerningShopperChance",
                "System.Int32", true);
            CheckMethod(assembly, "ShowcaseHelper", "GetDiscerningShopperCooldownOpenings",
                "System.Int32", true);
            CheckMethod(assembly, "ShowcaseHelper", "GetDisplayCasePrediction",
                "System.Collections.Generic.List<System.ValueTuple<System.String,System.Int32,System.String>>", true);
            CheckMethod(assembly, "StoreClientManager", "GetNormalCustomerCount",
                "System.Int32", false);
            CheckMethod(assembly, "StoreClientListDict", "GetClientDisplayName",
                "System.String", true, "System.String");
            CheckMethod(assembly, "TypeHelper", "GetTypeDisplayName",
                "System.String", true, "System.String");
            CheckMethod(assembly, "GeneralHelper", "GetDisplayNameFromID",
                "System.String", true, "System.String");
            CheckField(assembly, "StoreReputation", "perks",
                "System.Collections.Generic.List<StoreReputationPerk>", false);
            CheckMethod(assembly, "StoreReputation", "GetReputation", "System.Int32", false);
            CheckField(assembly, "StoreReputation", "strength", "System.Int32", false);
            CheckField(assembly, "StoreReputation", "influence", "System.Int32", false);
            CheckField(assembly, "StoreReputation", "maxInfluence", "System.Int32", false);
            CheckType(assembly, "StoreReputationPerk");
            CheckField(assembly, "StoreReputationPerk", "perkId", "System.String", false);
            CheckField(assembly, "StoreReputationPerk", "perkThreshold", "System.Int32", false);
            CheckField(assembly, "StoreReputationPerk", "perkType", "System.Int32", false);
            CheckField(assembly, "StoreReputationPerk", "isUnlocked", "System.Boolean", false);
            CheckField(assembly, "StoreReputationPerk", "iconName", "System.String", false);
            CheckField(assembly, "StoreReputationPerk", "isAlwaysActive", "System.Boolean", false);
            CheckField(assembly, "PlayerStore", "storeRequests");
            CheckField(assembly, "PlayerStore", "wantedStatus");
            CheckField(assembly, "StoreClient", "barterOffer");
            CheckMethod(assembly, "WaterHelper", "AddPureWater");
            CheckMethod(assembly, "WaterHelper", "GetCurrentCapacityML");
            CheckMethod(assembly, "WineHelper", "TryStartFermentation");
            CheckMethod(assembly, "WineHelper", "IsFermentingOrFinished");
            CheckMethod(assembly, "WineHelper", "GetWineQualityTier");
            CheckMethod(assembly, "MachineHydroponic", "GetGrowStatus");
            CheckMethod(assembly, "MachineHydroponic", "GetWaterSlot");
            CheckMethod(assembly, "MachineHydroponic", "GetInputSlot");
            CheckMethod(assembly, "EmporiumEntry", "GetAllOwnedItemsByTag");
            CheckMethod(assembly, "NetworkUpgrade", "Unlock");
            CheckMethod(assembly, "NetworkUpgrade", "IsUnlocked");
            CheckField(assembly, "PlayerStore", "networkUpgrade");
            CheckMethod(assembly, "LotteryHelper", "IsLotteryUnlocked");
            CheckMethod(assembly, "LotteryHelper", "GetCurrentEdition");
            CheckMethod(assembly, "LotteryHelper", "CanRedeemPrize");
            CheckMethod(assembly, "LotteryHelper", "TryRedeemPrize");
            CheckMethod(assembly, "DialoguePhoneUIManager", "SelectChoice");
            CheckMethod(assembly, "DialoguePhoneUIManager", "OnAdvancePressed");
            CheckMethod(assembly, "PhoneUIManager", "AutoCall");
            CheckMethod(assembly, "PhoneUIManager", "WillAnswerCall");
            CheckMethod(assembly, "PhoneUIManager", "OnPhoneDialogEnd");
            CheckField(assembly, "PlayerStore", "PhoneClientDict");
            CheckMethod(assembly, "PlayerStore", "GetWholesalePrice");
            CheckMethod(assembly, "EmporiumEntry", "GetAllNonOwnedItem");
            CheckMethod(assembly, "PlayerStore", "OnItemBought");
            CheckMethod(assembly, "PlayerStore", "MoveDealItemToInventory");
            CheckMethod(assembly, "CommissaryData", "BuyItem");
            CheckProperty(assembly, "GameItem", "modifiedXOrigin");
            CheckProperty(assembly, "GameItem", "modifiedYOrigin");

            CheckProperty(assembly, "TabletButton", "Interactable");
            CheckField(assembly, "TabletButton", "button");
            CheckField(assembly, "TabletButton", "label");
            CheckMethod(assembly, "PlayerStore", "CanSellThisItem");
            CheckMethod(assembly, "PlayerStore", "GetStoreInventory");
            CheckMethod(assembly, "StoreClient", "GetBudget");
            CheckMethod(assembly, "DialogUIManager", "GetCurrentDialog");

            // Guard audit P0: the shutter button is the only player entry point.
            CheckProperty(assembly, "StoreShutterButton", "Instance");
            CheckMethod(assembly, "StoreShutterButton", "OnClick");
            CheckMethod(assembly, "StoreShutterButton", "CloseShutter");
            CheckMethod(assembly, "StoreShutterButton", "EndAndClose");
            CheckField(assembly, "PlayerStore", "lockShutterButtonFlag");
            CheckField(assembly, "PlayerStore", "lockNextBellFlag");
            CheckMethod(assembly, "PlayerStore", "CanStartDay");
            CheckMethod(assembly, "PlayerStore", "IsClientDimissable");
            CheckMethod(assembly, "PlayerStore", "TryPlayerDismissCurrentClient");
            CheckProperty(assembly, "BargainUIManager", "IsOpen");
            CheckMethod(assembly, "BargainUIManager", "CanOfferDiscount");

            CheckType(assembly, "Dialogue");
            CheckField(assembly, "Dialogue", "plainText");
            CheckField(assembly, "Dialogue", "choices");
            CheckField(assembly, "Dialogue", "nextDialogue");

            // -----------------------------------------------------------------
            // S5: dialogue availability / readback and day-cycle readback. These
            // mirror the native button state and the single guarded callbacks.
            // -----------------------------------------------------------------
            CheckProperty(assembly, "DialogUIManager", "Instance", "DialogUIManager", true);
            CheckMethod(assembly, "DialogUIManager", "SelectChoice", "System.Void", false, "System.Int32");
            CheckMethod(assembly, "DialogUIManager", "IsChoice", "System.Boolean", false);
            CheckMethod(assembly, "DialogUIManager", "IsCurrentDialogEnd", "System.Boolean", false);
            CheckMethod(assembly, "DialogUIManager", "IsLineStillDisplaying", "System.Boolean", false, "Dialogue");
            CheckField(assembly, "Dialogue", "title", "System.String", false);
            CheckMethod(assembly, "Dialogue", "IsChoiceEnabledAt", "System.Boolean", false, "System.Int32");
            CheckMethod(assembly, "PlayerStore", "CanEndDay", "System.Boolean", false);

            // Exists, but the bridge must never expose it to the AI (it is the answer key).
            CheckMethod(assembly, "AppraisalHelper", "GetCorrectBaseValue");

            // -----------------------------------------------------------------
            // S2: installed Rev13 game identity. This is the installed assembly,
            // not proof that it is the current Steam stable build at recording time.
            // -----------------------------------------------------------------
            CheckConst(assembly, "PlayerStore", "VERSION_NUMBER", "System.String", "VERSION 100-Rev13");
            CheckField(assembly, "PlayerStore", "VERSION", "System.String", true);

            // -----------------------------------------------------------------
            // S3: new-game menu contracts (scene -> perks -> difficulty -> start).
            // Only members confirmed in the installed Rev13 assembly are listed.
            // -----------------------------------------------------------------
            CheckType(assembly, "NewGameData");
            CheckField(assembly, "NewGameData", "startType", "NewGameData.StartType", false);
            CheckField(assembly, "NewGameData", "startScenarioId", "System.String", false);
            CheckField(assembly, "NewGameData", "difficultyId", "System.String", false);
            CheckField(assembly, "NewGameData", "hardMode", "System.Boolean", false);
            CheckField(assembly, "NewGameData", "skipIntro", "System.Boolean", false);
            CheckField(assembly, "NewGameData", "endlessMode", "System.Boolean", false);
            CheckField(assembly, "NewGameData", "saveSlot", "System.Int32", false);
            CheckField(assembly, "NewGameData", "startingPerks", "System.Collections.Generic.List<StartingPerk>", false);
            CheckProperty(assembly, "NewGameData", "Instance", "NewGameData", true);

            CheckType(assembly, "StartingScenario");
            CheckField(assembly, "StartingScenario", "id", "System.String", false);
            CheckField(assembly, "StartingScenario", "startType", "NewGameData.StartType", false);
            CheckField(assembly, "StartingScenario", "comingSoon", "System.Boolean", false);
            CheckField(assembly, "StartingScenario", "skipIntro", "System.Boolean", false);
            CheckField(assembly, "StartingScenario", "hidden", "System.Boolean", false);
            CheckField(assembly, "StartingScenario", "tagTone", "StartingScenario.TagTone", false);
            CheckMethod(assembly, "StartingScenario", "GetAll", "System.Collections.Generic.List<StartingScenario>", true);
            CheckMethod(assembly, "StartingScenario", "GetVisible", "System.Collections.Generic.List<StartingScenario>", true);
            CheckMethod(assembly, "StartingScenario", "Get", "StartingScenario", true, "System.String");
            CheckMethod(assembly, "StartingScenario", "GetByStartType", "StartingScenario", true, "NewGameData.StartType");
            CheckMethod(assembly, "StartingScenario", "GetCurrent", "StartingScenario", true);
            CheckMethod(assembly, "StartingScenario", "CanStart", "System.Boolean", false);
            CheckMethod(assembly, "StartingScenario", "GetTitle", "System.String", false);
            CheckMethod(assembly, "StartingScenario", "GetDescription", "System.String", false);
            CheckMethod(assembly, "StartingScenario", "GetTag", "System.String", false);
            CheckMethod(assembly, "StartingScenario", "GetStartWith", "System.Collections.Generic.List<System.String>", false);
            CheckMethod(assembly, "StartingScenario", "ResolveId", "System.String", true, "NewGameData.StartType", "System.String");

            CheckType(assembly, "NewGamePanelFrame");
            CheckField(assembly, "NewGamePanelFrame", "headerTitle", "TMPro.TextMeshProUGUI", false);
            CheckField(assembly, "NewGamePanelFrame", "continueButton", "UnityEngine.UI.Button", false);

            CheckType(assembly, "NewGameSpecializationFrame");
            CheckField(assembly, "NewGameSpecializationFrame", "stripSlots", "UnityEngine.RectTransform", false);
            CheckField(assembly, "NewGameSpecializationFrame", "title", "TMPro.TextMeshProUGUI", false);
            CheckField(assembly, "NewGameSpecializationFrame", "description", "TMPro.TextMeshProUGUI", false);
            CheckField(assembly, "NewGameSpecializationFrame", "warning", "TMPro.TextMeshProUGUI", false);
            CheckField(assembly, "NewGameSpecializationFrame", "slotNormal", "UnityEngine.Sprite", false);
            CheckField(assembly, "NewGameSpecializationFrame", "slotSelected", "UnityEngine.Sprite", false);
            CheckField(assembly, "NewGameSpecializationFrame", "icons", "UnityEngine.Sprite[]", false);

            CheckType(assembly, "NewGameSpecializationUI");
            CheckProperty(assembly, "NewGameSpecializationUI", "IsOpen", "System.Boolean", true);
            CheckProperty(assembly, "NewGameSpecializationUI", "Selected", "StartingScenario", true);
            CheckMethod(assembly, "NewGameSpecializationUI", "Open", "System.Void", true);
            CheckMethod(assembly, "NewGameSpecializationUI", "Hide", "System.Void", true);
            CheckMethod(assembly, "NewGameSpecializationUI", "Close", "System.Void", true);
            CheckMethod(assembly, "NewGameSpecializationUI", "OnSlotClicked", "System.Void", true, "System.String");
            CheckMethod(assembly, "NewGameSpecializationUI", "OnContinueClicked", "System.Void", true);
            CheckMethod(assembly, "NewGameSpecializationUI", "PageCount", "System.Int32", true);
            CheckMethod(assembly, "NewGameSpecializationUI", "TurnPage", "System.Void", true, "System.Int32");

            CheckType(assembly, "NewGamePerksUI");
            CheckProperty(assembly, "NewGamePerksUI", "IsOpen", "System.Boolean", true);
            CheckProperty(assembly, "NewGamePerksUI", "SelectedPerks", "System.Collections.Generic.List<StartingPerk>", true);
            CheckMethod(assembly, "NewGamePerksUI", "Open", "System.Void", true);
            CheckMethod(assembly, "NewGamePerksUI", "Close", "System.Void", true);
            CheckMethod(assembly, "NewGamePerksUI", "OnSlotClicked", "System.Void", true, "System.String");
            CheckMethod(assembly, "NewGamePerksUI", "OnContinueClicked", "System.Void", true);
            CheckMethod(assembly, "NewGamePerksUI", "CanStart", "System.Boolean", true);
            CheckMethod(assembly, "NewGamePerksUI", "PageCount", "System.Int32", true);
            CheckMethod(assembly, "NewGamePerksUI", "SelectedPageCount", "System.Int32", true);
            CheckMethod(assembly, "NewGamePerksUI", "TurnPage", "System.Void", true, "System.Int32");
            CheckMethod(assembly, "NewGamePerksUI", "TurnSelectedPage", "System.Void", true, "System.Int32");
            CheckConst(assembly, "NewGamePerksUI", "MAX_PERK_POINT", "System.Int32", 3);
            CheckConst(assembly, "NewGamePerksUI", "BASE_PERK_COUNT", "System.Int32", 4);

            CheckType(assembly, "NewGamePerksFrame");
            CheckField(assembly, "NewGamePerksFrame", "instruction", "TMPro.TextMeshProUGUI", false);
            CheckField(assembly, "NewGamePerksFrame", "pointsCounter", "TMPro.TextMeshProUGUI", false);
            CheckField(assembly, "NewGamePerksFrame", "perksCounter", "TMPro.TextMeshProUGUI", false);
            CheckField(assembly, "NewGamePerksFrame", "availableSlots", "UnityEngine.RectTransform", false);
            CheckField(assembly, "NewGamePerksFrame", "selectedSlots", "UnityEngine.RectTransform", false);
            CheckField(assembly, "NewGamePerksFrame", "prevButton", "UnityEngine.UI.Button", false);
            CheckField(assembly, "NewGamePerksFrame", "nextButton", "UnityEngine.UI.Button", false);
            CheckField(assembly, "NewGamePerksFrame", "selectedPrevButton", "UnityEngine.UI.Button", false);
            CheckField(assembly, "NewGamePerksFrame", "selectedNextButton", "UnityEngine.UI.Button", false);
            CheckField(assembly, "NewGamePerksFrame", "perkSlotLock", "UnityEngine.Sprite", false);

            CheckType(assembly, "NewGameDifficultyUI");
            CheckProperty(assembly, "NewGameDifficultyUI", "IsOpen", "System.Boolean", true);
            CheckProperty(assembly, "NewGameDifficultyUI", "Selected", "Difficulty", true);
            CheckMethod(assembly, "NewGameDifficultyUI", "Open", "System.Void", true);
            CheckMethod(assembly, "NewGameDifficultyUI", "Hide", "System.Void", true);
            CheckMethod(assembly, "NewGameDifficultyUI", "Close", "System.Void", true);
            CheckMethod(assembly, "NewGameDifficultyUI", "OnPlateClicked", "System.Void", true, "System.String");
            CheckMethod(assembly, "NewGameDifficultyUI", "OnSkipIntroClicked", "System.Void", true);
            CheckMethod(assembly, "NewGameDifficultyUI", "OnEndlessClicked", "System.Void", true);
            CheckMethod(assembly, "NewGameDifficultyUI", "OnStartClicked", "System.Void", true);
            CheckMethod(assembly, "NewGameDifficultyUI", "PageCount", "System.Int32", true);
            CheckMethod(assembly, "NewGameDifficultyUI", "TurnPage", "System.Void", true, "System.Int32");

            CheckType(assembly, "NewGameDifficultyFrame");
            CheckField(assembly, "NewGameDifficultyFrame", "platesHost", "UnityEngine.RectTransform", false);
            CheckField(assembly, "NewGameDifficultyFrame", "skipIntro", "NewGameCheckbox", false);
            CheckField(assembly, "NewGameDifficultyFrame", "endless", "NewGameCheckbox", false);

            CheckType(assembly, "NewGameCheckbox");
            CheckField(assembly, "NewGameCheckbox", "root", "UnityEngine.RectTransform", false);
            CheckField(assembly, "NewGameCheckbox", "button", "UnityEngine.UI.Button", false);
            CheckField(assembly, "NewGameCheckbox", "border", "UnityEngine.UI.Image", false);
            CheckField(assembly, "NewGameCheckbox", "mark", "UnityEngine.UI.Image", false);
            CheckField(assembly, "NewGameCheckbox", "label", "TMPro.TextMeshProUGUI", false);
            CheckField(assembly, "NewGameCheckbox", "group", "UnityEngine.CanvasGroup", false);

            CheckType(assembly, "WildNetworkSlot");
            CheckField(assembly, "WildNetworkSlot", "id", "System.String", false);
            CheckField(assembly, "WildNetworkSlot", "onClick", "System.Action<System.String>", false);
            CheckField(assembly, "WildNetworkSlot", "onHover", "System.Action<System.String,System.Boolean>", false);
            CheckMethod(assembly, "WildNetworkSlot", "OnPointerClick", "System.Void", false, "UnityEngine.EventSystems.PointerEventData");

            CheckType(assembly, "StartingPerk");
            CheckField(assembly, "StartingPerk", "id", "System.String", false);
            CheckField(assembly, "StartingPerk", "cost", "System.Int32", false);
            CheckField(assembly, "StartingPerk", "maxSlot", "System.Int32", false);
            CheckField(assembly, "StartingPerk", "incompatiblePerks", "System.Collections.Generic.List<System.String>", false);
            CheckField(assembly, "StartingPerk", "type", "StartingPerk.StartingPerkType", false);
            CheckMethod(assembly, "StartingPerk", "GetLocalizedDisplayName", "System.String", false);
            CheckMethod(assembly, "StartingPerk", "GetLocalizedDescription", "System.String", false);
            CheckMethod(assembly, "StartingPerk", "GetPerkById", "StartingPerk", true, "System.String");

            CheckType(assembly, "StartingPerkList");
            CheckField(assembly, "StartingPerkList", "Perks", "System.Collections.Generic.List<StartingPerk>", true);
            CheckMethod(assembly, "StartingPerkList", "InitStartingPerk", "System.Void", true);

            CheckType(assembly, "Difficulty");
            CheckField(assembly, "Difficulty", "id", "System.String", false);
            CheckField(assembly, "Difficulty", "rules", "DifficultyRules", false);
            CheckMethod(assembly, "Difficulty", "GetTitle", "System.String", false);
            CheckMethod(assembly, "Difficulty", "GetSubtitle", "System.String", false);
            CheckMethod(assembly, "Difficulty", "IsAtLeast", "System.Boolean", false, "System.String");
            CheckMethod(assembly, "Difficulty", "GetAll", "System.Collections.Generic.List<Difficulty>", true);
            CheckMethod(assembly, "Difficulty", "Get", "Difficulty", true, "System.String");
            CheckMethod(assembly, "Difficulty", "ResolveId", "System.String", true, "System.String", "System.Boolean");
            CheckProperty(assembly, "Difficulty", "Current", "Difficulty", true);

            CheckType(assembly, "DifficultyRules");
            CheckField(assembly, "DifficultyRules", "retailMarkup", "System.Int32", false);
            CheckField(assembly, "DifficultyRules", "rentIncreaseMultiplier", "System.Single", false);
            CheckField(assembly, "DifficultyRules", "landlordGraceOwnedAtStart", "System.Boolean", false);

            CheckType(assembly, "MainMenuUIController");
            CheckProperty(assembly, "MainMenuUIController", "Instance", "MainMenuUIController", true);
            CheckField(assembly, "MainMenuUIController", "confirmPanelUIManager", "ConfirmPanelUIManager", false);
            CheckMethod(assembly, "MainMenuUIController", "OnNewGameClick", "System.Void", false);
            CheckMethod(assembly, "MainMenuUIController", "OnScenarioContinue", "System.Void", false, "StartingScenario");
            CheckMethod(assembly, "MainMenuUIController", "OnContinueClick", "System.Void", false);
            CheckMethod(assembly, "MainMenuUIController", "OnPerksContinue", "System.Void", false);
            CheckMethod(assembly, "MainMenuUIController", "OnPerkFinishedClick", "System.Void", false);
            CheckMethod(assembly, "MainMenuUIController", "StartNewGameClick", "System.Void", false);

            CheckType(assembly, "ConfirmPanelUIManager");
            CheckMethod(assembly, "ConfirmPanelUIManager", "OpenPanel", "System.Void", false,
                "System.String", "System.Action", "System.Action", "System.Boolean");
            // S4: the first-run prompt must be answered by the game's own Yes
            // callback; the bridge never dismisses the panel or assigns actions.
            CheckMethod(assembly, "ConfirmPanelUIManager", "OnYesClick", "System.Void", false);

            // -----------------------------------------------------------------
            // S3A: new-game menu adapters. These private members are the UI's own
            // lists, page cursors and first-run gate. The adapter reads them on the
            // Unity main thread; selection delegates to the exact native callback
            // (OnSlotClicked / OnPlateClicked) once and verifies by re-reading.
            // Public semantic ids are translated to these native slot arguments:
            // scenario/difficulty use the visible-list index, perks use perk.id.
            // -----------------------------------------------------------------
            CheckField(assembly, "NewGamePanelFrame", "continueLabel", "TMPro.TextMeshProUGUI", false);

            CheckField(assembly, "NewGameSpecializationUI", "frame",
                "NewGameSpecializationFrame", true);
            CheckField(assembly, "NewGameSpecializationUI", "scenarios",
                "System.Collections.Generic.List<StartingScenario>", true);
            CheckField(assembly, "NewGameSpecializationUI", "page", "System.Int32", true);

            CheckField(assembly, "NewGamePerksUI", "frame", "NewGamePerksFrame", true);
            CheckField(assembly, "NewGamePerksUI", "sorted",
                "System.Collections.Generic.List<StartingPerk>", true);
            CheckField(assembly, "NewGamePerksUI", "page", "System.Int32", true);
            CheckField(assembly, "NewGamePerksUI", "selectedPage", "System.Int32", true);
            CheckField(assembly, "NewGamePerksUI", "firstRun", "System.Boolean", true);
            CheckField(assembly, "NewGamePerksUI", "startingGame", "System.Boolean", true);

            CheckField(assembly, "NewGameDifficultyUI", "frame", "NewGameDifficultyFrame", true);
            CheckField(assembly, "NewGameDifficultyUI", "difficulties",
                "System.Collections.Generic.List<Difficulty>", true);
            CheckField(assembly, "NewGameDifficultyUI", "selected", "System.Int32", true);
            CheckField(assembly, "NewGameDifficultyUI", "page", "System.Int32", true);
            CheckField(assembly, "NewGameDifficultyUI", "startingGame", "System.Boolean", true);
            CheckField(assembly, "NewGameDifficultyUI", "skipIntroForced", "System.Boolean", true);
            CheckMethod(assembly, "Difficulty", "GetEffectLines",
                "System.Collections.Generic.List<System.String>", true, "Difficulty");

            // -----------------------------------------------------------------
            // S3B: native hover/prompt/page control used by the new-game bridge.
            // The perk screen keeps both rows in one private `views` list, so the
            // adapter can prove which exact native slot is hovered before a
            // single OnSlotClicked call. Hover callbacks are the real
            // WildNetworkSlot IPointerEnter/Exit handlers; no private flag is
            // assigned directly.
            // -----------------------------------------------------------------
            CheckField(assembly, "NewGamePerksUI", "views",
                "System.Collections.Generic.List<NewGamePerksUI.SlotView>", true);
            CheckType(assembly, "NewGamePerksUI+SlotView");
            CheckField(assembly, "NewGamePerksUI+SlotView", "perk", "StartingPerk", false);
            CheckField(assembly, "NewGamePerksUI+SlotView", "inSelectedRow", "System.Boolean", false);
            CheckField(assembly, "NewGamePerksUI+SlotView", "frameImage", "UnityEngine.UI.Image", false);
            CheckField(assembly, "NewGamePerksUI+SlotView", "hovered", "System.Boolean", false);

            CheckMethod(assembly, "WildNetworkSlot", "OnPointerEnter", "System.Void", false,
                "UnityEngine.EventSystems.PointerEventData");
            CheckMethod(assembly, "WildNetworkSlot", "OnPointerExit", "System.Void", false,
                "UnityEngine.EventSystems.PointerEventData");

            CheckField(assembly, "ConfirmPanelUIManager", "panel", "UnityEngine.GameObject", false);
            CheckField(assembly, "ConfirmPanelUIManager", "bodyTMP", "TMPro.TextMeshProUGUI", false);
            CheckField(assembly, "ConfirmPanelUIManager", "yesTMP", "TMPro.TextMeshProUGUI", false);
            CheckField(assembly, "ConfirmPanelUIManager", "noTMP", "TMPro.TextMeshProUGUI", false);
            CheckMethod(assembly, "LocHelper", "GetLocalizedMainMenu", "System.String", true,
                "System.String", "System.Object[]");

            // -----------------------------------------------------------------
            // S4: save identity and first-rent contracts (contract-first, no writes).
            // -----------------------------------------------------------------
            CheckProperty(assembly, "PlayerStore", "HasInstance", "System.Boolean", true);
            CheckField(assembly, "PlayerStore", "saveSlotId", "System.Int32", false);
            CheckField(assembly, "PlayerStore", "saveVersion", "System.String", false);
            CheckField(assembly, "PlayerStore", "runID", "System.String", false);
            CheckField(assembly, "PlayerStore", "runNumber", "System.Int32", false);
            CheckField(assembly, "PlayerStore", "startType", "NewGameData.StartType", false);
            CheckField(assembly, "PlayerStore", "startScenarioId", "System.String", false);
            CheckField(assembly, "PlayerStore", "startLocale", "System.String", false);
            CheckField(assembly, "PlayerStore", "lastSaveTime", "System.DateTime", false);
            CheckField(assembly, "PlayerStore", "runStats", "RunStats", false);
            CheckField(assembly, "PlayerStore", "playerCash", "System.Int32", false);
            CheckField(assembly, "PlayerStore", "dayUntilRent", "System.Int32", false);
            CheckField(assembly, "PlayerStore", "rentValue", "System.Int32", false);
            CheckField(assembly, "PlayerStore", "temporaryRent", "System.Int32", false);
            CheckField(assembly, "PlayerStore", "startingRent", "System.Int32", false);
            CheckMethod(assembly, "PlayerStore", "InitialSave", "System.Void", false);
            CheckMethod(assembly, "PlayerStore", "SaveGame", "System.Void", false);
            CheckMethod(assembly, "PlayerStore", "LoadGame", "System.Void", false);
            CheckMethod(assembly, "PlayerStore", "CanPayRent", "System.Boolean", false);
            CheckMethod(assembly, "PlayerStore", "OnRentPaid", "System.Void", false);
            CheckMethod(assembly, "PlayerStore", "OnRentNotPaid", "System.Void", false);
            CheckMethod(assembly, "PlayerStore", "GetRentValue", "System.Int32", false);
            CheckMethod(assembly, "PlayerStore", "GetRentDayCounter", "System.Int32", false);
            CheckMethod(assembly, "PlayerStore", "PayRentCheck", "System.Void", false);
            CheckMethod(assembly, "PlayerStore", "RentIncrease", "System.Void", false);

            CheckType(assembly, "SavePreviewData");
            CheckField(assembly, "SavePreviewData", "slotId", "System.Int32", false);
            CheckField(assembly, "SavePreviewData", "version", "System.String", false);
            CheckField(assembly, "SavePreviewData", "startType", "NewGameData.StartType", false);
            CheckField(assembly, "SavePreviewData", "startScenarioId", "System.String", false);
            CheckField(assembly, "SavePreviewData", "startLocale", "System.String", false);
            CheckField(assembly, "SavePreviewData", "dayCount", "System.Int32", false);
            CheckField(assembly, "SavePreviewData", "credit", "System.Int32", false);
            CheckField(assembly, "SavePreviewData", "lastSaveDateTime", "System.DateTime", false);

            CheckMethod(assembly, "SaveFiles", "Slot", "System.String", true, "System.Int32");
            CheckMethod(assembly, "SaveFiles", "NextFreeSlotId", "System.Int32", true,
                "System.Collections.Generic.List<System.Int32>");
            CheckMethod(assembly, "SaveFiles", "RegisterSlot", "System.Void", true, "System.Int32", "SavePreviewData");
            CheckMethod(assembly, "SaveFiles", "DeleteSlot", "System.Void", true, "System.Int32");

            CheckType(assembly, "StoreStation");
            CheckField(assembly, "StoreStation", "dayCounter", "System.Int32", false);
            CheckProperty(assembly, "StoreStation", "Instance", "StoreStation", true);

            CheckType(assembly, "RunStats");
            CheckField(assembly, "RunStats", "rentPaid", "System.Int32", false);
            CheckField(assembly, "RunStats", "rentWeeksPaid", "System.Int32", false);
            CheckField(assembly, "RunStats", "rentWeeksMissed", "System.Int32", false);
            CheckProperty(assembly, "RunStats", "Current", "RunStats", true);
            CheckMethod(assembly, "RunStats", "OnRentPaid", "System.Void", true, "System.Int32", "System.Int32");
            CheckMethod(assembly, "RunStats", "OnRentMissed", "System.Void", true);

            CheckType(assembly, "StoreClientList");
            CheckMethod(assembly, "StoreClientList", "CreateLandlordClient", "StoreClient", true);

            // ---------------------------------------------------------- S10
            // Native generic confirmation modal: the bridge reads its text and
            // presses exactly one of its two callbacks.
            CheckType(assembly, "GenericConfirmationUIManager");
            CheckProperty(assembly, "GenericConfirmationUIManager", "Instance");
            CheckField(assembly, "GenericConfirmationUIManager", "panel");
            CheckField(assembly, "GenericConfirmationUIManager", "bodyTMP");
            CheckField(assembly, "GenericConfirmationUIManager", "confirmButtonTMP");
            CheckField(assembly, "GenericConfirmationUIManager", "cancelButtonTMP");
            CheckField(assembly, "GenericConfirmationUIManager", "cancelButton");
            CheckField(assembly, "GenericConfirmationUIManager", "confirmButton");
            CheckMethod(assembly, "GenericConfirmationUIManager", "ConfirmButtonPressed");
            CheckMethod(assembly, "GenericConfirmationUIManager", "CancelButtonPressed");

            // R5/B2: the negotiation tablet's own confirm panel (yes/no). The
            // typed answer goes through the native public callbacks; the read
            // uses the frame identity, body text and the two option buttons.
            CheckType(assembly, "NegotiationTabletFrame");
            CheckField(assembly, "NegotiationTabletFrame", "confirmRoot");
            CheckField(assembly, "NegotiationTabletFrame", "confirmText");
            CheckField(assembly, "NegotiationTabletFrame", "confirmButtonHost");
            CheckField(assembly, "NegociationUIManager", "uiObject");
            CheckMethod(assembly, "NegociationUIManager", "OnConfirmPanelYes");
            CheckMethod(assembly, "NegociationUIManager", "OnConfirmPanelNo");
            CheckProperty(assembly, "NegociationUIManager", "Instance");

            // close_windows: the paper panel and the floating window handler.
            CheckProperty(assembly, "PaperUIManager", "Instance");
            CheckField(assembly, "PaperUIManager", "panel");
            CheckField(assembly, "PaperUIManager", "contractPanel");
            CheckField(assembly, "PaperUIManager", "deedPanel");
            CheckField(assembly, "PaperUIManager", "bookPanel");
            CheckMethod(assembly, "PaperUIManager", "CloseUI");
            CheckMethod(assembly, "PaperUIManager", "CloseContract");
            CheckMethod(assembly, "PaperUIManager", "CloseDeed");
            CheckMethod(assembly, "PaperUIManager", "CloseBook");
            CheckField(assembly, "WindowsHandler", "current");
            CheckProperty(assembly, "WindowsHandler", "visibleWindows");
            CheckMethod(assembly, "WindowsHandler", "HideAllMovable", "System.Void", true);
            CheckMethod(assembly, "PixelWindow", "IsVisible");

            // confirm_trade presses the native accept button, so the client
            // completes its business and can leave.
            CheckMethod(assembly, "NegociationUIManager", "OnOfferAcceptClick");
            // C module (B10-B16 trade guard): every guard member the pre-check reads.
            CheckMethod(assembly, "NegociationUIManager", "IsTradeLocked");
            CheckMethod(assembly, "NegociationUIManager", "IsNegociatingItemOwned");
            CheckMethod(assembly, "PlayerStore", "CanPay");
            CheckMethod(assembly, "PlayerStore", "CanBuyItemFromTable");
            CheckMethod(assembly, "PlayerStore", "CanSellThisItem");
            CheckField(assembly, "StoreClient", "isIntroduced");
            CheckField(assembly, "StoreClient", "serviceCost");
            CheckField(assembly, "StoreClient", "barterOffer");
            CheckField(assembly, "StoreClient", "gunOrderData");
            CheckField(assembly, "StoreClient", "isBusinessComplete");
            CheckMethod(assembly, "BarterOffer", "IsBarterAcceptable");
            CheckMethod(assembly, "GunOrderData", "IsAccepted");
            CheckProperty(assembly, "NegociationUIManager", "IsOpen");
            CheckField(assembly, "StoreClient", "isBusinessComplete");
            CheckField(assembly, "PlayerStore", "currentClientInstance");

            // Extra containers and the waiting-customer queue.
            CheckProperty(assembly, "EmporiumEntry", "vendingMachineElement");
            CheckProperty(assembly, "EmporiumEntry", "vendingFountainElement");
            CheckField(assembly, "EmporiumEntry", "hirelingInv");
            CheckField(assembly, "StoreClientManager", "clientStack");

            // Tablet panels: the calendar/night report screen stays open otherwise.
            CheckField(assembly, "AdvCalendarUIManager", "UIPanel");
            CheckMethod(assembly, "AdvCalendarUIManager", "CloseUI");
            CheckField(assembly, "ClienteleUIManager", "panel");
            CheckMethod(assembly, "ClienteleUIManager", "CloseUI");
            CheckField(assembly, "NotebookUIManager", "panelObject");
            CheckMethod(assembly, "NotebookUIManager", "CloseUI");
            CheckField(assembly, "ReputationUIManager", "uiPanel");
            CheckMethod(assembly, "ReputationUIManager", "CloseUI");
        }

        private static string ResolveGameAssembly(string[] args)
        {
            string explicitAssembly = GetOptionValue(args, "--pawnmind-game-assembly")
                ?? Environment.GetEnvironmentVariable("PAWNMIND_GAME_ASSEMBLY");
            if (!string.IsNullOrWhiteSpace(explicitAssembly))
            {
                return explicitAssembly.Trim();
            }

            string managed = GetOptionValue(args, "--pawnmind-managed")
                ?? Environment.GetEnvironmentVariable("PAWNMIND_GAME_MANAGED");
            if (!string.IsNullOrWhiteSpace(managed))
            {
                return Path.Combine(managed.Trim(), "Assembly-CSharp.dll");
            }

            return DefaultGameAssembly;
        }

        private static string GetOptionValue(string[] args, string name)
        {
            if (args == null)
            {
                return null;
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return args[i + 1];
                    }
                    return null;
                }

                if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
                {
                    return args[i].Substring(name.Length + 1);
                }
            }

            return null;
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = value.Trim();
            return normalized == "1"
                || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintGameIdentity(string path, Assembly assembly, bool requireGame)
        {
            Console.WriteLine("game assembly: " + path);
            try
            {
                Console.WriteLine("game assembly bytes: " + new FileInfo(path).Length);
            }
            catch (Exception e)
            {
                Console.WriteLine("game assembly bytes: UNKNOWN (" + e.GetType().Name + ")");
            }

            Console.WriteLine("game assembly sha256: " + ComputeSha256(path));
            Console.WriteLine("game version: " + ReadAssemblyVersion(assembly));
            Console.WriteLine("game version source: installed Assembly-CSharp.dll (PlayerStore.VERSION_NUMBER/VERSION)");
            Console.WriteLine("latest: UNVERIFIED (offline; confirm the Steam stable branch before recording/S8)");
            Console.WriteLine("strict: " + (requireGame ? "REQUIRE_GAME=1" : "REQUIRE_GAME=0"));
        }

        private static string ComputeSha256(string path)
        {
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder builder = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash)
                    {
                        builder.Append(b.ToString("x2"));
                    }
                    return builder.ToString();
                }
            }
            catch (Exception e)
            {
                return "UNKNOWN (" + e.GetType().Name + ": " + e.Message + ")";
            }
        }

        private static string ReadAssemblyVersion(Assembly assembly)
        {
            try
            {
                Type playerStore = assembly.GetType("PlayerStore", false);
                if (playerStore == null)
                {
                    return "UNKNOWN (PlayerStore type missing)";
                }

                FieldInfo versionNumber = playerStore.GetField("VERSION_NUMBER", AllMembers);
                if (versionNumber != null && versionNumber.IsLiteral)
                {
                    return Convert.ToString(versionNumber.GetRawConstantValue());
                }

                FieldInfo version = playerStore.GetField("VERSION", AllMembers);
                if (version != null)
                {
                    return Convert.ToString(version.GetValue(null));
                }
            }
            catch (Exception e)
            {
                return "UNKNOWN (" + e.GetType().Name + ": " + e.Message + ")";
            }

            return "UNKNOWN";
        }

        private static bool HasArg(string[] args, string expected)
        {
            foreach (string arg in args ?? Array.Empty<string>())
            {
                if (string.Equals(arg, expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static int RunContractSelfTest()
        {
            Assembly dynamicAssembly = BuildSelfTestAssembly();
            int failures = 0;

            ResetCounters();
            CheckType(dynamicAssembly, "SelfTestGood");
            CheckField(dynamicAssembly, "SelfTestGood", "Value", "System.Int32", false);
            CheckMethod(dynamicAssembly, "SelfTestGood", "Run", "System.String", false, "System.Int32");
            int exitCode = EvaluateContractRun();
            failures += Report("self-test good",
                _checks == 3 && _failures == 0 && exitCode == 0,
                "checks=" + _checks + " failures=" + _failures + " exit=" + exitCode);

            ResetCounters();
            string output = Capture(delegate
            {
                CheckField(dynamicAssembly, "SelfTestGood", "Value", "System.String", false);
            });
            exitCode = EvaluateContractRun();
            failures += Report("self-test field-type mismatch",
                _checks == 1 && _failures == 1 && exitCode == 1
                    && output.Contains("FAIL field SelfTestGood.Value")
                    && output.Contains("System.Int32"),
                "detected expected field-type mismatch");

            ResetCounters();
            output = Capture(delegate
            {
                CheckMethod(dynamicAssembly, "SelfTestGood", "Run", "System.Void", true, "System.String");
            });
            exitCode = EvaluateContractRun();
            failures += Report("self-test method-signature mismatch",
                _checks == 1 && _failures == 1 && exitCode == 1
                    && output.Contains("FAIL method SelfTestGood.Run")
                    && output.Contains("System.String"),
                "detected expected signature mismatch");

            ResetCounters();
            exitCode = EvaluateContractRun();
            failures += Report("self-test zero checks",
                _checks == 0 && exitCode == 1,
                "checks=" + _checks + " exit=" + exitCode);

            Console.WriteLine(failures == 0 ? "contract self-test OK" : "contract self-test failures: " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static Assembly BuildSelfTestAssembly()
        {
            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("PawnMind.ContractSelfTest"), AssemblyBuilderAccess.Run);
            ModuleBuilder module = assembly.DefineDynamicModule("PawnMind.ContractSelfTest");
            TypeBuilder type = module.DefineType("SelfTestGood", TypeAttributes.Public | TypeAttributes.Class);
            type.DefineField("Value", typeof(int), FieldAttributes.Public);
            MethodBuilder method = type.DefineMethod(
                "Run", MethodAttributes.Public, typeof(string), new[] { typeof(int) });
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldstr, "ok");
            il.Emit(OpCodes.Ret);
            type.CreateType();
            return assembly;
        }

        private static void ResetCounters()
        {
            _failures = 0;
            _checks = 0;
        }

        private static int Report(string name, bool passed, string detail)
        {
            if (passed)
            {
                Console.WriteLine("PASS " + name + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
                return 0;
            }

            Console.WriteLine("FAIL " + name + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            return 1;
        }

        private static string Capture(Action action)
        {
            TextWriter original = Console.Out;
            StringWriter writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                action();
            }
            finally
            {
                Console.SetOut(original);
            }
            return writer.ToString();
        }

        private static string ResolveTypeName(string name)
        {
            return (name ?? "").Replace('+', '.');
        }

        private static string FormatType(Type type)
        {
            if (type == null)
            {
                return "<null>";
            }

            if (type.IsByRef)
            {
                return FormatType(type.GetElementType()) + "&";
            }

            if (type.IsArray)
            {
                return FormatType(type.GetElementType()) + "[]";
            }

            if (type.IsGenericType)
            {
                Type definition = type.GetGenericTypeDefinition();
                string name = definition.FullName ?? definition.Name;
                int tick = name.IndexOf('`');
                if (tick >= 0)
                {
                    name = name.Substring(0, tick);
                }

                return ResolveTypeName(name)
                    + "<"
                    + string.Join(",", type.GetGenericArguments().Select(FormatType))
                    + ">";
            }

            return ResolveTypeName(type.FullName ?? type.Name);
        }

        private static string FormatMethod(MethodInfo method)
        {
            return (method.IsStatic ? "static " : "instance ")
                + FormatType(method.ReturnType) + " "
                + method.Name + "("
                + string.Join(", ", method.GetParameters().Select(p => FormatType(p.ParameterType)))
                + ")";
        }

        private static string ExpectedMethod(string methodName, string returnType, bool isStatic, string[] parameterTypes)
        {
            return (isStatic ? "static " : "instance ")
                + ResolveTypeName(returnType) + " "
                + methodName + "("
                + string.Join(", ", parameterTypes.Select(ResolveTypeName))
                + ")";
        }

        private static MethodInfo[] FindMethods(Type type, string methodName)
        {
            return type.GetMethods(AllMembers).Where(m => m.Name == methodName).ToArray();
        }

        private static Type FindType(Assembly assembly, string name)
        {
            try
            {
                return assembly.GetType(name, false);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL type " + name + " -> " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        private static void CheckType(Assembly assembly, string typeName)
        {
            _checks++;
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                Console.WriteLine("FAIL type " + typeName + " (not found)");
                _failures++;
                return;
            }

            Console.WriteLine("PASS type " + typeName);
        }

        private static void CheckMethod(Assembly assembly, string typeName, string methodName)
        {
            _checks++;
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                Console.WriteLine("FAIL method " + typeName + "." + methodName + " (type not found)");
                _failures++;
                return;
            }

            MethodInfo[] candidates;
            try
            {
                candidates = FindMethods(type, methodName);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL method " + typeName + "." + methodName + " -> " + e.GetType().Name);
                _failures++;
                return;
            }

            if (candidates.Length == 0)
            {
                Console.WriteLine("FAIL method " + typeName + "." + methodName + " (not found)");
                _failures++;
                return;
            }

            Console.WriteLine("PASS method " + typeName + "." + methodName);
        }

        private static void CheckMethod(
            Assembly assembly,
            string typeName,
            string methodName,
            string returnType,
            bool isStatic,
            params string[] parameterTypes)
        {
            _checks++;
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                Console.WriteLine("FAIL method " + typeName + "." + methodName + " (type not found)");
                _failures++;
                return;
            }

            MethodInfo[] candidates;
            try
            {
                candidates = FindMethods(type, methodName);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL method " + typeName + "." + methodName + " -> " + e.GetType().Name);
                _failures++;
                return;
            }

            if (candidates.Length == 0)
            {
                Console.WriteLine("FAIL method " + typeName + "." + methodName + " (not found)");
                _failures++;
                return;
            }

            foreach (MethodInfo candidate in candidates)
            {
                ParameterInfo[] parameters;
                try
                {
                    parameters = candidate.GetParameters();
                }
                catch (Exception e)
                {
                    Console.WriteLine("FAIL method " + typeName + "." + methodName + " -> " + e.GetType().Name);
                    _failures++;
                    return;
                }

                if (candidate.IsStatic != isStatic
                    || FormatType(candidate.ReturnType) != ResolveTypeName(returnType)
                    || parameters.Length != parameterTypes.Length)
                {
                    continue;
                }

                bool parametersMatch = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (FormatType(parameters[i].ParameterType) != ResolveTypeName(parameterTypes[i]))
                    {
                        parametersMatch = false;
                        break;
                    }
                }

                if (parametersMatch)
                {
                    Console.WriteLine("PASS method " + typeName + "." + methodName + " : "
                        + ExpectedMethod(methodName, returnType, isStatic, parameterTypes));
                    return;
                }
            }

            Console.WriteLine("FAIL method " + typeName + "." + methodName + " signature mismatch; expected "
                + ExpectedMethod(methodName, returnType, isStatic, parameterTypes)
                + "; actual: " + string.Join(" | ", candidates.Select(FormatMethod)));
            _failures++;
        }

        private static void CheckProperty(Assembly assembly, string typeName, string propertyName)
        {
            _checks++;
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                Console.WriteLine("FAIL property " + typeName + "." + propertyName + " (type not found)");
                _failures++;
                return;
            }

            PropertyInfo property;
            try
            {
                property = type.GetProperties(AllMembers).FirstOrDefault(p => p.Name == propertyName);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL property " + typeName + "." + propertyName + " -> " + e.GetType().Name);
                _failures++;
                return;
            }

            if (property == null)
            {
                Console.WriteLine("FAIL property " + typeName + "." + propertyName + " (not found)");
                _failures++;
                return;
            }

            Console.WriteLine("PASS property " + typeName + "." + propertyName);
        }

        private static void CheckProperty(
            Assembly assembly,
            string typeName,
            string propertyName,
            string propertyType,
            bool isStatic)
        {
            _checks++;
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                Console.WriteLine("FAIL property " + typeName + "." + propertyName + " (type not found)");
                _failures++;
                return;
            }

            PropertyInfo property;
            try
            {
                property = type.GetProperties(AllMembers).FirstOrDefault(p => p.Name == propertyName);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL property " + typeName + "." + propertyName + " -> " + e.GetType().Name);
                _failures++;
                return;
            }

            if (property == null)
            {
                Console.WriteLine("FAIL property " + typeName + "." + propertyName + " (not found)");
                _failures++;
                return;
            }

            MethodInfo accessor = property.GetGetMethod(true) ?? property.GetSetMethod(true);
            bool actualStatic = accessor != null && accessor.IsStatic;
            string actualType = FormatType(property.PropertyType);
            if (actualStatic != isStatic || actualType != ResolveTypeName(propertyType))
            {
                Console.WriteLine("FAIL property " + typeName + "." + propertyName + " expected "
                    + (isStatic ? "static " : "instance ") + ResolveTypeName(propertyType)
                    + ", actual " + (actualStatic ? "static " : "instance ") + actualType);
                _failures++;
                return;
            }

            Console.WriteLine("PASS property " + typeName + "." + propertyName + " : "
                + (isStatic ? "static " : "instance ") + actualType);
        }

        private static void CheckField(Assembly assembly, string typeName, string fieldName)
        {
            _checks++;
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                Console.WriteLine("FAIL field " + typeName + "." + fieldName + " (type not found)");
                _failures++;
                return;
            }

            FieldInfo field;
            try
            {
                field = type.GetField(fieldName, AllMembers);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL field " + typeName + "." + fieldName + " -> " + e.GetType().Name);
                _failures++;
                return;
            }

            if (field == null)
            {
                Console.WriteLine("FAIL field " + typeName + "." + fieldName + " (not found)");
                _failures++;
                return;
            }

            Console.WriteLine("PASS field " + typeName + "." + fieldName);
        }

        private static void CheckField(
            Assembly assembly,
            string typeName,
            string fieldName,
            string fieldType,
            bool isStatic)
        {
            _checks++;
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                Console.WriteLine("FAIL field " + typeName + "." + fieldName + " (type not found)");
                _failures++;
                return;
            }

            FieldInfo field;
            try
            {
                field = type.GetField(fieldName, AllMembers);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL field " + typeName + "." + fieldName + " -> " + e.GetType().Name);
                _failures++;
                return;
            }

            if (field == null)
            {
                Console.WriteLine("FAIL field " + typeName + "." + fieldName + " (not found)");
                _failures++;
                return;
            }

            string actualType = FormatType(field.FieldType);
            if (field.IsStatic != isStatic || actualType != ResolveTypeName(fieldType))
            {
                Console.WriteLine("FAIL field " + typeName + "." + fieldName + " expected "
                    + (isStatic ? "static " : "instance ") + ResolveTypeName(fieldType)
                    + ", actual " + (field.IsStatic ? "static " : "instance ") + actualType);
                _failures++;
                return;
            }

            Console.WriteLine("PASS field " + typeName + "." + fieldName + " : "
                + (isStatic ? "static " : "instance ") + actualType);
        }

        private static void CheckConst(
            Assembly assembly,
            string typeName,
            string fieldName,
            string fieldType,
            object expectedValue)
        {
            _checks++;
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                Console.WriteLine("FAIL const " + typeName + "." + fieldName + " (type not found)");
                _failures++;
                return;
            }

            FieldInfo field;
            try
            {
                field = type.GetField(fieldName, AllMembers);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL const " + typeName + "." + fieldName + " -> " + e.GetType().Name);
                _failures++;
                return;
            }

            if (field == null)
            {
                Console.WriteLine("FAIL const " + typeName + "." + fieldName + " (not found)");
                _failures++;
                return;
            }

            string actualType = FormatType(field.FieldType);
            object actualValue = field.IsLiteral ? field.GetRawConstantValue() : null;
            if (!field.IsLiteral
                || actualType != ResolveTypeName(fieldType)
                || !object.Equals(actualValue, expectedValue))
            {
                Console.WriteLine("FAIL const " + typeName + "." + fieldName + " expected "
                    + ResolveTypeName(fieldType) + " = " + Convert.ToString(expectedValue)
                    + ", actual literal=" + field.IsLiteral + " " + actualType + " = "
                    + Convert.ToString(actualValue));
                _failures++;
                return;
            }

            Console.WriteLine("PASS const " + typeName + "." + fieldName + " = " + Convert.ToString(actualValue));
        }
    }
}
