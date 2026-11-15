using System;
using PawnMind.Core;

namespace PawnMind.Game
{
    /// <summary>
    /// Tutorial hint adapter. The master switch is PlayerStore.tutorialEnable;
    /// hiding the already-visible hints additionally calls
    /// TutorialUIManager.CloseAllTutorial and hides StoreUIManager.tutorialGroup.
    /// This deliberately does not call HandleSkipIntro, which would also unlock
    /// services and change story flags.
    /// </summary>
    internal sealed class TutorialAdapter : ITutorialPort
    {
        public static readonly TutorialAdapter Instance = new TutorialAdapter();

        public TutorialSnapshot ReadTutorial()
        {
            var snapshot = new TutorialSnapshot();
            try
            {
                if (!PlayerStore.HasInstance)
                {
                    snapshot.Error = "not_in_game";
                    return snapshot;
                }

                PlayerStore store = PlayerStore.Instance;
                if (store == null)
                {
                    snapshot.Error = TutorialRules.Unavailable;
                    return snapshot;
                }

                snapshot.Readable = true;
                snapshot.Enabled = store.tutorialEnable;

                StoreUIManager ui = StoreUIManager.Instance;
                if (ui != null && ui.tutorialGroup != null)
                {
                    snapshot.GroupReadable = true;
                    snapshot.HintsHidden = !ui.tutorialGroup.activeSelf;
                }
            }
            catch (Exception)
            {
                snapshot = new TutorialSnapshot
                {
                    Readable = false,
                    Error = TutorialRules.Unavailable
                };
            }

            return snapshot;
        }

        public void DisableHints()
        {
            PlayerStore store = PlayerStore.Instance;
            if (store == null) throw new InvalidOperationException("not_in_game");

            store.tutorialEnable = false;

            TutorialUIManager tutorial = TutorialUIManager.Instance;
            if (tutorial != null) tutorial.CloseAllTutorial();

            StoreUIManager ui = StoreUIManager.Instance;
            if (ui != null && ui.tutorialGroup != null) ui.tutorialGroup.SetActive(false);
        }
    }
}
