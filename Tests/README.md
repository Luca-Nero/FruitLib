# Crowd movement regression checks

General interaction checks: `dotnet run --project Tests/InteractionHarness/InteractionTests.csproj -c Release`.
This links the production interaction runner and policy against native boundaries;
it covers inventory-free machine work, explicit contact/settling, delayed completion,
stale callbacks, cancellation, same-ID body replacement, blocked routes/floors,
posture ownership, tool loss/consumption, one-use machines and self interactions.
See [the interaction contract and live acceptance](../docs/interactions.md).

Run `dotnet run --project Tests/CrowdTests.csproj` from FruitLib's directory (.NET 10).
The harness links the production spot allocator and local passing controller against a small
deterministic world. It checks ten arrivals from one direction, spacing, lateral spread,
door clearance, blocked navmesh routes, passing-side persistence, alternate sides, floors,
moving traffic, and stalled walkers. It does not simulate Unity ragdoll physics.
The prediction checks also cover head-on right-side passing, crossing and receding traffic,
waiting when furniture prevents a detour, narrow passages, escape from existing contact,
coincident centres, and preserving approach targets.
Reciprocal movement checks cover deferred room requests, idle blockers, fixed priority for
stalled walkers, waiting while a partner moves, holding until the owner passes, restoring
the suspended destination and timeout, protected actions, blocked and other-floor pockets,
commands arriving before dispatch, and passing-direction commitment.

Build without deployment: `dotnet build FruitLib.csproj -c Release -p:SkipDeploy=true`.

In-game acceptance:

1. Send six to ten NeuroFrukt bodies to the same room or outdoor node from the same side.
   They should settle across the free space and later arrivals should pass the settled ones.
2. Repeat through one doorway. The doorway should stay clear of chosen standing spots, and
   bodies should only pass where there is room on the navmesh.
3. Put one stationary body in the route, with a wall on one side. A walker should pass on the
   open side, keep that side until beyond the blocker, then resume its path.
4. Repeat beside furniture, with a fallen body, and while another body crosses the route.
   Check for collisions, repeated pivots, and premature arrival reports.
5. Check a single walker, follow/approach, and item pickup to ensure ordinary movement and
   hand stances still work. Exact animation and physical clearance require this live check.
6. Send two walkers toward each other in open space, then cross two routes at right angles.
   They should turn before limb contact, or stop when the crossing route is occupied. Repeat
   with a wall blocking a passing side and through a narrow passage. Check that waiting
   bodies resume, and that an immovable blockage eventually triggers recovery or times out.
7. Start a walker facing directly at a nearby body while its intended route turns away.
   It should turn without driving forward into that body as its yaw catches up.
8. Leave an idle NPC in a doorway with a free pocket beside it. Send another NPC through.
   The blocker should step into the pocket, hold there until the walker passes, then remain
   there. Repeat with two stalled walkers: one yields and then resumes its original goal.
   Remove the pocket: the walker should wait without repeated turns, then report blocked or
   time out. No courtesy motion should interrupt reaching, handling, combat or held poses.
9. While a blocker is stepping aside, issue Stop or a new destination to it. The new command
   must take priority and the suspended old destination must not later resume. Repeat with
   the requesting walker dying or stopping, and change scene during the negotiation.


Local step checks (5.38.0) link the production LocalSteps controller: fixed-facing
lateral/backward commands, settling before arrival, stalled-step deadlines that
survive replanning, floor holes/drops, overhead scenery, blocked segments, bounded
distance, separating close contact, new blockers, and excessive yaw drift.
Courtesy checks also cover a narrow throat opening into a passing pocket, the two
committed legs, waiting through the junction, and refusing corridors with no pocket.

Live acceptance: repeat with an idle blocker and two opposing walkers in a doorway.
The yielding body should back along the corridor before moving sideways into a
full-width pocket. It should hold there until the owner passes and resume its
suspended destination afterward. Repeat with furniture entering either leg, Stop,
an injury, and a scene change. Short StepTo calls should settle before MoveEnded.
