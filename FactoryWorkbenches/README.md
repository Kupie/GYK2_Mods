# Factory Workbenches (GK2)

Two things for the underground factory:

1. The conveyor zone's builder desk (`builder_conveyor`, fixed, always on) can build the regular
   **Zombie Supplier Station** (`zombie_supplier_station`, 4 wooden_plank +
   4 nails_bronze, same `zombie_supplier_station_p` BuildingDef the other
   desks use).
2. The factory workbenches (assembly bench t1/t2/t3, furnace t1/t2, kitchen
   t1/t2) behave like normal workbenches: same building, same recipes, but a
   zombie put on the bench is an ordinary **Crafter** that posts
   `DeliveryOrder` / `PickupOrder`, and the station's caretaker brings the
   materials and hauls the outputs using the zone's storage, including the
   conveyor chests.

`zombie_conveyor_transporter_station` (the "zombie supply porter",
`ZombieType.ConveyorTransporter`) is a different system and is not touched.

## Save safety

- No BuildingDef, WGODef, CraftDef or ItemDef is added, and nothing is
  appended to any GameBalance list.
- No balance data is modified at all (not even `CraftDef.isConveyorCraft`):
  every change is a run-time decision inside a patch.
- No new serialized field or type. A save made with the mod only contains
  vanilla ids and vanilla types (`ZombieType.Crafter`, plain `CraftElement`,
  `DeliveryOrder`, `PickupOrder`, `zombie_supplier_station` objects).
- `WGODef.conveyorType` is never changed, so `GameBalance.conveyorWgosCache`
  still makes the benches `ConveyorWgoData` with a `ConveyorWorkbenchComponent`.

## Config

`BepInEx/config/kupie.gk2.factoryworkbenches.cfg`

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| General | Enabled | true | Master switch. Off: every patch passes straight through. |
| General | Add Normal Chests to building | true | Offer the simple chest, chest and large chest (`chest_rough_place_p`, `chest_place_p`, `chest_good_place_p`) on the conveyor desk. |
| Workbenches | Converted Bench IDs | the seven benches below | Comma separated wgo ids. |
| Workbenches | Belt Inputs Allowed | true | Whether items on input belts still feed converted benches (and cancel the caretaker deliveries they make redundant). Outputs are not affected, see below. |
| Workbenches | Belt First Delay Seconds | 5 | Only for Belt First: how long an output waits for the belt before a supplier may take it. Restarts each time the belt moves one of the bench's outputs. 1 to 600. |
| Workbenches | Output Preference | BeltFirst | `BeltFirst`, `BeltOnly` or `NoBelts` (outputs never go on belts). Applies whatever Belt Inputs Allowed is set to, see below. |
| Workbenches | LiftSingleRecipeQueueLimit | true | Lets converted benches queue several different recipes. |
| Maintenance | Debug Logging | false | Verbose log lines, for bug reports. |
| Maintenance | PrepareForUninstall | false | One shot clean-up before removing the mod, see below. |

Every `ConveyorElementType.Workbench` wgo in the game data (`wgoDefs.json`):

| Wgo id | In default list | Note |
| --- | --- | --- |
| conveyor_assemblybench_t1 / t2 / t3 | yes | |
| conveyor_furnace_t1 / t2 | yes | |
| conveyor_kitchen_t1 / t2 | yes | |
| conveyor_bioreactor | no | An auto crafter that belts already serve. Never touched by this mod; listing it is ignored with a warning. |
| conveyor_woodworkbench | no | Has no crafts and no BuildingDef in the game data. |

## Patches

No prefixes, no skipping prefixes.

| Target | Type | Why |
| --- | --- | --- |
| `BuildingDef.GetBuildingsInBuilder(Wgo)` | Postfix | Appends `BuildData` for the station def when the desk id matches, with the same filter as the vanilla loop (mode not None/Remove, unlocked, not locked), no duplicates. |
| `WgoData.CraftableType` (getter) | Postfix | Reports `Regular` for converted benches. Every reader (below) then takes the normal workbench path. |
| `ConveyorWgoData.GetCraftableMultiInventory(bool)` | Postfix (+ reverse-patch stub of `WgoData.GetCraftableMultiInventory`) | `ConveyorWgoData` overrides this with "own inventories + fuel containers". Converted benches get the normal result (worker inventory + every OpenInMultiInventory container in the zone) so storage counts like on a normal bench. |
| `CraftDefExtensions.CanActuallyStartCraft(CraftDef, WgoData)` | Postfix | Vanilla skips the needItems check on factory benches. The postfix redoes the normal branch when the vanilla answer was true. |
| `CraftInteractionHandler.OnCraftPressed` | Transpiler | The "only one recipe id in the queue" rule is an early return in the middle of the method, before the element is added. A postfix cannot undo a return. The transpiler replaces the single `Definition.conveyorType` read with a call to `QueueRule.TypeFor(WgoData)`. If the IL shape ever changes it does nothing and logs a warning. |
| `UICraftWindow.OnCraftStartPressed` | Transpiler | `isConveyorCraft` decides which element class is constructed, in the middle of the method, and the element is only ever passed to a callback. The transpiler replaces the single `CraftDef.isConveyorCraft` read with `ConveyorCraftRule.IsConveyorCraft(def, window)`. This is what avoids mutating `CraftDef`, so no `GameBalance.InitCache` patch is needed. |
| `ZombieDeliveryIndication.IsCraftStalledWithoutCaretaker(WgoData)` | Postfix | Vanilla returns false for every `isConveyorCraft` recipe, so the "no caretaker in this zone" icon never shows on converted benches. |
| `CanGiveItem(ConveyorComponent)` on every belt element class (cell, splitter, underground cell, station cell, chest, chest out, pallet) | Postfix | Belt Inputs Allowed = false: forces "no" when the asking component is a converted bench. |
| `ConveyorWorkbenchComponent.DoJobIn()` | Postfix | Inputs allowed: after vanilla pulled something from a belt, cancel the now redundant unclaimed deliveries and let the Crafter re-evaluate. |
| `ConveyorWorkbenchComponent.DoJobOut()` | Postfix | Vanilla only acts on `WaitingForOutputDrop`, so the postfix puts Crafter outputs on out belts and settles the `PickupOrder`. |
| `WorldZoneData.GetOrderForCaretaker(Item)` | Postfix | Keeps caretakers off pickup orders the belt is about to take. |
| `ZombieSystemData.ResumeCrafterWorkAfterLoad()` | Postfix | Only runs the opt-in PrepareForUninstall step, after the vanilla step. |

Not patched, on purpose:

- Placement and removal of the station (see below).
- Everything that decides which component or WgoData class is created.

### Every conveyorType / Workbench / CraftableType reader, and the decision

| Where | Decision |
| --- | --- |
| `WgoData.CraftableType` getter | Patched (postfix). |
| `CraftInteractionHandler.Interact` (AttachToCraftWgoData vs AttachToConveyorCraftWgoData) | Follows the getter, so a converted bench makes a Crafter. |
| `CraftComponent.ShouldRegisterInCraftSystem`, `TryDropConveyorWorkbenchCraftInventory`, `AddDestroyCraft`, `RemoveDestroyCraft`, `RemoveFromQueue`, `UpdateQueue` | Follow the getter. |
| `PlayerWorkComponent.CanWorkOn` | Follows the getter. Side effect: the player can hand-craft on a converted bench that has no zombie, like on any bench. |
| `ICraftable.CraftableType` | Interface implemented by `WgoData`, same getter. |
| `CraftDefExtensions` (line 44) | Patched (postfix). |
| `CraftInteractionHandler` (line 315) | Patched (transpiler), toggle. |
| `CraftInteractionHandler.IsConveyorAutoCrafter` (six call sites) | Unchanged: only true for the bioreactor, which this mod leaves alone. |
| `UICraftWindow` (line 167) and `ZombieDeliveryIndication` (line 35), the `isConveyorCraft` readers | Patched. |
| `GameBalance.CreateConveyorCache`, `GameSceneData.AddWgoData`, `ConveyorWgoData` component creation | Untouched (constraint). |
| Belt build connectors, `ConveyorBuildPointer`, `ConveyorCell/Splitter/StationCell/UndergroundComponent` (`giver.conveyorType`) | Unchanged. They decide how belts connect and animate; benches keep connecting to belts. |
| `Wgo.OnConveyorObjectRemoved`, `Wgo` power source check | Unchanged. |
| `UICraftsTabWidget`, `UIInfoWidget` background icons | Unchanged. Converted benches keep the factory look. |

## Part A: supplier station on the conveyor desk

- **Build path.** `BuildInteractionHandler` -> `BuildManager.TryEnable` ->
  `FormBuildData` -> `BuildingDef.GetBuildingsInBuilder` (patched) ->
  `UIBuildingWindowData` (groups by `BuildingDef.tab`; an empty tab is
  "tab_building_default", where the conveyor pieces are too) ->
  `BuildManager.EnableBuildMode` -> `BuildController` -> `BuildPointer`
  (mode `Place` gets a `WgoBuildPointer`, `ConveyorPlace` a
  `ConveyorBuildPointer`). Nothing on that path re-checks `buildsIn`: the
  only reader of that field in the whole decompiled game is
  `GameBalance.CreateBuildCache`. One caveat: `BuildManager` builds its
  `OnBuildPressed` / `CanBuild` handlers as compiler-generated closures that are not part of the
  decompiled sources, so those two bodies could not be read. They only receive
  a `BuildData` and the chosen items, so they have no way to reach the desk's
  `buildsIn` anyway.
- **Placement.** The zone comes from `TryGetNearestBuilderWorldZone`: the world
  zone whose `WorldZoneDef.builderId` equals the desk id (`conveyor`, plus the
  `conveyor_dev` zone). The station has `customBuildAreaId = ""` and
  `chooseCustomBuildAreaType = Soft`. In `WgoBuildPointer.UpdateSelectionCellsState`
  the Soft rule with no custom area only needs every cell to overlap *some*
  `BuildArea` (any id) inside the zone and to be free of other objects. The
  conveyor zone is covered by `conveyor_place` build areas (the chests and the
  transporter station use them), so no patch was added. This is the one thing
  that cannot be proven from the decompiled code, because the `BuildArea`
  colliders live in the scene files. If in-game the station shows red on
  `conveyor_place` cells, the fix is a postfix on
  `WgoBuildPointer.UpdateSelectionCellsState` for this one def and desk.
- **Removal.** The Remove tool is added to every tab of every desk by
  `UIBuildingWindowData`. Which objects it can remove comes from
  `GameBalance.removableWgos`, keyed by wgo id, so `zombie_supplier_station_r`
  (whose `buildsIn` lists other desks) applies in any zone. `Wgo.IsBuildRemovable`
  and `RemovePointer` only compare world zones, never desks. The removal craft
  refunds wooden_plank + nails_bronze from the def's `outputItems`. No patch and no new def.
- **Zone registration.** `GameSceneData.AddWgoData` -> `HandleWgoDataAddWorldZone`
  -> `WorldZoneData.TryAddWgoData`, which adds a wgo to the zone whose rect
  contains it. A station built in the conveyor zone is in the `conveyor` zone's
  `wgoDataList`, and its caretaker uses that zone's orders and `MultiInventoryWgoDatas`.

### Everyday chests on the conveyor desk

The desk entries of the simple chest, chest and large chest (20 / 30 / 40 slots) are
the `chest_rough_place_p`, `chest_place_p` and `chest_good_place_p` defs (wgos
`chest_rough_place`, `chest_place`, `chest_good_place`, all with `buildsIn` listing
the other desks but not `builder_conveyor`). Each is a construction site (`hp` 3,
`replaceToWgoOnDie`) that becomes the real chest, the same way
`conveyor_chest_t1_place` becomes a conveyor chest. They are appended by the same
postfix, with the same unlock filter, cost (unchanged) and no new defs. Placement
uses the same Soft, no custom area rule as the station, and removal is keyed by wgo
id (`chest_place_r`, `chest_r`, ...), so no extra patch is needed. The finished chests
are plain wgos with `OpenInMultiInventory`, so they register in the conveyor zone
and count as caretaker and bench storage; they have no belt connectors.

## Part B: converted benches

- **Zombie on the bench.** `CraftInteractionHandler.Interact` now calls
  `AttachToCraftWgoData`, which makes a `ZombieType.Crafter`.
- **Recipes and element class.** New queue entries are plain `CraftElement`s.
  The multi-output one-item-per-cycle `ConveyorCraftElement` is only for
  ConveyorCrafter zombies.
- **Mastery and tools.** `ZombieWgoData.CrafterTryPlaceOrderForCurrentCraftOrStartIt`
  returns quietly when `CrafterCanUseTool` or `CrafterIsEnoughMastery` fails
  (mastery is checked against the bench's `talent_orange` and each recipe's
  `talentLock`, 2 to 9 for the factory recipes). The warning is not lost: while
  the craft is queued or waiting, `Wgo` draws a `UICraftHintWidget`, and for a
  zombie worker it shows the missing-tool icon or the not-enough-mastery icon
  (`UICraftHintWidget.UpdateStatusIcon`). Nothing in that path looks at the
  conveyor type.
- **Storage.** The caretaker works on its own `WorldZoneData.MultiInventoryWgoDatas`.
  `conveyor_chest`, `_t1`, `_t2` and `_big_items` all have `OpenInMultiInventory`
  and an empty whitelist (an empty `WhiteListItemFilter` allows everything; the
  blacklist is two intro items and, on three of the four, the `overhead` group).
  So the caretaker takes materials from them and puts outputs into them.
  `ConveyorChestSlotData.slotItemId` is only read by
  `ConveyorChestComponent.TryGetItemIdToGive`, which is the belt-out path. The
  caretaker reads and writes `wgoData.Inventory` directly, so the slot filters
  are bypassed. A chest that belts drain can therefore receive items from the
  caretaker and hand them to a belt. That is not an error, but expect outputs
  to travel down that belt.
- **Belt Inputs Allowed and Output Preference.** Vanilla `ConveyorWorkbenchComponent` is active: `DoJobIn` pulls, through
  `GetItemFromConveyor`, one item at a time from an adjacent belt element when
  the first queued craft is not started, has `CraftStatus.NotEnoughResources`
  and the belt's `CanGiveItem(bench)` is true. `DoJobOut` only pushes to a belt
  while the craft status is `WaitingForOutputDrop`, which only a ConveyorCrafter
  zombie causes, and the bench's own `CanGiveItem` is always false.
  - **Belt Inputs Allowed = false**: the belt classes answer "no" to a converted
    bench, so belts never feed it and the caretaker does all the delivering. The
    output rule below still applies.
  - **Belt Inputs Allowed = true** (default) and **Output Preference** (any value,
    independent of the input setting): the bench stays on the regular Crafter
    state machine (never `WaitingForOutputDrop`, never `ConveyorCraftElement`)
    and the belts are added around it.
    - **Inputs.** Vanilla `DoJobIn` is untouched. It pulls into the bench craft
      inventory, which for a Crafter is also its worker inventory, so the
      Crafter sees belt-fed items when it decides what to order. A postfix on
      `DoJobIn` runs when it moved something and cancels the Crafter's
      `DeliveryOrder`s that the inventory now fully covers (pending output
      items of the same id do not count as available), but only orders with no
      executor. A claimed order is left to finish: cancelling one mid-trip can
      leave the caretaker walking to its station with a partly picked-up stack
      (`CaretakerOnOrderRemoved` re-routes it to a chest from only some of its
      states), and the worst case of letting it finish is one spare set of
      materials in the bench that the next craft uses. Removing an unclaimed
      order is safe for caretakers, since `CaretakerOnOrderRemoved` only acts on
      the order a caretaker is executing. After a cancellation the Crafter is
      told to re-evaluate (`CrafterTryPlaceOrderForCurrentCraftOrStartIt`, which
      returns at once while any order is left), because the vanilla triggers
      only fire when an order is executed, not when it is cancelled.
    - **Outputs.** A postfix on `DoJobOut` takes the Crafter's oldest unclaimed
      `PickupOrder` whose item is in the craft inventory, and for each empty
      out cell puts exactly that item on the belt (same
      `ConveyorMovableItemData` / connector direction / cell inventory steps as
      `PutItemToConveyor`), lowers the order's count, and when the order reaches
      zero does what a caretaker pickup does: finish the craft if it is still
      `WaitingForWorkerPickUp`, drop stored tech points, remove the order, call
      `CrafterOnOrderExecuted`. It never pushes `Inventory[0]`. It also runs for
      later orders of a multi-output craft after the craft has finished (the
      first completed pickup finishes it, like vanilla).
    - **No double handling.** The belt and the caretaker only touch orders whose
      `ExecutorUniqueId` is empty, and a caretaker sets it when it takes an order.
      Everything runs on the main thread, so there is no gap between check and claim.
    - **Output Preference** (only for benches that have an output belt connected
      while the conveyor system is running and powered; empty orders, big items
      and `overhead` items are never belt candidates): `BeltFirst` hides such a
      `PickupOrder` from caretakers for `Belt First Delay Seconds` (default 5, game time, restarted whenever the belt moves an output of that bench) (a postfix on
      `WorldZoneData.GetOrderForCaretaker` repeats its pass without the hidden
      order), after which a caretaker may take it if the belt has not; `NoBelts`
      never puts outputs on belts; `BeltOnly` hides them from caretakers for
      good, so a jammed belt or full chests stall the bench until fixed.
- **Power.** Regular crafting no longer runs through the conveyor system's zombie
  activity list, so converted benches do not need conveyor power to craft. Belt input and
  output still run on the conveyor tick, so they need power like any belt.

## Part C: existing saves and removing the mod

- **No migration.** Nothing is rewritten on load.
- **Zombies already on a bench.** They are `ZombieType.ConveyorCrafter`. A
  bench that holds one answers "not converted" (`Factory.IsRegularMode` looks at
  the worker), so it keeps the exact vanilla behaviour, including belt IO
  whatever the belt settings say. When the player picks the zombie up and puts it back,
  `Interact` sees a converted bench and makes it a Crafter.
- **Queued `ConveyorCraftElement`s.** Left as they are. If they are still
  queued when a Crafter zombie is put on, the Crafter path handles them (the
  element is self-consistent: it consumes materials once and runs one cycle per
  output). Cancelling uses the normal `RemoveFromQueue`. One difference: the
  normal Crafter path calls `OnCraftEnd` after every cycle instead of once at the
  end, so per-craft end expressions (for example tech points) can fire once per
  output for that leftover craft only.
- **Removing the mod, static trace of vanilla code.**
  - `ZombieType.Crafter` on a bench whose vanilla `CraftableType` is
    `ConveyorWorkbench`: `ZombieWgoData.PrepareForGame` handles `Crafter`
    without looking at the bench type, and `TryResumeCrafterWorkAfterLoad` restarts
    it. The only conveyor-specific difference is that the bench is not put in
    `CraftSystem.activeCrafts`; `ConveyorSystem` calls `PreFinishUpdate` for every
    workbench element (before its power check), which is what finishes crafts.
  - Plain `CraftElement`s in a conveyor bench queue: the class is vanilla and
    its statuses do not depend on the bench type.
  - `DeliveryOrder` / `PickupOrder` in the conveyor `WorldZoneData`: generic
    serialized orders, served by any caretaker or by the player with the
    bench's second interaction.
  - `zombie_supplier_station` in the conveyor zone: a normal wgo with a normal
    removal def.
  - Nothing found that throws or soft-locks. Vanilla loses some UI polish there
    (no "no caretaker" icon for `isConveyorCraft` recipes) and, if the player later queues
    a recipe from the vanilla craft window on a bench that still has a Crafter
    on it, a `ConveyorCraftElement` ends up with a Crafter, the
    combination this mod avoids.
    This is a reading of the decompiled code, not a game test.
- **PrepareForUninstall (opt-in).** Set to true, load the save, and every Crafter
  zombie on a factory bench is taken off it and the orders aimed at it are
  cleared. It uses the same steps as a bench deconstruction
  (`CraftComponent.AddDestroyCraft`): drop the zombie body next to the bench,
  `UnAttachFromWgoData` (which clears the zombie's orders and drops what it was
  carrying), `PutZombieFromGameSceneToStore`. Vanilla ConveyorCrafters are left
  alone. The option switches itself off afterwards.

## Known edge cases

- The placement claim above needs one in-game check.
- `CraftComponent.UpdateQueue` registers a bench in `CraftSystem.activeCrafts` and
  `ConveyorSystem` also calls `PreFinishUpdate` on it, so on a converted bench
  the short "finish hold" delay before a craft completes runs twice as fast.
  Cosmetic.
- Vanilla builds the conveyor desk list from `buildDefsInBuilder`; the station
  shows up in the default tab of the conveyor desk, next to belts and chests.
- Inlining: the `CraftableType` getter is 28 bytes of IL. The patch is applied
  before game code runs, as with the other mods here.
- The mini station has a WGODef (`zombie_supplier_station_mini`) but no
  BuildingDef in the dumped data, so there is nothing to offer and no option for it.

Built against `Kupie/gyk2_decomp`.
