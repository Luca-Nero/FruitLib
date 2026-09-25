# Custom models via AssetBundles

`FruitLib.FruitBundle` loads Unity AssetBundles: real prefabs with textures, colliders and
rigidbodies, built in the Unity editor. It replaces the `*_mesh.json` path for anything new.

Added in **3.2.0**; gate with `FruitVersion.Require("MyMod", 3, 2)`. Never used Unity? Start
with the [editor walkthrough](EDITOR_WALKTHROUGH.md).

Why this works although `dump.cs` shows `AssetBundle.LoadFromFile` stripped: UnityPlayer.dll
still registers the native loaders, and `AssetBundleNative` calls them directly.

Don't call `AssetBundle.LoadFromFile` / `LoadFromMemory` / `GetAllAssetNames` / `Unload` on the
interop type yourself. MelonLoader regenerates them, but their Unity 6 bodies pin arguments via
`Il2CppSystem.ReadOnlySpan.GetPinnableReference`, which is stripped too, so they throw
`Method not found`. `LoadAsset` is safe because the game never stripped it.

## One-time project setup

1. **Editor: exactly 6000.3.18f1** (the game's version, see `MelonLoader/Latest.log`). A different
   editor may produce bundles the game refuses or misreads.
2. Unity Hub → New project → template **Universal 3D** (URP). The game is URP. Call it e.g.
   `FruktBundles`, anywhere outside the mod repos.
3. **Edit → Project Settings → Player → Windows tab → Other Settings → Rendering:**
   untick *Auto Graphics API for Windows*, then make the list **Direct3D12, Direct3D11**
   (add Vulkan too if you like). The game can run either; the 0.17L test machine ran D3D11. A
   bundle only carries shaders compiled for the APIs in this list, and a missing one renders pink.
4. Copy the contents of `Frukt/Tools/UnityBundleBuilder/Assets/` over the project's `Assets/`
   (`Editor/` builder + test-suite generator, `FruitTest/Shaders/`, `GameStubs/`). A **FruitLib**
   menu appears. The reference project is `E:\Dev\Unity\AssetBundleTest`.
5. **FruitLib → Set Game Folder...** → the folder with `FRUKT.exe`
   (default `D:\SteamLibrary\steamapps\common\FRUKT Demo`).

## Step 1: prove the pipeline with the test cube

1. **FruitLib → Make Test Cube.** Creates `Assets/FruitProbe/ProbeCube.prefab`: an orange/navy
   checker-textured URP/Lit cube with a Rigidbody, tagged for bundle `fruitprobe`.
2. **FruitLib → Build Bundles.** Check the Console for warnings, then find
   `<game>/UserData/FruitBundles/fruitprobe.bundle`.
3. In `UserData/FruitLibConfig.ini` set `BundleProbe = true` (or tick *Test asset bundles* under
   Diagnostics in the mod menu).
4. Start the game, get into a level, look at open floor and press **F10**.

**Pass:** a checkered cube appears 2 m ahead and falls. The log has a `[FruitBundleProbe]` block
listing `assets/fruitprobe/probecube.prefab`, a `MeshRenderer`, and
`material 'ProbeMat' shader='Universal Render Pipeline/Lit' supported=True baseMap=ProbeChecker`.

**If it fails:**

| Symptom | Likely cause |
|---|---|
| `'fruitprobe.bundle' did not load` | Wrong editor version, or not built for StandaloneWindows64 |
| Exception mentioning `LoadFromFile` | The interop icall didn't resolve. Send the log |
| Cube is pink, `supported=False` | Graphics API list (setup step 3). **Shift+F10** retries with the game's shaders |
| Cube is solid colour, no checker | Texture didn't travel; check `baseMap=` in the log |
| Cube is invisible | Check `bounds=`; also that you aren't in a menu scene |

## Step 2: a real model (C4)

The cube's mesh is Unity's built-in one, so step 1 doesn't prove custom geometry.

1. Drag `Frukt/Models/C4/C4.fbx` (and its `Texture 0.png` / `Texture 1.png`) into `Assets/C4/`.
2. Select the FBX. Inspector → **Model** tab: *Scale Factor* so it's real size (check against the
   1 m default cube). Leave *Read/Write* off unless a mod needs the vertices from code.
   Unity's importer handles the handedness flip `obj_to_meshjson.py` does by hand.
3. **Materials** tab → *Extract Materials...* into `Assets/C4/`. Set each to
   `Universal Render Pipeline/Lit` and assign the textures.
4. Drag the FBX into a scene. Add a `BoxCollider` (or `MeshCollider`, *Convex* on for a
   Rigidbody) and a `Rigidbody`. Drag it back into `Assets/C4/` to make `C4.prefab`.
5. With the prefab selected, the bottom of the Inspector shows **AssetBundle**: *New...* → `bombsaway`.
6. **FruitLib → Build Bundles**, then F10 in game. You should see the probe cube and the C4 side by side.

## Using a bundle from a mod

Embed it in the mod DLL like the JSON meshes:

```xml
<ItemGroup>
  <EmbeddedResource Include="Bundles\bombsaway.bundle" />
</ItemGroup>
```

```csharp
using FruitLib;

private static FruitBundle _bundle;

public override void OnInitializeMelon()
{
    _bundle = FruitBundle.FromResource(Assembly.GetExecutingAssembly(), "bombsaway.bundle");
}

// when spawning
var c4 = _bundle.Spawn("C4", pos, rot);          // file name or full lower-case path
c4.AddComponent<MyC4Behaviour>();                // mod scripts are added after, never in the bundle
```

Or load individual assets: `_bundle.Load<Mesh>("C4Brick")`, `_bundle.Load<Texture2D>("Texture 0")`.

`FruitBundle.FromFile(path)` loads a loose file instead, handy while iterating: rebuild in Unity
and restart the game without rebuilding the mod.

### API at a glance

| Call | Does |
|---|---|
| `FruitBundle.FromResource(asm, "x.bundle")` | Load a bundle embedded in your DLL |
| `FruitBundle.FromFile(path)` / `FromBytes(bytes, key)` | Load from disk / from memory |
| `FruitBundle.FromFileAsync(path, done)` | Load without a hitch; run as a coroutine (`MelonCoroutines.Start`) |
| `bundle.Load<T>(name)` | One asset (`GameObject`, `Mesh`, `Material`, `AudioClip`, `RuntimeAnimatorController`...) |
| `bundle.Spawn(prefab, pos, rot)` | Load + instantiate, with pink-shader fallback |
| `bundle.AssetNames()` / `bundle.Unload(all)` | List contents / unload (`true` also destroys what came out of it) |
| `FruitNative.SetInteger`, `SetController` | Animator methods the game stripped |
| `FruitNative.SetBlendShapeWeight` / `GetBlendShapeWeight` | Blend shapes, likewise |
| `FruitDecals.Ensure()` / `Place(...)` | Decals (see below) |

Loading the same bundle twice returns the already-open one rather than failing, as a
second Unity load would.

## Rules

- **No mod scripts in bundles.** A prefab can carry Unity/URP components (renderers,
  colliders, rigidbodies, joints, animators, lights, particle systems, Timeline, UI, TMP) and
  the **game's own** components via stub scripts (see *Game components in bundles* below).
  Mod MonoBehaviours are injected Il2Cpp types that Unity can't deserialize from a bundle;
  add them after `Spawn`.
- **Shaders:** keep the bundle's own copy (default). It has exactly the variants its materials
  use. `rebindShaders: true` / `FruitBundle.RebindShaders` swaps to the game's copies. Call
  `FruitBundle.CaptureGameShaders()` before the first bundle load if you plan to. `Spawn`
  already falls back automatically, per material, when a bundled shader is unsupported.
- **One bundle per mod** is the easy default.
- **A game Unity upgrade means rebuilding bundles** with the new editor. Check `Latest.log` after
  each game update. The JSON meshes don't have this problem, which is why the loader stays.

## Capability test suite

Measures what bundles can and cannot do in the running game.

1. Unity: **FruitLib → Build Test Suite**. Generates everything under
   `Assets/FruitTest/Generated` (wiped each run) and builds five `fruittest*` bundles into the game.
2. Game: `BundleProbe = true`, stand somewhere open and flat, press **Ctrl+F10**. About 30 cases
   spawn on a 6-wide grid in front of you, each with a floating label (PASS / FAIL / LOOK / INFO).
   It takes ~40 s. Ctrl+F10 again tears everything down and reruns against freshly loaded bundles.
3. The report is `UserData/FruitBundles/FruitBundleTests_report.md`, rewritten after every case.
   Each case also logs one `[FruitBundleTests]` line.

| Group | Cases |
|---|---|
| Meshes | M1 generated + non-readable + convex MeshCollider · M2 90k verts / 32-bit indices · M3 submeshes · M4 blend shapes |
| Shading | T1 BC7/BC5/BC6H/RGBA32 · S1 URP stock shaders + keyword variants · S4 custom HLSL driven by a mod-set global · S5 refraction via opaque/depth textures · S7 MaterialPropertyBlock · S8 multi-pass x-ray |
| Animation | A1 skinned rig + Animator: bool/float/trigger/int params, controller swap · TL1 Timeline |
| Physics | P1 bounce material · P2 HingeJoint · P3 CharacterJoint (both classes absent from game code) · P4 ConfigurableJoint |
| Features | F1 particles · F2 light + URP light data · F3 camera→RenderTexture · F4 LOD · F5 decal · F6 local post-processing volume |
| Other | AU1 3D audio · UI1 world-space Canvas/Image/TMP · GC1 game component via stub · D1 cross-bundle dependency |
| Loading | B1 sync · B3 memory + Unload(true) + reload · SC1 scene bundle (additive) · B4 async (runs last; if the game crashes, it was this) |

**User bundles:** any other `*.bundle` in `UserData/FruitBundles` is loaded after the suite. Up to
8 of its prefabs are spawned, and every component the game actually instantiated is listed in the
report. That's the route for things the generator can't author:

- a **Shader Graph** material
- an imported **humanoid** character with an Avatar and a mocap clip (A1 is a Generic rig)
- a real production model (C4 / Javelin) with its actual textures

Build each into its own bundle (e.g. `user_vfx`); FruitLib → Build Bundles copies it across.

### Game components in bundles (GC1)

A bundle references a script as (assembly, namespace, class). `Assets/GameStubs` has an assembly
named `FRUKT` with a `Map.Spinner.Spinner` whose serialized fields match the game's, so the
prefab's component becomes the real game Spinner at load. Add more stubs the same way: copy the
namespace, class name and **serialized** field names and types from `dump.cs`. Everything else can
be left out.

### Stripped-API helpers the suite uses

`FruitNative` (public): `SetInteger`, `SetController` (Animator), `SetBlendShapeWeight` /
`GetBlendShapeWeight`. `FruitBundle.FromFileAsync` loads without a hitch. All go straight to the
engine's icalls; see `AssetBundleNative` for the marshalling rules.

### Tested and not possible (0.17L)

- **VFX Graph.** Loads, components exist, nothing draws. The game build has no VFX compute
  shaders or runtime resources (no setter exists for either). Use Shuriken particle systems.
- **Mod scripts on bundled prefabs.** By design: add them after `Spawn`. Game scripts do work
  (see GC1).

### Decals (FruitDecals)

The game ships URP's decal code but its renderer has no Decal feature, so a `DecalProjector`
draws nothing on its own. With FruitDecals they work (confirmed on 0.17L, test F5). `FruitDecals.Ensure()` adds a Screen Space decal feature to the active
renderer at runtime (memory only, until the game quits), and
`FruitDecals.Place(material, point, normal, size, lifetime)` stamps one onto a surface:

```csharp
if (Physics.Raycast(ray, out var hit))
    FruitDecals.Place(_bundle.Load<Material>("BloodSplat"), hit.point, hit.normal, 0.4f, lifetime: 20f);
```

Decal materials come from bundles (the game has no decal shader): create one in Unity with
**Create → Material**, shader **Shader Graphs/Decal**, assign *Base Map* (step by step in the
[walkthrough](EDITOR_WALKTHROUGH.md#6-a-decal-material-user_decals)). FruitBundleBuilder adds
a Screen Space Decal feature to the Unity project's renderers before every build (or use
**FruitLib → Enable Decals In Project**). Without it, Unity strips the `DecalScreenSpaceProjector`
pass from the material and nothing draws.
