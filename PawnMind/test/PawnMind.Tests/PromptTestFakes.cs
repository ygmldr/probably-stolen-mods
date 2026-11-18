using System;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// Shared fixtures for the S8 modal-prompt tests. The fake ports mirror the
    /// production split: Core decides whether an answer is legal, Bridge reads
    /// once before the single native click and once afterwards.
    /// </summary>
    internal static class PromptTestData
    {
        public const string WarningBody = "丢在地上的物品将被遗弃并永久丢失。确定要离开吗？";

        public static PendingPrompt Prompt(
            bool singleButton = false,
            string body = WarningBody,
            string confirmText = "离开",
            string cancelText = "取消")
        {
            return new PendingPrompt
            {
                Kind = PromptKinds.Confirmation,
                Target = PromptTargets.GenericConfirmation,
                Body = body,
                ConfirmText = confirmText,
                CancelText = cancelText,
                SingleButton = singleButton,
                ConfirmEnabled = true,
                CancelEnabled = !singleButton
            };
        }

        /// <summary>R5-B2: the native negotiation-tablet confirmation modal.</summary>
        public static PendingPrompt NegotiationConfirm()
        {
            return new PendingPrompt
            {
                Kind = PromptKinds.NegotiationConfirm,
                Target = PromptTargets.NegotiationTabletConfirm,
                Body = "卖出的总价超出顾客预算，仍要卖出吗？",
                ConfirmText = "是",
                CancelText = "否",
                SingleButton = false,
                ConfirmEnabled = true,
                CancelEnabled = true
            };
        }

        public static PromptSnapshot OpenWith(PendingPrompt prompt)
        {
            return new PromptSnapshot
            {
                Readable = true,
                Open = true,
                Prompt = prompt
            };
        }

        public static PromptSnapshot NegotiationConfirmOpen()
        {
            return OpenWith(NegotiationConfirm());
        }

        public static PromptSnapshot Closed()
        {
            return new PromptSnapshot { Readable = true, Open = false };
        }

        public static PromptSnapshot Open(bool singleButton = false)
        {
            return new PromptSnapshot
            {
                Readable = true,
                Open = true,
                Prompt = Prompt(singleButton)
            };
        }

        public static PromptSnapshot Unreadable()
        {
            return new PromptSnapshot
            {
                Readable = false,
                Open = false,
                Error = PromptErrors.ReadFailed
            };
        }

        public static PromptStateSummary State(
            int day = 3,
            long cash = 550,
            bool outside = false,
            string location = null,
            string shopState = null)
        {
            return new PromptStateSummary
            {
                Readable = true,
                Day = day,
                Cash = cash,
                Outside = outside,
                Location = location,
                ShopState = shopState
            };
        }
    }

    internal sealed class FakePromptPort : IPromptPort
    {
        public PromptSnapshot Snapshot = PromptTestData.Closed();
        public PromptSnapshot AfterAnswer;
        public PromptAnswerOutcome Outcome;
        public bool ThrowOnRead;
        public bool ThrowOnAnswer;
        public int Reads;
        public int AnswerCalls;
        public string LastChoice;
        public string LastKind;

        public PromptSnapshot Read()
        {
            Reads++;
            if (ThrowOnRead) throw new InvalidOperationException("prompt read failed");
            return Snapshot;
        }

        public PromptAnswerOutcome Answer(PendingPrompt prompt, string choice)
        {
            AnswerCalls++;
            LastChoice = choice;
            LastKind = prompt == null ? null : prompt.Kind;
            if (ThrowOnAnswer) throw new InvalidOperationException("native prompt callback failed");
            if (Outcome != null) return Outcome;
            if (AfterAnswer != null) Snapshot = AfterAnswer;
            return PromptAnswerOutcome.Called();
        }
    }

    internal sealed class FakePromptStatePort : IPromptStatePort
    {
        public PromptStateSummary Summary = PromptTestData.State();
        public bool Throw;
        public int Reads;

        public PromptStateSummary ReadPromptState()
        {
            Reads++;
            if (Throw) throw new InvalidOperationException("state read failed");
            return Summary;
        }
    }

    internal sealed class FakeNightReturnPort : INightReturnPort
    {
        public bool Outside = true;
        public int Items;
        public bool ThrowOnReturn;
        public Action OnReturn;
        public int ReturnCalls;

        public bool IsOutside
        {
            get { return Outside; }
        }

        public int ItemsOnGround
        {
            get { return Items; }
        }

        public void Return()
        {
            ReturnCalls++;
            if (ThrowOnReturn) throw new InvalidOperationException("native return failed");
            if (OnReturn != null) OnReturn();
        }
    }
}
