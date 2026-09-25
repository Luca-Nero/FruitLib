using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// URP decals, which the game ships the code for but never switched on: its renderer has no
    /// DecalRendererFeature, so a DecalProjector loads and draws nothing.
    ///
    /// Ensure() creates the feature at runtime and adds it to every renderer of the active URP
    /// asset, then marks each renderer dirty so URP rebuilds it on the next frame, calling the
    /// feature's Create() the way the editor does when you add one by hand. It uses the Screen
    /// Space technique: decals are drawn after opaques with normals reconstructed from depth,
    /// which the game's main camera already renders. DBuffer would need decal variants in the
    /// game's own Lit shader, which a build without decals strips.
    ///
    /// The change lives in memory only, until the game quits. Decal materials come from bundles
    /// (the game has no decal shader of its own), built in a project whose renderer has a Decal
    /// feature too - FruitBundleBuilder adds one - or Unity strips the pass they need.
    /// </summary>
    public static class FruitDecals
    {
        private static DecalRendererFeature _feature;

        /// <summary>Why the last Ensure() failed, if it did.</summary>
        public static string LastError { get; private set; }

        /// <summary>True once a decal feature is on the active renderer, ours or the game's.</summary>
        public static bool Active => FindActive() != null;

        /// <summary>
        /// Turns decals on. Safe to call repeatedly; takes effect from the next rendered frame.
        /// <paramref name="maxDrawDistance"/> is where decals fade out, in metres.
        /// </summary>
        public static bool Ensure(float maxDrawDistance = 60f)
        {
            if (FindActive() != null) return true;

            var urp = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
            if (urp == null) { LastError = "active render pipeline is not URP"; return false; }

            try
            {
                var list = urp.m_RendererDataList;
                int added = 0;
                for (int i = 0; i < list.Length; i++)
                {
                    var data = list[i];
                    if (data == null || HasDecal(data)) continue;

                    var f = ScriptableObject.CreateInstance<DecalRendererFeature>();
                    f.name = "FruitLib Decals";
                    f.hideFlags |= HideFlags.DontUnloadUnusedAsset;

                    var s = f.m_Settings ?? new DecalSettings();
                    s.technique = DecalTechniqueOption.ScreenSpace;
                    s.maxDrawDistance = maxDrawDistance;
                    s.decalLayers = false;
                    s.screenSpaceSettings ??= new DecalScreenSpaceSettings();
                    s.screenSpaceSettings.normalBlend = DecalNormalBlend.Low;
                    f.m_Settings = s;
                    f.SetActive(true);

                    // The map is the editor's serialization index for the list; keep it the
                    // same length so nothing that walks both trips over the difference.
                    data.m_RendererFeatures.Add(f);
                    data.m_RendererFeatureMap?.Add(0);
                    data.SetDirty();
                    _feature ??= f;
                    added++;
                }
                MelonLogger.Msg($"[FruitDecals] Decal feature added to {added} renderer(s) of '{urp.name}' (Screen Space, {maxDrawDistance} m).");
                return added > 0 || FindActive() != null;
            }
            catch (Exception e)
            {
                LastError = $"{e.GetType().Name}: {e.Message}";
                MelonLogger.Warning("[FruitDecals] Enabling decals failed: " + LastError);
                return false;
            }
        }

        /// <summary>
        /// Projects <paramref name="material"/> (a URP decal material, from a bundle) onto the
        /// surface at <paramref name="point"/> facing along <paramref name="normal"/>.
        /// <paramref name="depth"/> is the total thickness of the projection box, straddling the
        /// surface, centred on it; <paramref name="lifetime"/> &gt; 0 destroys it after that many seconds.
        /// Returns null if decals could not be enabled.
        /// </summary>
        public static DecalProjector Place(Material material, Vector3 point, Vector3 normal, float size,
                                           float lifetime = 0f, float depth = 0.3f, float? spinDegrees = null)
        {
            if (material == null || !Ensure()) return null;

            // Centred on the surface, so the box reaches depth/2 in front of it and depth/2 behind.
            // With the surface on the box's far face instead, depth precision decides per pixel
            // whether it is inside, and the decal clips in patches.
            var go = new GameObject("[FruitDecal]");
            go.transform.position = point;
            // A projector casts along its +Z, so it looks into the surface; spin about that axis.
            go.transform.rotation = Quaternion.LookRotation(-normal) *
                                    Quaternion.Euler(0, 0, spinDegrees ?? UnityEngine.Random.Range(0f, 360f));

            var dp = go.AddComponent<DecalProjector>();
            dp.material = material;
            dp.size = new Vector3(size, size, depth);
            dp.pivot = Vector3.zero;
            if (lifetime > 0f) Object.Destroy(go, lifetime);
            return dp;
        }

        private static DecalRendererFeature FindActive()
        {
            var urp = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
            if (urp == null) return null;
            var list = urp.m_RendererDataList;
            for (int i = 0; i < list.Length; i++)
            {
                var data = list[i];
                if (data == null) continue;
                var feats = data.rendererFeatures;
                for (int j = 0; j < feats.Count; j++)
                {
                    var d = feats[j]?.TryCast<DecalRendererFeature>();
                    if (d != null && d.isActive) return d;
                }
            }
            return null;
        }

        private static bool HasDecal(ScriptableRendererData data)
        {
            var feats = data.rendererFeatures;
            for (int j = 0; j < feats.Count; j++)
                if (feats[j]?.TryCast<DecalRendererFeature>() != null) return true;
            return false;
        }
    }
}
