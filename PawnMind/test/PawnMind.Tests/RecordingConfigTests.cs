using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class RecordingConfigTests
    {
        private const string Token = "0123456789abcdef0123456789abcdef";

        private static string BaseConfig()
        {
            return "port=16000\ntoken=" + Token + "\n";
        }

        [Test]
        public static void Config_Recording_Defaults_Are_Locked_And_Not_Auto_Filled()
        {
            BridgeConfig config = BridgeConfig.Create(BridgeConfig.DefaultPort, Token);

            Assert.Equal(string.Empty, config.RecordingMode, "recording mode defaults empty");
            Assert.Null(config.RecordingSlot, "recording slot defaults unset");
            Assert.False(config.RecordingIsolationAck, "isolation ack defaults false");
            Assert.False(config.RecordingDedicatedSlotAck, "dedicated-slot ack defaults false");
            Assert.False(config.RecordingSlotInvalid, "default slot is not marked invalid");

            RecordingSettings settings = RecordingSettings.From(config);
            Assert.Equal(RecordingSaveErrors.Unconfigured, settings.Error,
                "default config must not unlock recording writes");

            string text = config.Serialize();
            Assert.Contains(text, "recording_mode=\n", "mode key stays empty");
            Assert.Contains(text, "recording_slot=\n", "slot key stays empty");
            Assert.Contains(text, "recording_isolation_ack=false", "ack stays false");
            Assert.False(text.Contains("recording_isolation_ack=true"),
                "serialization must never auto-approve isolation");
            Assert.Contains(text, "recording_dedicated_slot_ack=false",
                "dedicated-slot ack stays false");
            Assert.False(text.Contains("recording_dedicated_slot_ack=true"),
                "serialization must never auto-approve a dedicated slot");
            Assert.Contains(text, "#   recording_isolation_ack",
                "serialized comment documents the isolation opt-in");
            Assert.Contains(text, "#   recording_dedicated_slot_ack",
                "serialized comment documents the dedicated-slot opt-in");
            Assert.Contains(text, "isolated environment",
                "serialized comment explains the isolation declaration");
            Assert.Contains(text, "existing played profile + new dedicated slot",
                "serialized comment explains the dedicated-slot declaration");
        }

        [Test]
        public static void Config_Recording_Triplet_Parses_And_RoundTrips()
        {
            string text = BaseConfig()
                + "recording_mode=recording\n"
                + "recording_slot=7\n"
                + "recording_isolation_ack=true\n";

            BridgeConfig parsed;
            string error;
            Assert.True(BridgeConfig.TryParse(text, out parsed, out error), "parse: " + error);
            Assert.Equal("recording", parsed.RecordingMode, "mode parsed");
            Assert.Equal(7, parsed.RecordingSlot.Value, "slot parsed");
            Assert.True(parsed.RecordingIsolationAck, "ack parsed");

            RecordingSettings settings = RecordingSettings.From(parsed);
            Assert.Null(settings.Error, "valid triplet must resolve");
            Assert.Equal(7, settings.Slot.Value, "resolved slot");

            string serialized = parsed.Serialize();
            Assert.Contains(serialized, "recording_mode=recording", "mode round-trip");
            Assert.Contains(serialized, "recording_slot=7", "slot round-trip");
            Assert.Contains(serialized, "recording_isolation_ack=true", "ack round-trip");
        }

        [Test]
        public static void Config_Recording_Dedicated_Slot_Ack_Parses_And_RoundTrips()
        {
            string text = BaseConfig()
                + "recording_mode=recording\n"
                + "recording_slot=40\n"
                + "recording_isolation_ack=false\n"
                + "recording_dedicated_slot_ack=true\n";

            BridgeConfig parsed;
            string error;
            Assert.True(BridgeConfig.TryParse(text, out parsed, out error), "parse: " + error);
            Assert.False(parsed.RecordingIsolationAck, "isolation ack stays an honest false");
            Assert.True(parsed.RecordingDedicatedSlotAck, "dedicated-slot ack parsed");

            RecordingSettings settings = RecordingSettings.From(parsed);
            Assert.Null(settings.Error, "dedicated-slot ack alone unlocks writes");
            Assert.Equal(40, settings.Slot.Value, "resolved slot");
            Assert.False(settings.IsolationAcknowledged, "isolation was not declared");
            Assert.True(settings.DedicatedSlotAcknowledged, "dedicated-slot was declared");
            Assert.Equal(RecordingAcknowledgment.DedicatedSlot, settings.Acknowledgment,
                "the effective acknowledgment is recorded");

            RecordingSlotPolicy policy = new RecordingSlotPolicy(settings);
            Assert.Equal(RecordingSlotState.Armed, policy.State,
                "dedicated-slot ack alone arms the policy");

            BridgeConfig numeric;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig() + "recording_mode=on\nrecording_slot=40\n"
                    + "recording_dedicated_slot_ack=1\n",
                out numeric,
                out error), "parse numeric dedicated-slot ack: " + error);
            Assert.True(numeric.RecordingDedicatedSlotAck, "1 is explicit true");
            Assert.Equal(RecordingAcknowledgment.DedicatedSlot,
                RecordingSettings.From(numeric).Acknowledgment,
                "numeric dedicated-slot ack unlocks the same way");

            string serialized = parsed.Serialize();
            Assert.Contains(serialized, "recording_isolation_ack=false",
                "isolation stays false in serialization");
            Assert.Contains(serialized, "recording_dedicated_slot_ack=true",
                "dedicated-slot round-trip");

            BridgeConfig reparsed;
            Assert.True(BridgeConfig.TryParse(serialized, out reparsed, out error),
                "serialized config parses: " + error);
            Assert.True(reparsed.RecordingDedicatedSlotAck,
                "dedicated-slot survives a round-trip");
            Assert.False(reparsed.RecordingIsolationAck,
                "isolation false survives a round-trip");
        }

        [Test]
        public static void Config_Recording_Both_Acks_Prefer_Isolation()
        {
            string text = BaseConfig()
                + "recording_mode=on\n"
                + "recording_slot=7\n"
                + "recording_isolation_ack=true\n"
                + "recording_dedicated_slot_ack=true\n";

            BridgeConfig parsed;
            string error;
            Assert.True(BridgeConfig.TryParse(text, out parsed, out error), "parse: " + error);
            Assert.True(parsed.RecordingIsolationAck, "isolation parsed");
            Assert.True(parsed.RecordingDedicatedSlotAck, "dedicated-slot parsed");

            RecordingSettings settings = RecordingSettings.From(parsed);
            Assert.Null(settings.Error, "both acks arm writes");
            Assert.True(settings.IsolationAcknowledged, "isolation raw flag is preserved");
            Assert.True(settings.DedicatedSlotAcknowledged,
                "dedicated-slot raw flag is preserved");
            Assert.Equal(RecordingAcknowledgment.Isolation, settings.Acknowledgment,
                "isolation is reported first when both declarations are present");

            RecordingSlotPolicy policy = new RecordingSlotPolicy(settings);
            Assert.Equal(RecordingSlotState.Armed, policy.State, "both acks arm the policy");

            string serialized = parsed.Serialize();
            Assert.Contains(serialized, "recording_isolation_ack=true",
                "isolation round-trip");
            Assert.Contains(serialized, "recording_dedicated_slot_ack=true",
                "dedicated-slot round-trip");
        }

        [Test]
        public static void Config_Recording_Mode_Off_Or_Unknown_Stays_Unconfigured()
        {
            string[] modes = { "", "off", "disabled", "banana" };
            for (int i = 0; i < modes.Length; i++)
            {
                BridgeConfig config;
                string error;
                Assert.True(BridgeConfig.TryParse(
                    BaseConfig()
                    + "recording_mode=" + modes[i] + "\n"
                    + "recording_slot=7\n"
                    + "recording_isolation_ack=true\n",
                    out config,
                    out error), "parse mode " + i + ": " + error);

                Assert.Equal(RecordingSaveErrors.Unconfigured, RecordingSettings.From(config).Error,
                    "only an explicit recording mode may arm writes: " + modes[i]);
            }

            BridgeConfig dedicatedOff;
            string dedicatedError;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig()
                + "recording_mode=off\n"
                + "recording_slot=40\n"
                + "recording_dedicated_slot_ack=true\n",
                out dedicatedOff,
                out dedicatedError), "parse dedicated ack with off mode: " + dedicatedError);
            Assert.Equal(RecordingSaveErrors.Unconfigured,
                RecordingSettings.From(dedicatedOff).Error,
                "a dedicated-slot ack does not bypass the explicit recording mode");
        }

        [Test]
        public static void Config_Recording_On_Alias_Is_Accepted()
        {
            BridgeConfig config;
            string error;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig()
                + "recording_mode=on\n"
                + "recording_slot=0\n"
                + "recording_isolation_ack=true\n",
                out config,
                out error), "parse: " + error);

            RecordingSettings settings = RecordingSettings.From(config);
            Assert.Null(settings.Error, "on is an explicit mode");
            Assert.Equal(0, settings.Slot.Value, "slot zero is valid");
        }

        [Test]
        public static void Config_Recording_Requires_Ack()
        {
            BridgeConfig missing;
            string error;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig() + "recording_mode=on\nrecording_slot=7\n",
                out missing,
                out error), "parse missing ack: " + error);
            Assert.Equal(RecordingSaveErrors.IsolationUnacknowledged,
                RecordingSettings.From(missing).Error, "missing ack locks");
            Assert.Equal(RecordingAcknowledgment.None,
                RecordingSettings.From(missing).Acknowledgment,
                "no acknowledgment method was used when both keys are absent");

            BridgeConfig explicitFalse;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig() + "recording_mode=on\nrecording_slot=7\nrecording_isolation_ack=false\n",
                out explicitFalse,
                out error), "parse false ack: " + error);
            Assert.Equal(RecordingSaveErrors.IsolationUnacknowledged,
                RecordingSettings.From(explicitFalse).Error, "false ack locks");

            BridgeConfig bothFalse;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig() + "recording_mode=on\nrecording_slot=7\n"
                    + "recording_isolation_ack=false\n"
                    + "recording_dedicated_slot_ack=false\n",
                out bothFalse,
                out error), "parse both false acks: " + error);
            Assert.Equal(RecordingSaveErrors.IsolationUnacknowledged,
                RecordingSettings.From(bothFalse).Error,
                "explicitly false acks keep the stable unacknowledged code");
            Assert.Equal(RecordingAcknowledgment.None,
                RecordingSettings.From(bothFalse).Acknowledgment,
                "explicitly false acks are not a confirmation method");

            BridgeConfig numeric;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig() + "recording_mode=on\nrecording_slot=7\nrecording_isolation_ack=1\n",
                out numeric,
                out error), "parse numeric ack: " + error);
            Assert.Null(RecordingSettings.From(numeric).Error, "1 is explicit true");
        }

        [Test]
        public static void Config_Recording_Requires_Valid_Slot()
        {
            BridgeConfig missing;
            string error;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig() + "recording_mode=on\nrecording_isolation_ack=true\n",
                out missing,
                out error), "parse missing slot: " + error);
            Assert.Null(missing.RecordingSlot, "missing slot stays unset");
            Assert.Equal(RecordingSaveErrors.SlotInvalid, RecordingSettings.From(missing).Error,
                "missing slot must use the invalid-slot code");

            BridgeConfig dedicatedMissing;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig() + "recording_mode=on\nrecording_dedicated_slot_ack=true\n",
                out dedicatedMissing,
                out error), "parse dedicated ack without slot: " + error);
            Assert.Equal(RecordingSaveErrors.SlotInvalid,
                RecordingSettings.From(dedicatedMissing).Error,
                "dedicated-slot ack still requires an explicit slot");
            Assert.Equal(RecordingAcknowledgment.None,
                RecordingSettings.From(dedicatedMissing).Acknowledgment,
                "an invalid slot never reports an effective acknowledgment");

            BridgeConfig negative;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig() + "recording_mode=on\nrecording_slot=-1\nrecording_isolation_ack=true\n",
                out negative,
                out error), "parse negative slot: " + error);
            Assert.Equal(RecordingSaveErrors.SlotInvalid, RecordingSettings.From(negative).Error,
                "negative slot must be rejected");
        }

        [Test]
        public static void Config_Recording_Invalid_Slot_Text_Does_Not_Reject_Whole_Config()
        {
            string text = BaseConfig()
                + "recording_mode=on\n"
                + "recording_slot=seven\n"
                + "recording_isolation_ack=true\n";

            BridgeConfig parsed;
            string error;
            Assert.True(BridgeConfig.TryParse(text, out parsed, out error),
                "an invalid recording slot must not force a config regeneration: " + error);
            Assert.Null(parsed.RecordingSlot, "invalid text leaves the typed slot unset");
            Assert.True(parsed.RecordingSlotInvalid, "invalid text is remembered");
            Assert.Equal("seven", parsed.RecordingSlotRaw, "raw invalid text is preserved");
            Assert.Equal(RecordingSaveErrors.SlotInvalid, RecordingSettings.From(parsed).Error,
                "invalid text locks with the stable slot code");

            string serialized = parsed.Serialize();
            Assert.Contains(serialized, "recording_slot=seven",
                "invalid value is preserved instead of silently cleared");
            Assert.Contains(serialized, "recording_isolation_ack=true",
                "preserving an invalid slot must keep the other explicit fields");
        }

        [Test]
        public static void Config_Recording_Valid_Slot_Setter_Tracks_Raw_Text()
        {
            BridgeConfig config = BridgeConfig.Create(BridgeConfig.DefaultPort, Token);
            config.RecordingMode = "on";
            config.RecordingSlot = 12;
            config.RecordingIsolationAck = true;

            Assert.Equal("12", config.RecordingSlotRaw, "typed setter records normalized text");
            Assert.False(config.RecordingSlotInvalid, "typed setter clears invalid flag");
            Assert.Null(RecordingSettings.From(config).Error, "typed setter yields a valid setting");
        }
    }
}
