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

Built against `Kupie/gyk2_decomp` - originally the pre-release/demo
decompile, refreshed mid-development to a full-release decompile once the
game shipped (a 2512-file diff between the two, force-pushed over the old
history). The specific APIs this mod depends on
(`GameBalanceBase.AddData`/`InitCache`, `GameBalance.CreateBuildCache`,
`Wgo.Spawn`, `WgoPartBakedDataCollection`) were re-verified against the
fresh decompile and are logic-identical to what was originally researched
against the pre-release one - no design changes were needed on account of
the refresh itself, just the timing bug described below. Still not the real
game - see "What's unverified" below before relying on this in a live save.

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

`FindNearestBuilderDesk()`'s job now is narrower than it used to be: it only
finds *where* to move this mod's own dedicated desk (see the next section) -
it no longer determines *which* desk's identity gets opened. `TryEnable`'s
one hard failure mode, `WgoExtensions.TryGetNearestBuilderWorldZone` (a
10-unit `Physics.OverlapBoxNonAlloc` around the desk's position that only
succeeds if it finds a `WorldZone` collider there whose
`WorldZoneDef.builderId` matches that desk's `Wgo.Id`), is why the fallback
patch on that method still exists - but its role changed from "rare-case
defensive fallback for a distant real desk hitting a physics-streaming
corner case" to "the *only* way this ever succeeds at all" once the
dedicated desk (next section) is involved, since its id is deliberately new
and will never naturally match any real `WorldZoneDef.builderId`. It falls
back to whichever loaded `WorldZone` is physically nearest the desk when the
strict match fails, so the grid still centers somewhere sensible instead of
the build window silently never appearing.

One consequence worth knowing: this fallback patch is itself gated on
`AllowBuildAnywhere.Value`. If that toggle is off while
`ShowEveryBuildingOnHotkeyOpen` is on, the aggregated menu won't open at all
- there's no other way its zone match can succeed. Not treated as a bug to
fix here (the two toggles are conceptually intertwined enough that this
mod's own remote-opening feature was always implicitly leaning on
`AllowBuildAnywhere` in some form), but worth knowing if `AllowBuildAnywhere`
is ever turned off on its own.

## Why the hotkey opens its own dedicated desk, not a real one

GK1's craft-anywhere menu didn't just skip the "must be at a desk"
restriction - it also aggregated every craft the player had unlocked across
every desk in the game, not just what one specific desk would normally
offer. GK2's equivalent data is per-desk
(`GameBalance.Me.buildDefsInBuilder[deskId]`), and
`BuildingDef.GetBuildingsInBuilder(desk)` is what normally turns one desk's
slice of that into the list `BuildManager.FormBuildData` assigns to its
private `buildDataList` field.

Two earlier versions of this feature tried to make some *existing* `Wgo`
carry the "show every building" identity, and both broke in real ways:

- Opening the real nearest desk directly and tracking it in a
  `Plugin.LastHotkeyDesk` field, checked by reference equality in a
  `FormBuildData` postfix, broke because a real desk is also the exact
  object every normal interaction and every legitimate menu-reopen routes
  through - there was no way to reliably tell "opened via the hotkey" apart
  from "the player is just standing at this desk" using only that object's
  identity. A `UIBuildingWindow.Close()`-triggered clear was added to
  handle the "walk up to the same desk again later" case, but it turned out
  to *also* fire during the normal place-one-item-then-reopen-the-list flow
  mid-session (contrary to the decomp-based reasoning that led to adding it
  in the first place) - so the aggregated list would silently drop back to
  that desk's own normal list after placing a single item.
- Spawning a disposable temp clone that *borrowed* the found desk's own id
  avoided the reuse problem, but reusing an id means sharing
  `GameBalance.Me.buildDefsInBuilder[thatId]` with every real `Wgo` of that
  type - writing an aggregated list into that shared dictionary entry would
  have corrupted the real desk's own normal menu too (confirmed via
  `GameBalance.CreateBuildCache()`, which builds that dictionary purely
  keyed by shared id string).

Both problems disappear if the desk itself is never real to begin with.
This mod registers its own dedicated `WGODef`
(`Plugin.AggregateDeskId`, `"buildanywhere_desk"` - a brand-new id nothing
else in the game will ever use) via
`GameBalance.Me.AddData(...)`/`GameBalance.Me.InitCache()`, the same runtime
balance-data-registration pattern this game itself uses, and seeds
`GameBalance.Me.buildDefsInBuilder["buildanywhere_desk"]` once with the
deduplicated, `test_`-prefix-excluded union of every real desk's own
building list. `BuildingDef.GetBuildingsInBuilder` already does its own
unlock-status filtering internally (`isNeedsUnlock`/`unlockedBuildings`/
`lockedBuildings`, confirmed by reading it directly) - it re-derives what's
actually unlocked on every single call, so seeding the list once is
sufficient forever; nothing needs to be recomputed as the player unlocks
more buildings later. This means **`FormBuildData` needs no patch at all** -
vanilla, unpatched code does everything this feature needs once this desk's
`buildDefsInBuilder` entry exists.

Registration (`Plugin.RegisterAggregateDesk()`) is *not* reliably done from
`Awake()` alone, despite an earlier version of this mod assuming it was.
`GameBalance.Me` is only guaranteed populated once `MainGame.Start()` has
run (confirmed via decomp: `GameBalance.LoadGameBalance()` is only ever
called from `GameBalance.Me`'s own lazy getter and from `MainGame.Start()`),
and a BepInEx plugin's `Awake()` runs earlier than that with no ordering
guarantee either way - in practice, `GameBalance.Me` is null when `Awake()`
runs, `RegisterAggregateDesk()`'s own null guard fires and returns without
seeding anything, and (since nothing retried it) the very first hotkey
press threw `KeyNotFoundException` inside `BuildingDef.GetBuildingsInBuilder`.
DataDumper (this repo's own data-dumping mod) already works around exactly
this timing by polling for `GameBalance.Me` in `Update()` instead of
touching it in `Awake()`; this mod's fix is similar in spirit but simpler,
since it already has a natural on-demand trigger. `OpenBuildMenu()` now
calls `RegisterAggregateDesk()` again, every press, right before
`GetOrMoveAggregateDesk` - cheap once it has already succeeded (its
internal checks are just a `GetDataOrNull` and a `ContainsKey`), and by the
time a player can press the hotkey they're already in an active session, so
`GameBalance.Me` is guaranteed ready. `Awake()` still calls it once too, as
a harmless best-effort attempt, but it's no longer load-bearing. If
registration still somehow fails on a given press, `OpenBuildMenu()` falls
back to opening the real nearest desk for that one press instead of
guaranteed-crashing - self-healing, since the next press just retries.
Confirmed via decomp that nothing in normal gameplay calls
`GameBalance.InitCache()`/`CreateBuildCache()` again after
`MainGame.Start()` (no save-load, scene-transition, or unlock path
re-triggers it), so once the entry is seeded it can't get silently cleared
out later by the game's own code.

One harmless side effect of spawning this desk: the console logs
`No WgoPartBakedData with id:[buildanywhere_desk]` the first time it's ever
spawned. Confirmed via decomp (`WgoPartBakedDataCollection.Get`, called
unconditionally from `Wgo.PrecomputeSerializedBounds` regardless of
`ignoreChunkRegistration`) that this is just a dictionary-miss fallback to
`WgoPartBakedData.Empty` for a deliberately part-less `WGODef` - not an
error, and not something worth suppressing.

`Plugin.AggregateDesk` is this dedicated desk, spawned once (lazily, on the
first `OpenBuildMenuKey` press) and then simply *moved* - reparented and
repositioned - to wherever `FindNearestBuilderDesk()` finds on every later
press, rather than destroyed and respawned like the earlier clone-based
version needed to be. Since its id never changes between presses (unlike a
clone that had to match whichever real desk was found), there's nothing
that requires rebuilding it each time. Verified safe to hand-construct with
only an `id` set (every other `WGODef` field left at its C# default) by
tracing the full `Wgo.Spawn`/`WgoData` construction pipeline: every field
read along that path is guarded by a `TryGetValue`, a
`string.IsNullOrEmpty` check, or a sentinel-returning cache lookup, never a
direct dereference that could null-ref. No Addressables/prefab asset is
ever touched for it either, since the one method that would touch one
(`InitVisualBindings()`) is only reachable through chunk registration,
which this desk's `Wgo.Spawn` call always skips via
`ignoreChunkRegistration: true`.

Left inactive after spawning (`gameObject.SetActive(false)`, i.e. never
calling `UpdateChunkVisibility(true)`) and with `WgoData.IsInteractable =
false` set explicitly, as two independently-sufficient guarantees this desk
can never be walked up to and interacted with normally. The game's
interaction detection (`PlayerInteractionComponent.Update`) is a live
`Physics.OverlapBox` sweep every frame that provably cannot return
colliders on an inactive `GameObject` at all - there's no registry-based
interaction path that could bypass this - and `IsInteractable` is the exact
per-instance field that same sweep checks, as a second layer in case it
were ever active for some other reason.

`ShowEveryBuildingOnHotkeyOpen` now picks *which* desk gets opened rather
than patching what one desk shows: when it's on, the hotkey moves
`AggregateDesk` to the nearest found desk and opens that; when it's off, it
opens the real nearest desk directly, getting that desk's own normal list
from unpatched vanilla code either way.

`AggregateDesk` staying valid identity through `BuildManager.Disable()`'s
reopen-the-browse-window path and Move Stations' `ReopenBuildMenu()`
(`Kupie/GYK2_DECOMP/Gk2MoveStations/GK2MoveStations/MoveStationsPlugin.cs`)
needs no special-casing at all now - it's a real, persistent object that
simply exists for the mod's whole runtime, not something whose identity
needs defending against being reused or cleared.

### Why the build menu shows "Build Anywhere" instead of the raw id

`WGODef` has no display-name field at all - it only inherits `id` from
`BalanceBaseObject` (confirmed via decomp). This game's own convention
instead has a `Wgo`'s id double as a localization *key*:
`UIBuildingWindow.Redraw()` draws the build window's header via
`UIInfoWidgetData.Header`, which calls `LLBase.L(wgoData.id)` directly - so
without a real loc entry for `AggregateDeskId`, `L()` falls back to its
documented dictionary-miss behavior of returning the raw key string
verbatim, which is exactly why `"buildanywhere_desk"` showed up literally
in the menu before this fix.

There's no field to set on `WGODef` to fix this - the fix has to register a
real entry in the live loc table for that key. `LL` (the concrete loc-table
class, `LLBase`'s only subclass) is fully `public`, and `LLBase.L()` reads
straight from its `public`, non-serialized `dictionary`/`idsToMetaInfo`
instance fields - so `Plugin.RegisterAggregateDeskDisplayName()` writes
directly into those, rather than going through `AddLangString()`/
`InitHashDictionary()` (which clears and rebuilds the *entire* table from
separate `txtIds`/`txts` lists - more than this needs, and risks wiping
anything a language-mod-loader added there that hasn't made it into those
lists yet). `NestedLocalesMetaInfo`'s default constructor leaves
`hasMetaInfo` false, which is exactly what skips `L()`'s
nested-locale-insertion branch for a plain literal string like `"Build
Anywhere"`.

The one obstacle: the loaded table itself lives in `LLBase`'s `protected
static currentLang` field - no public static accessor returns the `LL`
instance itself (`LLBase.CurrentLang` only exposes the language id
string). Reflection is needed to reach it; everything after that is
ordinary public API. This is registered the same lazy/retried way as
`RegisterAggregateDesk` (best-effort in `Awake()`, retried in
`OpenBuildMenu()` right before first use each session) - the loc table's
load timing isn't something this mod controls either, so it gets the same
treatment. A failure here is non-fatal either way: worst case the menu
just shows the raw id again until a later retry succeeds, never a crash.

### Why Move Stations' "Move" button is suppressed on this desk

Move Stations (`Kupie/GYK2_DECOMP/tree/main/GK2MoveStations`) has no
Harmony patches or per-desk gate of its own - it injects its "Move" row
into the build browsing list via a `Canvas.willRenderCanvases` poll
(`TryInjectMoveMenuRow()`, private, void, no params) that clones the
vanilla "Remove" list entry whenever no row is already present. Clicking
it doesn't work correctly when `AggregateDesk`'s menu is open - it fails
to close the menu before opening the move-picker, most likely because some
part of its close-button-lookalike scan or the `BuildManager`/
`WgoBuildPointer` state it touches indirectly assumes a normal desk's
lifecycle, which `AggregateDesk`'s synthetic one (inactive `GameObject`,
non-interactable, moved rather than placed) doesn't match. Rather than
debug that further, `Plugin.PatchMoveStationsMoveButtonSuppression()`
prevents the row from ever being created while `AggregateDesk`'s menu is
open, so nothing broken is ever clickable - real desks are unaffected,
since the check only fires for `TryInjectMoveMenuRow` calls, and Move
Stations' own "Move" flow is left completely untouched for them.

This takes no compile-time dependency on Move Stations' assembly -
resolved and patched manually at runtime via `HarmonyLib.AccessTools`
(`TypeByName`/`Method`, both null-safe: they return `null` on a miss rather
than throwing), so it's a clean no-op with no Harmony error if Move
Stations isn't installed at all, or if a future version of it renames or
removes this method. Patched once, from `Awake()` only - unlike
`GameBalance`/the loc table, if Move Stations' assembly isn't loaded by
the time every plugin's `Awake()` has run, it never will be for that
session, so there's no lazy-retry needed here the way there is for those.

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
- Whether the build browsing window displays the opened desk's own name/icon
  anywhere in its UI - `AggregateDesk`'s hand-built `WGODef` has no real
  display name or icon set (every field besides `id` is left at its C#
  default, verified safe for the spawn pipeline itself, but not traced all
  the way through to whatever the window renders). Worst case this shows as
  blank/default text somewhere in the build browse window's UI - cosmetic
  only, not something that would break placement or the building list
  itself, but not confirmed either way against a running build.
- Zone Visuals is the first feature in this mod that renders its own
  runtime geometry rather than reading/writing data, so its in-game visual
  appearance (whether the fill/border alpha values read clearly, whether
  scene lighting affects the `Standard`-shader material more than expected,
  legibility at various zoom levels) hasn't been confirmed against a
  running build.

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

## Dump Zones: seeing the whole game's zone layout up front

`AllowBuildAnywhere` removes the *check* that gates placement on a matching
`WorldZone`, but it doesn't change the fact that camera confinement and
ground/elevation math are still tied to whichever zone the build session
opened in (see "Confirmed limitation" above) - building far outside any zone
is still likely to run into that. Rather than having to walk the entire map
to find out where zones do and don't exist, `DumpZonesKey` writes every
`WorldZone` in the *entire game* to a CSV, not just whatever happens to be
loaded right now.

This reads `MainGame.WorldData.gameSceneDataList` - one `GameSceneData` per
scene in the whole game, fully populated from the save file the moment the
player is in-game, independent of which Unity scene is actually
loaded/active - rather than `FindObjectsByType<WorldZone>()` (the pattern
this mod's own patches use elsewhere), which would silently under-report to
just the current scene. Each `GameSceneData.worldZones` entry
(`WorldZoneData`) already carries baked, absolute-world-space geography
(`pos`, `wholeZoneRect`), so no scene needs to be streamed in to read it.

Output goes to `BepInEx/BuildAnywhere_Output/worldZones.csv`, one row per
zone, columns:

- `Id`, `GameSceneId`, `WorldZoneType` - the zone's own id, which scene it
  belongs to, and its `WorldZoneData.WorldZoneType` (`Default` /
  `SimpleNotContainer`).
- `BuilderId` - the desk id this zone is linked to
  (`WorldZoneData.Definition.builderId`), blank if the zone's id doesn't
  resolve to a known `WorldZoneDef` at all (e.g. leftover dev/test zones -
  left in the dump rather than silently dropped).
- `PosX`, `PosY`, `PosZ` - the zone's anchor position (`WorldZoneData.pos`).
- `RectXMin`, `RectZMin`, `RectXMax`, `RectZMax` - the zone's bounding rect
  over the XZ ground plane (`WorldZoneData.wholeZoneRect`). Note the `Z`
  naming: `Rect.yMin`/`Rect.yMax` are world *Z* here, not height - `Rect` is
  a 2D struct being reused for an XZ ground-plane footprint, the same way
  the game's own baking code (`WorldZoneBakedData.SetFrom`) uses it.

## Zone Visuals: seeing WorldZone boundaries while you play

`DumpZonesKey` answers "where are the zones" as a spreadsheet; `ToggleZoneVisualsKey`
answers it in-world, live, while actually deciding where to build.
Toggling it on draws a translucent cyan fill plus a more solid cyan border
around every `WorldZone` currently loaded, refreshed every couple of
seconds.

This uses real runtime geometry, not `Gizmos`/`Debug.DrawLine` - both are
editor-only in this game (every usage in the decomp lives inside
`OnDrawGizmos(Selected)` or debug-only call sites) and are invisible in the
shipped build. Instead this mirrors the one rendering technique the game
itself proves works at runtime: `MeshRenderer`/`MeshFilter`, the same
mechanism the vanilla build-mode grid overlay (`BuildGrid3D`/
`ElevationGridQuad`) uses. Each marker is a procedurally-instantiated
`GameObject` (`new GameObject`, no prefab/Addressables needed) with a
hand-built mesh, following `ElevationGridQuad.GetSharedMesh()`'s proven
4-vertex/2-triangle/up-normal quad shape.

The material is `new Material(Shader.Find("Sprites/Default"))`, with just
`.color` set - not `"Standard"` manually forced into Transparent (Fade)
mode, which is what this originally shipped with and turned out to render
fully opaque in-game regardless of alpha (confirmed by testing alpha as low
as 0.01 and still seeing a solid fill). Root cause, confirmed via decomp:
the doc comment that recipe originally relied on as proof
(`LazyTerrainSurfaceUtility.cs` also calling `Shader.Find("Standard")`) was
wrong - that class only ever uses Standard's default *Opaque* mode, and its
`MeshRenderer` is explicitly disabled (it exists purely to host a
`MeshCollider` for terrain physics, never actually drawn), so it never
proved Transparent mode renders correctly here at all. Nothing else in this
game's own code uses Standard's Transparent mode either, and there's no
shader-variant-preservation mechanism (`ShaderVariantCollection`,
`Shader.WarmupAllShaders`) anywhere - consistent with Unity's build-time
shader stripping simply not keeping the `_ALPHABLEND_ON` variant, since
nothing in the shipped game needs it; toggling that keyword at runtime
silently no-ops and the shader falls back to its always-present Opaque
variant, exactly matching the symptom. `Sprites/Default` sidesteps this
instead of working around it: it's alpha-blended unconditionally, baked
directly into the shader with no keyword gate to get stripped, and
confirmed present in this build via this game's own extensive uGUI usage
(264 files use `UnityEngine.UI`) - Unity's shader stripper always retains
`Sprites/Default`/`UI/Default` alongside it, since `Image`/`Graphic`
rendering depends on one of them. Same accepted limitation either way:
there's no way to also make this render "through" walls (ignore depth
testing) from script - occlusion by solid geometry between the camera and
a marker isn't something a shader swap changes.

Only currently-loaded zones are shown, via `FindObjectsByType<WorldZone>()`
- the same pattern this mod's own patches already use elsewhere -
deliberately *not* `DumpZonesKey`'s whole-game
`MainGame.WorldData.gameSceneDataList` read. Total `WorldZoneData` instances
across the whole game is likely low hundreds to low thousands; eagerly
spawning a marker for every one of them at once, most nowhere near the
player, isn't worth the real Addressables/instantiation-count risk that
scale implies. Markers refresh on a timer (every 1.5s) rather than reacting
to a scene-load/unload event, because no such subscribable event exists
(`GameSceneManager`'s load/unload methods take one-shot per-call callbacks,
not events) - this just re-scans periodically, same as this mod's other
"what's loaded right now" patches do on each use, just repeated instead of
once per press.

Each marker is a flat plane, not a real 3D box - zones themselves have no
persisted vertical extent in this game. `WorldZoneData.Init(BoxCollider)`
and `WorldZoneBakedData.SetFrom(WorldZone)` both only ever read a zone
collider's X/Z size into `wholeZoneRect`, never Y, and
`WorldZoneElevationArea.Awake()` actively flattens its own footprint
collider to near-zero height if it's ever non-trivial - direct evidence
this codebase treats zone-shaped colliders as ground footprints, not
volumes.

What height that flat plane is drawn at is a separate, deliberate choice:
each marker floats at the *player's own current height* (plus a small fixed
offset), re-sampled on every refresh so it tracks the player up and down
stairs, hills, and basements - rather than sitting at the zone's own ground
level, where it would easily be hidden behind buildings, terrain, fences, or
decorations. This is a real limitation worth being explicit about: it is
**not** occlusion-ignoring "see through walls" rendering. `ZTest` isn't a
script-settable property on the `Standard` shader, and shipping a custom
always-on-top shader isn't something a mod can do without Unity's
asset-bundle tooling - so a marker can still be hidden by solid geometry
directly between the camera and it. Floating near the player is a practical
mitigation (out of ground clutter, roughly in the player's own sightline),
not a true fix.

## Current Zone Info: seeing where you are, live

`ShowCurrentZoneInfo` shows a simple centered line of text near the top of
the screen, live-updating: the id and X/Z bounds of whatever `WorldZone`
you're currently standing in (`"No Zone"` while standing outside every
zone). Driven by `PlayerData.CurrentWorldZoneData` - the same field the
game itself uses to track this, set/cleared by `PlayerPhysicalBody` as you
cross zone boundaries - and the same `id`/`wholeZoneRect` fields
`DumpZonesKey` already reads, just for whichever one zone you're in right
now instead of the whole game at once. Cheap enough (one property read,
one string format) to refresh every frame - no periodic-rescan timer like
Zone Visuals needs.

The text itself is `TextMeshProUGUI`, not legacy `UnityEngine.UI.Text` -
confirmed via decomp that this game's own UI is built exclusively on
TextMeshPro (151 files reference it, zero legacy `Text` usage anywhere in
the game's own code). A runtime `TextMeshProUGUI` needs a real font asset
assigned or it renders nothing, and nothing in this game's own code ever
reads `TMP_Settings.defaultFontAsset` - so rather than trust that default
(the same kind of unverified assumption that caused the Zone Visuals fill
material to silently render opaque earlier in this mod's development, see
above), this copies `.font` off an already-live `TextMeshProUGUI` found in
the scene - the same idea the game's own
`UISteamWorkshopCreatorWindow.ApplyGameTextStyle` uses, though not quite
the same implementation: that method copies a *specific, known* element's
`.fontSharedMaterial` directly, while this mod has no such specific
element to target (it needs a font from *whatever* text happens to be
loaded) - copying an arbitrary found instance's own material risked
inheriting unusual styling (an unusual face color or alpha) baked into
that specific instance rather than a normal readable default, so this
uses the font asset's own default material instead
(`templateText.font.material`) - a safe, normal baseline regardless of
which live text component happened to be picked as the font source.
Building the overlay is deferred until a live text component actually
exists to copy from - retried every frame from `Update()` while the
toggle is on, the same lazy-retry shape this mod already uses for
`GameBalance`/loc-table readiness elsewhere.

That search is also deferred until `MainGame.PlayerData` is populated -
i.e. until a save is actually loaded, not just whenever the first live
`TextMeshProUGUI` happens to appear. An earlier version searched
immediately, which meant it could copy a font off main-menu-scoped UI
while still loading - confirmed by testing that the overlay correctly
showed `"No Zone"` at the main menu, then went permanently blank the
moment a save loaded. That matches Unity's asset-lifetime behavior
exactly: if the copied font/material belonged to the menu scene, it gets
unloaded (and the copied reference goes dangling) the moment that scene
unloads on the transition into gameplay - TMP silently renders nothing
for a destroyed font reference, no error or exception, it just stops
appearing. Waiting for `MainGame.PlayerData` means this only ever copies
a font from in-game UI, which stays valid for the rest of that session.

The overlay's `Canvas` (`ScreenSpaceOverlay` + `CanvasScaler`, no
`GraphicRaycaster` since it's display-only) mirrors
`UISteamWorkshopCreatorWindow.CreateInstance()`'s own proven recipe, and is
parented under this mod's own persistent `GameObject` so it survives scene
loads the same way that object already does - ordinary BepInEx platform
behavior (every plugin sits under a chainloader root marked
`DontDestroyOnLoad`), not something specific to this game's decomp. The
Canvas uses a deliberately high `sortingOrder` (`32000`) so it can't end up
drawn underneath one of the game's own HUD/menu canvases, and the
font-template search explicitly includes inactive `TextMeshProUGUI`
components (`FindObjectsInactive.Include`) rather than only active ones -
whether anything happens to be active at the exact moment this first looks
isn't something this mod controls, and a merely-inactive component still
has a perfectly usable font to copy.

## Zone Size Overrides: resizing a named zone

`ZoneSizeOverrides` lets you extend (or shrink) a specific `WorldZone` in
any of the four compass directions - one override per zone,
**semicolon-separated**, format `zoneId,east,south,north,west`, each value
a delta in world units: how far to push that edge outward (0 to leave it
alone, negative to pull it inward instead). Run `DumpZonesKey` first to
find a zone's id. East/west move the X bounds; north/south move the Z
bounds (world Z - the same "`Rect.y` is actually world Z" quirk this mod
documents elsewhere). For example, `home,0,40,0,0` extends the `home` zone
40 units further south, leaving its other three edges untouched; for two
zones at once, `home,0,40,0,0;graveyard,10,0,0,0`.

Semicolons, not newlines, separate multiple entries - confirmed against a
real BepInEx `.cfg` file that this has to fit on one physical line.
BepInEx writes a `ConfigEntry<string>` as a single `Key = Value` line, and
a second raw line typed after it isn't a continuation of the value - it
falls outside the entry entirely (an earlier version of this feature
assumed newline-separated entries would work; that assumption was wrong,
caught when testing against an actual written `.cfg` file). Newlines are
still accepted as an *extra* separator alongside semicolons, in case some
other editing surface - a config-manager plugin's multi-line text field,
say - does preserve real newlines; that costs nothing either way.

Deltas, not absolute coordinates, on purpose: they're computed relative to
whatever the zone's edge actually is at the moment each patch below runs
(see the compounding note under `WorldZoneData_PrepareForGame_Patch`), so
the same line keeps meaning "40 further south" without needing to be
recalculated by hand every time you re-check a zone's current coordinates.

Confirmed via decomp that `WorldZoneData.wholeZoneRect` is not actually
what gates build placement - `WgoExtensions.TryGetNearestBuilderWorldZone`'s
physics overlap and `WgoBuildPointer.UpdateSelectionCellsState`'s per-cell
zone check both query the real `BoxCollider` on the `WorldZone` GameObject,
never `wholeZoneRect` directly. So this needs two coordinated Harmony
prefixes, not one:

- `WorldZoneData_Init_Patch`, on `WorldZoneData.Init(BoxCollider)` - runs
  every time that zone's scene streams in. Mutates the *collider itself*
  (center/size) to the expanded world-space footprint before the original
  method derives `wholeZoneRect` from it, reusing `Init`'s own formula
  (`wholeZoneRect` is built from
  `zoneCollider.transform.TransformPoint(zoneCollider.center)` and
  `zoneCollider.size`, with no scale factor applied - confirmed by reading
  it directly) with the configured deltas applied on top, rather than
  inventing new geometry. Only the XZ footprint changes; the collider's
  existing world-space Y center/size is preserved. This is what actually
  makes build placement respect the new size. Always computed relative to
  the collider Unity just instantiated fresh from the zone's unmodified
  Addressable prefab - never relative to anything this mod wrote back
  previously - so this can't compound no matter how many times the scene
  streams in or how many sessions the override stays configured.
- `WorldZoneData_PrepareForGame_Patch`, on `WorldZoneData.PrepareForGame()`
  - the once-per-boot method that bakes a zone's navmesh region off
  `wholeZoneRect`, which can run *before* that zone's `WorldZone`
  GameObject/collider even exists yet (scenes stream in later). Applies
  the same deltas directly to `wholeZoneRect` here too, so the navmesh
  bakes correctly from the start rather than staying stuck at the old size
  until the collider catches up later.

  Unlike the `Init` patch above, this one isn't guaranteed collision-free
  across sessions: `wholeZoneRect` (unlike the collider, always
  re-instantiated fresh) is persisted in the save file, so on a continued
  save this reads whatever was saved last session - already-expanded, if
  this override was active then. For a zone you actually walk into that
  session, this self-corrects the moment `Init()` runs (always relative to
  the pristine prefab collider), and saving again after that persists the
  correct value. The narrow edge case is a zone whose scene is never
  visited in a given session - its navmesh bake for that session compounds
  one more step on top of whatever was already saved, until you do visit
  it. Worth knowing, not something worth engineering around given how
  narrow it is.

Both patches key off the same zone id and deltas, so it doesn't matter
which one a given zone hits first in a session - they converge either way
(modulo the narrow `PrepareForGame` edge case above).

**This is a real, wide-reaching change, not a cosmetic one** - resizing a
zone's actual bounds can affect more than where you can build: navmesh
baking, worker task assignment (caretaker/gardener/conveyor-transporter
orders are all zone-scoped), storage/delivery network membership, and
quality/achievement scoring (e.g. zone-quality-threshold achievements) are
all keyed off the same `wholeZoneRect`/collider this feature changes.
That's an intentional trade-off for this feature, not an oversight - use
it deliberately.

## Config

`BepInEx/config/kupie.gk2.buildanywhere.cfg` after the first run:

- `General` / `AllowBuildAnywhere` (default `true`)
- `General` / `OpenBuildMenuKey` (default `Ctrl+B`)
- `General` / `ShowEveryBuildingOnHotkeyOpen` (default `true`)
- `General` / `ToggleZoneVisualsKey` (default `F10`)
- `General` / `ShowCurrentZoneInfo` (default `false`)
- `General` / `ZoneSizeOverrides` (default empty - see "Zone Size Overrides" above for the
  `zoneId,east,south,north,west` format)
- `Debug` / `Debug Logs` (default `false`)
- `Debug` / `DumpZonesKey` (default off, `None` - the whole-game CSV dump is a one-off
  troubleshooting/planning tool, not something worth a permanently-bound key)
