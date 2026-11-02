# General interactions (FruitLib 5.39.0)

An interaction describes a live working point, the actor's arm/tool reach, allowed
working postures, required contact, and an explicit result. The same controller is
used by melee and FruitLab treatment; pickup queries its stance selection while
retaining the existing grip/attachment lifecycle. It has no medical or NeuroFrukt dependency.

`FruitBodyControl.Interact(actor, spec, ended)` returns **accepted**, never completed.
It progresses through Accepted, Positioning, Facing, Posing, Reaching and Applying.
The final state is Completed, Failed or Cancelled. An attempt has an immutable identity
and deadline; old callbacks cannot finish a replacement attempt.

For example, a future machine with a world-space button can use:

```csharp
FruitBodyControl.Interact(actor, new FruitInteractionSpec {
    Name = "press-machine-button",
    Source = "MyMachineMod",
    ResolvePoint = () => machine.Valid ? button.position : (Vector3?)null,
    Valid = () => machine.Valid && machine.CanActivate,
    Postures = FruitInteractionPostures.Stand | FruitInteractionPostures.Lean,
    Begin = operation => operation.Apply(
        () => machine.TryActivate(actor), "machine activated")
}, operation => RecordResult(operation.Id, operation.Stage, operation.Detail));
```

The machine example illustrates the API; no machinery mod is added by FruitLib.
All callbacks must run on the game's main thread. Keep `ResolvePoint` and `Valid`
cheap and free of side effects. Points are actual wound locations, grips, strike
surfaces or controls, not body centres or social stopping rings.

For held tools, set `FruitWorldItem.Interaction` (or `FruitProp.Interaction` on a
placeable item) to a builder returning a spec. The item and named target are bound
by FruitLib. A handling recipe waits on the interaction before advancing, including
`FruitHandling.InHand`. The existing `HandsEnded` event reports Used only after
completion. `OnUse` stays the synchronous player/legacy callback. Existing mods
with their own asynchronous OnUse jobs must opt into Interaction to gain this lifecycle.

An immediate world change belongs in `operation.Apply(Func<bool>, detail)`.
It verifies current contact/prerequisites before invoking the function exactly once,
then records its result. A successful operation may consume its item or invalidate
its own machine prerequisites. A delayed implementation retains the operation token
and calls Apply when ready. It can also call Fail with a concrete reason. Complete
is available for an already verified result from a native asynchronous operation;
it rechecks current prerequisites/contact. Starting an operation is not completion.

For an item consumed inside Apply, use `ConsumeInteractionItem(operation)` before
deleting its world object. This detaches it without an unrelated Dropped/Lost action
receipt. A failed or cancelled attempt releases only its own hand placement/pose
and calls its Cancel callback once. Stop, a new movement order, replacement item
use, a missing actor/target/tool, lost contact, loss of footing or the deadline ends
the attempt. Cancellation callbacks should remove external jobs belonging to that
attempt, rather than remove a newer job by actor ID alone.

`Reach` is the shoulder-to-hand reach; `ToolReach` is the working tip extension
beyond the hand. A tool can also provide `FruitWorldItem.WorkingPoint`, a live
tip resolver: contact then requires its actual tip to touch the working point.
This prevents declared reach from manufacturing contact for a misaligned blade/probe.
It is explicit metadata, not inferred damage or a new stabbing
mechanic. For a strike, positioning establishes reach, then the native swing supplies
the impact result. A missed strike does not complete the attack. Existing punch,
shove and carried-object swing semantics remain the damage providers.

Stances are scored around the working point with same-floor navmesh routes, body
spacing, arm clearance, reach and posture cost. Near positioning uses local steps;
longer positioning uses a reserved stance. Turning drift and moved targets trigger
up to four attempts within the original deadline. The working hand is not driven
back to the normal carrying pose while an interaction owns it. Actual contact and
pose settling are verified after positioning, and while delayed application is active.

Stand, Lean, Kneel and Crouch are allowed by default. Sit is opt-in: its caller must
provide a suitable clear support through its scene and Valid contract. These are
stationary working poses. Self interactions do not walk after their own moving target.
Predicted posture lowering is a candidate-selection estimate; real native reach
must still pass after posing. Injured/down actors may fail the native posture gate.

**Crouch-walking and crawling are not implemented in this change.** The existing
pose system takes over leg rotations and releases them when walking starts, while
the native locomotion controller exposes standing gait clips. Low locomotion needs
a separate gait with support contacts, clearance, occupied-hand constraints and
transitions; attaching the stationary crouch to standing walking would not provide it.

## Diagnostics and validation

`InteractionDiagnostics` is enabled by default in FruitLib's Diagnostics settings.
`[FruitInteraction]` records go to MelonLoader's log. Transitions and movement
outcomes always produce a record while enabled; active interactions/hand stages
sample at most once a second. Records include attempt ID, stage, actor, flat/3D
distance, vertical separation, hand error, requested reach, pose/weight, balance,
movement state, working point, stance, retries and a result/reason. The hand error
is to the working point; with a tool it includes its declared extension. Actor
distance is diagnostic, not the contact predicate. Disable the setting to stop logging.

The production-linked offline harness is
`dotnet run --project Tests/InteractionHarness/InteractionTests.csproj -c Release`.
It verifies orchestration and geometry decisions against native boundary stubs;
it does not establish ragdoll stability, visible contact or Unity clearance.

Before deploying, compile FruitLib, FruitLab and NeuroFrukt with `-p:SkipDeploy=true`.
FruitLab/NeuroFrukt compile against FruitLib.dll in ModRefs. Refresh that DLL from
the new FruitLib build before compiling consumers. Both require FruitLib 5.39.0,
so release/deploy the matching library and consumers together. A normal FruitLib
build refreshes ModRefs and deploys; SkipDeploy intentionally skips both copies.

Live acceptance must include:

1. Pick up floor and shelf items, including an explicit grip, beside furniture.
   Observe working posture and hand contact; check turning/positioning drift.
2. Punch/shove a standing and a fallen body; swing a carried item. Moving out of
   range must miss, with no premature Arrived/Used receipt. Check bare-handed
   strikes still work and no active carrying pose overwrites the swing.
3. Administer tablets and injections to standing and fallen patients. Bandage a
   specific selected wound. Used/PLAN done must occur after the dose/dressing;
   loss of contact before application must fail without changing the patient.
4. Repeat a request during work, change the target, issue Stop, drop the tool,
   knock the actor down and reset the scene. An old callback must not end a new
   attempt, and hands/poses must return to their previous ownership afterward.
5. Run several simultaneous interactions in a crowd. Observe waiting, bounded
   stance retries, remaining doorway clearance and frame time. New interaction
   stance searches are scheduled one per frame; expensive path queries are
   skipped when their minimum possible score cannot beat an existing candidate.
6. A test machinery caller should use the inventory-free contract above. Verify
   its native state changes once, after real hand contact, including delayed
   and one-use controls. No machinery scene is shipped in this pass.

The acceptance criterion is success without player nudges, or a truthful bounded
failure explaining which positioning/contact requirement prevented the action.
