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

This only fires for menus opened via `OpenBuildMenuKey` - a
`Plugin.HotkeyOpenInProgress` flag is set for the duration of that call and
checked in the patch, so interacting with a desk normally in the ordinary
game flow still shows just that desk's own recipe list, untouched.

## Why the hotkey also needs a second patch

`BuildManager.TryEnable(Wgo builder, ...)` is public and doesn't check
interaction distance on its own - `BuildInteractionHandler.Interact()` just
calls it directly on whatever desk you clicked. Its one hard failure mode is
`WgoExtensions.TryGetNearestBuilderWorldZone`: it only searches within 10
units of the desk for a `WorldZone` whose `WorldZoneDef.builderId` matches
that desk's id, and returns false if it doesn't find one. Without a second
patch there, the hotkey would open the menu on a distant desk, silently fail
that check, and the build window would never appear. The postfix on that
method falls back to whichever loaded `WorldZone` is physically nearest the
desk when the strict match fails, so the grid still centers somewhere
sensible.

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

Worth a BepInEx log check on first use (`Debug` config option logs every
`TryEnable` call and result) and some in-game poking before trusting it on a
real save.

## Config

`BepInEx/config/kupie.gk2.buildanywhere.cfg` after the first run:

- `General` / `AllowBuildAnywhere` (default `true`)
- `General` / `OpenBuildMenuKey` (default `Ctrl+B`)
- `General` / `ShowEveryBuildingOnHotkeyOpen` (default `true`)
- `General` / `Debug` (default `false`)
