using System;
using System.Reflection;

namespace FruitLib
{
    /// <summary>
    /// Works round Il2CppInterop 1.5.1's lookup of game types while it injects a class.
    ///
    /// RegisterTypeInIl2Cpp walks every slot of the base class's vtable and turns each slot's
    /// parameter types back into managed types with Type.GetType("&lt;name&gt;, &lt;il2cpp assembly&gt;"),
    /// retrying with the "Il2Cpp" namespace prefix but keeping the il2cpp assembly name. The game's
    /// types live in an il2cpp assembly called FRUKT, whose managed side MelonLoader names
    /// Il2CppFRUKT, so both lookups fail and the injection throws "Couldn't find System.Type for
    /// Il2Cpp type: ..., FRUKT". It bites only bases whose vtable takes a game type as a
    /// parameter: ContextMenuAction implements IComparable&lt;ContextMenuAction&gt;, so its
    /// CompareTo(ContextMenuAction) did (5.27.0's FruitPropMenu never injected). GodInventoryItem's
    /// virtuals take floats at most, which is why FruitHeldItem always registered.
    ///
    /// <see cref="Register"/> answers the runtime's request for assembly "X" with the already
    /// loaded "Il2CppX" for the length of one registration, so the prefixed retry finds the type.
    /// Since 5.28.0.
    /// </summary>
    internal static class FruitInterop
    {
        internal static void Register(Action register)
        {
            ResolveEventHandler handler = OnResolve;
            AppDomain.CurrentDomain.AssemblyResolve += handler;
            try { register(); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= handler; }
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            string name;
            try { name = new AssemblyName(args.Name).Name; }
            catch { return null; }
            if (string.IsNullOrEmpty(name) || name.StartsWith("Il2Cpp", StringComparison.Ordinal)) return null;

            string wanted = "Il2Cpp" + name;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (asm.GetName().Name == wanted) return asm; }
                catch { /* a dynamic assembly without a name; not ours */ }
            }
            return null;
        }
    }
}
