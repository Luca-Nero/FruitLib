using MelonLoader;

namespace FruitLib
{
    public static class FruitVersion
    {
        internal const string VersionConst = "4.0.0";

        public static string Current => VersionConst;

        public static readonly int Major;
        public static readonly int Minor;
        public static readonly int Patch;

        public const string MelonName   = "FruitLib";
        public const string MelonAuthor = "Luca_Nero";

        static FruitVersion()
        {
            var p = VersionConst.Split('.');
            int major = 0, minor = 0, patch = 0;
            if (p.Length > 0) int.TryParse(p[0], out major);
            if (p.Length > 1) int.TryParse(p[1], out minor);
            if (p.Length > 2) int.TryParse(p[2], out patch);
            Major = major; Minor = minor; Patch = patch;
        }

        public static bool AtLeast(int major, int minor = 0, int patch = 0)
        {
            if (Major != major) return Major > major;
            if (Minor != minor) return Minor > minor;
            return Patch >= patch;
        }

        public static bool Require(string modName, int major, int minor = 0, int patch = 0)
        {
            if (AtLeast(major, minor, patch)) return true;

            MelonLogger.Error(
                $"[FruitLib] '{modName}' requires FruitLib {major}.{minor}.{patch} or newer, " +
                $"but {Current} is installed '{modName}' will not start. " +
                $"Update FruitLib.dll in the Mods folder.");
            return false;
        }
    }
}
