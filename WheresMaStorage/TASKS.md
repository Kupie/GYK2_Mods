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
- [x] Phase 3 - Tier 4 (drops, loot magnet, hand-tool destroy): **shipped,
      partial** - hand tool destroy only (`HandToolDestroy.cs`). Drop
      collection still needs two more decomp reads (`DropView.cs`,
      `CollectDrop`/`CollectResDrop`) before it can be implemented safely.
      Loot magnet still has one open question (the real Collider type) that
      likely needs an in-game check, not just decomp.
- [x] Phase 4 - Tier 3 (QoL/UI toggles): **shipped, partial** - used-space
      and world-zone-name in titles (`InventoryTitles.cs`), and hiding items
      the open window won't accept from the player's inventory/bag panels
      (`HideUnavailableItems.cs`). Dimming needs more research before it's
      safe, empty-widget-row hiding has no GK2 equivalent, and section
      gaps/5-column bag layout/filtered-picker slot hiding are blocked on
      Inspector/prefab data this environment can't inspect - see below.
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

## Phase 3 - Tier 4 (gameplay conveniences) - researched, not built

### Drop system - confirmed structure

The predicted `DropSystem`/`DropData`/`DropView*` cluster exists exactly as
guessed (`Assembly-CSharp/DropSystem.cs`, `DropData.cs`, `DropView.cs`, plus
`DropSearcher.cs`, `DropCollector.cs`, `GameSceneData.cs`'s drop lists):

- Every ground drop in a loaded/unloaded scene lives in one of two public
  lists on `GameSceneData`: `droppedItems` (spawned, has a `DropView` if the
  scene is loaded) and `queuedDrops` (scene not loaded yet, no view). Both
  are `List<DropData>`. `WorldData.gameSceneDataList` (confirmed earlier,
  Phase 1) reaches every scene's lists regardless of load state - so an
  `OnGameStarted` pass can see every drop in the save, loaded or not, the
  same way `CapacityBonus.ApplyAllContainers` already walks every container.
- `DropData.DropType` (`DropType` enum, confirmed values `Item` and
  `WgoData`) is the key distinguishing field. A drop becomes `WgoData` type
  when `item.Definition.isLinkedToWgo` is true at construction - these are
  drops that resolve to a placed world object rather than a simple pickup
  (`DropCollector.CanCollectDrop` explicitly excludes `DropType.WgoData` and
  `ItemSize.Big` from ordinary pickup, confirmed in `DropCollector.cs`).
  This is the closest GK2 equivalent to GK1's "skip crates" - a crate/
  scripted-spawn drop is very plausibly `DropType.WgoData`, and excluding
  that type from auto-collect is both consistent with what vanilla already
  treats as "not a normal pickup" and low-risk.
  **Not confirmed**: no `ItemDef`/`DropData` property maps to "quest item"
  specifically. `DropData.CanNotBeAutoDestroyed` /
  `NeverAutoDestroyDropSerializedItemProperty` exist, but they gate the
  despawn timer (`DropSystem.CustomUpdate`'s corpse-decay-style cleanup),
  not collection eligibility - reusing that flag to also skip auto-collect
  would conflate two different vanilla concepts and could skip drops GK1
  never intended to skip (or vice versa). No quest-item marker was found
  anywhere in the decomp. Flagging rather than guessing: auto-collect should
  key off `DropType != WgoData` only, until/unless a real quest-flag
  equivalent turns up.
- Existing vanilla mechanism to build on: `DropSystem.CollectAllGameResDropsToPlayer(float duration)`
  already does a full-save sweep and animates matching drops toward the
  player, but it's filtered to `DropData.IsResDrop` only (`Item.id.StartsWith("game_res_")`
  - the game's abstract currency-ish resources, not general items). GK1's
  "collect eligible ground drops" is broader than that - ordinary item
  pickups too - so this method is a model to follow (same scene-list
  iteration, same `MainGame.PlayerData.CollectResDrop`-style consumption)
  rather than something to call directly with a widened filter; it's typed
  specifically around `IsResDrop`, not driven by a generic predicate.
- `MainGame.PlayerData.CollectDrop(DropView)` (used by `DropCollector`) and
  `.CollectResDrop(DropData)` (used by `DropSystem`) are the two vanilla
  entry points that add a drop's item to the player's inventory and clean
  it up - collect-on-load should call one of these rather than
  hand-rolling inventory-add + `RemoveDrop`, to inherit whatever bookkeeping
  they already do (both not yet read line-by-line).
- "Relocate near the Keeper's house" (the `DropHandlingMode.MoveNearKeepersHouse`
  alternative already stubbed as an enum in `Plugin.cs`) has no obvious
  single-call vanilla equivalent. `DropData.Position` has a public setter,
  but a spawned `DropView`'s world transform is a separate, unconfirmed
  question (does moving `DropData.Position` reposition an already-spawned
  `DropView`, or only affect newly spawned ones?) - not read yet. The
  simpler, lower-risk implementation is likely remove-and-redrop
  (`GameSceneData.RemoveDrop` + `DropSystem.DropItemAsDropView`/`DropItem`
  at the new position) rather than mutating a live `DropData.Position` and
  hoping the view follows - needs `DropView.cs` read in full before
  deciding, not done yet.
- "Keeper's house" as a fixed point: no id/location confirmed yet - needs
  the actual player-house `WorldZoneData`/spawn-point identifier, likely
  from the same `worldZoneDefs.json` dump used for Phase 2's well/mine ids
  (`home` is a plausible zone id, seen in that dump's id list - not yet
  cross-checked against what the game actually calls the Keeper's house).

### Player-only pickup range ("loot magnet") - confirmed structure, one open question

`DropSearcher` (`Assembly-CSharp/DropSearcher.cs`) is the "magnet" - it's a
`MonoBehaviour` living on a child transform of the player only (confirmed:
its one non-player reference is `SpitProjectile.cs` checking whether a
projectile hit the searcher's collider, not a second `DropSearcher`
instance elsewhere; `DropSystem.GetPlayerDropCollectorTransform` is the only
place one is looked up, via `playerController.GetComponentInChildren<DropSearcher>()`).
So there is no NPC/zombie sharing to worry about - confirms the GK1 feature
description ("player only") is already naturally true of this component,
nothing to gate.

Mechanism: `OnTriggerEnter`/`OnTriggerStay` fire from a Unity trigger
**Collider** on the same GameObject (not visible in code - collider
type/radius is set on the prefab/scene object, not a C# field), and for
each eligible drop inside it, `dropView.TryMoveToCollector(collectorObjectTransform)`
pulls the drop toward the player (the actual pickup-into-inventory step is
a separate, smaller-radius `DropCollector` on another object, confirmed by
its similarly-shaped but distinct `OnTriggerEnter`/`CollectDrop` flow -
`DropSearcher` only ever moves a drop, never collects it directly).
`DropCollector.CanCollectDrop` blocks `DropType.WgoData` and `ItemSize.Big`
from pickup entirely, independent of range.

**Open, not yet confirmed**: no code-visible float "radius" field exists to
multiply - GK1's `PlayerLootMagnetRange` config (a float, 2-20 units) needs
a Unity-side value to scale, most likely a `SphereCollider.radius` on the
same GameObject as `DropSearcher`, but the exact Collider type isn't
determinable from a code-only decomp. Concrete next step: a Harmony postfix
on `DropSearcher`'s Unity lifecycle method (`Awake`/`Start` - neither is
overridden in the decompiled class, so the vanilla behavior comes entirely
from Inspector-configured components; patching would mean hooking
whichever `MonoBehaviour` method the prefab's collider is guaranteed to
exist by, or more robustly, `MainGame.OnGameStarted` -> find the player's
`DropSearcher` -> `GetComponent<SphereCollider>()` (falling back to
`CapsuleCollider`/`BoxCollider` if that comes back null) -> scale by the
config value. Needs one in-game check this environment can't do (no GK2
install) to confirm the actual Collider type before shipping.

### Hand tool destroy - shipped (`HandToolDestroy.cs`), different shape than GK1

Not what the task description guessed ("vanilla prevents throwing out
*equipped* tools" - implying a per-slot/equipped-state runtime check).
Confirmed instead: destroy eligibility in the inventory context menu
(`PlayerInventoryUIItemOpHandler.OnPlayerInvItemPressed2` ->
`TryDestroyItem`) is gated purely by `ItemDef.CanNotBeDestroyed`, a flat
per-item-definition flag (`this.canNotBeDestroyed.EvaluateBool()`, a
`LazyExpression`) with no equipped/slot-state check anywhere in that method.
So GK2 tools are very likely just data-flagged `canNotBeDestroyed = true` in
their `ItemDef`, the same way any other "can't destroy this" item would be,
rather than GK1's presumably code-level "don't let go of your active tool"
guard.

Implementation: this can't be done the same way as `StackSizeBonus.cs`
(directly overwriting a plain field) because `canNotBeDestroyed` is a
`LazyExpression`, not a plain bool - replacing it with an always-false
expression isn't researched, and wasn't needed. Shipped instead as a
Harmony postfix on the `ItemDef.CanNotBeDestroyed` *property getter*,
forcing `false` when `__instance.isTool` and `AllowHandToolDestroy` is on -
`ItemDef.isTool` alone (no `type` check needed; `StackSizeBonus.cs`'s
tool-category branch uses the same single field). A getter patch works here
(unlike `Item.InventorySize` in Phase 1) because
`PlayerInventoryUIItemOpHandler.TryDestroyItem` - the only confirmed
reader - goes through the property; **not verified against every other spot
in the decomp that might read the private `canNotBeDestroyed` field
directly**, so if some other undiscardable-item check turns out to bypass
the property the same way `InventorySize`'s internal checks did, tools could
still show as destroyable in the UI without actually being destroyable
there. Flagging since this wasn't exhaustively checked, not because a
problem was found.

### Remaining Phase 3 work

- `DropCollection.cs`: an `OnGameStarted` pass over
  `WorldData.gameSceneDataList[*].droppedItems`/`queuedDrops`, filtering out
  `DropType.WgoData`, calling `MainGame.PlayerData.CollectResDrop`-style
  consumption (needs `CollectDrop`/`CollectResDrop` read in full first to
  confirm which is right for a generic item, not just a game-res one) for
  the collect-to-inventory mode; the relocate-near-house mode needs
  `DropView.cs` read first per above, and the Keeper's-house reference point
  confirmed, before it can be implemented safely.
- `LootMagnetRange.cs`: needs one unresolved question (the real Collider
  type on `DropSearcher`'s GameObject) before it can be written with
  confidence rather than guessed at - flagged as the one piece of this phase
  that may need an actual in-game check (or a community/wiki source) rather
  than being resolvable from decomp alone.

## Phase 4 - Tier 3 (QoL/UI) - two shipped (`InventoryTitles.cs`), rest researched

Confirmed the task description's guess: GK2 uses an `InventoryWidgetData`/
`*WidgetData` family (`InventoryHeaderWidgetData`, `BagInventoryWidgetData`,
`MultiInventoryWidgetData`, etc., all under `Assembly-CSharp/`) rather than
one panel class. Went through each of the seven GK1 sub-features against
that structure. Two are worth porting, one needs more research before a
safety verdict, one has no GK2 equivalent at all, and three are blocked by
the same class of problem Phase 3's loot magnet hit: an Inspector/prefab-
configured Unity value with no field visible in a code-only decomp.

### Shipped: show used space in inventory panel titles

Clean and low-risk. `InventoryHeaderWidget.UpdateHeader()` (private,
patchable) is the single method every panel header goes through - player
inventory, tool belt, chests, bags, all of it - and it already has
`this.data.Inventory` in scope, whose `.Data` (an `Item`, confirmed Phase 1)
has both `InventoryFillSize` and `InventorySize`. A Harmony postfix that
appends `" (fill/size)"` to `this.header.text` after vanilla sets it covers
every panel uniformly, no reverse-lookup or per-window-type plumbing needed.

Also confirmed along the way: `LLBase.L(id)` (the localization lookup
`UpdateHeader` runs header text through) falls back to returning `id`
unchanged when it's not a real dictionary key
(`LazyBearTechnology/LLBase.cs:430-434`) - so composing arbitrary text
through `InventoryHeaderWidgetData.CustomHeaderId` instead of a header-text
postfix would also have worked, and won't get silently swallowed or
mistranslated. The postfix approach is still preferred: it works
unconditionally, whereas `CustomHeaderId` only applies when the window
that builds the header data bothers to set it (most don't, they just leave
it null and fall back to `Inventory.ViewId`).

Implemented exactly as planned, in `InventoryTitles.cs`
(`InventoryHeaderWidget_UpdateHeader_Patch`), behind `Show Used Space In
Titles`.

### Shipped: show world zone name in container titles

Same header-text-postfix mechanism, but needs one more piece: `Inventory`
has no owner backreference (confirmed Phase 2), so `UpdateHeader`'s postfix
can't get from "this is the inventory being drawn" to "this is the zone it's
in" on its own. The zone *is* known at the point a chest window actually
opens though - `ChestInteractionHandler`/`UIBaseChestWindowData`'s
constructor has the real `WgoData` (and therefore `WgoData.WorldZoneData`)
right there. Shape: patch `UIBaseChestWindowData`'s constructor to record
`Inventory -> zone name` in a small static map when a chest window opens,
and have the header postfix consult that map (falling back to no zone
suffix when the inventory isn't in it - covers the player's own panel and
anything not chest-backed). More state to manage than the used-space
feature, but nothing in it is unconfirmed or blocked - just more surface
area for a cosmetic feature, which is why it's listed separately rather than
bundled with the first one.

Implemented exactly as planned, in `InventoryTitles.cs`
(`InventoryTitles.ZoneNameByInventory`, populated by
`UIBaseChestWindowData_Ctor_Patch`, consumed by the same header postfix
above), behind `Show World Zone In Titles`. Zone display name uses the same
`"wz_" + zone.id` loc-key convention confirmed in `WorldZoneWidget.cs`
(vanilla's own map zone label).

### Shipped: hide items the open window won't accept, instead of graying them out

A follow-up request, not one of GK1's original seven - "hide invalid
selections" but scoped to the player's own inventory/bag panels rather than
the filtered-picker windows (grave parts, autopsy, etc.) that sub-feature
originally meant. Confirmed the same `CustomItemsAvailableCondition`/
`CustomItemsNotShowCondition` split noted under "disable dimming" below
drives this too, and it's a much cleaner target than dimming:

- `InventoryWidget.Redraw()` (`Assembly-CSharp/InventoryWidget.cs`, the base
  for both the player's main inventory panel and `BagInventoryWidget`, an
  open bag's contents) already computes, per cell,
  `data.CustomItemsAvailableCondition(item)` to decide gray-out
  (`ItemRelatedWidgetState.Disabled`), and separately runs a hide pass that
  `SetActive(false)`s any cell where `data.CustomItemsNotShowCondition(item)`
  is true - two independent predicates, confirmed by reading the method in
  full. Vanilla almost never sets the second one for the player's own panel
  (`CustomItemsNotShowCondition` has a protected setter, only assignable
  through the constructor), so unsellable/won't-fit items always render, just
  grayed.
- Confirmed two real vanilla availability predicates this now hides items
  for: `Trading.cs`'s `PlayerItemsAvailableCondition` (can this item be sold
  to the currently open vendor - `Vendor.CanSellItemToPlayer` is the reverse
  direction, this is `Trading.cs`'s own player-sells-to-vendor check) for the
  vendor window's player-side listing, and
  `PlayerInventoryUIItemOpHandler.PlayerItemsAvailabilityCondition` (`item.Definition.CanBeInsertedInBag(...)`)
  for the player's main panel while a bag is open - exactly the two cases
  named in the request ("not able to be sold or placed in something").
- Implementation (`HideUnavailableItems.cs`): rather than trying to inject a
  predicate into the protected-setter `CustomItemsNotShowCondition`, a
  Harmony postfix on `InventoryWidget.Redraw()` reuses vanilla's own
  `SetActive(false)` mechanism for any cell whose
  `CustomItemsAvailableCondition` already said no - same effect as if
  vanilla's hide pass covered it too, without touching vanilla's actual hide
  predicate or risking cells vanilla intentionally keeps interactive-but-dim
  for some other reason.
- Not covered: `ToolBeltInventoryWidget`, `BodyOrgansInventoryWidget`/
  `BodyPocketInventoryWidget`, and `VendorDealInventoryWidget` each have
  their own separate `Redraw()` override with the same two-predicate shape
  (confirmed present via grep, not individually read) - not patched here.
  If hiding is wanted in those too, each needs its own postfix following the
  same pattern, once its own `uiItemCells`/`data` field names are confirmed.

### Needs more research before a safety verdict: disable inventory-panel dimming

The mechanism exists and was easy to find:
`ItemRelatedWidgetState` (confirmed enum: `Default, Disabled, Inactive,
Selected, NotSet`) is passed per-section when a chest window is built -
`UIBaseChestWindowData`'s constructor (read in full during Phase 2 research)
gives the player's own section `Default`/`Inactive` and the pooled
zone-container section `Disabled`/`Disabled`. `InventoryHeaderWidget` has a
matching active/inactive header style pair
(`IsActiveViewState` -> `headerActiveStyle`/`headerInactiveStyle`).
**Not confirmed, and why this isn't a "worth porting" verdict yet**: whether
`Disabled`/`Inactive` on an item cell is purely a visual dim or also gates
interaction (drag/click-to-move) wasn't checked - the code that actually
*reads* `ItemRelatedWidgetState` when drawing/wiring up an item cell (not
yet located; likely in `UIItemCell` or `InventoryWidgetDataHelper`) needs to
be read before forcing every section to `Default` can be called safe. If
it's purely cosmetic, this is a small, well-targeted patch. If it also
ungates interaction, forcing it off would let players drag out of the
zone-pool section GK2 intentionally made read-only there, silently changing
game logic under a "just a UI toggle" description - worth getting right
before shipping, not worth guessing on.

### No GK2 equivalent found: hide always-empty vanilla widget rows

GK1's stockpile/tavern/soul/warehouse-shop/bag widget rows have zero
matching classes anywhere in the decomp - no `*Stockpile*`, `*Tavern*`,
`*Warehouse*`, or `*Soul*Widget*` file exists. These read as GK1-specific
UI panels (or GK1-specific game systems - the "soul" and "warehouse shop"
mechanics themselves may simply not exist in GK2) rather than GK2 concepts
under different names. Recommend dropping this sub-feature rather than
forcing an approximation - there's nothing concrete to hide.

### Blocked on Inspector/prefab data, same as the loot magnet: three sub-features

None of these have a code-visible field to patch - the relevant value lives
on a Unity layout component configured in the editor, the same class of gap
that blocked Phase 3's loot-magnet Collider radius:

- **Remove vertical gaps between inventory sections** (player panel/vendor
  panel independently) - no `VerticalLayoutGroup`/spacing field found
  anywhere in the `*WidgetData` classes; section spacing is near-certainly
  an Inspector-set `spacing` value on a `VerticalLayoutGroup` component on
  the panel prefab.
- **Force bag-inventory widgets onto a fixed 5-column layout** - no
  `GridLayoutGroup`/`constraintCount`/`columns` field in
  `BagInventoryWidgetData`, `InventoryWidgetData`, or
  `InventoryWidgetDataHelper`; same Inspector-configured-component gap.
- **Hide invalid/inactive item slots in filtered pickers** (grave parts,
  soul healing, autopsy, organ enhancer, rat cell, alchemy) - only shallowly
  checked (confirmed `UIAutopsyWindowData` exists as autopsy's real GK2
  equivalent; the other five weren't individually read). Not blocked in the
  same confirmed way as the other two here, just not researched deeply
  enough yet to have a verdict - lowest priority within an already-lowest-
  priority tier, six separate windows to read for what GK1 itself treats as
  a minor grey-out-vs-hide distinction.

All three would need either an actual GK2 install (inspect the prefab/scene
directly) or a decompiled asset dump beyond what this code-only decomp
provides, the same gap flagged for the loot magnet in Phase 3.

## Conventions

Tabs, no em dashes, comments explain why a hook was chosen (not what the
code does), update README.md to match what ships, flag anything unverified
against the real assembly rather than stating it as confirmed.
