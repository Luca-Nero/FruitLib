# Utilities

Small pieces every mod ends up needing.

## FruitPaths

Where your files go. Configs live in MelonLoader's `UserData` folder, not next to the DLLs in
`Mods`: `Mods` stays a folder of mods, and settings survive a player clearing it to reinstall.

```csharp
public static string IniPath => FruitPaths.Config("MyModConfig.ini", typeof(ConfigLoader).Assembly);
```

`Config(fileName, owner)` returns `UserData/<fileName>`. The first time it's asked, if an older
build of your mod left the file next to its DLL, it's moved across, so an update keeps the
player's settings. If both exist, `UserData` wins and the old one is left alone.
`FruitPaths.UserData` is the folder itself, created if missing.

## FruitScene

Scene lookups that survive this build's IL2CPP stripping.

```csharp
var cam   = FruitScene.First<Camera>();          // first instance, or null
int count = FruitScene.Count<AudioListener>();   // how many
```

**Use `First<T>` instead of `Object.FindObjectOfType<T>`, everywhere.** The singular Unity
overload is stripped from the game and throws `NotSupportedException` at runtime, even though it
compiles. Scene lookups usually sit inside a `try/catch`, so the exception is swallowed and your
feature silently never runs. Inactive objects are included by default, because the game parks
unselected tools and views disabled instead of destroying them.

## FruitForces

A shared registry of force fields, so one mod's physics can bend another's without either
referencing the other. A gravity-well mod registers a sampler; everything that integrates its own
motion (FruitLib's projectiles included) adds the sum to its acceleration.

```csharp
// The field owner:
FruitForces.Register("MyMod:Wells", pos => SumOfMyWellsAt(pos));   // acceleration, m/s²
FruitForces.Unregister("MyMod:Wells");

// Anything that moves itself:
if (FruitForces.Any) velocity += FruitForces.SampleAt(position) * dt;
```

Samplers return **acceleration**, not force, so callers need no mass. Return `Vector3.zero`
outside your radius. They run per moving object per frame, so no allocation and no scene queries:
read a few cached positions. Re-registering an id replaces it.

## FruitUpdateCheck

```csharp
FruitUpdateCheck.Register("MyMod", "1.2.0", "GitHubOwner", "MyModRepo");
```

Checks the repo's latest GitHub release once at launch and lists anything out of date in a HUD
panel, showing the installed and newer versions. It fails silently when offline or rate-limited,
and players can turn it off under **Updates** in FruitLib's settings.
