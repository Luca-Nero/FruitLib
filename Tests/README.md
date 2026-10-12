# Crowd movement regression checks

Run `dotnet run --project Tests/CrowdTests.csproj` from FruitLib's directory (.NET 10).
The harness links the production spot allocator and local passing controller against a small
deterministic world. It checks ten arrivals from one direction, spacing, lateral spread,
door clearance, blocked navmesh routes, passing-side persistence, alternate sides, floors,
moving traffic, and stalled walkers. It does not simulate Unity ragdoll physics.
The prediction checks also cover head-on right-side passing, crossing and receding traffic,
waiting when furniture prevents a detour, narrow passages, escape from existing contact,
coincident centres, and preserving approach targets.

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
