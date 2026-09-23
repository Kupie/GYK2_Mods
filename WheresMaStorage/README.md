# WheresMaStorage (GK2)

A phased port of the GK1 mod of the same name
([p1xel8ted/Graveyard-Keeper-Mods](https://github.com/p1xel8ted/Graveyard-Keeper-Mods/tree/main/src/WheresMaStorage))
to Graveyard Keeper 2. GK2's world-object and inventory systems (the
`Wgo`/`WgoData`/`WGODef` split, `MultiInventory`, `Inventory`/`Item`) are
architecturally different from GK1's, so this isn't a transliterated Harmony
patch set - it's a from-scratch reimplementation of the GK1 mod's behavior
against GK2-native APIs, built in phases. See `TASKS.md` for the full plan,
what's shipped, and the decomp research behind each phase.

## What's implemented so far (Phase 1: capacity + stacking)

### Extra inventory capacity

- `Player Inventory Bonus Slots` - extra slots on top of the player's base
  inventory and tool belt (vanilla defaults: 20 and 14).
- `Container Inventory Bonus Slots` - extra slots on top of every placed
  container's (chest, etc.) vanilla base size, applied to every container
  already in the save on load and to any container placed afterward.

Both only ever add to the *vanilla* base size - editing the value, or
triggering it again on a game reload, recovers the real base size first and
never compounds a bonus on top of an earlier one.

If a container currently holds more items than a *lowered* bonus would
allow, that container is left alone (not shrunk) and a warning is logged.
There's no in-game prompt for this yet - see `TASKS.md`'s shrink-safety
section.

### Stack size

- `Modify Stack Size` - master toggle.
- `Stack Size For Stackables` - target stack size (default 999) applied to
  every enabled category below. Only ever raises a stack size.
- Always applies to anything already stackable in vanilla (general
  resources/consumables).
- Optional, per-category toggles to make normally single-stack items
  stackable: tools (on by default), weapons (on), equipment (on), prayer/
  preach items (on), grave/autopsy items - organs, bones, skull, embalm
  (off by default, matching the GK1 mod's default).

Pen/paper/ink and chisel stacking (two more GK1 toggles) aren't implemented
- see `TASKS.md` for why.

## Why `Item.InventorySize` needs the public setter, not a getter patch

Same reasoning as this solution's `MoreInventorySlots` mod: several of
`Item`'s own capacity checks read the private backing field directly rather
than going through the `InventorySize` property, so a Harmony postfix on the
getter alone would only change what the UI displays, not how much you can
actually hold. This mod writes the field via the setter instead.

## What's not implemented yet

The mod's actual headline feature - a shared inventory pool while crafting/
building or interacting with containers, drawing from every eligible
container in the same world zone - is not in this build. GK2 already has
most of the underlying mechanism (`MultiInventory`, already wired into
`ChestInteractionHandler`); extending it to cover crafting desks, exclusion
rules and distance sorting is the next phase. QoL/UI toggles and the
gameplay-convenience tier (hand tool destroy, drop collection, loot magnet)
are also not started. See `TASKS.md` for the full breakdown.

## Config

`BepInEx/config/kupie.gk2.wheresmastorage.cfg` after the first run, under
`Capacity` and `Item Stacking`.

## Compatibility

Player/tool-belt capacity is the same technique - and touches the same
fields - as this solution's `MoreInventorySlots` mod. Running both at once
means whichever mod's `OnGameStarted`/config-change handler runs last wins
for those two values; they aren't additive. Not an issue if you only run
one of the two for player/tool-belt sizing.
