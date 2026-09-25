using System;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// Proves AssetBundle loading end to end on a running game. With BundleProbe on, the key
    /// loads every *.bundle in UserData/FruitBundles, logs what is inside, and spawns each
    /// bundle's first prefab two metres in front of the camera with a report on its renderers.
    /// Shift+key spawns with the materials rebound to the game's shaders, for comparison.
    ///
    /// Off by default. It is a development tool, not a feature.
    /// </summary>
    internal static class FruitBundleProbe
    {
        internal static bool Enabled => FruitHudConfig.BundleProbe;

        private static string Folder => Path.Combine(FruitPaths.UserData, "FruitBundles");

        internal static void Tick()
        {
            FruitBundleTests.Tick();
            if (!Enabled) return;
            var key = FruitHudConfig.BundleProbeKey;
            if (key == KeyCode.None || !Input.GetKeyDown(key)) return;

            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            {
                FruitBundleTests.Start();
                return;
            }

            bool rebind = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            Run(rebind);
        }

        private static void Run(bool rebind)
        {
            Directory.CreateDirectory(Folder);
            var files = Directory.GetFiles(Folder, "*.bundle");
            if (files.Length == 0)
            {
                MelonLogger.Msg($"[FruitBundleProbe] No *.bundle files in {Folder}.");
                return;
            }

            FruitBundle.CaptureGameShaders();

            var cam = Camera.main;
            Vector3 pos = cam != null ? cam.transform.position + cam.transform.forward * 2f : Vector3.zero;

            foreach (var file in files)
            {
                try
                {
                    var fb = FruitBundle.FromFile(file);
                    if (fb == null) continue;

                    var names = fb.AssetNames();
                    var sb = new StringBuilder($"[FruitBundleProbe] {Path.GetFileName(file)}: {names.Length} asset(s)\n");
                    string prefab = null;
                    foreach (var n in names)
                    {
                        sb.AppendLine("    " + n);
                        if (prefab == null && n.EndsWith(".prefab")) prefab = n;
                    }

                    if (prefab == null) { sb.Append("    no prefab to spawn"); MelonLogger.Msg(sb.ToString()); continue; }

                    var go = fb.Spawn(prefab, pos, Quaternion.identity, rebind);
                    if (go == null) { sb.Append($"    spawn of {prefab} failed"); MelonLogger.Msg(sb.ToString()); continue; }
                    go.name = "[FruitBundleProbe] " + go.name;
                    pos += (cam != null ? cam.transform.right : Vector3.right) * 1.0f;

                    sb.AppendLine($"    spawned {prefab} at {go.transform.position} (shaders {(rebind ? "rebound to game" : "from bundle")})");
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        string mesh = mf != null && mf.sharedMesh != null
                            ? $"{mf.sharedMesh.name} v={mf.sharedMesh.vertexCount} readable={mf.sharedMesh.isReadable}"
                            : "(no MeshFilter)";
                        sb.AppendLine($"    {r.GetType().Name} '{r.name}' {mesh} bounds={r.bounds.size}");
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) { sb.AppendLine("      material: null"); continue; }
                            var s = m.shader;
                            string tex = m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null
                                ? m.GetTexture("_BaseMap").name : "none";
                            sb.AppendLine($"      material '{m.name}' shader='{(s != null ? s.name : "null")}' " +
                                          $"supported={(s != null && s.isSupported)} baseMap={tex}");
                        }
                    }
                    foreach (var c in go.GetComponentsInChildren<Collider>(true))
                        sb.AppendLine($"    collider {c.GetType().Name} '{c.name}'");
                    MelonLogger.Msg(sb.ToString());
                }
                catch (Exception e) { MelonLogger.Warning($"[FruitBundleProbe] {Path.GetFileName(file)} failed: {e}"); }
            }
        }
    }
}
