using System;
using System.Globalization;

namespace PawnMind.Core
{
    /// <summary>
    /// Connection settings shared by the in-game bridge and the external MCP server.
    /// Stored as a tiny line-based file so the mod does not need the "files" capability:
    ///   # pawnmind bridge config v1
    ///   port=15527
    ///   token=&lt;64 hex chars&gt;
    /// </summary>
    public sealed class BridgeConfig
    {
        public const int DefaultPort = 15528;
        public const int MinPort = 1024;
        public const int MaxPort = 65535;
        public const int MinTokenLength = 32;

        public int Port { get; set; }
        public string Token { get; set; }
        public bool AllowTestItems { get; set; }

        /// <summary>
        /// User-confirmed game identity for recording (set during S8 preflight).
        /// Empty means all write actions stay locked; the mod never fills these
        /// from the currently installed build.
        /// </summary>
        public string ExpectedGameVersion { get; set; }
        public string ExpectedAssemblyMvid { get; set; }

        /// <summary>
        /// Recording opt-in. Empty/off is the default and keeps every recording
        /// write locked. The mod never writes these three values from the
        /// installed game identity or the currently active save slot.
        /// </summary>
        public string RecordingMode { get; set; }
        public int? RecordingSlot
        {
            get { return _recordingSlot; }
            set
            {
                _recordingSlot = value;
                _recordingSlotRaw = value.HasValue
                    ? value.Value.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
                _recordingSlotInvalid = false;
            }
        }
        public bool RecordingIsolationAck { get; set; }
        public string RecordingSlotRaw { get { return _recordingSlotRaw; } }
        public bool RecordingSlotInvalid { get { return _recordingSlotInvalid; } }

        private int? _recordingSlot;
        private string _recordingSlotRaw = string.Empty;
        private bool _recordingSlotInvalid;

        public BridgeConfig()
        {
            Port = DefaultPort;
            Token = string.Empty;
            ExpectedGameVersion = string.Empty;
            ExpectedAssemblyMvid = string.Empty;
            RecordingMode = string.Empty;
            RecordingIsolationAck = false;
        }

        public static BridgeConfig Create(int port, string token)
        {
            return new BridgeConfig { Port = port, Token = token ?? string.Empty };
        }

        public bool TryValidate(out string error)
        {
            if (Port < MinPort || Port > MaxPort)
            {
                error = "port out of range (" + MinPort + "-" + MaxPort + ")";
                return false;
            }

            if (string.IsNullOrEmpty(Token) || Token.Length < MinTokenLength)
            {
                error = "token must be at least " + MinTokenLength + " characters";
                return false;
            }

            for (int i = 0; i < Token.Length; i++)
            {
                char c = Token[i];
                if (char.IsWhiteSpace(c) || c == '=')
                {
                    error = "token contains an invalid character";
                    return false;
                }
            }

            error = null;
            return true;
        }

        public string Serialize()
        {
            return "# pawnmind bridge config v1\n"
                + "port=" + Port.ToString(CultureInfo.InvariantCulture) + "\n"
                + "token=" + Token + "\n"
                + "# Set expected_* only after confirming the recording game build; empty locks writes.\n"
                + "expected_game_version=" + (ExpectedGameVersion ?? string.Empty) + "\n"
                + "expected_assembly_mvid=" + (ExpectedAssemblyMvid ?? string.Empty) + "\n"
                + "# Recording is user-only: set all three keys after confirming an isolated slot.\n"
                + "recording_mode=" + (RecordingMode ?? string.Empty) + "\n"
                + "recording_slot=" + (_recordingSlot.HasValue
                    ? _recordingSlot.Value.ToString(CultureInfo.InvariantCulture)
                    : (_recordingSlotRaw ?? string.Empty)) + "\n"
                + "recording_isolation_ack=" + (RecordingIsolationAck ? "true" : "false") + "\n";
        }

        /// <summary>
        /// Parses only the recording slot text. Malformed text is remembered as
        /// invalid instead of rejecting the whole config: regenerating the file
        /// would silently discard the user's other explicit settings.
        /// </summary>
        private void SetRecordingSlotText(string raw)
        {
            _recordingSlotRaw = raw ?? string.Empty;
            _recordingSlotInvalid = false;
            _recordingSlot = null;

            if (_recordingSlotRaw.Length == 0) return;

            int value;
            if (int.TryParse(_recordingSlotRaw, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value))
            {
                _recordingSlot = value;
            }
            else
            {
                _recordingSlotInvalid = true;
            }
        }

        public static bool TryParse(string text, out BridgeConfig config, out string error)
        {
            config = null;
            error = null;

            if (text == null)
            {
                error = "config is null";
                return false;
            }

            int port = DefaultPort;
            string token = string.Empty;
            bool allowTestItems = false;
            string expectedGameVersion = string.Empty;
            string expectedAssemblyMvid = string.Empty;
            string recordingMode = string.Empty;
            string recordingSlotRaw = string.Empty;
            bool recordingIsolationAck = false;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    error = "malformed line: " + line;
                    return false;
                }

                string key = line.Substring(0, equals).Trim().ToLowerInvariant();
                string value = line.Substring(equals + 1).Trim();

                if (key == "port")
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
                    {
                        error = "invalid port: " + value;
                        return false;
                    }
                }
                else if (key == "token")
                {
                    token = value;
                }
                else if (key == "allow_test_items")
                {
                    allowTestItems = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                        || value == "1";
                }
                else if (key == "expected_game_version")
                {
                    expectedGameVersion = value;
                }
                else if (key == "expected_assembly_mvid")
                {
                    expectedAssemblyMvid = value;
                }
                else if (key == "recording_mode")
                {
                    recordingMode = value;
                }
                else if (key == "recording_slot")
                {
                    recordingSlotRaw = value;
                }
                else if (key == "recording_isolation_ack")
                {
                    recordingIsolationAck = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                        || value == "1";
                }
            }

            BridgeConfig parsed = Create(port, token);
            parsed.AllowTestItems = allowTestItems;
            parsed.ExpectedGameVersion = expectedGameVersion;
            parsed.ExpectedAssemblyMvid = expectedAssemblyMvid;
            parsed.RecordingMode = recordingMode;
            parsed.RecordingIsolationAck = recordingIsolationAck;
            parsed.SetRecordingSlotText(recordingSlotRaw);
            if (!parsed.TryValidate(out error)) return false;

            config = parsed;
            return true;
        }
    }
}
