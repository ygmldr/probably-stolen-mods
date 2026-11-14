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

            BridgeConfig explicitFalse;
            Assert.True(BridgeConfig.TryParse(
                BaseConfig() + "recording_mode=on\nrecording_slot=7\nrecording_isolation_ack=false\n",
                out explicitFalse,
                out error), "parse false ack: " + error);
            Assert.Equal(RecordingSaveErrors.IsolationUnacknowledged,
                RecordingSettings.From(explicitFalse).Error, "false ack locks");

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
