# Performance monitor

`FruitPerfMon` is a live overlay showing frame rate, mod-registered counters and
timed sections. Toggle it in-game with **F11**; press **R** while it's showing to
reset peak values.

It draws as a [HUD panel](hud.md) pinned to the top right, so it never displaces
gameplay HUDs when you switch it on.

## Counters

Register a getter; it's polled every frame and displayed with a running peak.

```csharp
FruitPerfMon.RegisterCounter("MyMod Things", () => _things.Count);
FruitPerfMon.RegisterCounter("MyMod VFX",    () => VfxRunner.ActiveCount);
```

Prefix names with your mod so a busy overlay stays readable. Names are unique
key, registering the same name twice replaces the getter.

**Getters run every frame whether or not the overlay is visible.** Return a
field, not a computed result. `_list.Count` is fine, a LINQ query over the scene
is not.

## Timers

Bracket a section to see its rolling average and peak in milliseconds.

```csharp
FruitPerfMon.Begin("MyMod Scatter");
DoExpensiveWork();
FruitPerfMon.End("MyMod Scatter");
```

Averaged over the last 60 samples. `End` without a matching `Begin` is ignored.
These use a `Stopwatch` per name, so they measure wall-clock time on the calling
thread, fine for spotting a spike, not a substitute for a real profiler.

Leaving timers in shipped code is normal and cheap, but don't wrap something
called thousands of times per frame; the timing overhead becomes the measurement.

## Reading the numbers elsewhere

```csharp
float pressure = FruitPerfMon.PressureLevel;   // 0..1
float baseline = FruitPerfMon.LongFps;         // ~300-frame average
```

`PressureLevel` is the larger of two signals: how far below `TargetFps` the
current short-window frame rate sits, and how far it has dropped relative to the
long-window average. The second matters because a machine that never reaches 60
shouldn't read as permanently overloaded, a sustained 45 fps reports low
pressure, while 60 dropping to 45 reports high.

Use it to scale your own effects back under load:

```csharp
int budget = Mathf.RoundToInt(maxParticles * (1f - FruitPerfMon.PressureLevel));
```

## Tuning

```csharp
FruitPerfMon.ToggleKey    = KeyCode.F11;   // public static fields set at init
FruitPerfMon.TargetFps    = 60f;
FruitPerfMon.PressureDrop = 0.15f;         // relative drop before pressure registers
```

These are global, not per-mod. Changing them affects every mod reading
`PressureLevel`, so leave them alone unless the user asked.
