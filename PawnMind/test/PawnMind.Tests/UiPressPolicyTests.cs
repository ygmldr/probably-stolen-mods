using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// The generic press op may only dismiss the two overlays that have no typed
    /// adapter. Everything else must stay refused (it would be an arbitrary UI
    /// callback invocation).
    /// </summary>
    internal static class UiPressPolicyTests
    {
        [Test]
        public static void Overlay_Dismiss_Buttons_Are_Allowed()
        {
            Assert.True(UiPressPolicy.IsAllowedControl(
                "CanvasScreenLayer2/_ControlOverLay/CloseOverlayButton"),
                "the control-hint overlay close button");
            Assert.True(UiPressPolicy.IsAllowedControl(
                "CanvasScreenLayer2/_DialogueOptionUI/Panel/CloseButton"),
                "the dialogue settings close button");
            Assert.True(UiPressPolicy.IsAllowedControl(
                "CanvasScreenLayer2/_DialogueOptionUI/Panel/ApplyButton"),
                "the dialogue settings apply button");
            Assert.True(UiPressPolicy.IsAllowedControl(
                "NegotiationTablet/Confirm/Buttons/Button"),
                "the day-end confirmation raises a modal nothing else can answer");
        }

        [Test]
        public static void Unity_Duplicate_Name_Suffix_Is_Ignored()
        {
            Assert.True(UiPressPolicy.IsAllowedControl(
                "CanvasScreenLayer2/_DialogueOptionUI/Panel/CloseButton (1)"),
                "Unity renames the second CloseButton to 'CloseButton (1)'");
            Assert.True(UiPressPolicy.IsAllowedControl(
                "CanvasScreenLayer2/_DialogueOptionUI/Panel/ApplyButton (12)"),
                "multi-digit duplicates");
        }

        [Test]
        public static void Arbitrary_Controls_Stay_Forbidden()
        {
            foreach (string path in new[]
            {
                "CanvasPaperUI/NegotiationTablet/Close",
                "CanvasPaperUI/NegotiationTablet/Buttons/Button",
                "CanvasScreenLayer2/_ControlOverLay/OpenOverlayButton",
                "CanvasStoreUI/DebugPanel/SpawnItem",
                "CanvasScreenLayer2/_DialogueOptionUI/Panel/ResetButton",
                "_ControlOverLay/CloseOverlayButton",
                "",
                "   ",
                "CanvasScreenLayer2/_ControlOverLay/CloseOverlayButtonX"
            })
            {
                Assert.False(UiPressPolicy.IsAllowedControl(path),
                    "must stay refused: " + path);
            }
        }

        [Test]
        public static void Partial_Or_Malformed_Suffixes_Are_Not_Stripped()
        {
            Assert.False(UiPressPolicy.IsAllowedControl(
                "CanvasScreenLayer2/_ControlOverLay/CloseOverlayButton (x)"),
                "a non-numeric suffix is part of the name");
            Assert.False(UiPressPolicy.IsAllowedControl(
                "CanvasScreenLayer2/_ControlOverLay/CloseOverlayButton ("),
                "an unterminated suffix is part of the name");
        }
    }
}
