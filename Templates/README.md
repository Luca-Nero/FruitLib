# Mod templates

Starting files for a FruitLib mod. They're the same patterns BombsAway, GunsGunsGuns and
Singularity use, so a new mod starts where the existing ones ended up. Copy them, rename
`MyMod` throughout, and delete what you don't need.

| File | What it is | Change |
|---|---|---|
| `MyMod.csproj` | netstandard2.1 class library, Release strips symbols, deploys to every live FRUKT install on build | the file name and the `MyMod` in the deploy message |
| `Directory.Build.props` | every game, MelonLoader and FruitLib assembly as a non-copied reference | nothing, except to refresh it after a game update |
| `FruitGate.cs` | checks FruitLib is installed and new enough before anything touches it | the namespace only |
| `Core.cs` | the MelonMod: gate, config, menu, HUD, one inventory item, perf section, update check | names, version, FruitLib minimum, your item |
| `Config.cs` | settings as public static fields with menu attributes | your settings |
| `ConfigLoader.cs` | reads and writes the sectioned, commented ini in UserData | namespace, `FileName`, `Title`, `FieldHelp` |
| `.github/workflows/release.yml` | pushing a `v*` tag builds and attaches the DLL to a GitHub release | the ModRefs repository it checks out |

## Rules the templates follow

- **FruitLib is an optional dependency.** `FruitGate.Check` runs first and, if FruitLib is
  missing or too old, the mod logs why and unregisters. Anything that touches a FruitLib
  type lives in its own `[MethodImpl(NoInlining)]` method, called only after the gate
  passed; otherwise the JIT fails on the missing type before the check can run.
- **No `PatchAll`.** MelonLoader applies every `[HarmonyPatch]` in the assembly already;
  calling it again installs each hook twice.
- **One inventory item per variant.** `SetDisplay` can't refresh a copy already on the
  toolbar, so an item that cycles through variants shows a stale icon there.
- **Config lives in UserData**, through `FruitPaths.Config`, so reinstalling the mod
  doesn't lose settings.
- **Deploy is local only.** The PostBuild step skips itself on CI, with
  `-p:SkipDeploy=true`, and for any install carrying `DEPRECATED_DONT_BUILD_AGAINST.md`.

## Keeping them current

These are copies, not a shared dependency: each mod builds in its own repository. When a
template improves, carry the change into the mods by hand. The one to keep exactly in sync
is `FruitGate.cs`, which should be byte-identical everywhere apart from the namespace.
