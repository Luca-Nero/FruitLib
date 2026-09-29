# Performance monitor

`FruitPerfMon` is a live debug overlay. Frame rate and load sit at the top, and under them
each mod has its own section with counters, timed sections and live values.

| Key | Does |
|---|---|
| **F11** | Show / hide the overlay |
| **Shift+F11** | Next view: every mod's counters at once, then each mod in full, then back round |
| **Ctrl+F11** | Reset the peaks of what's on screen |

The key and the target frame rate are in FruitLib's settings under *Performance*. The
overlay is a [HUD panel](hud.md) pinned to the top right, so it never displaces gameplay
HUDs.

## Your mod's section

Ask for it once, by the name players know your mod by, and keep the handle:

```csharp
static readonly ModPerf Perf = FruitPerfMon.For("MyMod");
```

Asking again with the same name returns the same handle. Names inside a section are
yours alone, so there's no need to prefix them.

### Counters

A number, shown with its running peak. Shown in the all-mods view and in your own.

```csharp
Perf.Counter("Things", () => _things.Count)
    .Counter("VFX",    () => VfxRunner.ActiveCount);
```

Registering a name again replaces the getter. A getter that throws is disabled, marked
in the overlay and logged once.

### Values

Any state worth watching while you debug, as text. Shown only in your mod's own view.

```csharp
Perf.Value("Selected", () => Selected.ToString())
    .Value("Hologram", () => _holoRunning ? "via " + _holoSource : "off");
```

### Timers

A section's rolling average and peak in milliseconds, over the last 60 samples.

```csharp
using (Perf.Time("Scatter"))
    DoExpensiveWork();

// or, when a using block doesn't fit:
Perf.Begin("Scatter");
DoExpensiveWork();
Perf.End("Scatter");
```

`End` without a matching `Begin` is ignored. Each name has one `Stopwatch`, so this is
wall-clock time on the calling thread: good for spotting a spike, not a profiler. Timers
are cheap enough to leave in shipped code, but don't wrap something that runs thousands
of times a frame; the timing overhead becomes the measurement.

### Cost

Counter and value getters run once a frame **only while their view is on screen**, so a
hidden overlay costs nothing. Peaks are therefore only tracked while shown. Return a
field, not a scene query: `_list.Count` is fine, a LINQ query over the scene is not.

Timers record whether the overlay is showing or not.

## Reading the load elsewhere

```csharp
float pressure = FruitPerfMon.PressureLevel;   // 0..1
float baseline = FruitPerfMon.LongFps;         // ~300-frame average
```

`PressureLevel` is the larger of two signals: how far below `TargetFps` the current
short-window frame rate sits, and how far it has dropped relative to the long-window
average. The second matters because a machine that never reaches 60 shouldn't read as
permanently overloaded: a sustained 45 fps reports low pressure, while 60 dropping to 45
reports high.

Use it to scale your own effects back under load:

```csharp
int budget = Mathf.RoundToInt(maxParticles * (1f - FruitPerfMon.PressureLevel));
```

`TargetFps` comes from FruitLib's settings, since it's shared by every mod that reads the
load. `PressureDrop` (default 0.15, the relative drop before pressure registers) is a
static field; leave it alone unless the player asked.

## Migrating from 4.x

The flat `FruitPerfMon.RegisterCounter` / `Begin` / `End` still compile, marked
`[Obsolete]`. They file everything under your assembly's name. Move to
`FruitPerfMon.For("MyMod")` to pick the section name yourself and to get values.
The **R** key no longer resets peaks: it's the game's rotate key. Use Ctrl+F11.
