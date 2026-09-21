# BuildAnywhere (GK2) - redesign (replaces the previous TASKS.md)

Repo: Kupie/GYK2_Mods, BuildAnywhere/ subfolder. The previous design (nearest-
zone fallback patch + global HotkeyOpenInProgress flag + FormBuildData
override keyed on that flag) was more machinery than the problem needs. Rip
all three of those out and replace with the design below, which mirrors what
GK1's IBuildWhereIWant actually does once you account for GK2's build-list
data living in a dictionary rather than on the Wgo instance.

## Core redesign

GK1 clones one hardcoded, always-available desk (`mf_wood_builddesk`, found
once via `FindObjectsOfType`, never searched again) and hands the clone a
craft list built straight from the global data table - the clone is never
interacted with normally, it's a disconnected anchor object.

GK2's desk build lists come from `GameBalance.Me.buildDefsInBuilder[desk.Id]`,
a dictionary populated once at startup from static data, keyed by id string,
not tied to any specific Wgo instance. That's why the direct port needs one
small patch instead of zero, but it collapses to this:

1. Find a real Builder-type desk once (reuse `FindNearestBuilderDesk`), cache
   it as `Plugin.AnchorDesk`, never search again unless it's been
   destroyed/unloaded (null-check and re-search as a fallback).
2. On each `OpenBuildMenuKey` press: destroy the previous clone if one
   exists, then spawn a fresh one at `AnchorDesk`'s exact position and scene,
   with `AnchorDesk`'s exact id, as `Plugin.CurrentClone`. Same id + same
   position means `WgoExtensions.TryGetNearestBuilderWorldZone`'s physics
   check succeeds on its own, no patch needed - it's sitting in the same real
   zone the anchor always does.
   - Use `Wgo.Spawn(new WgoData(id, position, sceneId) { isTempObject = true },
     parentTransform, ...)`, matching the pattern in
     `BuildPointer.PrepareAndSpawnWgoBuildPointer` in the decomp. VERIFY the
     full parameter list against the real assembly before using it - I only
     have one call site to go on and don't know what the trailing bool
     parameters control.
3. Call `TryEnable(CurrentClone, null)`.
4. One patch on `BuildManager.FormBuildData`, keyed on object identity, not a
   flag:
   ```
   private static void Postfix(BuildManager __instance, Wgo buildDesk, ref bool __result)
   {
       if (!__result || buildDesk != Plugin.CurrentClone) return;
       // ...same aggregation/unlock-filtering logic as before, assign to __instance.buildDataList
   }
   ```
   A real desk a player walks up to normally is never affected, since it's
   never reference-equal to `CurrentClone`.

## Delete entirely

- `WgoExtensions_TryGetNearestBuilderWorldZone_Patch` - not needed, the
  clone's zone-match is real.
- `Plugin.HotkeyOpenInProgress` and all the logic around scoping it to a
  single call - not needed. The reference-equality check on `CurrentClone`
  is valid for the clone's entire lifetime, so it survives
  `BuildManager.Disable()`'s reopen-the-browse-window path and Move
  Stations' `ReopenBuildMenu()` (which also just re-calls `TryEnable` on
  whatever `Wgo` it captured) automatically, with no special-casing for
  either.

## What this does NOT fix, and why that's expected

The camera will still move to wherever `AnchorDesk` physically is, every
time the hotkey is used, same as GK1 always warping to the wood desk's fixed
location. That's inherent to how GK2 frames the build camera for any desk
opened from a distance, not a symptom of the removed workarounds. If zero
camera movement is wanted, that's a separate, deeper task (skipping
`BuildController`'s camera-follow/confine code) - don't fold it into this
fix, decide separately whether it's worth doing.

## Open question worth resolving before relying on AllowBuildAnywhere

Placement itself (task below, the zone-bypass toggle) works at the
per-cell level in `WgoBuildPointer.UpdateSelectionCellsState`, independent of
which zone the session nominally opened in. But `BuildModeCameraController.
Enable(followTarget, boundingVolume)` calls
`TrySet3DConfinerBounds(boundingVolume)` with the anchor zone's own collider.
Confirm in-game whether this actually hard-confines camera *panning* to that
volume, not just where the camera starts. If it does, being able to place
objects anywhere is useless in practice if the camera can't physically reach
that spot. If confinement is real, check whether passing a much larger
bounding volume (or skipping the confiner call when `AllowBuildAnywhere` is
on) fixes it without breaking anything else `EnableBuildMode` relies on that
collider for (elevation/ground-plane math uses the same zone, separately -
see `UpdatePointerAtPos`).

## Unchanged from before

**Split AllowBuildAnywhere into independent zone-bypass and collision-bypass
toggles** - this is orthogonal to the above (it's about placement validity
inside an active build session, not which desk/menu got opened) and the
prior guidance still applies: `WgoBuildPointer.UpdateSelectionCellsState`'s
loop needs partial reimplementation using `BuildSelectionCell.
OverlapBoxNonAlloc` (public) rather than discarding the whole result, since
right now it's all-or-nothing.

**Move Stations compatibility**
(`Kupie/GYK2_DECOMP/Gk2MoveStations/GK2MoveStations/MoveStationsPlugin.cs`) -
should now just work given the reference-equality design above, since its
`ReopenBuildMenu` calls `TryEnable` on the same captured `Wgo`. Still worth
confirming its own move-mode (~line 4280, `OnMoveMenuClicked`, its own
"native grid" snapshot logic) doesn't bypass `WgoBuildPointer` entirely - if
it does, the collision-bypass toggle above won't reach it and that's a
separate, smaller follow-up to flag rather than solve preemptively.

## Conventions

Tabs, no em dashes, comments explain why a hook was chosen, update README.md
to match what ships, flag anything unverified against the real assembly
rather than stating it as confirmed.