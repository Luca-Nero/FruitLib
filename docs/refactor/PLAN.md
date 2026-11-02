# FruitLib refactor plan

The plan for FruitLib's makeover, written so agents can pick up one milestone at a time. Scoped with the designer on 2026-11-02 from the audit of 5.39.0. Evidence is in [`baseline/`](baseline/).

**Status:** draft for the designer's review. No milestone has started.

## What this refactor does

FruitLib grew around one private mod. NeuroFrukt will never be released, yet its body stack is the largest thing in the library and installs hooks for every player. This refactor gives that code back to NeuroFrukt, cuts what's left of the debugging and fallback weight, then tidies the library that remains and its public surface.

| | Today | After |
|---|---|---|
| Lines in the library | 32,514 | about 20,000 |
| Hooks installed for every player | 39 | 27 |
| Hooks on per-frame or per-physics-step paths | 3 | 0 |
| Public surface | 1,277 members | Only what a released mod needs, reviewed by the designer |
| Debug code in the shipped DLL | 3,573 lines | 0 |

### The designer's decisions

These are settled. Don't reopen them inside a milestone.

| Decision | Result |
|---|---|
| NeuroFrukt is a personal project and will never be released | Anything only it uses leaves FruitLib, or is removed |
| What FruitLab touches stays | It will be released. BombsAway, GunsGunsGuns, FruitCity and Singularity stay supported too |
| Body stack | Moves to NeuroFrukt |
| `FruitEyeCamera`, `FruitTextInput` | Move to NeuroFrukt |
| `FruitMaps` | Stays. A map-loading framework comes later, outside this plan |
| `FruitForces` | Stays |
| `FruitInteractionSpec` | Stays, to be generalized later |
| JSON mesh loader | Stays for now. The designer will decide how to communicate its removal |
| IMGUI fallbacks | Removed. The native menu gets a second look as part of that |
| GitHub update check | Stays, on by default |
| Probes and diagnostics | Moved out of the shipped DLL |
| `FruitDecals`, unused gait knobs, older walking paths, obsolete APIs | Removed |

### What's out of scope

- Gameplay or tuning changes. What stays should behave the same, except where a milestone says otherwise.
- New features, including the map framework and generalizing interactions.
- Removing the JSON mesh loader.
- Rewriting NeuroFrukt. It gets its code back working, and what it does with it later is its own business.
- Installing hooks on demand. It's parked: see [Parking lot](#parking-lot).

Ideas that come up go in the parking lot, not into a milestone.

## The finding that shapes the plan

FruitLab calls `FruitBodyControl.Interact`, which reaches the whole walking stack: `Interact → TickInteraction → PositionInteraction → GoTo → FollowPath → FruitNav`. On paper, the stack can't leave.

At runtime it can. FruitLab only calls `Interact` when a body uses an item (`by != null`). That only comes from `FruitBodyControl.Use`, and only NeuroFrukt calls `Use`. The player's paths pass `null` and never walk. So in a FruitLab release with no NeuroFrukt, that code never runs today.

**Consequence:** a released FruitLab has no bodies using medical items unless a mod supplies them, the same as now. A small interface keeps that door open. See [R2-M1](#r2-m1).

The cut itself is small. Code that stays touches the stack in 15 lines, and FruitLab calls 7 members:

| Staying code | Reaches for | Replacement |
|---|---|---|
| `FruitLab`: `Interact`, `InteractionOf`, `CanApplyInteraction`, `ConsumeInteractionItem`, `Holding`, `Drop` | `FruitBodyControl` | The body-actions interface |
| `FruitLab`: `SurfacePoint` | `FruitBodyControl.Strikes` | Moves to `FruitBodies`; the player's paths use it |
| `FruitInteraction.Complete`, `Fail`, `Apply` | `FruitBodyControl` | The body-actions interface |
| `FruitInteractionTrace` | `FruitPose`, `FruitNav`, `FruitBodyControl` | Moves with the stack |
| `FruitItems`, `FruitPropMenu`: drop from the holder's hand | `FruitBodyControl.Drop` | The body-actions interface |
| `FruitItems`: naming an object | `FruitSight.Name` | The naming code stays in FruitLib |
| `FruitPlaces`: four distance sums | `FruitNav.Flat` | A small helper in Utilities |
| `FruitMenu.IsInputSuppressed` | `FruitTextInput` | A way for mods to register an input blocker |
| `FruitInteraction`: posture fields | `FruitPoseShape`, `FruitBone` | These two data types stay; `FruitPose` moves |

To reproduce this, read [`baseline/scope_report.txt`](baseline/scope_report.txt). It comes from a semantic compile of FruitLib and the six active mods, all with 0 errors.

### What moves, what goes

| Code | Lines | Fate |
|---|---|---|
| `FruitBodyControl`, all 17 partials | 5,792 | Moves |
| `FruitPose`, `FruitPose.Native` | 826 | Moves, except `FruitPoseShape` and `FruitBone` |
| `FruitNav`, `FruitNavNative`, `FruitNavDraw` | 1,357 | Moves |
| `FruitSight` | 675 | Moves, except object naming |
| `FruitEyeCamera`, `FruitTextInput`, `FruitPng` | 580 | Moves |
| `FruitHandsProbe`, `FruitNavProbe`, `FruitSightProbe` | 2,025 | Moves |
| `FruitGaitProbe` | 552 | Removed |
| Total | about 11,800 | |
| 25 settings: `StepAssist`, `Walk*`, `Nav*`, `Pickup*` and the moved probes' | | Move to NeuroFrukt's config |
| `docs/gait.md`, `nav.md`, `poses.md`, `sight.md`, `interactions.md`, and the bodies half of `items.md` | | Move, except what FruitLab's authors need about the interface |

## How agents work on this

### Rules

1. **One milestone per session.** Read this section, then your milestone. Don't drift into the next one. Ideas go in the parking lot.
2. **Stay inside what your milestone owns.** Each one lists the files it may change. If you need another file, stop and ask the supervisor.
3. **Tests first** where the code can run outside the game.
4. **Acceptance criteria belong to the designer.** No agent rewrites or weakens one. If a criterion can't be met, leave it failing and report it with the evidence.
5. **Gameplay and public API calls belong to the designer.** Technical choices inside a milestone are yours.
6. **Behaviour stays the same** unless the milestone says otherwise. If you find a bug, record it and leave it, unless fixing it is the milestone.
7. **Never deploy.** Always build with `-p:SkipDeploy=true`. A plain build copies into the live Steam install and into `ModRefs`, which every other mod compiles against. Only the supervisor deploys, after the gates pass.
8. **Never push, tag or release.** Those are the designer's.

### Who does what

| Worker | Good for | Limits |
|---|---|---|
| **Claude session in a worktree** | All code that touches the game. The only worker that can build the library | Paid. One per file set |
| **`claude-code` on the board** (`repo=fruitlib`) | Code that runs without game DLLs: harnesses, gate tools | Its containers have no `ModRefs`, so it can't build the library |
| **`deepseek-flash`** (`allow_cloud=true`, project `fruitlib`) | Maps, sweeps, doc drafts, public-member classification, first-pass reviews | Read-only, reads `/home/user/repos/fruitlib`, reports in chat |
| **`deepseek-pro`** (`difficulty=high`) | Reviewing a milestone's diff, harder reading | About $0.25 to $0.50 per ticket. Treat findings as hypotheses to check |
| **Local worker** | Only very small tasks | One at a time, and T-107 hit its 30-minute limit on one subsystem |
| **Supervisor** | Assigns, reviews, merges, bumps versions, deploys | Holds the hub files |
| **Designer** | Live checks in the game, API and gameplay calls, release | Asked once per phase, not per milestone |

Cloud workers read the main checkout's working tree, so they don't see a worktree's changes. Give them the diff as text. They can't read NeuroFrukt, FruitLab or any other mod, and NeuroFrukt stays that way unless the designer lists it.

### Hub files

Many milestones want these, and two parallel changes to them conflict. Only one milestone at a time may hold them; the supervisor assigns the hold.

`Core/FruitLibMod.cs`, `Core/FruitLibConfig.cs`, `Core/FruitVersion.cs`, `FruitLib.csproj`, `Directory.Build.props`

Parallel milestones don't edit these. They list what they need in their report, and the supervisor applies it when merging.

### Gates

Every merge passes all of these. [R0-M3](#r0-m3) builds the script.

| Gate | Passes when |
|---|---|
| **Build** | `dotnet build FruitLib.csproj -c Release -p:SkipDeploy=true` succeeds, with no more warnings than the milestone started with (5 today) |
| **Harnesses** | Pass, and no check count drops unless the checks moved with their code. Today: crowd 151, interaction 50 |
| **API snapshot** | The public surface matches the committed snapshot, or the diff is part of the milestone and explained |
| **Hook ledger** | The installed hooks match the committed list |
| **Consumers** | BombsAway, GunsGunsGuns, FruitLab, Singularity, FruitCity and NeuroFrukt compile against the fresh DLL, in a scratch copy of `ModRefs` |
| **Layers** | No dependency appears that the committed list doesn't allow. From R3 on |

Passing the gates doesn't prove the game works. Each phase ends with the designer's live check.

## Phases

| Phase | What | Can run in parallel |
|---|---|---|
| [R0](#r0-safety-net) | Safety net: gates, buildable worktrees, a baseline | Partly |
| [R1](#r1-cut-the-weight) | Gate the diagnostics, remove what was agreed, remove IMGUI | Yes, 4 streams |
| [R2](#r2-give-neurofrukt-its-code-back) | The body stack moves to NeuroFrukt | No, in order |
| [R3](#r3-tidy-what-remains) | Lifecycle, reset fixes, layers | Partly |
| [R4](#r4-public-surface) | Decide and shrink the public API | Yes, 6 streams |
| [R5](#r5-docs-and-600) | Docs, templates, mods updated, 6.0.0 | Yes, then one release step |

R1 comes before R2 so that less dead code has to move. R2 comes before R3 and R4 so that nobody tidies or reviews code that's leaving.

---

## R0: Safety net

**Exit:** the gates pass on a clean checkout, a worktree can build, and the designer has a baseline to compare against.

<a id="r0-m1"></a>
### R0-M1: Land everything in git
- **Goal:** every repo holds what's actually running. `master` must build, because worktrees start from it.
- **Owns:** git state only.
- **Who:** supervisor, with the designer's go-ahead.
- **Done when:**
  - `claude/add-claude-md` is merged into FruitLib's `master`, with `aaf417d` included.
  - A fresh checkout of `master` builds with `-p:ModRefsDir=...` and `-p:SkipDeploy=true`.
  - NeuroFrukt is a git repo with its current state committed. It has no remote.

<a id="r0-m2"></a>
### R0-M2: Builds that work from a worktree
- **Goal:** any worktree can build FruitLib and the mods without touching the game.
- **Owns:** `FruitLib.csproj`, `Directory.Build.props` (hub hold), `Tests/*.csproj`.
- **Who:** Claude session.
- **Why:** `Directory.Build.props` looks for `ModRefs` only one or two folders up, which fails in a worktree. `FruitLib.csproj:58` defaults `FruktModRefsDir` to `E:\Dev\ModDev\Frukt\ModRefs`, which doesn't exist.
- **Done when:**
  - One environment variable or property points every project at `ModRefs`, documented in `CLAUDE.md`.
  - A build in a worktree under `.claude/worktrees/` succeeds.
  - A build with `SkipDeploy` leaves the Steam install and `ModRefs` untouched, confirmed by file timestamps.

<a id="r0-m3"></a>
### R0-M3: The gate script
- **Goal:** one command runs every gate.
- **Owns:** `Tests/Gates/` (new), `Tests/CrowdTests.csproj`.
- **Who:** `claude-code` on the board can draft the tools; a Claude session wires the consumer builds, which need `ModRefs`.
- **Start from:** `baseline/fruitinv.Program.cs.txt` (public surface and hooks) and `baseline/fruitscope.Program.cs.txt` (semantic reachability). Both worked in the audit.
- **Watch out:** `Tests/CrowdTests.csproj` compiles every `.cs` file below `Tests/` unless excluded, so `Gates/` must be removed from its glob, as `InteractionHarness/` already is.
- **Done when:**
  - `Tests/Gates/Run.ps1` runs every gate in the [table](#gates) and exits non-zero on any failure.
  - The API snapshot and hook list are committed: 39 always-installed hooks, as today.
  - Deliberately adding a public method makes it fail, and so does adding a `[HarmonyPatch]`.
  - The consumer gate builds against a scratch `ModRefs` and leaves the real one untouched.

<a id="r0-m4"></a>
### R0-M4: The designer's baseline
- **Goal:** a record of how things behave now, so later changes can be compared against it.
- **Owns:** `docs/refactor/Live-Checks.md` (new).
- **Who:** a Claude session drafts the checklist; the designer runs it.
- **Done when:** the checklist covers the following, and the designer has run it with results recorded.
  - The MODS menu, every settings page, and Esc stepping back
  - F8 and F11
  - An inventory item from each of BombsAway, GunsGunsGuns, FruitLab and Singularity: placing, holding, using
  - FruitLab's medical items on a patient as the player
  - A BombsAway blast, a GunsGunsGuns shot with a wall in the way
  - FruitCity loading from the map list, then RESET MAP
  - NeuroFrukt: a body walking a path, picking something up, using it, talking through the text box

---

## R1: Cut the weight

**Exit:** the shipped DLL holds no debug code that stays in FruitLib, no IMGUI apart from `FruitTextInput` (which moves in R2), and nothing on the removal list. The designer repeats the live checks.

Streams A to D don't share files, except hub edits, which the supervisor applies.

<a id="r1-m1"></a>
### R1-M1: Gate the diagnostics (stream A)
- **Goal:** the diagnostics that stay in FruitLib only exist in `-p:DevTools=true` builds.
- **Owns:** `Debug/FruitBallisticsProbe.cs`, `Debug/FruitInventoryAudit.cs`, `World/FruitEventLog.cs`, `Ballistics/FruitBlastInjury.cs` (the one line), `Inventory/FruitInventoryNative.cs` (the one line).
- **Who:** Claude session. A later review by `deepseek-flash`.
- **Why these only:** `FruitHandsProbe`, `FruitNavProbe` and `FruitSightProbe` move in R2, and the gait probe is removed in R1-M3. `FruitInteractionTrace` moves too.
- **Done when:**
  - A Release build contains no `FruitBallisticsProbe`, `FruitInventoryAudit`, `FruitEventLog` or their settings, shown by the API snapshot and by reflecting over the DLL.
  - A `DevTools` build still compiles, with all of them working.
  - No production code names a `Debug/` class outside `#if FRUITLIB_DEVTOOLS`.
  - `EventLog` and `EventPanel` no longer keep body tracking on in a Release build.
  - The README's claim about debug hooks is correct.

<a id="r1-m2"></a>
### R1-M2: Stop GunsGunsGuns depending on the trace (stream B)
- **Goal:** `FruitTrace` can be gated.
- **Owns:** `2_GunsGunsGunsRefractored`, `Utilities/FruitTrace.cs`, `Utilities/FruitDiagnostics.cs`.
- **Who:** Claude session.
- **Done when:**
  - GunsGunsGuns has no reference to `FruitTrace`. Its 12 `Mark` calls are removed, or use its own log if they still earn it.
  - `FruitTrace` exists only in `DevTools` builds.

<a id="r1-m3"></a>
### R1-M3: The removal list (stream C)
- **Goal:** remove what was agreed.
- **Owns:** `Content/FruitDecals.cs`, `Debug/FruitGaitProbe.cs`, the listed members of `World/FruitBodyControl*.cs` and `Hud/FruitPerfMon.cs`.
- **Who:** Claude session.
- **Removes:**
  - `FruitDecals`, and its uses in `Debug/FruitBundleTests.cs` (`DevTools` only).
  - `FruitGaitProbe` and its settings.
  - `YawDamping`, `StartRampSeconds`, `TuneGrounder`, `WalkPrediction`, `ProbeLocomotion`.
  - The older walking paths: `NavSmoothWalking`, `NavWidthAware`, `Order.Legacy`, and the half of `FruitNavProbe`'s course that compares against them.
  - `FruitPerfMon.RegisterCounter`, `Begin`, `End`, and `FruitVersion.Require`.
- **Done when:** the API snapshot shows only these gone, NeuroFrukt's walking is unchanged in the live check, and the crowd and interaction harnesses pass.
- **Note:** this breaks FruitTweaks until it's revived. It's on hiatus, so that's accepted.

<a id="r1-m4"></a>
### R1-M4: Remove the IMGUI fallbacks (stream D)
- **Goal:** one way to draw the menu and the HUD, and a clear report when it fails.
- **Owns:** `Menu/FruitMenu.cs`, `Menu/FruitMenuNative.cs`, `Menu/FruitMenuScreen.cs`, `Menu/FruitMenuPage.cs`, `Hud/FruitHud.cs`, `Hud/FruitHudNative.cs`, `docs/config-and-menu.md`, `docs/hud.md`.
- **Who:** Claude session, with `deepseek-pro` reviewing the failure paths.
- **The second look:** before removing anything, list how the native path can fail: the 120-frame build budget, `GiveUp`, a missing font, a clone that throws. The designer said the native system needs a second look, so that list goes in the report. Where it can fail, the fix is to report it, or to fix it if it's small.
- **Done when:**
  - `DrawPanel`, `DrawToggleButton`, `DrawImgui` and the `NativeHud` setting are gone.
  - When the native menu can't build, the log says what failed and where the ini is.
  - The live check passes for the MODS menu, every settings page, HUD panels and F8.

---

## R2: Give NeuroFrukt its code back

**Exit:** FruitLib has no body stack, 27 hooks, and every mod works. The scan shows no member in FruitLib used only by NeuroFrukt, apart from any the designer chose to keep. The designer repeats all the live checks.

These milestones go strictly in order, one writer at a time, because they all touch the same seam. This is the riskiest phase.

<a id="r2-m1"></a>
### R2-M1: The body-actions interface
- **Goal:** FruitLab and FruitLib's item code stop naming `FruitBodyControl`, while everything still lives in FruitLib.
- **Owns:** a new `World/FruitBodyActions.cs`, `World/FruitInteraction.cs`, `World/FruitItems.cs`, `World/FruitPropMenu.cs`, `World/FruitBodyControl.Interactions.cs`, `4_FruitLab/Items/`.
- **Who:** Claude session. The supervisor reviews the shape before FruitLab changes.
- **Shape:** a small public interface that a mod can implement, covering `Interact`, `InteractionOf`, `CanApply`, `ConsumeItem`, `Holding`, `Drop`, and `EndInteraction` and `Apply` for `FruitInteraction`. With no implementation, `Interact` returns false, `Holding` returns null and `Drop` does nothing. `FruitBodyControl` is the implementation for now.
- **Done when:**
  - FruitLab compiles with no reference to `FruitBodyControl`.
  - A harness test covers the no-implementation behaviour: it returns false and doesn't throw.
  - FruitLab's medical items work as the player, and with a NeuroFrukt body, in the live check.

<a id="r2-m2"></a>
### R2-M2: Cut the remaining connections
- **Goal:** nothing that stays names anything that moves.
- **Owns:** `World/FruitItems.cs`, `World/FruitPlaces.cs`, `World/FruitSight.cs`, `World/FruitBodies.cs`, `World/FruitBodyControl.Strikes.cs`, `World/FruitPose.cs`, `Menu/FruitMenu.cs`, `Utilities/`.
- **Who:** Claude session.
- **Does:**
  - Moves `SurfacePoint` to `FruitBodies`.
  - Moves object naming out of `FruitSight`.
  - Moves `FruitPoseShape` and `FruitBone` to their own file.
  - Moves `FruitNav.Flat` to Utilities.
  - Adds a way to register an input blocker, and uses it for `FruitTextInput`.
- **Done when:** a check shows no file that stays names a class that moves. Add this check to the gates for the rest of R2.

<a id="r2-m3"></a>
### R2-M3: Move the stack
- **Goal:** the body stack lives in NeuroFrukt and runs from there.
- **Owns:** `World/FruitBodyControl*.cs`, `World/FruitPose*.cs`, `World/FruitNav*.cs`, `World/FruitSight.cs`, `World/FruitInteractionTrace.cs`, the moved probes, `Tests/`, the hub files, and all of `12_NeuroFrukt`. It holds the hub for the whole milestone.
- **Who:** one Claude session, for the whole milestone. Mostly mechanical, but all in one change, since neither side compiles halfway through.
- **Decisions already made:**
  - The code goes to `12_NeuroFrukt/Body/` and keeps its class names, under a NeuroFrukt namespace.
  - FruitLib adds `InternalsVisibleTo("NeuroFrukt")` for what the stack still needs, like `FruitBody.Bind`, `FruitWounds.Burst` and `FruitBodies.ReportOnce`. That keeps those members out of the public API.
  - NeuroFrukt's `Core` drives the ticks and resets, and implements the body-actions interface.
  - The 25 settings move to NeuroFrukt's config.
  - The crowd and interaction harnesses move with their code. FruitLib keeps a small harness of its own, from R2-M1.
- **Watch out:**
  - Tick order changes, because NeuroFrukt's ticks now run from its own `OnUpdate`, after or before FruitLib's. `FruitBodies.Tick` must have run before the stack reads bodies. Check it, and record the result.
  - The stack's reset used to run from FruitLib's scene and map-reset handlers. It needs both, from NeuroFrukt.
  - Moved patches are applied by MelonLoader when NeuroFrukt loads, so they keep working.
- **Done when:**
  - The hook ledger shows 31 in FruitLib (39 less the 8 for pose and walking), and NeuroFrukt holds those 8.
  - All harnesses pass from their new home with the same counts: crowd 151, interaction 50 or more.
  - FruitLab, BombsAway, GunsGunsGuns, Singularity and FruitCity build with no changes.
  - The live check passes for NeuroFrukt walking, carrying, using and interacting, including a scene change and RESET MAP.

<a id="r2-m4"></a>
### R2-M4: Move the leftovers
- **Goal:** the remaining NeuroFrukt-only code goes.
- **Owns:** `World/FruitEyeCamera.cs`, `Hud/FruitTextInput.cs`, `Utilities/FruitPng.cs`, `Core/FruitLibMod.cs` (hold), `12_NeuroFrukt`.
- **Who:** Claude session.
- **Does:** moves those three, which removes FruitLib's last `OnGUI` and 4 more hooks. Then it re-runs the scan and lists what's still used only by NeuroFrukt: from the audit, about 176 lines in `FruitPlaces`, 53 in `FruitItems`, 27 in `FruitBodies` and 11 in `FruitEvents`. Each member moves, or the supervisor marks it to stay as useful to other mods.
- **Done when:** the hook ledger shows 27, `FruitLibMod` has no `OnGUI`, and the scan's list is empty or signed off.

<a id="r2-m5"></a>
### R2-M5: Move the docs
- **Goal:** the docs describe what's in each place.
- **Owns:** `docs/`, `12_NeuroFrukt/docs/`.
- **Who:** `deepseek-flash` drafts; the supervisor reviews.
- **Done when:** FruitLib's docs mention no moved API and NeuroFrukt holds the guides listed [above](#what-moves-what-goes). FruitLib's docs and setting descriptions no longer mention NeuroFrukt, including `SightProbe`'s and `GaitProbe`'s text.

---

## R3: Tidy what remains

**Exit:** the audit's knots are gone or justified, and every subsystem starts, ticks and resets the same way.

First, re-run the scan. The audit's three knots are smaller now, and each milestone below should be re-scoped from what's still there.

<a id="r3-m1"></a>
### R3-M1: One lifecycle
- **Goal:** `FruitLibMod` stops knowing about every subsystem.
- **Owns:** a new `Core/FruitLifecycle.cs`, `Core/FruitLibMod.cs` (hold), one registration line per subsystem.
- **Who:** Claude session, tests first.
- **Done when:**
  - Subsystems register their tick, scene and reset work in one place, in today's order, recorded in a test.
  - A subsystem that throws can't stop the others, in any phase. Today's scene resets aren't protected.
  - `FruitLibMod` calls only the lifecycle.
  - A harness test covers order and failure isolation.

<a id="r3-m2"></a>
### R3-M2: Fix the reset gaps
- **Goal:** state doesn't outlive what it refers to.
- **Owns:** `World/FruitItems.cs`, `World/FruitPropMenu.cs`, `World/FruitPlaces.cs`.
- **Who:** Claude session. **Behaviour changes, so the designer sees them first.**
- **Does:**
  - Confirms in game whether `FruitItems` and `FruitPropMenu` keep stale state across a scene change, since they reset only on RESET MAP. `FruitPropMenu`'s reset is what frees its GC handles.
  - Decides `FruitPlaces`: its lists persist until a mod calls `Clear`, and `ClearAll` has no caller.
- **Done when:** both resets run on a scene change if the check confirms the leak, and the live check passes for props and menus across a scene change.

<a id="r3-m3"></a>
### R3-M3: Safer startup
- **Goal:** a failure at startup is reported and contained.
- **Owns:** `Ballistics/FruitWounds.cs`, `Ballistics/FruitBlastInjury.cs`.
- **Who:** Claude session.
- **Does:** `Il2CppType.Of<LimbEffectorReceiver>` runs in a static initializer in both files, so a failure there would break the whole class with a `TypeInitializationException`. Makes it lazy and reports it once.
- **Done when:** there's no game call left in a static initializer. The gate scan can check this.

<a id="r3-m4"></a>
### R3-M4: Clean up the layers
- **Goal:** dependencies point one way.
- **Owns:** per stream, below. These can run in parallel.
- **Who:** Claude sessions; `deepseek-flash` drafts the allowed-edges list.
- **Streams:**
  - **Config and menu:** `FruitSetting` and the attributes move to Core, `FruitLog` stops reading `FruitLibConfig`. Owns `Menu/FruitSetting.cs`, `Utilities/FruitLog.cs`.
  - **Performance:** the frame-pressure signal moves to Utilities, and Ballistics registers its own counters, so `FruitPerfMon` stops naming Ballistics. Owns `Hud/FruitPerfMon.cs`, plus the reads in `Ballistics/`.
  - **Update check:** `FruitUpdateCheck` stops depending on the HUD. Owns `Utilities/FruitUpdateCheck.cs`.
  - **Inventory and items:** one-way only. `FruitInventory` reaches `FruitItems`, or the reverse, not both. Owns `Inventory/FruitInventory.cs`, `World/FruitItems.cs`, `World/FruitWorldEvents.cs`.
- **Done when:** the Layers gate is on, with its allowed list committed, and any knot that remains has a one-line reason in that list.

---

## R4: Public surface

**Exit:** the public API is what was decided on purpose, and the designer has approved it.

<a id="r4-m1"></a>
### R4-M1: Classify every public member
- **Goal:** a decision for each public member.
- **Owns:** `docs/refactor/Surface.md` (new).
- **Who:** `deepseek-flash` drafts one ticket per subsystem. **The designer decides.**
- **Classes:**
  - **Keep:** a mod uses it, or it's a sensible part of the feature.
  - **Option:** a tuning value that belongs in a settings object rather than a public static field.
  - **Internal:** nothing outside FruitLib needs it.
  - **Remove:** nothing needs it, inside or out.
- **Input:** `baseline/scope_members.json` has every member with the mods that use it. Re-run it first, since R2 will have changed it.
- **Decide here:**
  - Does `FruitLibConfig` stay public? Its remaining 40-odd settings are all writable fields, and no mod names one today.
  - How do third-party mods find out? A removed member fails at runtime with `MissingMethodException`.
- **Done when:** every public member has a class, and the designer has approved `Surface.md`.

<a id="r4-m2"></a>
### R4-M2 to M7: Apply it, per subsystem
One milestone each, in parallel. Each owns its folder, applies `Surface.md`, and may only change what's listed there. A mod that breaks gets its fix in the same milestone.

| ID | Subsystem | Owns | Public today, about |
|---|---|---|---|
| R4-M2 | Ballistics | `Ballistics/` | 284 |
| R4-M3 | Inventory | `Inventory/` | 103 |
| R4-M4 | Items and props | `World/FruitItems.cs`, `FruitPropMenu.cs`, `FruitPropUse.cs`, `FruitInteraction.cs` | 137 |
| R4-M5 | Bodies, events, places | `World/FruitBodies*.cs`, `FruitEvents.cs`, `FruitWorldEvents.cs`, `FruitPlaces.cs` | 153 |
| R4-M6 | Menu, HUD, config | `Menu/`, `Hud/`, `Core/FruitLibConfig.cs` (hold) | 155 |
| R4-M7 | Maps, content, utilities | `Maps/`, `Content/`, `Utilities/` | 122 |

- **Who:** Claude sessions, since these need a build. `deepseek-flash` reviews each diff.
- **Done when:** the API snapshot matches `Surface.md` for that subsystem, and every gate passes.

---

## R5: Docs and 6.0.0

**Exit:** FruitLib 6.0.0 is ready to release, and the designer has released it.

<a id="r5-m1"></a>
### R5-M1: Rewrite the guides
- **Goal:** the docs match the code.
- **Owns:** `docs/`, `Templates/`, `README.md`.
- **Who:** `deepseek-flash` drafts one guide per ticket; a Claude session checks that the examples compile.
- **Done when:**
  - Every member named in the docs exists, checked by a script against the API snapshot.
  - The version in `docs/README.md` comes from one place. Today it says 5.29.1.
  - The keys table is right, which it isn't today.
  - `Templates/` builds against the new DLL.

<a id="r5-m2"></a>
### R5-M2: Update the mods
- **Goal:** each mod requires the FruitLib it was tested with.
- **Owns:** one mod per stream, in parallel.
- **Who:** Claude sessions.
- **Done when:** each of BombsAway, GunsGunsGuns, FruitLab, Singularity, FruitCity and NeuroFrukt builds with no obsolete calls, and its `FruitGate` minimum is 6.0.0.

<a id="r5-m3"></a>
### R5-M3: Update the primer
- **Goal:** the next agent gets the right picture.
- **Owns:** `Frukt/MODDING_PRIMER.md`.
- **Who:** `deepseek-flash` can't read it, so a Claude session.
- **Done when:** it describes 6.0.0's surface and how to build.

<a id="r5-m4"></a>
### R5-M4: Release
- **Who:** the supervisor prepares it; **the designer tags it**, since a tag publishes.
- **Done when:**
  - Every gate passes.
  - The designer's final live check passes on the real deploy.
  - `release.yml` is updated: it installs .NET 8, while the harnesses target .NET 10.
  - The notes tell third-party authors what was removed and what replaces it.

---

## Waiting for the designer

To confirm before R0 starts:

1. **Merge the branches.** Your commit `aaf417d` is on `claude/add-claude-md`, not `master`. AIStack's change is on `claude/fruitlib-cloud-repo`. R0-M1 needs them merged.
2. **NeuroFrukt in git.** It has no repo today, and R2 moves 11,000 lines into it. The plan makes it a local repo with no remote.
3. **Is this plan public?** `docs/` goes to GitHub when pushed, and this file describes NeuroFrukt. The source already mentions it, and R2-M5 removes those mentions. `docs/refactor/baseline/` is already in `aaf417d`, though not pushed. If you'd rather keep all this private, take it out of git before pushing. The cloud workers still read it from the working tree.
4. **Hiatus mods.** The plan assumes FruitTweaks, BloodColor and NoHUD get no migration. FruitTweaks breaks at R1-M3 until it's revived. Link and Rewind don't use FruitLib.
5. **Bodies using FruitLab's items.** Confirm that a released FruitLab needs no body stack of its own, as described [above](#the-finding-that-shapes-the-plan).

## Parking lot

Not part of this plan. Add ideas here rather than to a milestone.

- **Map-loading framework.** The designer's plan, so people can add maps easily. R4-M7 only tidies `FruitMaps`.
- **Removing the JSON mesh loader.** GunsGunsGuns and FruitLab use it, and the designer wants to think about how to communicate it.
- **Generalizing interactions.** R2-M1's interface and `FruitInteractionSpec` are a starting point.
- **Hooks on demand.** The audit raised this because of hot-path hooks. R2 removes all three, leaving 27 on menus, inventory events, maps, shots and pain spikes. That removes most of the case for it. Measure first, if it comes up again.
- **Trimming NeuroFrukt.** Once it owns the stack, it can drop what it doesn't use.
- **Other mods on the board.** Each needs its own listing in `repos.toml`.

## Evidence

In [`baseline/`](baseline/), taken from 5.39.0:

| File | What |
|---|---|
| `public_api.tsv` | Every public member, with `file:line` |
| `patches.json` | Every hook |
| `internal_graph.json`, `scc.py` | Class dependencies and the knots |
| `consumers.json`, `member_use.json` | What each mod names, by syntax |
| `scope_members.json`, `scope_report.txt` | What each mod reaches, by semantic compile. Prefer these |
| `fruitinv.*.txt`, `fruitscope.*.txt` | Source of the two scanners |

The audit page: https://claude.ai/artifact/8aoL6YsJQmpLoh5nC6xx4Z
