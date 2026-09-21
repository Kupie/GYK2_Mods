# BuildAnywhere (GK2)

A port of p1xel8ted's GK1 mod "I Build Where I Want!" (`IBuildWhereIWant`) onto
GK2. Not a line-for-line port - GK2's building system (`BuildManager` /
`BuildController` / `BuildPointer` / `WgoBuildPointer`) doesn't share any code
with GK1's (`BuildModeLogics` / `WorldGameObject` / `FlowGridCell`), so this
re-derives the same two features against GK2's actual API:

1. Place objects anywhere in view, ignoring the current zone's boundary and
   other objects in the way.
2. Open the build menu from any loaded Builder desk, not just the specific
   one whose zone you're currently standing in.

Built against `Kupie/GYK2_DECOMP` (the demo decompile), not the real game -
see "What's unverified" below before relying on this in a live save.

## Why this patches `UpdateSelectionCellsState`, not the ground grid

The first place that looks like the right patch target is
`BuildGridData.FormGridData` / `BuildCellData.UpdateData` - that's what draws
the tinted grid overlay across the ground while you're in build mode, and it
does have its own "is this cell inside the zone" check
(`worldZoneRect.Contains(...)`). But it's purely cosmetic. The overlay can
show a cell as blocked while the game still lets you build there, or vice
versa, because the actual yes/no decision is made independently by
`WgoBuildPointer.UpdateSelectionCellsState()` - a second, unrelated physics
query that runs per selection cell (the small markers directly under the
object you're placing) and sets `shownAsActive`. `TryDoBuildAction()` (the
method that actually spawns the object) only reads `shownAsActive` - it never
looks at the ground grid at all. So patching the grid would change what you
see without changing what you can do; patching `UpdateSelectionCellsState` is
the one change that actually matters.

That method folds three separate questions into the one `shownAsActive` bool:

1. Can you afford it right now (`canTakeResources` - inventory check, plus
   `BuildingDef.limitMax` if the building has a placement cap)
2. Is every cell still inside a `WorldZone` whose id matches the zone you
   opened build mode in
3. Is every cell clear of blocking areas, other Wgos, and the hard-block
   layer (layer 29) some terrain uses

`AllowBuildAnywhere` re-derives `shownAsActive` from just #1 in a postfix,
discarding whatever #2/#3 decided. Resource cost is still enforced - this
doesn't give items for free, it only removes the positional restrictions.
The postfix also has to redo the `cells[*].IsAvailableForBuild` and
`moduleSlotArea.ApplyVisibility(...)` propagation the original method's tail
does, since the original already wrote the pre-override value to both before
the postfix runs.

`canTakeResources`, `shownAsActive`, `cells`, and `moduleSlotAreas` are all
private/protected fields in the game's own source (only `BuildSelectionCell`'s
`IsAvailableForBuild` and `ModuleSlotArea.ApplyVisibility` are public). This
reads/writes them as plain fields on the assumption the game assembly is
publicized at build time (`Krafs.Publicizer`, `PublicizeAll` - see the
`.csproj`), same as `AvailableInDemoPatcher` already does.

## Why the hotkey clones the desk instead of opening it directly

`BuildManager.TryEnable(Wgo builder, ...)`'s one hard failure mode is
`WgoExtensions.TryGetNearestBuilderWorldZone`: it runs a 10-unit
`Physics.OverlapBoxNonAlloc` around the desk's position and only succeeds if
it finds a `WorldZone` collider there whose `WorldZoneDef.builderId` matches
that desk's `Wgo.Id`. A desk found far from the player still passes this,
since the check is centered on the desk, not the player - so patching that
method isn't actually necessary. What doesn't work is opening the real desk
found by `FindNearestBuilderDesk()` from a distance for anything other than
this one check: GK1's own `IBuildWhereIWant` doesn't open the real hardcoded
wood desk either, it clones it once and hands the *clone* to its build-mode
entry point, and that clone is never interacted with normally, it's a
disconnected anchor object.

This mod does the same thing. `Plugin.AnchorDesk` is a real Builder-type
desk, found once via `FindNearestBuilderDesk()` and cached (re-searched only
if it's later found destroyed or unloaded). Every `OpenBuildMenuKey` press
destroys the previous clone if one exists, then spawns a fresh one via
`Wgo.Spawn` at `AnchorDesk`'s exact position, scene and id, and calls
`TryEnable` on that clone (`Plugin.CurrentClone`) instead of on `AnchorDesk`
itself. Same id and same position means
`TryGetNearestBuilderWorldZone`'s physics check succeeds on its own - the
clone is sitting in the same real zone the anchor always does - so no patch
on that method is needed at all, and the previous version's fallback patch
on it has been removed.

## Why the hotkey menu shows every building, not just the desk's own list

GK1's craft-anywhere menu didn't just skip the "must be at a desk" restriction
- it also aggregated every craft the player had unlocked across every desk in
the game, not just what the one desk you cloned would normally offer. GK2's
equivalent data is per-desk (`GameBalance.Me.buildDefsInBuilder[deskId]`), and
`BuildingDef.GetBuildingsInBuilder(desk)` is what normally turns one desk's
slice of that into the list `BuildManager.FormBuildData` assigns to its
private `buildDataList` field.

`ShowEveryBuildingOnHotkeyOpen` patches `FormBuildData` to replace that field
with every building from every desk's list combined, deduplicated, and run
through the exact same per-building unlock checks
`GetBuildingsInBuilder` itself uses (`isNeedsUnlock` /
`unlockedBuildings` / `lockedBuildings`) - so it still respects what's
actually been unlocked rather than dumping the entire tech tree regardless of
progress, matching GK1's own `IsCraftVisible`-filtered behavior rather than
a flat cheat-everything list. `FormBuildData` and `buildDataList` are both
private on `BuildManager`, accessed directly here on the same publicizer
assumption as above.

This only fires for the clone `Plugin.CurrentClone` spawns on each
`OpenBuildMenuKey` press, checked by reference equality
(`buildDesk == Plugin.CurrentClone`) rather than a flag - a real desk a
player walks up to normally is never reference-equal to that clone, so
interacting with a desk normally in the ordinary game flow still shows just
that desk's own recipe list, untouched. Because the check is on object
identity rather than a flag scoped to one call, it also stays correct
through `BuildManager.Disable()`'s reopen-the-browse-window path and Move
Stations' `ReopenBuildMenu()` (`Kupie/GYK2_DECOMP/Gk2MoveStations/
GK2MoveStations/MoveStationsPlugin.cs`), which both just re-call `TryEnable`
on whatever `Wgo` they captured - no special-casing needed for either, since
that `Wgo` is still `CurrentClone`.

## What this does NOT cover

- **Remove mode** is untouched on purpose - it goes through `RemovePointer`,
  not `WgoBuildPointer`, so this patch never sees it.
- **Military base / conveyor / upgrade placement** (`FightingBuildPointer`,
  `ConveyorBuildPointer`, `MilitaryBaseBuildPointer`, `UpgradeBuildPointer`)
  all subclass `WgoBuildPointer` and inherit `UpdateSelectionCellsState`
  unless they override it - not individually checked against the real game,
  so they may or may not pick up the same behavior.
- **The ground grid overlay itself** still shows the normal red/green tiles -
  only the actual placement result is overridden. Cosmetic only; didn't seem
  worth the extra patch surface for a visual-only mismatch.
- **`FightBuilder` desks** (military base building) are skipped by the
  hotkey's desk search on purpose - they need an extra
  `Func<List<Inventory>>` this mod doesn't try to supply.
- **Camera movement on hotkey open** - the camera still moves to wherever
  `AnchorDesk` physically is every time the hotkey is used, same as GK1
  always warping to the wood desk's fixed location. That's inherent to how
  GK2 frames the build camera for any desk opened from a distance, not a
  symptom of anything this mod could patch away without a much bigger
  change (skipping `BuildController`'s camera-follow/confine code
  entirely) - not attempted here.

## Open question worth resolving before relying on AllowBuildAnywhere

Placement itself works at the per-cell level in
`WgoBuildPointer.UpdateSelectionCellsState`, independent of which zone the
session nominally opened in. But `BuildModeCameraController.Enable(
followTarget, boundingVolume)` calls `TrySet3DConfinerBounds(boundingVolume)`
with the anchor zone's own collider. Unconfirmed whether this hard-confines
camera *panning* to that volume, not just where the camera starts - if it
does, being able to place objects anywhere is of limited use if the camera
can't physically reach that spot. Worth checking in-game, and if confinement
is real, whether passing a larger bounding volume (or skipping the confiner
call when `AllowBuildAnywhere` is on) fixes it without breaking whatever
else `EnableBuildMode` relies on that same collider for (elevation/
ground-plane math uses the same zone separately, in `UpdatePointerAtPos`).

## What's unverified

This was written against the decompiled demo source, not a compiled test
against the real game - I don't have a GK2 install to build or run against.
Specifically unconfirmed:

- Whether `WgoBuildPointer` and `BuildPointerObject`'s private field names
  and layout match the shipped assembly exactly (should, since GK2 runs the
  Mono BepInEx backend rather than IL2CPP, so no renaming/obfuscation step
  sits between the decompile and the real DLL - but the demo build could
  still differ from retail in ways that aren't visible here).
- Whether `FightingBuildPointer` / `ConveyorBuildPointer` /
  `MilitaryBaseBuildPointer` / `UpgradeBuildPointer` override
  `UpdateSelectionCellsState` themselves (in which case this patch wouldn't
  reach them) - not checked.
- General in-game feel: whether ignoring the zone-match check produces any
  visual glitching (wrong-zone lighting/fog/floor tiles under the placed
  object) that GK1's mod didn't have to worry about, since GK1's zones and
  GK2's zones aren't the same kind of thing.
- The direct field/method access assumes `PublicizeAll` reaches every
  assembly this project references via `Directory.Build.props`'s wildcard
  `Reference Include="$(Gyk2ManagedPath)\*.dll"` (Assembly-CSharp included) -
  matches how `AvailableInDemoPatcher` already uses it, but this project
  wasn't actually built and run to confirm the publicized DLL resolves the
  same way a second time.
- `SpawnAnchorClone` passes `anchor.transform.parent` as the clone's
  `parentTransform` to `Wgo.Spawn`, on the assumption that a real desk's
  own parent transform is a reasonable stand-in for "the anchor's scene" -
  matching the shape of the one call site this was checked against
  (`BuildPointer.PrepareAndSpawnWgoBuildPointer`, which instead uses
  `MainGame.PlayerController.CurrentGameScene.transform`, unusable here
  since the anchor desk may not be in the player's current scene). Not
  confirmed whether a real desk's `transform.parent` is always the scene
  root rather than some intermediate chunk container.
- The clone is deliberately left inactive (no `UpdateChunkVisibility(true)`
  call after `Wgo.Spawn`, unlike `PrepareAndSpawnWgoBuildPointer`) so it
  never visually doubles up the real desk it's cloned from. Not confirmed
  in-game that `TryEnable`/`FormBuildData`/the build window don't depend on
  the desk `Wgo`'s `GameObject` being active for anything.

Worth a BepInEx log check on first use (`Debug` config option logs every
`TryEnable` call and result) and some in-game poking before trusting it on a
real save.

## Known follow-ups, not done here

- **Splitting `AllowBuildAnywhere` into independent zone-bypass and
  collision-bypass toggles.** Right now it's all-or-nothing: `#2` (zone
  match) and `#3` (collision/blocking) in
  `WgoBuildPointer.UpdateSelectionCellsState`'s three checks above are
  overridden together. Doing this properly needs partial reimplementation
  of that method's loop using `BuildSelectionCell.OverlapBoxNonAlloc`
  (public) instead of discarding the whole result. Orthogonal to the
  anchor-clone design above - it's about placement validity inside an
  active build session, not which desk/menu got opened.
- **Move Stations compatibility**
  (`Kupie/GYK2_DECOMP/Gk2MoveStations/GK2MoveStations/MoveStationsPlugin.cs`)
  should now just work given the reference-equality design above, since its
  `ReopenBuildMenu` calls `TryEnable` on the same captured `Wgo`. Still
  worth confirming its own move-mode (`OnMoveMenuClicked`'s "native grid"
  snapshot logic) doesn't bypass `WgoBuildPointer` entirely - if it does,
  the collision-bypass toggle above won't reach it, and that would be a
  separate, smaller follow-up.

## Config

`BepInEx/config/kupie.gk2.buildanywhere.cfg` after the first run:

- `General` / `AllowBuildAnywhere` (default `true`)
- `General` / `OpenBuildMenuKey` (default `Ctrl+B`)
- `General` / `ShowEveryBuildingOnHotkeyOpen` (default `true`)
- `General` / `Debug` (default `false`)
