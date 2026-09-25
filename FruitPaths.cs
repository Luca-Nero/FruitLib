using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MelonLoader;

namespace FruitLib
{
    /// <summary>
    /// Where mods keep their files. Configs live in MelonLoader's UserData folder, not next to
    /// the DLLs in Mods: Mods stays a folder of mods, and settings survive a player clearing
    /// it out to reinstall.
    /// </summary>
    public static class FruitPaths
    {
        private static string _userData;
        private static readonly HashSet<string> _migrated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>MelonLoader's UserData folder, created if missing.</summary>
        public static string UserData
        {
            get
            {
                if (_userData != null) return _userData;
                try
                {
                    // Reached by reflection, as FruktLink and Rewind do: MelonEnvironment moved
                    // namespace between MelonLoader versions, and a missing typeref would stop
                    // FruitLib loading at all rather than just falling back.
                    var t = typeof(MelonMod).Assembly.GetType("MelonLoader.Utils.MelonEnvironment");
                    if (t?.GetProperty("UserDataDirectory")?.GetValue(null) is string dir && !string.IsNullOrEmpty(dir))
                        _userData = dir;
                }
                catch { }

                _userData ??= Path.Combine(Directory.GetCurrentDirectory(), "UserData");
                Directory.CreateDirectory(_userData);
                return _userData;
            }
        }

        /// <summary>
        /// The path for a config file in UserData. If an older build of the calling mod left the
        /// file next to its DLL in Mods, it is moved across the first time this is asked, so an
        /// update keeps the player's settings. If both exist, UserData wins and the old one is
        /// left alone.
        /// </summary>
        /// <param name="fileName">e.g. "GGGConfig.ini"</param>
        /// <param name="owner">The mod's own assembly; locates where the file used to live.
        /// Defaults to the caller.</param>
        public static string Config(string fileName, Assembly owner = null)
        {
            string target = Path.Combine(UserData, fileName);
            if (!_migrated.Add(target)) return target;

            try
            {
                owner ??= Assembly.GetCallingAssembly();
                string oldDir = Path.GetDirectoryName(owner.Location);
                if (string.IsNullOrEmpty(oldDir)) return target;

                string old = Path.Combine(oldDir, fileName);
                if (!File.Exists(old) || string.Equals(Path.GetFullPath(old), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    return target;

                if (File.Exists(target))
                {
                    MelonLogger.Msg($"[FruitLib] {fileName}: using the copy in UserData; an older one is still in {oldDir} and can be deleted.");
                    return target;
                }

                File.Move(old, target);
                MelonLogger.Msg($"[FruitLib] moved {fileName} from Mods to UserData.");
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitLib] could not move {fileName} to UserData: {e.Message}"); }

            return target;
        }
    }
}
