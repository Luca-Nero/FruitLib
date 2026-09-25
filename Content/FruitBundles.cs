using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// A Unity AssetBundle, loaded from a file or from a resource embedded in a mod.
    ///
    /// The game's own code has AssetBundle.LoadFromFile/LoadFromMemory stripped, so dump.cs
    /// makes bundles look unavailable. They are not: UnityPlayer.dll still registers the native
    /// loaders, and AssetBundleNative calls them directly. Bundles must be built with the game's
    /// exact editor version (6000.3.18f1 as of 0.17L) - see docs/BUNDLES.md.
    /// </summary>
    public sealed class FruitBundle
    {
        public string Name { get; }
        public AssetBundle Bundle { get; private set; }

        private FruitBundle(string name, AssetBundle bundle) { Name = name; Bundle = bundle; }

        // Unity refuses to load the same bundle twice while the first is still loaded, so
        // repeat requests for one source hand back the instance already open.
        private static readonly Dictionary<string, FruitBundle> _open =
            new Dictionary<string, FruitBundle>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Loads a bundle file from disk. Null (with a warning) if it fails.</summary>
        public static FruitBundle FromFile(string path)
        {
            if (_open.TryGetValue(path, out var open) && open.Bundle != null) return open;
            if (!File.Exists(path)) { MelonLogger.Warning($"[FruitBundle] No bundle at '{path}'."); return null; }

            AssetBundle ab;
            try { ab = AssetBundleNative.LoadFromFile(path); }
            catch (Exception e) { MelonLogger.Warning($"[FruitBundle] LoadFromFile threw for '{path}': {e.Message}"); return null; }

            return Register(path, Path.GetFileName(path), ab);
        }

        /// <summary>
        /// Loads a bundle embedded in <paramref name="assembly"/> as an EmbeddedResource. The
        /// resource name is matched by suffix, so "weapons.bundle" finds
        /// "MyMod.Bundles.weapons.bundle". Pass Assembly.GetExecutingAssembly() from the mod.
        /// </summary>
        public static FruitBundle FromResource(Assembly assembly, string resourceSuffix)
        {
            string key = assembly.GetName().Name + "::" + resourceSuffix;
            if (_open.TryGetValue(key, out var open) && open.Bundle != null) return open;

            string resName = null;
            foreach (var n in assembly.GetManifestResourceNames())
                if (n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
            if (resName == null)
            {
                MelonLogger.Warning($"[FruitBundle] No embedded resource ending '{resourceSuffix}' in '{assembly.GetName().Name}'.");
                return null;
            }

            byte[] bytes;
            using (var s = assembly.GetManifestResourceStream(resName))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }

            return FromBytes(bytes, key, resourceSuffix);
        }

        /// <summary>
        /// Loads a bundle from bytes already in memory. <paramref name="key"/> identifies it for
        /// the already-open check, e.g. its file or resource name.
        /// </summary>
        public static FruitBundle FromBytes(byte[] bytes, string key, string name = null)
        {
            if (_open.TryGetValue(key, out var open) && open.Bundle != null) return open;

            AssetBundle ab;
            try { ab = AssetBundleNative.LoadFromMemory(bytes); }
            catch (Exception e) { MelonLogger.Warning($"[FruitBundle] LoadFromMemory threw for '{key}': {e.Message}"); return null; }

            return Register(key, name ?? key, ab);
        }

        /// <summary>
        /// Loads a bundle file without blocking the frame. Run it as a coroutine
        /// (MelonCoroutines.Start); <paramref name="done"/> gets the bundle, or null on failure.
        /// </summary>
        public static IEnumerator FromFileAsync(string path, Action<FruitBundle> done)
        {
            if (_open.TryGetValue(path, out var open) && open.Bundle != null) { done?.Invoke(open); yield break; }
            if (!File.Exists(path)) { MelonLogger.Warning($"[FruitBundle] No bundle at '{path}'."); done?.Invoke(null); yield break; }

            IntPtr op;
            try { op = AssetBundleNative.BeginLoadFromFileAsync(path); }
            catch (Exception e) { MelonLogger.Warning($"[FruitBundle] LoadFromFileAsync threw for '{path}': {e.Message}"); done?.Invoke(null); yield break; }
            if (op == IntPtr.Zero) { done?.Invoke(null); yield break; }

            while (!AssetBundleNative.IsDone(op)) yield return null;

            AssetBundle ab = null;
            try { ab = AssetBundleNative.EndLoadFromFileAsync(op); }
            catch (Exception e) { MelonLogger.Warning($"[FruitBundle] async result threw for '{path}': {e.Message}"); }
            done?.Invoke(Register(path, Path.GetFileName(path), ab));
        }

        private static FruitBundle Register(string key, string name, AssetBundle ab)
        {
            if (ab == null)
            {
                MelonLogger.Warning($"[FruitBundle] '{name}' did not load. Usual causes: built with a different " +
                                    "Unity version than the game, built for the wrong platform, or already loaded.");
                return null;
            }
            var fb = new FruitBundle(name, ab);
            _open[key] = fb;
            MelonLogger.Msg($"[FruitBundle] '{name}' loaded, {fb.AssetNames().Length} asset(s).");
            return fb;
        }

        /// <summary>Every asset path in the bundle, lower-cased as Unity stores them.</summary>
        public string[] AssetNames() => AssetBundleNative.GetAllAssetNames(Bundle);

        /// <summary>
        /// Loads one asset by file name ("Javelin") or full path
        /// ("assets/bundles/javelin.prefab"). Null if absent or not a <typeparamref name="T"/>.
        /// </summary>
        public T Load<T>(string name) where T : Object
        {
            // The generic LoadAsset<T> has no body in the game (RVA -1), so go through the
            // Type overload and cast on this side.
            var obj = Bundle.LoadAsset(name, Il2CppType.Of<T>());
            if (obj == null) { MelonLogger.Warning($"[FruitBundle] '{name}' not found in '{Name}' as {typeof(T).Name}."); return null; }
            obj.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return obj.TryCast<T>();
        }

        /// <summary>
        /// Loads a prefab and instantiates it. The prefab's materials keep the shaders compiled
        /// into the bundle unless <paramref name="rebindShaders"/> is set; see RebindShaders.
        /// </summary>
        public GameObject Spawn(string prefab, Vector3 position, Quaternion rotation, bool rebindShaders = false)
        {
            var src = Load<GameObject>(prefab);
            if (src == null) return null;
            var go = Object.Instantiate(src, position, rotation);
            FixupRenderers(go, rebindShaders);
            return go;
        }

        /// <summary>Unloads the bundle. <paramref name="unloadAssets"/> also destroys what came out of it.</summary>
        public void Unload(bool unloadAssets)
        {
            if (Bundle == null) return;
            AssetBundleNative.Unload(Bundle, unloadAssets);
            Bundle = null;
        }

        // ── Shaders ─────────────────────────────────────────────────────────────

        // Game copies, captured before any bundle adds its own copy under the same name -
        // after that, Shader.Find could return either.
        private static readonly Dictionary<string, Shader> _gameShaders = new Dictionary<string, Shader>();
        private static readonly string[] CommonShaders =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Particles/Unlit",
            "Universal Render Pipeline/Particles/Lit",
        };

        /// <summary>
        /// Captures the game's own copies of the common URP shaders. Call it once from
        /// OnInitializeMelon or a scene load, before loading any bundle, if you plan to rebind.
        /// </summary>
        public static void CaptureGameShaders()
        {
            foreach (var n in CommonShaders)
                if (!_gameShaders.ContainsKey(n))
                {
                    var s = Shader.Find(n);
                    if (s != null) _gameShaders[n] = s;
                }
        }

        /// <summary>
        /// Swaps each material's shader for the game's copy of the same name. The bundle's own
        /// copy carries exactly the keyword variants its materials use, so keeping it is
        /// normally better. Rebinding is the fallback for when that copy is unsupported (pink),
        /// typically because the bundle was built without the game's graphics API.
        /// </summary>
        public static void RebindShaders(GameObject root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.shader != null && _gameShaders.TryGetValue(m.shader.name, out var game))
                        m.shader = game;
        }

        private static void FixupRenderers(GameObject go, bool rebind)
        {
            if (rebind) { RebindShaders(go); return; }

            // An unsupported shader renders pink but does not throw, so check for it here
            // and fall back to the game's copy for that material only.
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.shader != null && !m.shader.isSupported
                        && _gameShaders.TryGetValue(m.shader.name, out var game))
                    {
                        MelonLogger.Warning($"[FruitBundle] '{m.name}': bundled '{m.shader.name}' unsupported here, using the game's.");
                        m.shader = game;
                    }
        }
    }
}
