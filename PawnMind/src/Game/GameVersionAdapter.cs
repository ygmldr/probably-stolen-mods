using System;
using System.Reflection;
using PawnMind.Core;

namespace PawnMind.Game
{
    /// <summary>
    /// Reads the installed game identity from confirmed public metadata only.
    /// Called on the Unity main thread while the mod is enabled; callers cache the
    /// resulting strings so HTTP handling never needs to touch Unity. No extra file
    /// capability, no save access and no hidden fields are used.
    /// </summary>
    internal static class GameVersionAdapter
    {
        public static GameIdentity Read()
        {
            return new GameIdentity(ReadVersionNumber(), ReadAssemblyMvid());
        }

        private static string ReadVersionNumber()
        {
            try
            {
                // VERSION_NUMBER is a public const string. Read it through metadata
                // rather than a direct reference: a direct const access would be
                // inlined at mod compile time and would report the build we compiled
                // against instead of the assembly actually loaded by the game.
                FieldInfo field = typeof(PlayerStore).GetField(
                    "VERSION_NUMBER",
                    BindingFlags.Public | BindingFlags.Static);
                if (field == null) return null;

                object value = null;
                if (field.IsLiteral)
                {
                    try
                    {
                        value = field.GetRawConstantValue();
                    }
                    catch (Exception)
                    {
                        // Fall back to the generic getter below.
                    }
                }

                if (value == null) value = field.GetValue(null);
                return value == null ? null : Convert.ToString(value);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadAssemblyMvid()
        {
            try
            {
                Guid mvid = typeof(PlayerStore).Assembly.ManifestModule.ModuleVersionId;
                return mvid.ToString("D");
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
