# BuildAnywhere (GK2) - next task (replaces the previous TASKS.md)

Repo: Kupie/GYK2_Mods, BuildAnywhere/ subfolder.

## Status: the clone/AnchorDesk redesign this file used to specify was reverted

The previous version of this file specified caching a real Builder desk once
as `Plugin.AnchorDesk` and spawning a fresh clone of it (`Plugin.CurrentClone`)
on every hotkey press, mirroring GK1's `IBuildWhereIWant`. That shipped, and
it caused a real bug: every hotkey session resolved to the exact same fixed
`WorldZone` forever (whichever desk was found first), so the camera always
jumped back to that one location - e.g. always back to the house - no matter
where the player had gone since. Root cause and full writeup are in
README.md's "Why the hotkey re-finds the desk on every press" section.

The fix already shipped: `AnchorDesk`/`CurrentClone` are gone, the hotkey
searches for the nearest Builder desk fresh on every press again and opens it
directly (no cloning), and a `Plugin.LastHotkeyDesk` field does the
identity-based `FormBuildData` detection the clone used to do, keyed to the
real desk instead. See README.md for the current design and its one accepted
trade-off (the `LastHotkeyDesk` same-desk-reuse edge case).

Nothing here needs picking back up for that bug. The rest of this file is a
new, unrelated task.

## Next task: building in areas with no vanilla WorldZone coverage

Researched, not implemented. `AllowBuildAnywhere` currently only bypasses the
zone-*boundary* and collision checks inside `WgoBuildPointer.
UpdateSelectionCellsState` - it doesn't help if the player is standing
somewhere with no `WorldZone` at all, since `BuildManager.TryEnable` requires
a matching zone to exist near the *desk* just to open build mode in the first
place, and the whole build grid/elevation math is keyed to that one zone for
the entire session regardless of where the cursor moves.

Confirmed via the decomp (`WorldZone.cs`, `WorldZoneDef.cs`, the real
`worldZoneDefs.json` data dump, `WorldZoneData.cs`) that vanilla zones are
hand-placed boxes (one Addressable prefab per zone id, not a tiled/procedural
system), real gaps exist, and the game already handles the player being in
zero zones gracefully (`PlayerPhysicalBody.ExitWorldZone` just clears
`CurrentWorldZoneData` to null - no crash, no special-cased "must have a
zone" assumption elsewhere in the player-facing code).

### Recommended approach: spawn a dedicated catch-all WorldZone

Register a synthetic `WorldZoneDef` (e.g. `id = "buildanywhere_catchall"`,
`builderId` matching whatever desk id needs it) via
`GameBalanceBase.AddData<WorldZoneDef>` (public), then construct a matching
`WorldZone` by hand - not via `WorldZone.Spawn()`, which loads an Addressable
prefab keyed by id that won't exist for a made-up id. Instead:
`GameObject.Instantiate` a bare `GameObject`, `AddComponent<WorldZone>()` +
`AddComponent<BoxCollider>()`, size the collider to blanket the map (or at
least stay within 10 units of every relevant desk, satisfying
`TryGetNearestBuilderWorldZone`'s search radius), and call `WorldZone.Init`
(public) with matching `WorldZoneData`.

Two things to get right, both confirmed via decomp, not yet tried:

- `GameBalanceBase`'s lookup cache (`Dictionary<string,int>`, built once by
  `InitCache()` at startup) is **not** rebuilt by `AddData` - a freshly added
  `WorldZoneDef` is invisible to `GetDataOrNull`/`GetData` (used by
  `WgoExtensions.IsBuilderForWorldZone` and `WorldZoneData.Definition`) until
  something calls `GameBalance.Me.InitCache()` again after adding it.
- The `WorldZoneData`'s `id`, the `WorldZone` component's `id`, and the
  `WorldZoneDef.id` all have to match (`ObjectLinkedToDefinition.Definition`
  resolves by that shared string), and for
  `WgoBuildPointer.UpdateSelectionCellsState`'s per-cell check
  (`worldZone.Data.Definition.id == this.worldZoneId`) to pass, the catch-all
  zone's id needs to be the one `TryGetNearestBuilderWorldZone` actually
  resolves for the session - trivially true if it's the only zone within
  range of the relevant desks.

This is more surgical than the alternative below: it only affects whatever
explicitly queries the new zone (build placement, and zone membership for any
Wgo that happens to fall inside its bounds), leaving every vanilla zone's own
bounds and membership untouched.

### Alternative considered, not recommended: resize existing vanilla zones

Geometrically simple (`BoxCollider` size/center are plain fields, resizable
at runtime), but confirmed via decomp to reach well beyond build placement.
`WorldZoneData.wholeZoneRect.Contains(...)` gates real zone membership, which
drives: quality scoring and achievement unlocks (e.g. the `graveyard` zone's
quality-≥200 achievement), navmesh cutting/baking (`PrepareForGame()` sizes
the zone's entire `navigationGraph` region off `wholeZoneRect`), worker task
assignment (`GetOrderForCaretaker`/`GetOrderForGardener`/
`GetOrderForConveyorTransporter` are all zone-scoped), storage/delivery
network membership (`multiInventoryWgoDatas`), and player-facing state
(`KnowledgeSystem.visitedWorldZones`, the "entered zone" quality widget).
Resizing a vanilla zone to cover build-placement gaps risks silently pulling
unrelated buildings/objects into its scoring, navmesh, worker-assignment, and
delivery logic. Not worth it when the catch-all-zone approach above achieves
the same placement goal without touching any of that.

## Conventions

Tabs, no em dashes, comments explain why a hook was chosen, update README.md
to match what ships, flag anything unverified against the real assembly
rather than stating it as confirmed.
