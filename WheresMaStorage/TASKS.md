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

- [x] Phase 1 - Tier 2 (capacity + stacking): **shipped**.
- [x] Phase 2 - Tier 1 (shared inventory pool): **shipped, partial** - master
      toggle, well/quarry exclusion, distance sort and the zombie-access
      toggle are in. Zombie mill exclusion, wilderness containers, the
      vendor "personal inventory only" override, and ZombieWgoData/
      ConveyorWgoData craft variants are not - see "What's not covered yet"
      below.
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

This is the mod's actual reason to exist. The decomp finding is bigger than
Phase 1's writeup assumed: **the shared pool is not half-shipped, it's
already shipped for both chests and craft desks.** GK2 has a zone-scoped
`MultiInventory` mechanism (`Assembly-CSharp/MultiInventory.cs`) and every
place that matters already constructs one. Tier 1 is not "add pooling to
craft desks" - it's "the pool already exists everywhere GK1 wanted it;
implement GK1's *restrictions and refinements* on top of it."

### Confirmed: crafting/building desks already pool inventory

All of `CraftInteractionHandler` (the `WGOInteractionHandlerBase` subclass
behind ordinary craft desks - cooking, smithing, etc.) and `BuildManager`/
`UIBuildingWindow`/`UITownBuildingWindow` (behind the Builder desk and town
building) funnel through pooled inventory already:

- `WgoData.GetCraftableMultiInventory(bool excludeWorkerInventory = false)`
  (`Assembly-CSharp/WgoData.cs:1594`) is the one method every craft-desk UI
  and every craft-start/consume path reads from - confirmed call sites
  include `CraftElementBase.CanStartCraft`/`StartCraft` (the actual
  can-I-craft check and the actual item consumption on craft start),
  `UIBaseCraftWindowData`, `UIBaseCraftSelectionWindowData`,
  `UIBaseCraftWidgetData`, `UITooltipNeedsItemWidgetData`, and more. There is
  exactly one inventory-sourcing method to extend, not several UI-specific
  ones.
  ```csharp
  public virtual MultiInventory GetCraftableMultiInventory(bool excludeWorkerInventory = false)
  {
      MultiInventory multiInventory = new MultiInventory();
      if (!excludeWorkerInventory && this.Worker != null)
          multiInventory.Add(this.Worker.WorkerInventory);
      if (this.WorldZoneData != null)
      {
          multiInventory.Add(new MultiInventory(this.WorldZoneData, null, false));
          if (!excludeWorkerInventory && !(this.Worker is ZombieWgoData))
              multiInventory.Add(this.CraftInventory);
          PlayerData playerData = MainGame.PlayerData;
          if (!excludeWorkerInventory && playerData.CurrentWorldZoneData == this.WorldZoneData
              && this.Worker is PlayerController != MainGame.PlayerController)
              multiInventory.Add(playerData.Inventory);
      }
      else
      {
          multiInventory.Add(new MultiInventory(new List<Inventory> { this.Inventory, this.CraftInventory }));
      }
      return multiInventory;
  }
  ```
  This already: pools every eligible container in the desk's zone (via the
  same `MultiInventory(WorldZoneData, ...)` constructor chests use), adds
  the desk's own craft-input buffer, adds the assigned worker's inventory
  (a zombie worker's own carried items, or the player's, when one is
  assigned to the desk), and separately folds in the *interacting* player's
  own inventory when they're not already counted as the worker. A desk with
  no `WorldZoneData` (edge case, not yet chased down - a desk placed outside
  any zone?) falls back to just its own `Inventory` + `CraftInventory`.
  `ZombieWgoData` and `ConveyorWgoData` override this method for their own
  variants; not yet read in detail, flagged for when zombie-specific
  behavior is implemented (see exclusion toggle below).
- Building mode pools the same way but simpler:
  `BuildManager.cs:58: new MultiInventory(MainGame.PlayerController.PlayerData, true)`
  and `UIBuildingWindow.cs:28` / `UITownBuildingWindow.cs:26` do the
  equivalent - the `MultiInventory(PlayerData, bool addCurrentPlayerWorldZone
  = true)` constructor (`MultiInventory.cs`, confirmed earlier) pools the
  player's own inventory plus every eligible container in their *current*
  zone.
- `ChestInteractionHandler` (confirmed previously) uses the lower-level
  `MultiInventory(WorldZoneData, WgoData excludeWgoData, bool
  includePlayerInventory)` constructor directly, same underlying pool.

So: chests, craft desks, the Builder desk and town building all already draw
from "every eligible container in the same zone" today, in vanilla, with no
mod installed. GK1's core sentence - "the player can draw from every
eligible container/inventory in the same world zone... not just what's in
their own inventory or the one chest they're standing at" - is **already
true in GK2**. There is nothing to build for the base mechanic itself.

### What's actually left to implement for Tier 1

All of it is refinement/restriction on the existing pool, not new pooling:

1. **Exclusion rules (wells, zombie mill, quarry)** - vanilla's
   `MultiInventory(WorldZoneData, ...)` constructor doesn't gate on zone
   type at all; every zone's eligible WGOs go in. Blocked on a real gap:
   `WorldZoneDef` (`Assembly-CSharp/WorldZoneDef.cs`) has no `type`/`tag`
   field to distinguish a well/zombie-mill/quarry zone from any other - it's
   `builderId`, quality-display fields, and enter/exit expression lists,
   nothing categorical. Grepping the code for `"well"`/`"quarry"`/
   `"zombie_mill"` string literals found nothing - these zone ids live only
   in data (balance JSON), which isn't in this code-only decomp. **Next
   concrete step for this sub-feature**: get the real zone ids, either from
   the `DataDumper` mod already in this solution (if it dumps
   `WorldZoneDef`/`WorldZoneData` balance entries) or by finding the game's
   own balance data files directly. Once the ids are known, filtering is
   straightforward: wrap the pool construction and drop entries whose
   `WgoData.WorldZoneData.Definition.id` matches an excluded id, gated per
   config toggle.
2. **Zombie access toggle** - zombies already get pool access today, for
   free: `WgoData.GetCraftableMultiInventory` adds `this.Worker.WorkerInventory`
   whenever a worker (including a `ZombieWgoData`) is assigned, and the
   zone-wide container pool applies regardless of who's working the desk.
   `AllowZombiesAccessToSharedInventory` (off = restrict, since vanilla
   defaults to "on") means suppressing part of vanilla behavior, not adding
   it - likely a Harmony postfix on `GetCraftableMultiInventory` (and
   whatever `ZombieWgoData`/`ConveyorWgoData` override with, not yet read)
   that strips zone-container entries from the result when the worker is a
   `ZombieWgoData` and the toggle is off. Needs those two overrides read
   before implementing, to avoid missing a second code path.
3. **Sort by distance from crafter** - confirmed gap: `MultiInventory.Sort()`
   (private) only orders by `FuelContainerSerializedItemProperty` presence,
   never by distance, and `Inventory` (`Assembly-CSharp/Inventory.cs`,
   confirmed by full read) carries no owner/position back-reference - there
   is no way to ask an `Inventory` "where are you". The distance has to be
   computed at the point of use, where the crafter's position is available.
   Concrete plan: a Harmony postfix on `WgoData.GetCraftableMultiInventory`
   (and the `MultiInventory(PlayerData, bool)` constructor for
   building/town-building), which re-scans the owning `WorldZoneData`'s
   `wgoDataList`, builds a one-shot `Dictionary<Inventory, Vector3>` from
   each `WgoData.Inventory` to that `WgoData.Position` (confirmed public
   `Vector3 Position` on `WgoData`), and re-sorts `multiInventory.
   inventoryList` by distance from the desk's own `Position`/the player's
   position. `inventoryList` is a public field, so no reflection needed to
   reorder it post-construction.
4. **"Show only personal inventory at vendor" override** - not investigated;
   need to find the vendor/trade window and whether it already builds a
   `MultiInventory` or just reads `PlayerData.Inventory` directly (if the
   latter, there's nothing to override - this toggle may be a no-op in GK2,
   worth confirming before promising it in the README).
5. **Wilderness containers** (configurable containers reachable outside any
   zone) - genuinely new pooling, not a restriction. Every existing pool
   constructor is zone-keyed (`WorldZoneData` or "player's current zone");
   a container the player has no zone for is invisible to all of them. This
   needs its own pass: a config-driven list of WGO ids/positions to always
   fold into the pool regardless of zone, appended after the vanilla
   construction. Not started.

### Confirmed: no invalidation hook needed

`MultiInventory` is always constructed fresh, on demand, at the point of use
(chest open, craft-window open, build-mode enable) rather than cached
anywhere persistent. There's no GK1-style pool object that can go stale, so
none of the restrictions above need a `GameSave.GlobalEventsCheck`-style
invalidation event - a Harmony patch on the handful of construction points
identified above (`GetCraftableMultiInventory`, the `MultiInventory`
constructors themselves, or `ChestInteractionHandler.Interact`) is
sufficient and self-refreshing on every reopen.

### Implementation (`SharedInventoryPool.cs`) - shipped

Three Harmony patches, all postfixes:

1. **`MultiInventory(WorldZoneData, WgoData, bool)` constructor.** This one
   constructor is called directly by `ChestInteractionHandler`, and
   internally by both `WgoData.GetCraftableMultiInventory` and the
   `MultiInventory(PlayerData, bool)` constructor - patching it once covers
   exclusion (`SharedInventory` master toggle, well/quarry) for chests,
   craft desks and building in a single place, rather than patching three
   call sites separately as originally planned. For chests specifically
   (the only confirmed caller that passes a non-null `excludeWgoData` - the
   chest itself), this same patch also does the distance sort, since
   nothing is added to a chest's pool after this constructor returns.
2. **`WgoData.GetCraftableMultiInventory` postfix.** Craft desks call
   `MultiInventory.Add()` several times after the constructor above runs
   (worker inventory, craft buffer, player inventory), and `Add()` always
   re-runs the vanilla `Sort()` (fuel-container priority), which would
   silently undo a distance sort applied inside the constructor patch. This
   postfix re-sorts by distance from the desk's own position after all
   those `Add()` calls are done, and also implements the zombie-access
   toggle (stripping the zone's pooled containers entirely when a
   `ZombieWgoData` is the worker and the toggle is off - zombies already get
   pool access for free in vanilla, so this toggle only ever removes,
   nothing to add).
   **Known gap**: `ZombieWgoData` and `ConveyorWgoData` each override this
   method with their own implementation; Harmony patches the specific
   `MethodInfo` it's given, not every virtual dispatch target, so this patch
   does not reach either override. Zombie-specific and conveyor-belt
   crafting are unrestricted/unsorted by this mod for now, not broken -
   flagged in a code comment rather than silently missed.
3. **`MultiInventory(PlayerData, bool)` constructor postfix.** Same
   Add()-re-sorts-nothing problem as craft desks doesn't actually apply
   here (this constructor only does `Insert(0, playerData.inventory)` after
   the zone constructor, and `Insert` doesn't call `Sort()`) but the
   distance sort still needs to run *after* that insert to place the
   player's own inventory correctly (distance 0, first) alongside the
   zone's pooled containers - covers `BuildManager`, `UIBuildingWindow` and
   `UITownBuildingWindow`, all of which use this constructor.

Distance sort's reference-position handling: an entry the code can't map
back to a `WgoData` (a worker's carried inventory, a desk's own craft
buffer, the interacting player's own inventory) sorts as distance 0 - i.e.
first, ahead of every pooled zone container. That's a deliberate default,
not a fallback of convenience: personal/immediate storage genuinely is
"closer" than anything pooled from elsewhere in the zone, matching GK1's
"closest first" intent even without a literal position for these entries.

### What's not covered yet

- **Zombie mill exclusion** - no known zone id (see above); the config
  toggle GK1 has for this was deliberately not added, rather than adding a
  toggle that would silently do nothing.
- **Wilderness containers** - genuinely new pooling (item 5 above), not
  started.
- **Vendor "personal inventory only" override** - not investigated (item 4
  above); the vendor/trade window hasn't been checked for whether it pools
  at all.
- **ZombieWgoData/ConveyorWgoData craft variants** - see the known gap in
  `GetCraftableMultiInventory`'s patch above.
- No in-game testing yet - same caveat as Phase 1, this environment has no
  GK2 install or `dotnet`/`msbuild` to compile against, so this is grounded
  in decomp reading, not a verified build.

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
