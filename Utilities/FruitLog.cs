using MelonLoader;

namespace FruitLib
{
    /// <summary>
    /// Informational logging that stays out of the console unless the player asked for it.
    ///
    /// With a dozen mods loaded, "page built", "slot 5 in hand" and "model built" lines from
    /// each of them bury the one warning that matters. <see cref="Info"/> writes only while
    /// FruitLib's Diagnostics setting "Verbose log" is on; it takes effect immediately.
    /// Warnings and errors should still go straight to MelonLogger: those always print.
    ///
    /// Mods can use this too, so one switch quietens everyone:
    /// <code>FruitLog.Info("[MyMod] thing happened");</code>
    /// </summary>
    public static class FruitLog
    {
        public static bool Verbose => FruitLibConfig.VerboseLog;

        public static void Info(string message)
        {
            if (Verbose) MelonLogger.Msg(message);
        }
    }
}
