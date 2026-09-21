using System;
using Il2CppInfrastructure.Components.ManagedBehaviours;
using Il2CppViews.Generic;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// Copies a piece of the pause menu, with the services Zenject would have injected.
    ///
    /// Object.Instantiate does no injection, and most of this menu is built from
    /// ManagedBehaviours that expect their services to be in place before Awake. A clone made
    /// the naive way wakes up with null everywhere: it never joins the update loop, so its
    /// hover never animates, and its screen transition never registers a reveal edge, so it
    /// never wipes. Both failures are silent - the object is there and looks right until you
    /// touch it.
    ///
    /// So the clone is made inside an inactive object, its services are filled in from the
    /// original while it is still asleep, and only then is it activated. That is the order
    /// Zenject uses, and it is why <see cref="Make{T}"/> hands back an inactive clone and
    /// leaves waking it to the caller - there is always something to set in between.
    ///
    /// The game's own IMonoFactory would do all of this properly, and it is reachable off
    /// ContextMenuWindow. It is not used here because Create&lt;T&gt; is generic and IL2CPP
    /// only carries the instantiations the game itself needed - and this copies types the
    /// game never passes to it.
    /// </summary>
    internal static class FruitMenuClone
    {
        /// <summary>
        /// An inactive copy of <paramref name="prototype"/>, services filled in, parented to
        /// <paramref name="parent"/>. Activate it once you have finished configuring it.
        /// </summary>
        internal static T Make<T>(T prototype, Transform parent, string name) where T : Component
        {
            if (prototype == null) throw new ArgumentNullException(nameof(prototype));
            if (parent == null) throw new ArgumentNullException(nameof(parent));

            var crib = new GameObject("FruitLib_Crib");
            crib.SetActive(false);

            try
            {
                var clone = UnityEngine.Object.Instantiate(prototype, crib.transform, false);
                clone.gameObject.name = name;

                Inject(prototype.gameObject, clone.gameObject);

                clone.transform.SetParent(parent, false);
                return clone;
            }
            finally { UnityEngine.Object.Destroy(crib); }
        }

        /// <summary>
        /// Fills in every service on <paramref name="clone"/> from the matching component on
        /// <paramref name="source"/>.
        ///
        /// Two kinds, because two kinds is all this menu uses. Every ManagedBehaviour wants
        /// the core services provider, which is what registers it with the update loop.
        /// Every SceneRevealEdgeScreenTransition additionally wants its own update loop and
        /// the shared reveal-edge service - the latter being what makes separate screens
        /// agree on where the wipe edge is, rather than each drawing its own.
        /// </summary>
        private static void Inject(GameObject source, GameObject clone)
        {
            // Falls back to the last one we saw. A prefab held in a field - the line prefab a
            // SettingTable builds its rows from, say - was never part of the scene and so was
            // never injected, which leaves nothing on it to copy. The provider is one shared
            // service either way, so the one from the last thing we cloned is the right one.
            var provider = FindProvider(source) ?? _lastKnownProvider;
            if (provider != null)
            {
                foreach (var mb in clone.GetComponentsInChildren<ManagedBehaviour>(true))
                    if (mb != null) mb.m_coreServicesProvider = provider;
            }
            else if (NeedsProvider(clone))
            {
                // Only worth saying when there is something that would have used it. A slider
                // row is built from plain MonoBehaviours - SettingSlider and SliderCore both -
                // so it has nothing to inject and nothing to miss.
                MelonLogger.Warning($"[FruitMenu] no core services provider under '{source.name}'; " +
                                    "the copy will not animate.");
            }

            var sourceRides = source.GetComponentsInChildren<SceneRevealEdgeScreenTransition>(true);
            if (sourceRides == null || sourceRides.Length == 0) return;

            SceneRevealEdgeScreenTransition template = null;
            foreach (var r in sourceRides)
                if (r != null && r.m_revealEdges != null) { template = r; break; }

            if (template == null)
            {
                MelonLogger.Warning($"[FruitMenu] no reveal-edge service under '{source.name}'; " +
                                    "the copy will not wipe.");
                return;
            }

            foreach (var r in clone.GetComponentsInChildren<SceneRevealEdgeScreenTransition>(true))
            {
                if (r == null) continue;
                r.m_updateLoop  = template.m_updateLoop;
                r.m_revealEdges = template.m_revealEdges;
            }
        }

        private static bool NeedsProvider(GameObject clone)
        {
            foreach (var mb in clone.GetComponentsInChildren<ManagedBehaviour>(true))
                if (mb != null) return true;
            return false;
        }

        /// <summary>
        /// Snaps the hover highlight off every button under <paramref name="root"/>.
        ///
        /// A button learns the pointer has left from OnPointerExit, and that never arrives if
        /// the object is deactivated or wiped away while the pointer is still on it. The
        /// highlight is then still lit when it comes back, on a button nobody is pointing at.
        /// Anything handing a screen off to something else should call this on the way out.
        /// </summary>
        internal static void ClearHover(GameObject root)
        {
            if (root == null) return;

            try
            {
                foreach (var b in root.GetComponentsInChildren<MenuLineButton>(true))
                    if (b != null) { try { b.Flash.Snap(false); } catch { } }

                foreach (var b in root.GetComponentsInChildren<SettingOptionButton>(true))
                    if (b != null) { try { b.Flash.Snap(false); } catch { } }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] clearing hover failed: {e.Message}"); }
        }

        /// <summary>The last provider found on anything, for sources that carry none of their own.</summary>
        private static Il2CppServices.Infrastructure.IManagedBehaviourCoreServicesProvider _lastKnownProvider;

        private static Il2CppServices.Infrastructure.IManagedBehaviourCoreServicesProvider
            FindProvider(GameObject source)
        {
            foreach (var mb in source.GetComponentsInChildren<ManagedBehaviour>(true))
            {
                if (mb == null || mb.m_coreServicesProvider == null) continue;

                _lastKnownProvider = mb.m_coreServicesProvider;
                return _lastKnownProvider;
            }
            return null;
        }
    }
}
