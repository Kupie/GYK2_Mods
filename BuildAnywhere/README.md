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

## Why the hotkey re-finds the desk on every press

An earlier version of this mod mirrored GK1's own `IBuildWhereIWant`, which
clones one hardcoded, always-available desk once and hands the *clone* to
its build-mode entry point rather than the real desk - a disconnected anchor
object, never interacted with normally. This mod copied that shape: find a
real Builder-type desk once, cache it forever, and spawn a fresh clone of it
at the same position on every hotkey press.

That caused a real bug. `BuildManager.TryEnable` (opening the crafting/build
window) doesn't move the camera at all, but the moment the player actually
selects something to place, `BuildController.EnableBuildMode` reads
`worldZone.GetBuildPos()` - the real, fixed, scene-authored position of
whichever `WorldZone` the desk resolves to - and moves the camera and the
entire build grid overlay there. Because the cached desk never changed, every
hotkey session after the first resolved to that same original zone forever,
so the camera always jumped back to wherever the first desk this mod ever
found happened to be (a fixed spot in the house, for example), no matter
where the player had gone since.

The fix: find the nearest Builder-type desk fresh on every `OpenBuildMenuKey`
press via `FindNearestBuilderDesk()` (no caching across presses) and call
`TryEnable` directly on that real desk - no cloning. This makes the resolved
`WorldZone`, and therefore where the camera ends up once an item is placed,
naturally track wherever the player currently is, instead of freezing at
whatever desk was found first.

Suppressing the camera movement itself, instead of fixing which desk gets
opened, was considered and rejected. The build grid is physically
instantiated at the zone's real position and driven every frame by raycasts
against the live camera (`BuildController.UpdatePointerAtPos`); hiding the
visual jump without also relocating the whole grid would leave the grid
off-screen and make remote placement unusable. Doing it properly would mean
patching three independent Cinemachine mechanisms, including a
compiler-generated coroutine only reachable via a fragile IL transpile - not
worth it for this fix. So the camera does still move whenever an item is
actually placed - that's confirmed unavoidable - it just now moves to
wherever the *current* nearest desk's zone actually is.

One consequence of going back to finding desks from the player's (possibly
distant) current position on every press, rather than reusing one anchor
whose zone-match was already proven once: `BuildManager.TryEnable(Wgo
builder, ...)`'s one hard failure mode,
`WgoExtensions.TryGetNearestBuilderWorldZone` (a 10-unit
`Physics.OverlapBoxNonAlloc` around the desk's position that only succeeds if
it finds a `WorldZone` collider there whose `WorldZoneDef.builderId` matches
that desk's `Wgo.Id`), is worth defending again. A correctly-tagged desk
should normally already resolve a zone on its own regardless of player
distance (the check is centered on the desk, not the player), but a distant
desk is more likely to hit physics-streaming corner cases vanilla code never
exercises (vanilla only ever calls this while standing next to the desk). So
the fallback patch on that method - removed by the clone-based version, since
a clone spawned at a position with an already-proven zone match didn't need
it - is back: it falls back to whichever loaded `WorldZone` is physically
nearest the desk when the strict match fails, so the grid still centers
somewhere sensible instead of the build window silently never appearing.
It's zero-cost when the strict match already succeeds.

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

This only fires for the desk `Plugin.LastHotkeyDesk` was last set to by an
`OpenBuildMenuKey` press, checked by reference equality
(`buildDesk == Plugin.LastHotkeyDesk`) rather than a flag. Because the check
is on object identity rather than a flag scoped to one call, it stays correct
through `BuildManager.Disable()`'s reopen-the-browse-window path and Move
Stations' `ReopenBuildMenu()` (`Kupie/GYK2_DECOMP/Gk2MoveStations/
GK2MoveStations/MoveStationsPlugin.cs`), which both just re-call `TryEnable`
on whatever `Wgo` they captured - no special-casing needed for either, since
that `Wgo` is still `LastHotkeyDesk`. This is unaffected by dropping the
clone: `BuildManager.Disable()` calls `FormBuildData(currentBuildDesk)`
directly rather than through `TryEnable` again, so the reference-equality
trick works identically whether the referenced object is a clone or a real
desk.

Since `LastHotkeyDesk` now points at a real desk rather than a synthetic
per-press clone, leaving it set forever would be a bug: hotkey desk A, close
the menu, then later walk up and interact with desk A *normally* (before
hotkeying any other desk) - that normal interaction would still match
`LastHotkeyDesk` and incorrectly show the aggregated list instead of just
desk A's own list.

`UIBuildingWindow_Close_Patch` fixes this: a postfix on `UIBuildingWindow`'s
(inherited, not overridden) `LazyWindow<UIBuildingWindowData>.Close()` that
sets `LastHotkeyDesk = null`. `Close()` only fires when the player actually
backs out of the browse list (`OnPressedBack`) or clicks its close button -
not during mid-session placement (place one item, cancel back to the list,
place another), which goes through `BuildManager.Disable()` ->
`OpenBuildingWindow()` -> `Open()` -> `ShowWindow()` and just redraws the
already-shown window without ever calling `Close()`/`HideWindow()`, since
entering placement mode never clears the window's `isShown` flag. Confirmed
via the decomp that `FightingGameController` treats
`BuildController.IsBuildModeActive` and `UIBuildingWindow.IsShown` as two
independent states that can both be true at once, which is why placement
mode alone never trips this patch. One gap: the exact compiler-generated
local function that runs when the player selects an item to place couldn't
be read directly (this decompile strips compiler-generated display-class
bodies repo-wide), so that conclusion rests on the independent
`FightingGameController` evidence rather than reading that callback's body -
see `Plugin.cs`'s doc comment on `UIBuildingWindow_Close_Patch` for the same
caveat.

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

## Confirmed limitation: the camera confiner bounds every placement, not just AllowBuildAnywhere's own check

`AllowBuildAnywhere`'s postfix only overrides the per-cell validity check in
`WgoBuildPointer.UpdateSelectionCellsState`. It does not touch
`BuildModeCameraController.Enable(followTarget, boundingVolume)`, which calls
`TrySet3DConfinerBounds(boundingVolume)` with the session's `WorldZone`'s own
collider every time an item is placed. Confirmed via the decomp: this
bounding volume is load-bearing for more than the camera - the same collider
also anchors `BuildController.UpdatePointerAtPos`'s ground-plane and
elevation math for the placement grid itself
(`WorldZone.GroundPlaneY`/`TryGetBuildElevationY`). Relaxing or skipping the
confiner call without also relocating what it drives isn't a small change,
so it isn't attempted here.

Net effect: "build anywhere" means anywhere within the opened zone's own
camera-reachable space, not literally anywhere in the loaded world -
`AllowBuildAnywhere` removes the zone-*boundary* and collision checks on
where you can place things, but the camera (and therefore what you can
actually reach to place) is still confined to whatever volume the session's
zone provides.

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
- Whether the compiler-generated local function that runs when the player
  selects an item to place (inside build mode's placement flow) ever calls
  `UIBuildingWindow.Close()` - not directly checked, since this decompile
  strips compiler-generated display-class bodies everywhere. The conclusion
  that it doesn't rests on independent evidence instead - see
  `UIBuildingWindow_Close_Patch`'s section above.

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
  (public) instead of discarding the whole result. Orthogonal to which
  desk/menu got opened - it's about placement validity inside an active
  build session.
- **Move Stations compatibility**
  (`Kupie/GYK2_DECOMP/Gk2MoveStations/GK2MoveStations/MoveStationsPlugin.cs`)
  should now just work given the reference-equality design above, since its
  `ReopenBuildMenu` calls `TryEnable` on the same captured `Wgo`. Still
  worth confirming its own move-mode (`OnMoveMenuClicked`'s "native grid"
  snapshot logic) doesn't bypass `WgoBuildPointer` entirely - if it does,
  the collision-bypass toggle above won't reach it, and that would be a
  separate, smaller follow-up.
- **Building in areas with no vanilla `WorldZone` coverage at all.**
  Researched but deliberately not implemented this round - see
  `TASKS.md` for the detailed design write-up. Two approaches were found
  feasible: spawning a dedicated "catch-all" `WorldZone`/`WorldZoneDef` pair
  at runtime (recommended - surgical, only affects what explicitly queries
  the new zone), or expanding/resizing existing vanilla zone colliders
  (simpler code, but verified to reach into achievement unlocks, quality
  scoring, navmesh baking, worker task assignment, and delivery/storage
  routing - all keyed off zone membership).

## Config

`BepInEx/config/kupie.gk2.buildanywhere.cfg` after the first run:

- `General` / `AllowBuildAnywhere` (default `true`)
- `General` / `OpenBuildMenuKey` (default `Ctrl+B`)
- `General` / `ShowEveryBuildingOnHotkeyOpen` (default `true`)
- `General` / `Debug` (default `false`)
