using PawnMind.Core;
using static PawnMind.Tests.Assert;

namespace PawnMind.Tests
{
    /// <summary>
    /// D3 module: shop/day async semantics. Ending the day, sleeping and opening
    /// the shop are native animations; a single unchanged readback is not proof
    /// that nothing happened, and "the day changed" is not proof that the save
    /// file was written.
    ///
    /// Evidence level: L0-static (pure classification). The real 2.75s shutter
    /// animation, blackscreen and save-file writes remain NOT_RUN.
    /// </summary>
    internal static class OperationSemanticsTests
    {
        // ----------------------------------------------------------- end_day

        [Test]
        public static void EndDay_Completed_Only_When_The_Shop_Is_Closed()
        {
            Equal(ExecutionStatus.Completed,
                ShopOperationSemantics.EndDayExecution(true, true, null, true),
                "dispatched and the readback shows CLOSED");

            Equal(ExecutionStatus.Running,
                ShopOperationSemantics.EndDayExecution(true, true, null, false),
                "the shutter animation is still running; not a no-op, not a success");
        }

        [Test]
        public static void EndDay_Unreadable_Readback_Is_Uncertain()
        {
            Equal(ExecutionStatus.Uncertain,
                ShopOperationSemantics.EndDayExecution(true, false, "readback_unavailable", false),
                "dispatched but the result cannot be observed");

            Equal(ExecutionStatus.Uncertain,
                ShopOperationSemantics.EndDayExecution(true, true, "readback_regression", true),
                "a regression is never a success");
        }

        [Test]
        public static void EndDay_Not_Dispatched_Is_Rejected()
        {
            Equal(ExecutionStatus.Rejected,
                ShopOperationSemantics.EndDayExecution(false, false, null, false),
                "a guard refusal never dispatched anything");
        }

        // ------------------------------------------------------------- sleep

        [Test]
        public static void Sleep_Separates_Day_Change_From_Save_Evidence()
        {
            SleepSemantics changed = ShopOperationSemantics.Sleep(
                dispatched: true,
                readbackReadable: true,
                readbackError: null,
                dayBefore: 3,
                dayAfter: 4,
                saveEvidence: ShopOperationSemantics.SaveUnknown);

            True(changed.DayChanged, "the day advanced");
            Equal(ExecutionStatus.Completed, changed.Execution,
                "a visible day change completes the request");
            Equal("save_unknown", changed.SaveEvidence,
                "the day change is not proof that the save file was written");
            True(changed.SaveUnknown, "save unknown is explicit");
            False(changed.SaveCompleted, "never claim a save without evidence");
        }

        [Test]
        public static void Sleep_Without_A_Day_Change_Is_Still_Running()
        {
            SleepSemantics same = ShopOperationSemantics.Sleep(
                dispatched: true,
                readbackReadable: true,
                readbackError: null,
                dayBefore: 3,
                dayAfter: 3,
                saveEvidence: ShopOperationSemantics.SaveUnknown);

            False(same.DayChanged, "no day change yet");
            Equal(ExecutionStatus.Running, same.Execution,
                "the blackscreen sequence may still be running: pending, not no_effect");
        }

        [Test]
        public static void Sleep_With_Save_Evidence_Reports_Both_Facts()
        {
            SleepSemantics saved = ShopOperationSemantics.Sleep(
                dispatched: true,
                readbackReadable: true,
                readbackError: null,
                dayBefore: 3,
                dayAfter: 4,
                saveEvidence: ShopOperationSemantics.SaveCompleted);

            True(saved.SaveCompleted, "save completed is explicit");
            False(saved.SaveUnknown, "not unknown when evidence exists");
            Equal("save_completed", saved.SaveEvidence, "stable code");
        }

        [Test]
        public static void Sleep_Unreadable_Or_Regression_Is_Uncertain()
        {
            SleepSemantics unreadable = ShopOperationSemantics.Sleep(
                true, false, "readback_unavailable", 3, 4, ShopOperationSemantics.SaveUnknown);
            Equal(ExecutionStatus.Uncertain, unreadable.Execution, "unreadable");

            SleepSemantics regression = ShopOperationSemantics.Sleep(
                true, true, "readback_regression", 4, 3, ShopOperationSemantics.SaveUnknown);
            Equal(ExecutionStatus.Uncertain, regression.Execution, "regression");

            SleepSemantics rejected = ShopOperationSemantics.Sleep(
                false, false, null, 3, 3, ShopOperationSemantics.SaveUnknown);
            Equal(ExecutionStatus.Rejected, rejected.Execution, "not dispatched");
        }

        // ---------------------------------------------------------- open_shop

        [Test]
        public static void P0_OPEN_01_Repeated_Open_While_Shutter_Busy_Is_Refused()
        {
            Equal(OpenShopErrors.ShutterBusy,
                OpenShopPolicy.Evaluate(true, true, false, false, "OPEN", true),
                "the animation lock refuses a second click");
        }

        [Test]
        public static void P0_OPEN_01_Open_Outside_Morning_Is_Refused()
        {
            Equal(OpenShopErrors.ShopNotMorning,
                OpenShopPolicy.Evaluate(true, false, false, false, "OPEN", true),
                "already open: the native button ignores the click");
            Equal(OpenShopErrors.ShopNotMorning,
                OpenShopPolicy.Evaluate(true, false, false, false, "CLOSED", true),
                "closed day cannot be opened again");
            Equal(null,
                OpenShopPolicy.Evaluate(true, false, false, false, "MORNING", true),
                "no refusal in the morning");
        }

        [Test]
        public static void OpenShop_Arrest_And_Shootout_Block_First()
        {
            Equal(OpenShopErrors.BusyArrest,
                OpenShopPolicy.Evaluate(true, false, true, false, "MORNING", true),
                "arrest state");
            Equal(OpenShopErrors.BusyShootout,
                OpenShopPolicy.Evaluate(true, false, false, true, "MORNING", true),
                "shootout state");
            Equal(OpenShopErrors.CannotOpen,
                OpenShopPolicy.Evaluate(true, false, false, false, "MORNING", false),
                "native CanStartDay refused");
        }

        [Test]
        public static void OpenShop_Unreadable_Or_Not_In_Game_Is_Refused()
        {
            Equal(OpenShopErrors.NotInGame,
                OpenShopPolicy.Evaluate(false, false, false, false, "MORNING", true),
                "no game");
            Equal(null,
                OpenShopPolicy.Evaluate(true, false, false, false, "MORNING", true),
                "the morning with an idle shutter is legal");
        }
    }
}
