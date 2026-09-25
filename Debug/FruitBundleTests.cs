using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// What a Unity AssetBundle can and cannot do in this game, measured on the running game.
    ///
    /// Ctrl + BundleProbeKey (with BundleProbe on) loads the fruittest* bundles that
    /// Tools/UnityBundleBuilder's "FruitLib/Build Test Suite" produces, spawns one test case per
    /// grid cell in front of the camera with a floating status label, checks what code can
    /// check, and marks the rest LOOK with what to look for. Any other *.bundle in the folder
    /// (not fruittest*/fruitprobe) is loaded as a user bundle and its prefabs inventoried.
    /// Pressing it again tears everything down, reloads the bundles fresh and reruns.
    ///
    /// The report goes to UserData/FruitBundles/FruitBundleTests_report.md and is rewritten
    /// after every case, so a crash still leaves the last case that ran on disk.
    /// </summary>
    internal static class FruitBundleTests
    {
        private enum St { PASS, FAIL, LOOK, INFO, SKIP }

        private sealed class Case
        {
            public string Id, Title, Look = "";
            public St Status = St.INFO;
            public bool Done;     // INFO is also a real result, so "still running" is tracked apart from Status
            public float Ground;
            public TextMeshPro Label;
            public readonly List<string> Notes = new List<string>();
            public void Note(string s) => Notes.Add(s);
        }

        private static readonly List<Case> _cases = new List<Case>();
        private static readonly List<Case> _env = new List<Case>();
        private static readonly List<GameObject> _spawned = new List<GameObject>();
        private static readonly List<FruitBundle> _bundles = new List<FruitBundle>();

        private static bool _running;
        private static int _pulseId;
        private static FruitBundle _main;
        private static Vector3 _origin, _fwd, _right;
        private static int _cell;
        private static TMP_FontAsset _font;
        private static bool _hasOpaque, _hasDepth, _hasDecal, _postFx;

        private static string Folder => Path.Combine(FruitPaths.UserData, "FruitBundles");
        private static string ReportPath => Path.Combine(Folder, "FruitBundleTests_report.md");

        internal static void Start()
        {
            if (_running) { MelonLogger.Msg("[FruitBundleTests] Already running."); return; }
            MelonCoroutines.Start(Guarded(Run(), null));
        }

        /// <summary>Drives the _FruitPulse global the S4 shader reads.</summary>
        internal static void Tick()
        {
            if (_pulseId != 0) Shader.SetGlobalFloat(_pulseId, 0.5f + 0.5f * Mathf.Sin(Time.time * 4f));
        }

        internal static void ResetForScene()
        {
            // SC1 loads a scene additively, which raises the same scene-initialized event as a
            // level change. Clearing then wiped every result recorded before it.
            if (_running) return;
            _spawned.Clear();
            _cases.Clear();
        }

        // ── Runner ──────────────────────────────────────────────────────────────

        // Runs a coroutine with nested coroutines flattened, so that an exception anywhere in a
        // case fails that case instead of killing the whole run (C# allows no yield inside try/catch).
        private static IEnumerator Guarded(IEnumerator body, Case c)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(body);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                bool moved;
                object cur = null;
                try
                {
                    moved = top.MoveNext();
                    if (moved) cur = top.Current;
                }
                catch (Exception e)
                {
                    if (c != null) { c.Status = St.FAIL; c.Note("threw " + Short(e)); }
                    else MelonLogger.Warning("[FruitBundleTests] run aborted: " + e);
                    yield break;
                }
                if (!moved) { stack.Pop(); continue; }
                if (cur is IEnumerator nested) { stack.Push(nested); continue; }
                yield return cur;
            }
        }

        private static IEnumerator Wait(float seconds)
        {
            float end = Time.time + seconds;
            while (Time.time < end) yield return null;
        }

        private static IEnumerator Run()
        {
            _running = true;
            try
            {
                Teardown();
                Directory.CreateDirectory(Folder);
                MelonLogger.Msg("[FruitBundleTests] ===== starting =====");

                _pulseId = Shader.PropertyToID("_FruitPulse");
                FruitBundle.CaptureGameShaders();
                Layout();
                FindFont();
                Environment();
                WriteReport();

                var shared = Load("fruittest_shared");
                _main = Load("fruittest");

                var b1 = Info("B1", "LoadFromFile (sync), native icall");
                b1.Status = _main != null ? St.PASS : St.FAIL;
                if (_main != null) b1.Note($"{_main.AssetNames().Length} assets in fruittest");
                b1.Note("fruittest_shared " + (shared != null ? "loaded" : "MISSING"));
                if (_main == null) { b1.Note("nothing else can run - build the suite first (FruitLib > Build Test Suite)"); yield break; }

                // Meshes
                yield return RunCase("M1", "Generated mesh, non-readable, convex MeshCollider", "M1_Knot", "gold knot drops and rests on the ground", M1);
                yield return RunCase("M2", "90k-vertex mesh, 32-bit indices", "M2_BigGrid", "blue rippled sheet, tilted", M2);
                yield return RunCase("M3", "Submeshes, 3 materials", "M3_Submeshes", "three cubes: red, green, blue", M3);
                yield return RunCase("M4", "Blend shape (stripped API, via FruitNative)", "M4_BlendShape", "purple ball pulses between smooth and spiky", M4);
                // Textures and shading
                yield return RunCase("T1", "Texture formats BC7 / BC5 / BC6H / RGBA32", "T1_Textures", "four tiles: checker, bumpy normal colours, glowing HDR, holes (cutout)", T1);
                yield return RunCase("S1", "URP stock shaders + keyword variants", "S1_LitVariants", "six spheres: checker+bumps+glow, see-through blue, holes, yellow, flat green, flat red", S1);
                yield return RunCase("S4", "Custom HLSL: vertex wobble + mod-driven global", "S4_CustomWobble", "green sphere wobbles; orange rim pulses ~0.7 Hz", S4);
                yield return RunCase("S5", "Custom HLSL: refraction + depth fade", "S5_Refraction", "lens bends the checker behind it; cyan glow where it meets the ground", S5);
                yield return RunCase("S7", "MaterialPropertyBlock on instanced material", "S7_PropertyBlocks", "three balls: red, green, blue", S7);
                yield return RunCase("S8", "Multi-pass shader: x-ray through walls", "S8_XRay", "magenta silhouette visible through the grey wall", S8);
                // Animation
                yield return RunCase("A1", "Skinned mesh + Animator (params, trigger, int, controller swap)", "A1_Skinned", "orange tube sways, bends, waves; ends spinning on its base", A1);
                yield return RunCase("TL1", "Timeline: animation + activation tracks", "TL1_Timeline", "cyan cube rises and falls; yellow ball blinks every second", TL1);
                // Physics
                yield return RunCase("P1", "PhysicsMaterial bounce", "P1_Bouncy", "pink ball bounces several times", P1, 0f);
                yield return RunCase("P2", "HingeJoint chain (class stripped from game code)", "P2_HingeChain", "chain hangs and swings, links stay connected", Chain);
                yield return RunCase("P3", "CharacterJoint chain (class stripped from game code)", "P3_CharacterChain", "chain hangs and swings, links stay connected", Chain);
                yield return RunCase("P4", "ConfigurableJoint spring", "P4_SpringJoint", "green block bobs under the grey anchor", P4);
                // Rendering features
                yield return RunCase("F1", "Particle system (URP particle material)", "F1_Particles", "orange sparks fountain upward", F1);
                yield return RunCase("F2", "Point light + URP additional light data", "F2_Light", "orange glow lights the ground around it", F2);
                yield return RunCase("F3", "Camera -> RenderTexture -> screen", "F3_Monitor", "screen shows a live view looking back at you", F3);
                yield return RunCase("F4", "LOD group", "F4_LOD", "red sphere close up; blue cube once you back off ~20 m", F4);
                yield return RunCase("F5", "URP decal projector", "F5_Decal", "orange/navy checker projected on the ground", F5);
                yield return RunCase("F6", "Local post-processing volume", "F6_Volume", "walk into the pink bubble: the world turns grey-pink", F6);
                // Audio / UI / game code
                yield return RunCase("AU1", "AudioClip + 3D AudioSource", "AU1_Audio", "LISTEN: alternating beeps from the black box, louder as you approach", AU1);
                yield return RunCase("UI1", "World-space Canvas: Image + TextMeshProUGUI", "UI1_Canvas", "gradient panel reading 'Hello from a bundle'", UI1);
                yield return RunCase("GC1", "Game component (Map.Spinner) via stub script", "GC1_Spinner", "platform with red post spins up", GC1);
                // Bundle mechanics
                yield return RunCase("D1", "Cross-bundle dependency (material in fruittest_shared)", "D1_Dependency", "checkered sphere (pink or blank = dependency lost)", D1);
                var b3 = Info("B3", "LoadFromMemory + Unload(true) + reload");
                yield return Guarded(MemoryAndUnload(b3), b3);
                Finish(b3);
                var sc1 = Info("SC1", "Scene bundle, loaded additively");
                yield return Guarded(SceneBundle(sc1), sc1);
                Finish(sc1);

                // Last: first real use of the async icalls. If the game dies here, it was this.
                var b4 = Info("B4", "LoadFromFileAsync (native, hitch-free)");
                b4.Note("if the game crashed and this is the last line, async loading is the culprit");
                WriteReport();
                yield return Guarded(Async(b4), b4);
                Finish(b4);

                yield return Guarded(UserBundles(), null);
            }
            finally
            {
                _running = false;
                foreach (var c in _cases) UpdateLabel(c);
                WriteReport();
                Summary();
            }
        }

        private static IEnumerator RunCase(string id, string title, string prefab, string look,
                                        Func<Case, GameObject, IEnumerator> check, float lift = 0f)
        {
            var c = New(id, title);
            c.Look = look;
            GameObject go = null;
            try { go = Spawn(c, prefab, lift, _main); }
            catch (Exception e) { c.Status = St.FAIL; c.Note("spawn threw " + Short(e)); }

            if (go != null) yield return Guarded(check(c, go), c);
            else if (c.Status != St.FAIL) { c.Status = St.FAIL; c.Note($"prefab '{prefab}' missing from bundle"); }

            Finish(c);
        }

        private static void Finish(Case c)
        {
            c.Done = true;
            UpdateLabel(c);
            WriteReport();
            MelonLogger.Msg($"[FruitBundleTests] {c.Id} {c.Status}  {c.Title}  | {string.Join(" | ", c.Notes)}");
        }

        // ── Cases ───────────────────────────────────────────────────────────────

        private static IEnumerator M1(Case c, GameObject go)
        {
            var mf = go.GetComponentInChildren<MeshFilter>();
            var mesh = mf.sharedMesh;
            c.Note($"verts={mesh.vertexCount}");
            try { c.Note("isReadable=" + mesh.isReadable + " (bundle stored false)"); }
            catch (Exception e) { c.Note("Mesh.isReadable threw " + Short(e)); }
            var mc = go.GetComponentInChildren<MeshCollider>();
            c.Note($"MeshCollider={(mc != null && mc.sharedMesh != null)} convex={(mc != null && mc.convex)}");

            yield return Wait(2.5f);
            float above = mf.transform.position.y - c.Ground;
            c.Note($"came to rest {above:F2} m above ground");
            c.Status = mesh.vertexCount > 0 && mc != null && above > -0.3f ? St.PASS : St.FAIL;
        }

        private static IEnumerator M2(Case c, GameObject go)
        {
            var mesh = go.GetComponentInChildren<MeshFilter>().sharedMesh;
            c.Note($"verts={mesh.vertexCount} indexFormat={mesh.indexFormat}");
            c.Status = mesh.vertexCount > 65535 && mesh.indexFormat == IndexFormat.UInt32 ? St.PASS : St.FAIL;
            yield break;
        }

        private static IEnumerator M3(Case c, GameObject go)
        {
            var mf = go.GetComponentInChildren<MeshFilter>();
            var mr = go.GetComponentInChildren<MeshRenderer>();
            c.Note($"subMeshCount={mf.sharedMesh.subMeshCount} materials={mr.sharedMaterials.Length}");
            c.Status = mf.sharedMesh.subMeshCount == 3 && mr.sharedMaterials.Length == 3 ? St.PASS : St.FAIL;
            yield break;
        }

        private static IEnumerator M4(Case c, GameObject go)
        {
            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
            try { c.Note("blendShapeCount=" + smr.sharedMesh.blendShapeCount); }
            catch (Exception e) { c.Note("Mesh.blendShapeCount (regenerated) threw " + Short(e)); }

            try { smr.SetBlendShapeWeight(0, 50f); c.Note("regenerated SetBlendShapeWeight: no exception"); }
            catch (Exception e) { c.Note("regenerated SetBlendShapeWeight threw " + Short(e)); }

            FruitNative.SetBlendShapeWeight(smr, 0, 100f);
            float w = FruitNative.GetBlendShapeWeight(smr, 0);
            c.Note($"FruitNative set 100, read back {w:F0}");
            c.Status = Mathf.Abs(w - 100f) < 0.5f ? St.PASS : St.FAIL;

            for (float t = 0; t < 4f; t += Time.deltaTime)
            {
                if (smr == null) yield break;
                FruitNative.SetBlendShapeWeight(smr, 0, 50f + 50f * Mathf.Sin(t * 3f));
                yield return null;
            }
            FruitNative.SetBlendShapeWeight(smr, 0, 100f);
        }

        private static IEnumerator T1(Case c, GameObject go)
        {
            var expect = new Dictionary<string, TextureFormat>
            {
                ["T1_BC7"] = TextureFormat.BC7, ["T1_BC5Normal"] = TextureFormat.BC5,
                ["T1_BC6H_HDR"] = TextureFormat.BC6H, ["T1_RGBA32Cutout"] = TextureFormat.RGBA32,
            };
            int ok = 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var tex = r.sharedMaterial?.GetTexture("_BaseMap")?.TryCast<Texture2D>();
                bool match = tex != null && expect.TryGetValue(r.name, out var f) && tex.format == f;
                if (match) ok++;
                c.Note($"{r.name}: {(tex != null ? $"{tex.format} {tex.width}px mips={tex.mipmapCount}" : "no texture")}");
            }
            c.Status = ok == expect.Count ? St.PASS : St.FAIL;
            yield break;
        }

        private static IEnumerator S1(Case c, GameObject go)
        {
            bool all = true;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var m = r.sharedMaterial;
                bool sup = m != null && m.shader != null && m.shader.isSupported;
                all &= sup;
                string kw = "";
                try { kw = string.Join(",", m.shaderKeywords); } catch (Exception e) { kw = "keywords threw " + Short(e); }
                c.Note($"{r.name}: '{m?.shader?.name}' supported={sup} [{kw}]");
            }
            c.Status = all ? St.LOOK : St.FAIL;
            yield break;
        }

        private static IEnumerator S4(Case c, GameObject go) => ShaderCase(c, go, 2, "rim pulse is driven by Shader.SetGlobalFloat(_FruitPulse) from FruitLib each frame");

        private static IEnumerator S5(Case c, GameObject go)
            => ShaderCase(c, go, 1, $"main camera renders with opaque texture={_hasOpaque}, depth texture={_hasDepth}. " +
                                    (_hasOpaque ? "" : "Opaque texture OFF: expect a flat dark body. ") +
                                    (_hasDepth ? "" : "Depth texture OFF: expect no contact glow."));

        private static IEnumerator S8(Case c, GameObject go) => ShaderCase(c, go, 2, "second pass is LightMode SRPDefaultUnlit with ZTest Greater");

        private static IEnumerator ShaderCase(Case c, GameObject go, int passes, string note)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var s = r.sharedMaterial?.shader;
                if (s == null || !s.name.StartsWith("FruitTest/")) continue;
                c.Note($"'{s.name}' supported={s.isSupported} passes={s.passCount} (expect {passes})");
                c.Status = s.isSupported && s.passCount == passes ? St.LOOK : St.FAIL;
            }
            c.Note(note);
            yield break;
        }

        private static IEnumerator S7(Case c, GameObject go)
        {
            int id = Shader.PropertyToID("_BaseColor");
            var cols = new[] { Color.red, Color.green, Color.blue };
            int i = 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mpb = new MaterialPropertyBlock();
                mpb.SetColor(id, cols[i++ % 3]);
                r.SetPropertyBlock(mpb);
            }
            c.Note($"{i} renderers given their own _BaseColor; material instancing={go.GetComponentInChildren<Renderer>().sharedMaterial.enableInstancing}");
            c.Status = St.LOOK;
            yield break;
        }

        private static IEnumerator A1(Case c, GameObject go)
        {
            var an = go.GetComponent<Animator>();
            var bone = go.transform.Find("Rig/Bone0/Bone1");
            string[] states = { "Idle", "Bend", "Wave", "ModeTwo", "AltState" };
            string State()
            {
                var info = an.GetCurrentAnimatorStateInfo(0);
                foreach (var s in states)
                    if (info.IsName(s)) return s;
                return "?";
            }
            int ok = 0, total = 0;
            void Check(bool cond, string what) { total++; if (cond) ok++; c.Note((cond ? "ok: " : "BAD: ") + what); }

            c.Note($"controller='{an.runtimeAnimatorController?.name}' bones={go.GetComponentInChildren<SkinnedMeshRenderer>().bones.Length}");
            yield return Wait(0.5f);
            Check(State() == "Idle", $"starts in Idle ({State()})");
            var r0 = bone.localRotation;
            yield return Wait(0.5f);
            Check(Quaternion.Angle(r0, bone.localRotation) > 0.5f, "bones move");

            an.SetBool("Bent", true);
            yield return Wait(0.6f);
            Check(State() == "Bend", $"SetBool Bent -> Bend ({State()})");
            an.SetFloat("Speed", 2.5f);
            yield return null;
            Check(Mathf.Abs(an.GetFloat("Speed") - 2.5f) < 0.01f, "SetFloat/GetFloat Speed");
            yield return Wait(1f);
            an.SetBool("Bent", false);
            yield return Wait(0.6f);
            Check(State() == "Idle", $"back to Idle ({State()})");

            an.SetTrigger("Wave");
            yield return Wait(0.3f);
            Check(State() == "Wave", $"SetTrigger Wave -> Wave ({State()})");
            yield return Wait(1.2f);
            Check(State() == "Idle", $"Wave exits to Idle ({State()})");

            FruitNative.SetInteger(an, "Mode", 2);
            yield return Wait(0.6f);
            Check(State() == "ModeTwo", $"FruitNative.SetInteger Mode=2 -> ModeTwo ({State()})");
            FruitNative.SetInteger(an, "Mode", 0);
            yield return Wait(0.6f);

            var alt = _main.Load<RuntimeAnimatorController>("A1_AltController");
            FruitNative.SetController(an, alt);
            yield return Wait(0.5f);
            Check(an.runtimeAnimatorController != null && an.runtimeAnimatorController.name == "A1_AltController" && State() == "AltState",
                  $"FruitNative.SetController -> '{an.runtimeAnimatorController?.name}' ({State()})");

            c.Note($"{ok}/{total} steps");
            c.Status = ok == total ? St.PASS : St.FAIL;
        }

        private static IEnumerator TL1(Case c, GameObject go)
        {
            var dir = go.GetComponent<PlayableDirector>();
            var mover = go.transform.Find("Mover");
            c.Note($"asset='{dir.playableAsset?.name}' playOnAwake={dir.playOnAwake}");
            float y0 = mover.localPosition.y, minY = y0, maxY = y0;
            for (float t = 0; t < 1.5f; t += Time.deltaTime)
            {
                minY = Mathf.Min(minY, mover.localPosition.y);
                maxY = Mathf.Max(maxY, mover.localPosition.y);
                yield return null;
            }
            c.Note($"state={dir.state} time={dir.time:F2}s, mover travelled {maxY - minY:F2} m");
            c.Status = dir.state == PlayState.Playing && dir.time > 0 && maxY - minY > 0.2f ? St.PASS : St.FAIL;
        }

        private static IEnumerator P1(Case c, GameObject go)
        {
            var rb = go.GetComponentInChildren<Rigidbody>();
            var col = go.GetComponentInChildren<Collider>();
            c.Note($"physics material='{col.sharedMaterial?.name}' bounciness={col.sharedMaterial?.bounciness}");
            bool falling = false;
            int bounces = 0;
            for (float t = 0; t < 4f; t += Time.deltaTime)
            {
                float vy = rb.linearVelocity.y;
                if (vy < -1.5f) falling = true;
                else if (falling && vy > 1f) { bounces++; falling = false; }
                yield return null;
            }
            c.Note($"{bounces} bounce(s) seen");
            c.Status = bounces >= 2 ? St.PASS : St.FAIL;
        }

        private static IEnumerator Chain(Case c, GameObject go)
        {
            var anchor = go.transform.Find("Anchor");
            var links = Enumerable.Range(0, 5).Select(i => go.transform.Find("Link" + i)).ToArray();
            c.Note("Link0 components: " + Inventory(links[0].gameObject, false));

            var chain = new[] { anchor }.Concat(links).ToArray();
            var rest = new float[chain.Length - 1];
            for (int i = 0; i < rest.Length; i++) rest[i] = Vector3.Distance(chain[i].position, chain[i + 1].position);

            links[4].GetComponent<Rigidbody>().AddForce(_right * 3f, ForceMode.Impulse);
            float stretch = 0, swing = 0;
            for (float t = 0; t < 3f; t += Time.deltaTime)
            {
                for (int i = 0; i < rest.Length; i++)
                    stretch = Mathf.Max(stretch, Mathf.Abs(Vector3.Distance(chain[i].position, chain[i + 1].position) - rest[i]));
                swing = Mathf.Max(swing, Vector3.ProjectOnPlane(links[4].position - anchor.position, Vector3.up).magnitude);
                yield return null;
            }
            float drop = anchor.position.y - links[4].position.y;
            c.Note($"worst link stretch {stretch:F2} m, swing {swing:F2} m, bottom link {drop:F2} m below anchor (rest ~2.4)");
            c.Status = stretch < 0.25f && drop < 3.2f && swing > 0.2f ? St.PASS : St.FAIL;
            if (c.Status == St.FAIL && drop >= 3.2f) c.Note("links fell away: the joints did not hold");
        }

        private static IEnumerator P4(Case c, GameObject go)
        {
            var anchor = go.transform.Find("Anchor");
            var weight = go.transform.Find("Weight");
            c.Note("Weight components: " + Inventory(weight.gameObject, false));
            yield return Wait(3f);
            float below = anchor.position.y - weight.position.y;
            c.Note($"weight {below:F2} m below anchor (limit 1.2)");
            c.Status = below > -0.3f && below < 1.8f ? St.PASS : St.FAIL;
        }

        private static IEnumerator F1(Case c, GameObject go)
        {
            var ps = go.GetComponentInChildren<ParticleSystem>();
            yield return Wait(1f);
            c.Note($"isPlaying={ps.isPlaying} particles={ps.particleCount}");
            c.Status = ps.particleCount > 0 ? St.PASS : St.FAIL;
        }

        private static IEnumerator F2(Case c, GameObject go)
        {
            var light = go.GetComponentInChildren<Light>();
            int data = go.GetComponentsInChildren<UniversalAdditionalLightData>(true).Length;
            c.Note($"light={(light != null ? $"{light.type} range={light.range}" : "missing")} UniversalAdditionalLightData={data}");
            c.Status = light != null && data > 0 ? St.LOOK : St.FAIL;
            yield break;
        }

        private static IEnumerator F3(Case c, GameObject go)
        {
            var cam = go.GetComponentInChildren<Camera>();
            int data = go.GetComponentsInChildren<UniversalAdditionalCameraData>(true).Length;
            var rt = cam?.targetTexture;
            c.Note($"camera={cam != null} targetTexture={(rt != null ? $"{rt.width}x{rt.height}" : "null")} UniversalAdditionalCameraData={data}");
            c.Status = cam != null && rt != null ? St.LOOK : St.FAIL;
            yield break;
        }

        private static IEnumerator F4(Case c, GameObject go)
        {
            var lod = go.GetComponent<LODGroup>();
            c.Note($"lodCount={lod?.lodCount}");
            c.Status = lod != null && lod.lodCount == 2 ? St.LOOK : St.FAIL;
            yield break;
        }

        private static IEnumerator F5(Case c, GameObject go)
        {
            var dp = go.GetComponentsInChildren<DecalProjector>(true);
            var mat = dp.Length > 0 ? dp[0].material : null;
            c.Note($"DecalProjector={dp.Length} material='{mat?.name}' shader='{mat?.shader?.name}' supported={mat?.shader?.isSupported}");
            if (dp.Length == 0 || mat == null) { c.Status = St.FAIL; yield break; }

            // The pass the Screen Space technique draws. It is only in the bundle if the Unity
            // project's own renderer had a Decal feature at build time (FruitBundleBuilder adds one).
            var passes = new List<string>();
            try { for (int i = 0; i < mat.passCount; i++) passes.Add(mat.GetPassName(i)); }
            catch (Exception e) { passes.Add("pass names threw " + Short(e)); }
            bool hasPass = passes.Contains("DecalScreenSpaceProjector");
            c.Note($"material passes: [{string.Join(", ", passes)}]");

            c.Note($"game renderer had a Decal feature: {_hasDecal}");
            if (!_hasDecal)
            {
                bool on = FruitDecals.Ensure();
                c.Note(on ? "FruitDecals.Ensure(): Screen Space decal feature added at runtime" : "FruitDecals.Ensure() failed: " + FruitDecals.LastError);
                for (int i = 0; i < 3; i++) yield return null;   // the renderer rebuilds on the next frame
            }
            bool active = FruitDecals.Active;
            c.Note($"decal feature active now: {active}");

            // The API a blood splatter would use: stamp a second, smaller decal beside the first.
            var at = go.transform.position + _right * 1.2f;
            if (Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out var hit, 5f))
            {
                var placed = FruitDecals.Place(mat, hit.point, hit.normal, 0.8f);
                if (placed != null) _spawned.Add(placed.gameObject);
                c.Note("FruitDecals.Place() beside it: " + (placed != null ? "placed" : "failed"));
            }

            c.Status = !active ? St.FAIL : hasPass ? St.LOOK : St.INFO;
            c.Look = "orange/navy checker projected on the ground, plus a smaller rotated one to its right";
            if (active && !hasPass)
                c.Note("feature is on, but this bundle's decal material lacks the DecalScreenSpaceProjector pass: rebuild the test suite with the updated FruitBundleBuilder (it adds a Decal feature to the Unity project first)");
        }

        private static IEnumerator F6(Case c, GameObject go)
        {
            var vol = go.GetComponentsInChildren<Volume>(true);
            var profile = vol.Length > 0 ? vol[0].sharedProfile : null;
            c.Note($"Volume={vol.Length} profile='{profile?.name}' overrides={profile?.components?.Count}");
            c.Note($"main camera renders post-processing: {_postFx}");
            c.Status = profile == null ? St.FAIL : _postFx ? St.LOOK : St.INFO;
            yield break;
        }

        private static IEnumerator AU1(Case c, GameObject go)
        {
            var src = go.GetComponentInChildren<AudioSource>();
            yield return Wait(0.5f);
            var clip = src.clip;
            c.Note($"clip='{clip?.name}' length={clip?.length:F2}s isPlaying={src.isPlaying} AudioListeners in scene={FruitScene.Count<AudioListener>()}");
            c.Status = clip != null && clip.length > 1f && src.isPlaying ? St.LOOK : St.FAIL;
        }

        private static IEnumerator UI1(Case c, GameObject go)
        {
            var canvas = go.GetComponentInChildren<Canvas>();
            int images = go.GetComponentsInChildren<UnityEngine.UI.Image>(true).Length;
            var texts = go.GetComponentsInChildren<TextMeshProUGUI>(true);
            c.Note($"Canvas={(canvas != null ? canvas.renderMode.ToString() : "missing")} Image={images} TextMeshProUGUI={texts.Length}" +
                   (texts.Length > 0 ? $" text='{texts[0].text}' font='{texts[0].font?.name}'" : " (built without TMP Essentials?)"));
            c.Status = canvas != null && images > 0 ? St.LOOK : St.FAIL;
            yield break;
        }

        private static IEnumerator GC1(Case c, GameObject go)
        {
            var found = go.GetComponentsInChildren<Il2CppMap.Spinner.Spinner>(true);
            if (found.Length == 0)
            {
                c.Status = St.FAIL;
                c.Note("stub did not resolve to the game's Spinner. Platform has: " + Inventory(go.transform.Find("Platform").gameObject, false));
                yield break;
            }
            var s = found[0];
            bool fields = Mathf.Approximately(s.m_accelerationSpeed, 321f) && Mathf.Approximately(s.m_torqueMultiplier, 7f);
            c.Note($"resolved to game Spinner; m_accelerationSpeed={s.m_accelerationSpeed} m_torqueMultiplier={s.m_torqueMultiplier} (bundle: 321 / 7)");
            try { s.StartRotation(); s.SetTargetAngularVelocity(720f); c.Note("StartRotation + SetTargetAngularVelocity(720) called"); }
            catch (Exception e) { c.Note("calling it threw " + Short(e)); }

            yield return Wait(2.5f);
            float av = s.GetComponent<Rigidbody>().angularVelocity.magnitude;
            c.Note($"angular velocity after 2.5 s: {av:F2} rad/s");
            c.Status = fields && av > 0.5f ? St.PASS : St.FAIL;
        }

        private static IEnumerator D1(Case c, GameObject go)
        {
            var m = go.GetComponentInChildren<Renderer>().sharedMaterial;
            var tex = m?.GetTexture("_BaseMap");
            c.Note($"material='{m?.name}' texture='{tex?.name}'");
            c.Status = m != null && m.name.StartsWith("SharedMat") && tex != null ? St.PASS : St.FAIL;
            yield break;
        }

        private static IEnumerator MemoryAndUnload(Case c)
        {
            string path = Path.Combine(Folder, "fruittest_mem.bundle");
            if (!File.Exists(path)) { c.Status = St.SKIP; c.Note("fruittest_mem.bundle missing"); yield break; }

            var mem = FruitBundle.FromBytes(File.ReadAllBytes(path), "fruittest_mem:bytes", "fruittest_mem (bytes)");
            if (mem == null) { c.Status = St.FAIL; c.Note("LoadFromMemory returned null"); yield break; }
            c.Note("LoadFromMemory ok");
            var first = Spawn(c, "MEM_Cube", 0f, mem);
            c.Note("spawned from memory bundle: " + (first != null));

            yield return Wait(0.5f);
            mem.Unload(true);
            yield return null;
            var r = first != null ? first.GetComponentInChildren<Renderer>() : null;
            c.Note($"after Unload(true): spawned cube material={(r != null && r.sharedMaterial != null ? "still there" : "gone (expected: Unload(true) destroys loaded assets)")}");

            var again = FruitBundle.FromFile(path);
            _bundles.Add(again);
            var second = again != null ? again.Spawn("MEM_Cube", first != null ? first.transform.position + Vector3.up * 0.8f : _origin, Quaternion.identity) : null;
            if (second != null) _spawned.Add(second);
            c.Note("reloaded from file after unload: " + (second != null));
            c.Status = first != null && second != null ? St.PASS : St.FAIL;
            c.Look = "blue cube (the one from before the unload may have lost its material)";
            UpdateLabel(c);
        }

        private static IEnumerator SceneBundle(Case c)
        {
            var sb = Load("fruittest_scene");
            if (sb == null) { c.Status = St.SKIP; c.Note("fruittest_scene.bundle missing or failed"); yield break; }

            try
            {
                var op = SceneManagerAPI.ActiveAPI.LoadSceneAsyncByNameOrIndex("FruitTestScene", -1, new LoadSceneParameters(LoadSceneMode.Additive), false);
                c.Note("SceneManagerAPI.ActiveAPI.LoadSceneAsyncByNameOrIndex returned " + (op != null ? "an operation" : "null"));
            }
            catch (Exception e) { c.Note("scene load threw " + Short(e)); c.Status = St.FAIL; yield break; }

            GameObject root = null;
            for (float t = 0; t < 6f && root == null; t += Time.deltaTime)
            {
                root = GameObject.Find("FruitTestSceneRoot");
                yield return null;
            }
            if (root == null) { c.Status = St.FAIL; c.Note("scene root never appeared"); yield break; }

            var pos = NextCell(out float ground);
            root.transform.position = pos;
            _spawned.Add(root);
            MakeLabel(c, pos);
            c.Note($"scene '{root.scene.name}' loaded, isLoaded={root.scene.isLoaded}, roots={root.scene.rootCount}; root moved in front of you");
            c.Look = "grey slab with red/green/blue/yellow pillars and a blue light";
            c.Status = St.PASS;
            UpdateLabel(c);
        }

        private static IEnumerator Async(Case c)
        {
            string path = Path.Combine(Folder, "fruittest_async.bundle");
            if (!File.Exists(path)) { c.Status = St.SKIP; c.Note("fruittest_async.bundle missing"); yield break; }

            FruitBundle result = null;
            bool done = false;
            int frames = 0;
            var t0 = Time.realtimeSinceStartup;
            var load = FruitBundle.FromFileAsync(path, b => { result = b; done = true; });
            while (load.MoveNext()) { frames++; yield return load.Current; }
            c.Note($"completed={done} after {frames} frame(s), {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms");
            if (result == null) { c.Status = St.FAIL; yield break; }
            _bundles.Add(result);

            var go = Spawn(c, "ASYNC_Cube", 0f, result);
            c.Look = "green cube";
            c.Status = go != null ? St.PASS : St.FAIL;
            UpdateLabel(c);
        }

        private static IEnumerator UserBundles()
        {
            foreach (var file in Directory.GetFiles(Folder, "*.bundle"))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("fruittest") || name == "fruitprobe.bundle") continue;

                var c = Info("U:" + Path.GetFileNameWithoutExtension(name), "User bundle " + name);
                var b = Load(Path.GetFileNameWithoutExtension(name));
                if (b == null) { c.Status = St.FAIL; c.Note("did not load"); continue; }
                var names = b.AssetNames();
                c.Note($"{names.Length} assets: " + string.Join(", ", names.Take(20)));

                foreach (var prefab in names.Where(n => n.EndsWith(".prefab")).Take(8))
                {
                    GameObject go = null;
                    try { go = Spawn(c, prefab, 0f, b); }
                    catch (Exception e) { c.Note($"{prefab}: spawn threw {Short(e)}"); }
                    if (go == null) continue;
                    c.Note($"{Path.GetFileNameWithoutExtension(prefab)}: {Inventory(go, true)}");
                    yield return Wait(0.2f);
                }

                // Decal materials have no prefab to spawn: project each onto the ground instead.
                foreach (var matPath in names.Where(n => n.EndsWith(".mat")).Take(8))
                {
                    var mat = b.Load<Material>(matPath);
                    if (mat == null || mat.shader == null || !mat.shader.name.Contains("Decal")) continue;
                    var pos = NextCell(out _);
                    MakeLabel(c, pos);
                    if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out var hit, 5f))
                    {
                        var dp = FruitDecals.Place(mat, hit.point, hit.normal, 1f, spinDegrees: 0f);
                        if (dp != null) _spawned.Add(dp.gameObject);
                        c.Note($"{Path.GetFileNameWithoutExtension(matPath)}: decal '{mat.shader.name}' " +
                               (dp != null ? "projected on the ground" : "could not be placed: " + FruitDecals.LastError));
                    }
                }
                c.Status = St.LOOK;
                c.Look = "judge by eye; the notes list every component the game instantiated";
                Finish(c);
            }
        }

        // ── Environment ─────────────────────────────────────────────────────────

        private delegate IntPtr VfxResourcesFn();

        private static void Environment()
        {
            var e = new Case { Id = "E0", Title = "Environment" };
            _env.Clear();
            _env.Add(e);
            e.Note($"Unity {Application.unityVersion}, {SystemInfo.graphicsDeviceType} on {SystemInfo.graphicsDeviceName}");
            try
            {
                var urp = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
                if (urp == null) { e.Note("current pipeline is not URP?"); return; }
                _hasDepth = urp.supportsCameraDepthTexture;
                _hasOpaque = urp.supportsCameraOpaqueTexture;
                e.Note($"URP asset '{urp.name}': depthTexture={_hasDepth} opaqueTexture={_hasOpaque} HDR={urp.supportsHDR} MSAA={urp.msaaSampleCount}");

                var list = urp.m_RendererDataList;
                for (int i = 0; i < list.Length; i++)
                {
                    var d = list[i];
                    if (d == null) continue;
                    var ud = d.TryCast<UniversalRendererData>();
                    var feats = new List<string>();
                    for (int j = 0; j < d.rendererFeatures.Count; j++)
                    {
                        var f = d.rendererFeatures[j];
                        if (f == null) continue;
                        string type = f.GetIl2CppType().Name;
                        if (type.Contains("Decal") && f.isActive) _hasDecal = true;
                        feats.Add($"{type}{(f.isActive ? "" : " (off)")}");
                    }
                    e.Note($"renderer {i} '{d.name}' mode={(ud != null ? ud.renderingMode.ToString() : "?")} features=[{string.Join(", ", feats)}]");
                }
            }
            catch (Exception ex) { e.Note("URP inspection threw " + Short(ex)); }

            try
            {
                var cam = Camera.main;
                var data = cam != null ? cam.GetComponent<UniversalAdditionalCameraData>() : null;
                _postFx = data != null && data.renderPostProcessing;
                e.Note($"main camera '{cam?.name}': renderPostProcessing={_postFx}");
                if (data != null)
                {
                    // A camera can override the pipeline asset's depth/opaque settings either way;
                    // these are what it actually renders with.
                    e.Note($"main camera overrides: color={data.requiresColorOption} depth={data.requiresDepthOption} " +
                           $"-> resolved colorTexture={data.requiresColorTexture} depthTexture={data.requiresDepthTexture}");
                    _hasOpaque |= data.requiresColorTexture;
                    _hasDepth |= data.requiresDepthTexture;
                }
            }
            catch (Exception ex) { e.Note("camera inspection threw " + Short(ex)); }

            try
            {
                // VFX Graph simulates with compute shaders referenced from the project's VFXManager
                // settings. A game built without the VFX package has none, and there is no setter.
                var get = IL2CPP.ResolveICall<VfxResourcesFn>("UnityEngine.VFX.VFXManager::get_runtimeResources_Injected");
                IntPtr h = get != null ? get() : IntPtr.Zero;
                var res = h != IntPtr.Zero ? UnityEngine.Bindings.Unmarshal.FromIntPtrUnsafe(h).Target : null;
                e.Note("VFXManager.runtimeResources: " + (res != null ? res.ToString() : "null (VFX Graph cannot simulate in this build)"));
            }
            catch (Exception ex) { e.Note("VFX inspection threw " + Short(ex)); }
            MelonLogger.Msg("[FruitBundleTests] " + string.Join(" | ", e.Notes));
        }

        // ── Layout, spawning, labels ────────────────────────────────────────────

        private static void Layout()
        {
            var cam = Camera.main ?? FruitScene.First<Camera>(false);
            var t = cam != null ? cam.transform : null;
            var pos = t != null ? t.position : Vector3.zero;
            _fwd = t != null ? Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized : Vector3.forward;
            if (_fwd.sqrMagnitude < 0.01f) _fwd = Vector3.forward;
            _right = Vector3.Cross(Vector3.up, _fwd);
            _origin = pos + _fwd * 5f;
            _cell = 0;
        }

        private static Vector3 NextCell(out float ground)
        {
            int i = _cell++;
            var p = _origin + _right * ((i % 6 - 2.5f) * 3f) + _fwd * (i / 6 * 3.5f);
            if (Physics.Raycast(p + Vector3.up * 6f, Vector3.down, out var hit, 30f)) p = hit.point;
            else p.y -= 1.6f;
            ground = p.y;
            return p;
        }

        private static GameObject Spawn(Case c, string prefab, float lift, FruitBundle from)
        {
            var pos = NextCell(out float ground);
            c.Ground = ground;
            var go = from.Spawn(prefab, pos + Vector3.up * lift, Quaternion.LookRotation(-_fwd));
            MakeLabel(c, pos);
            if (go == null) return null;
            go.name = $"[FruitTest] {c.Id} {go.name}";
            _spawned.Add(go);
            return go;
        }

        private static void FindFont()
        {
            try { _font = TMP_Settings.defaultFontAsset; } catch { }
            if (_font == null)
                try { _font = FruitScene.First<TMP_FontAsset>(); } catch { }
        }

        private static void MakeLabel(Case c, Vector3 at)
        {
            if (c.Label != null) return;
            try
            {
                var go = new GameObject("[FruitTest] label " + c.Id);
                go.transform.position = at + Vector3.up * 3.2f;
                go.transform.rotation = Quaternion.LookRotation(_fwd);
                var t = go.AddComponent<TextMeshPro>();
                if (_font != null) t.font = _font;
                t.fontSize = 2.2f;
                t.alignment = TextAlignmentOptions.Center;
                t.rectTransform.sizeDelta = new Vector2(2.8f, 1.2f);
                c.Label = t;
                _spawned.Add(go);
                UpdateLabel(c);
            }
            catch (Exception e) { MelonLogger.Warning("[FruitBundleTests] label failed: " + Short(e)); }
        }

        private static void UpdateLabel(Case c)
        {
            if (c.Label == null) return;
            string col = c.Status switch
            {
                St.PASS => "#44ff66", St.FAIL => "#ff4444", St.LOOK => "#ffdd33", St.INFO => "#55ccff", _ => "#aaaaaa",
            };
            c.Label.text = $"<b>{c.Id}</b> {c.Title}\n<color={col}>{(c.Done || !_running ? c.Status.ToString() : "running...")}</color>";
        }

        // ── Bundles ─────────────────────────────────────────────────────────────

        private static FruitBundle Load(string name)
        {
            var b = FruitBundle.FromFile(Path.Combine(Folder, name + ".bundle"));
            if (b != null) _bundles.Add(b);
            return b;
        }

        private static void Teardown()
        {
            foreach (var go in _spawned)
                if (go != null) Object.Destroy(go);
            _spawned.Clear();
            var sceneRoot = GameObject.Find("FruitTestSceneRoot");
            if (sceneRoot != null) Object.Destroy(sceneRoot);

            // Unload(true) so a rebuilt bundle on disk is what the next run sees.
            foreach (var b in _bundles)
                try { b.Unload(true); } catch (Exception e) { MelonLogger.Warning("[FruitBundleTests] unload: " + Short(e)); }
            _bundles.Clear();
            _cases.Clear();
            _main = null;
        }

        // ── Cases, report ───────────────────────────────────────────────────────

        private static Case New(string id, string title)
        {
            var c = new Case { Id = id, Title = title };
            _cases.Add(c);
            return c;
        }

        private static Case Info(string id, string title) => New(id, title);

        private static string Inventory(GameObject go, bool children)
        {
            var comps = children ? go.GetComponentsInChildren<Component>(true) : go.GetComponents<Component>();
            var names = new List<string>();
            foreach (var comp in comps)
                names.Add(comp == null ? "<missing script>" : comp.GetIl2CppType().Name);
            return string.Join(", ", names.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
        }

        private static string Short(Exception e)
        {
            string msg = e.Message ?? "";
            int nl = msg.IndexOf('\n');
            return $"{e.GetType().Name}: {(nl > 0 ? msg.Substring(0, nl) : msg)}";
        }

        private static void WriteReport()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# FruitBundleTests - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
                foreach (var e in _env) foreach (var n in e.Notes) sb.AppendLine("- " + n);
                sb.AppendLine();
                sb.AppendLine("| Id | Test | Status | Look for | Details |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var c in _cases)
                    sb.AppendLine($"| {c.Id} | {c.Title} | **{c.Status}** | {c.Look} | {string.Join("<br>", c.Notes).Replace("|", "/")} |");
                sb.AppendLine();
                sb.AppendLine("PASS = verified by code. LOOK = loaded and set up correctly; confirm by eye. " +
                              "INFO = loads, but the game's setup keeps it from showing. FAIL = broken.");
                File.WriteAllText(ReportPath, sb.ToString());
            }
            catch (Exception e) { MelonLogger.Warning("[FruitBundleTests] report write failed: " + Short(e)); }
        }

        private static void Summary()
        {
            var counts = _cases.GroupBy(c => c.Status).Select(g => $"{g.Key}={g.Count()}");
            MelonLogger.Msg($"[FruitBundleTests] ===== done: {string.Join(" ", counts)} - report: {ReportPath} =====");
        }
    }
}
