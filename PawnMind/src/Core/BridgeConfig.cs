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

        public BridgeConfig()
        {
            Port = DefaultPort;
            Token = string.Empty;
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
                + "token=" + Token + "\n";
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
            }

            BridgeConfig parsed = Create(port, token);
            parsed.AllowTestItems = allowTestItems;
            if (!parsed.TryValidate(out error)) return false;

            config = parsed;
            return true;
        }
    }
}
