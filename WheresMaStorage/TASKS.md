# WheresMaStorage (GK2) - phased port plan

Repo: Kupie/GYK2_Mods, WheresMaStorage/ subfolder.

Porting the GK1 mod of the same name (p1xel8ted/Graveyard-Keeper-Mods,
src/WheresMaStorage) to GK2. GK2's world-object/inventory model (Wgo/WgoData/
WGODef split, `MultiInventory`, `Inventory`/`Item`) is architecturally
different from GK1's (monolithic `WorldGameObject`/`ObjectDefinition`), so
this is a from-scratch reimplementation of the GK1 mod's *behavior* against
GK2-native APIs, not a transliterated patch set. See the task description
this file was created from for the full GK1 feature list, tiered by how
load-bearing each piece is.

## Status

- [x] Phase 1 - Tier 2 (capacity + stacking): **shipped**, this commit.
- [ ] Phase 2 - Tier 1 (shared inventory pool): researched below, not
      implemented.
- [ ] Phase 3 - Tier 4 (drops, loot magnet, hand-tool destroy): not started.
- [ ] Phase 4 - Tier 3 (QoL/UI toggles): not started.
- [ ] Shrink-safety confirmation dialog (deferred sub-feature of Tier 2):
      not started - see its own section below.

## Phase 1 - Tier 2 (capacity + stacking) - shipped

`CapacityBonus.cs`:
- Player inventory + tool belt: same approach as
  `MoreInventorySlots/InventoryBonus.cs` (write `Item.InventorySize`'s
  private backing field via its public setter, not a getter postfix - see
  that mod's README for why a getter patch doesn't work). One config value
  (`Player Inventory Bonus Slots`) drives both, unlike MoreInventorySlots'
  two separate sliders - flagged as an open question below.
- Containers: extended the same technique to every `WgoData` in the save
  whose `Definition.inventorySize > 0 && Definition.OpenInMultiInventory`
  (confirmed via decomp, `WGODef.cs` - `OpenInMultiInventory` is the same
  flag GK2's own `MultiInventory(WorldZoneData, ...)` constructor uses to
  decide which WGOs count as shared storage, so it's a reasonable proxy for
  "is this a chest-like container" without needing an id allowlist).
  Enumerated via `WorldData.gameSceneDataList[*].wgoDataList` (confirmed
  public field, `GameSceneData.cs`) at `OnGameStarted`, plus a Harmony
  postfix on `WorldData.AddWgoData(WgoData, bool)` for containers placed
  mid-session (confirmed this is the real add path both the 3-arg
  `AddWgoData(string, Vector3, ...)` overload and `GameSceneData.AddWgoData`
  funnel through).
- Applied-bonus bookkeeping mirrors MoreInventorySlots: track what was last
  applied (per-player fields, `Dictionary<SGuid, int>` for containers keyed
  by `WgoData.UniqueId`) and subtract it back out before reapplying, so a
  config edit or repeated `OnGameStarted` never compounds.

`StackSizeBonus.cs`:
- `ItemDef` instances are shared balance-data singletons
  (`GameBalance.Me.itemDefs`, confirmed public `List<ItemDef>`), one per item
  id - unlike capacity there's no per-instance state, just iterate that list
  and raise `ItemDef.stackCount` (confirmed plain public int, same field
  `MultiInventory.CanAddItemsToInventories` reads for stack math).
- Category membership read off `ItemDef.type` (`ItemType` enum, confirmed
  values: `Axe/Shovel/Pickaxe/Hammer/FishingRod` = tools, `Sword/Bow/Pike` =
  weapons, `BodyArmor/Collar` = equipment, `Preach` = prayer,
  `Brain/Heart/Flesh/Bones/Skull/Guts/Skin/Embalm` = grave/autopsy items) and
  `ItemDef.isTool` (confirmed bool field). "General stackables" = anything
  already stackable in vanilla (`stackCount > 1`), matching GK1's ungated
  default category.
- Only ever raises a stack size, tracked against a recorded vanilla baseline
  per item id, same anti-compounding shape as the capacity bonus.

### Open questions / gaps flagged rather than guessed at

- **Pen/paper/ink and chisel stacking** (GK1's two remaining, off-by-default
  stacking toggles): no equivalent found. Neither `ItemType` nor `isTool`
  distinguishes these from other tools/small items in the decomp - GK1
  likely keyed them off specific item ids. Not implemented; would need the
  real GK2 item ids for pen, paper, ink and chisel(s), found either by
  grepping decomp's data dumps (`DataDumper` mod in this solution may help
  produce a live item-id list) or by decompiling `worldZoneDefs.json`-style
  balance data if the ids aren't in code. Flagging rather than approximating
  with a wrong `ItemType` guess.
- **One capacity slider instead of two**: GK1 has separate
  `AdditionalPlayerInventorySpace` / `AdditionalContainerInventorySpace`.
  This port folds player+tool-belt into the existing
  `MoreInventorySlots`-style single value and adds one more for containers -
  two total, matching GK1's split. If MoreInventorySlots is also installed,
  both mods target the same `PlayerData.inventory`/`toolBeltInventory`
  fields and will race (last write wins per `OnGameStarted`/config-change
  ordering) - same caveat GK1's own README documents for its conflict with
  MoreInventorySlots and Oyasumi Infinite Stack. Worth a load-order note in
  this mod's README once Tier 1 ships and the README gets written properly.
- Shrink-safety (dropping/confirming when a slider is lowered below current
  fill) deliberately skipped per the phasing instructions - raw resize works
  first, dialog UX comes after Tier 1.

## Phase 2 - Tier 1 (shared inventory pool) - researched, not built

This is the mod's actual reason to exist, and the good news from decomp: GK2
**already has** a zone-scoped multi-inventory concept, so this phase is
extending an existing system rather than reimplementing GK1's manual
`WorldMap._objs` scan + reflection-based inventory assembly from scratch.

Confirmed via decomp (`Assembly-CSharp/MultiInventory.cs`,
`ChestInteractionHandler.cs`, `UIBaseChestWindowData.cs`):

- `MultiInventory(WorldZoneData worldZoneData, WgoData excludeWgoData = null,
  bool includePlayerInventory = false)` already builds exactly the pool GK1's
  "shared inventory" feature wants: every `Inventory` in the zone whose
  `WgoData.Definition.inventorySize != 0 && Definition.OpenInMultiInventory`,
  optionally including every player's own inventory in that zone
  (`worldZoneData.playerDataList`), excluding one specified WgoData (used so
  a chest doesn't list itself).
- `ChestInteractionHandler.Interact` already calls this exact constructor
  (`new MultiInventory(currentWorldZoneData, this.assignedWgo.Data, false)`)
  and hands the result into `UIBaseChestWindowData` alongside the player's
  own inventory, when a player opens *any* chest. **This means opening a
  chest today, in vanilla GK2, already pools every other eligible container
  in the same zone** - GK1's "shared inventory pool while interacting with
  certain world objects" is already half-shipped by the base game for
  chests specifically. What's actually missing relative to GK1:
  1. Crafting/building desks - need to find whichever
     `WGOInteractionHandlerBase` subclass backs craft stations (Builder
     desk, cooking, etc.) and check whether it constructs a `MultiInventory`
     the same way `ChestInteractionHandler` does, or only checks the
     player's own inventory. Not yet checked - **next concrete step**.
  2. `includePlayerInventory` is `false` in the `ChestInteractionHandler`
     call, i.e. only *other* players in the zone are pooled, not relevant
     here (singleplayer), but worth understanding before extending this
     call site.
  3. No exclusion toggles - vanilla's call doesn't gate on well/zombie
     mill/quarry zone type at all. Implementing GK1's
     `ExcludeWellsFromSharedInventory` etc. means either filtering
     `worldZoneData.wgoDataList` before constructing `MultiInventory`, or
     constructing it and then removing entries whose zone/WGO type matches
     an excluded category. Needs the actual zone-type/WGO-tag identifiers
     for wells, zombie mill and quarry, not yet looked up.
  4. Wilderness containers (configurable containers reachable outside any
     zone) - GK1 feature, no GK2 equivalent investigated yet. Since
     `MultiInventory`'s zone constructor is keyed to a single
     `WorldZoneData`, out-of-zone containers would need a second pass, not
     covered by extending the existing constructor alone.
  5. Sort by distance from crafter - `MultiInventory.Sort()` (private)
     currently sorts by whether an inventory has a
     `FuelContainerSerializedItemProperty`, not by distance. Reordering
     `inventoryList` by distance after construction (a wrapper/extension
     rather than patching the private `Sort()`) is the likely approach -
     GK1's `SortByDistanceFromCrafter` toggle wants this at the point of use
     (crafting/building), which is also where the crafter's position is
     known; doing it inside `MultiInventory` itself has no "distance from
     what" reference point.
  6. Zombies' own access to the shared pool, and "show only personal
     inventory at vendor" - separate features, not investigated.

Before writing any patch: read every `WGOInteractionHandlerBase` subclass
(confirmed location: `Assembly-CSharp/`) to find the craft-desk equivalent of
`ChestInteractionHandler`, and confirm whether it already pools inventories
or needs a Harmony patch to construct/pass a `MultiInventory` the way
`ChestInteractionHandler.Interact` does. That answer decides whether Tier 1's
core mechanic is "extend an existing call" (cheap) or "add a new call site"
(more surface area, more risk of missing an edge case vanilla already
handles for chests, like network/multiplayer command attributes on
`WorldData.AddWgoData`).

`GameSave.GlobalEventsCheck` / a GK1-style pool-invalidation hook has not
been checked against GK2's equivalent - `MultiInventory` is constructed
fresh on each `Interact()` call rather than cached, so it's unclear GK2 needs
an invalidation event here at all (no persisted pool object to go stale).
Confirm this before assuming an invalidation hook is needed.

## Phase 3 - Tier 4 (gameplay conveniences) - not started

Self-contained, listed here for the record, not yet researched:
- Hand tools discardable (vanilla prevents throwing out equipped tools) -
  find the drop/destroy-item path that currently blocks tools and the guard
  condition to bypass.
- Auto-collect eligible ground drops on load, or relocate near the Keeper's
  house, skipping quest-flagged/scripted drops and crates - per the original
  task description, GK2 likely has a `DropSystem`/`DropData`/`DropView*`
  cluster instead of GK1's single `DropResGameObject` class; not yet located
  in decomp.
- Player-only pickup range increase ("loot magnet") - find the pickup-radius
  check and confirm it's keyed to the player controller specifically, not
  shared with NPC/zombie AI.

## Phase 4 - Tier 3 (QoL/UI) - not started, lowest priority

Per the phasing guidance, several of these may not be worth the Harmony
surface area if GK2's UI differs enough from GK1's `InventoryPanelGUI`
architecture (GK2 appears to use an `InventoryWidgetData`/`*WidgetData`
family instead of one panel class, per the task description - not yet
confirmed against decomp). Do this last, and flag any sub-feature that looks
not worth porting rather than forcing it in. Not researched yet.

## Conventions

Tabs, no em dashes, comments explain why a hook was chosen (not what the
code does), update README.md to match what ships, flag anything unverified
against the real assembly rather than stating it as confirmed.
