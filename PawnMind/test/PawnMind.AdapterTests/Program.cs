using System;
using System.Reflection;

namespace PawnMind.AdapterTests
{
    /// <summary>
    /// Contract test against the installed game assembly. It does not run the mod:
    /// it fails when a member the bridge relies on disappears or changes shape.
    /// </summary>
    internal static class Program
    {
        private const string GameAssembly =
            @"D:\Program Files\steam\steamapps\common\Probably Stolen\Probably Stolen_Data\Managed\Assembly-CSharp.dll";

        private static int _failures;

        private static int Main()
        {
            if (!System.IO.File.Exists(GameAssembly))
            {
                Console.WriteLine("SKIP adapter tests: Assembly-CSharp.dll not found at");
                Console.WriteLine("  " + GameAssembly);
                return 0;
            }

            Assembly assembly = Assembly.LoadFrom(GameAssembly);

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
            CheckProperty(assembly, "BargainUIManager", "IsOpen");
            CheckMethod(assembly, "BargainUIManager", "CanOfferDiscount");

            CheckType(assembly, "Dialogue");
            CheckField(assembly, "Dialogue", "plainText");
            CheckField(assembly, "Dialogue", "choices");
            CheckField(assembly, "Dialogue", "nextDialogue");

            // Exists, but the bridge must never expose it to the AI (it is the answer key).
            CheckMethod(assembly, "AppraisalHelper", "GetCorrectBaseValue");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "adapter contract OK" : "adapter failures: " + _failures);
            return _failures == 0 ? 0 : 1;
        }

        private static Type FindType(Assembly assembly, string name)
        {
            try
            {
                return assembly.GetType(name, false);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL " + name + " -> " + e.GetType().Name + ": " + e.Message);
                _failures++;
                return null;
            }
        }

        private static void CheckType(Assembly assembly, string typeName)
        {
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                Console.WriteLine("FAIL type " + typeName);
                _failures++;
                return;
            }

            Console.WriteLine("PASS type " + typeName);
        }

        private static void CheckMethod(Assembly assembly, string typeName, string methodName)
        {
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                _failures++;
                return;
            }

            bool found = false;
            try
            {
                foreach (MethodInfo candidate in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (candidate.Name == methodName) { found = true; break; }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL method " + typeName + "." + methodName + " -> " + e.GetType().Name);
                _failures++;
                return;
            }

            if (!found)
            {
                Console.WriteLine("FAIL method " + typeName + "." + methodName);
                _failures++;
                return;
            }

            Console.WriteLine("PASS method " + typeName + "." + methodName);
        }

        private static void CheckProperty(Assembly assembly, string typeName, string propertyName)
        {
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                _failures++;
                return;
            }

            bool found = false;
            try
            {
                foreach (PropertyInfo candidate in type.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (candidate.Name == propertyName) { found = true; break; }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL property " + typeName + "." + propertyName + " -> " + e.GetType().Name);
                _failures++;
                return;
            }

            if (!found)
            {
                Console.WriteLine("FAIL property " + typeName + "." + propertyName);
                _failures++;
                return;
            }

            Console.WriteLine("PASS property " + typeName + "." + propertyName);
        }

        private static void CheckField(Assembly assembly, string typeName, string fieldName)
        {
            Type type = FindType(assembly, typeName);
            if (type == null)
            {
                _failures++;
                return;
            }

            FieldInfo field = type.GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (field == null)
            {
                Console.WriteLine("FAIL field " + typeName + "." + fieldName);
                _failures++;
                return;
            }

            Console.WriteLine("PASS field " + typeName + "." + fieldName);
        }
    }
}
