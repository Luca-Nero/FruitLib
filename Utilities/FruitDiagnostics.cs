namespace FruitLib
{
    /// <summary>
    /// The startup snapshot of the Diagnostics switch.
    ///
    /// Diagnostic Harmony hooks are installed or not when MelonLoader applies FruitLib's
    /// patches, once, at load: each debug patch class answers Harmony's <c>Prepare()</c> from
    /// here, and so does the crash trace, so the two always agree. Flipping the setting in the
    /// menu changes the ini; it takes effect on the next launch.
    ///
    /// Off by default: the equip path runs through several of these hooks for every item the
    /// player holds, native ones included, and none of it is worth paying for in normal play.
    /// </summary>
    internal static class FruitDiagnostics
    {
        private static bool? _hooks;

        internal static bool Hooks
        {
            get
            {
                if (_hooks.HasValue) return _hooks.Value;
                FruitLibConfig.EnsureLoaded();
                _hooks = FruitLibConfig.DebugHooks;
                return _hooks.Value;
            }
        }
    }
}
