using System;
using System.IO;
using MelonLoader;

namespace FruitLib
{
    /// <summary>
    /// Breadcrumbs that survive a hard crash.
    ///
    /// A native crash kills the process between one instruction and the next: nothing reaches
    /// Player.log, and whatever MelonLoader had not flushed yet never reaches Latest.log
    /// either. Every Mark here opens, appends and closes the file, so the last line on disk is
    /// the last step that actually completed. Slow by design - use it on events, never per
    /// frame.
    ///
    /// Written to UserData/FruitLib_trace.log, which each launch starts fresh.
    /// </summary>
    public static class FruitTrace
    {
        private static string _path;
        private static bool   _broken;

        public static string FilePath
        {
            get
            {
                if (_path != null) return _path;
                _path = Path.Combine(FruitPaths.UserData, "FruitLib_trace.log");
                try { File.WriteAllText(_path, $"FruitLib {FruitVersion.Current} trace, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n"); }
                catch (Exception e) { Fail(e); }
                return _path;
            }
        }

        public static void Mark(string what)
        {
            if (_broken) return;
            try { File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {what}\n"); }
            catch (Exception e) { Fail(e); }
        }

        private static void Fail(Exception e)
        {
            _broken = true;
            MelonLogger.Warning($"[FruitTrace] cannot write the trace file, tracing is off: {e.Message}");
        }
    }
}
