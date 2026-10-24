using System;
using System.Linq;
using System.Reflection;
using SimHub.Plugins;

namespace RennsportPlugin
{
    /// <summary>
    /// Stops SimHub from detecting its built-in "Rennsport" game (shared memory reader, SimHub 9.14+).
    /// Both games detect the same process, so with automatic game switching SimHub keeps jumping from
    /// "Rennsport (API)" to the built-in one. SimHub reads a game's process names once from SimHubWPF.exe.config
    /// ("RennsportProcesses") into the private SupportedGameManager._ProcessNames; an empty array means
    /// "never running", null makes SimHub read the config again. Runtime only, no SimHub files are touched.
    /// </summary>
    internal static class BuiltInGameBlocker
    {
        private const string BuiltInCode = "Rennsport";

        private static readonly FieldInfo ProcessNamesField =
            typeof(SupportedGameManager).GetField("_ProcessNames", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Blocks or unblocks the built-in game; returns a line for the log.</summary>
        public static string Apply(bool block)
        {
            try
            {
                var game = Configuration.GetGamesSnapshot()
                    .FirstOrDefault(g => g != null && g.GetType() == typeof(SupportedGameManager) && g.Code == BuiltInCode);
                if (game == null) return "SimHub has no built-in Rennsport game, nothing to block";
                if (ProcessNamesField == null || ProcessNamesField.FieldType != typeof(string[]))
                    return "Cannot block SimHub's built-in Rennsport game (SimHub internals changed)";

                ProcessNamesField.SetValue(game, block ? new string[0] : null);
                return block
                    ? "SimHub's built-in Rennsport game will not be auto-detected (use \"Rennsport (API)\")"
                    : "SimHub's built-in Rennsport game detection restored";
            }
            catch (Exception ex)
            {
                return "Cannot block SimHub's built-in Rennsport game: " + ex.Message;
            }
        }
    }
}
