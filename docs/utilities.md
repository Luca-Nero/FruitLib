# Utilities

Small pieces every mod ends up needing.

## FruitLog

Informational logging that stays quiet unless the player turned on *Verbose log* in
FruitLib's Diagnostics settings. With a dozen mods loaded, routine "built", "registered"
and "equipped" lines from each of them bury the one warning that matters.

```csharp
FruitLog.Info("[MyMod] model built");        // only with Verbose log on
if (FruitLog.Verbose) DumpExpensiveState();   // skip the work too
MelonLogger.Warning("[MyMod] no camera");     // warnings and errors: always, straight to MelonLogger
```

Keep your one "MyMod vX loaded." line unconditional; everything routine after it goes
through `FruitLog.Info`. The switch is live, no restart needed.

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

`FruitPaths.WriteAllTextAtomic(path, contents)` (5.5.0) writes a file via a temp file and a
replace, so a crash mid-write can't leave a truncated ini. Use it for every config write.

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

A shared registry of force fields and winds, so one mod's physics can bend another's without
either referencing the other. A gravity-well mod registers a field; everything that integrates its
own motion (FruitLib's projectiles included) adds the sum to its acceleration.

```csharp
// The field owner:
FruitForces.Register("MyMod:Wells", pos => SumOfMyWellsAt(pos));   // acceleration, m/s²
FruitForces.Unregister("MyMod:Wells");                             // removes fields and winds

// Anything that moves itself:
if (FruitForces.Any) velocity += FruitForces.SampleAt(position) * dt;
```

Fields return **acceleration**, not force, so callers need no mass. Return `Vector3.zero`
outside your radius. They run per moving object per frame, so no allocation and no scene queries:
read a few cached positions. Re-registering an id replaces it. A field that throws is reported
once and switched off, so it can't take out other mods' physics or flood the log.

### Fields that see the round

The position-only form above is the simple case. The fuller form gets a `ForceQuery` (position,
velocity, `MassKg`, `Dt`, and the `Projectile` when it is a FruitLib round, plus `SpecId`) and a
`ForceOptions` filter that is checked before your field is called:

```csharp
FruitForces.Register("MyMod:Repulsor", q =>
{
    Vector3 away = q.Position - repulsorPos;
    float d = away.magnitude;
    return d < radius ? away / d * (strength * (1f - d / radius)) : Vector3.zero;
},
new ForceOptions
{
    SpecPrefix = "MyMod.",        // only rounds whose spec id starts with this (null = all)
    IncludeCosmetic = true,       // default: a remote player's shot bends the same as on the host
    IncludeOther = false,         // skip non-rounds, i.e. SampleAt callers (default: true)
});
```

`FruitForces.Accelerate(query)` is the same sum for a caller that has more to say than a position
(`SampleAt` is `Accelerate` with only the position filled in). The older
`Register(id, ForceSampler)` overload, position in and acceleration out, still works unchanged.

### Wind

A wind moves the air, not the round. A round's drag is computed against the air around it, so a
crosswind drifts a light or slow round further than a heavy one, which is how it should be, and
no field of yours has to know the round's mass.

```csharp
FruitForces.RegisterWind("MyMod:Weather", pos => windDirection * windSpeed);   // air velocity, m/s
Vector3 air = FruitForces.WindAt(somePosition);                                // summed over every wind
```

Winds add up, re-registering an id replaces it, and `Unregister` removes it.

To do more than accelerate a round (redirect it, teleport it, stop it, change what a surface does
to it), use `FruitBallistics.ProjectileStep` and `BeforeSurfaceHit` instead; see
[ballistics](ballistics.md#steering-a-round).

## FruitUpdateCheck

```csharp
FruitUpdateCheck.Register("MyMod", "1.2.0", "GitHubOwner", "MyModRepo");
```

Checks the repo's latest GitHub release once at launch and lists anything out of date in a HUD
panel, showing the installed and newer versions. It fails silently when offline or rate-limited,
and players can turn it off under **Updates** in FruitLib's settings.
