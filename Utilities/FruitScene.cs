using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// Scene lookups that survive IL2CPP stripping on 0.14.
    ///
    /// <b>The trap:</b> <c>Object.FindObjectOfType&lt;T&gt;(bool)</c> — the SINGULAR
    /// overload — is stripped from this build and throws
    /// <c>NotSupportedException: Method unstripping failed</c> at runtime. It compiles
    /// cleanly, because the reference assemblies still declare it. The plural
    /// <c>FindObjectsOfType&lt;T&gt;(bool)</c> is present and works.
    ///
    /// This is worth a type of its own because of how it fails. The call is usually
    /// inside a <c>try/catch</c> — scene lookups generally are — so the exception is
    /// swallowed and whatever the lookup was for silently never happens. It cost a
    /// debugging round in Rewind (a player-exclusion that quietly never ran) and it was
    /// about to cost one here: the 0.1 mods use the singular form freely, and the 0.14
    /// port carried ten of those calls across before this was caught.
    ///
    /// Prefer <see cref="First{T}"/> over the Unity call everywhere, including in new
    /// code — there is no version of this where the singular overload is the better
    /// choice, and one that slips through is invisible until someone notices a feature
    /// not working.
    /// </summary>
    public static class FruitScene
    {
        /// <summary>
        /// First instance of <typeparamref name="T"/> in the scene, or null.
        /// Inactive objects are included by default: the game parks unselected tools and
        /// views disabled rather than destroying them, so excluding them finds nothing.
        /// </summary>
        public static T First<T>(bool includeInactive = true) where T : Object
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<T> all;
            try { all = Object.FindObjectsOfType<T>(includeInactive); }
            catch { return null; }

            if (all == null) return null;

            foreach (var item in all)
                if (item != null) return item;

            return null;
        }

        /// <summary>How many of <typeparamref name="T"/> are in the scene. Handy for the
        /// "expected exactly one, found three" diagnostics that follow a bad lookup.</summary>
        public static int Count<T>(bool includeInactive = true) where T : Object
        {
            try
            {
                var all = Object.FindObjectsOfType<T>(includeInactive);
                return all?.Length ?? 0;
            }
            catch { return 0; }
        }
    }
}
