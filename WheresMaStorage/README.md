# WheresMaStorage (GK2)

A phased port of the GK1 mod of the same name
([p1xel8ted/Graveyard-Keeper-Mods](https://github.com/p1xel8ted/Graveyard-Keeper-Mods/tree/main/src/WheresMaStorage))
to Graveyard Keeper 2. GK2's world-object and inventory systems (the
`Wgo`/`WgoData`/`WGODef` split, `MultiInventory`, `Inventory`/`Item`) are
architecturally different from GK1's, so this isn't a transliterated Harmony
patch set - it's a from-scratch reimplementation of the GK1 mod's behavior
against GK2-native APIs, built in phases. See `TASKS.md` for the full plan,
what's shipped, and the decomp research behind each phase.

## What's implemented so far

### QoL/UI toggles (Phase 4, partial)

- `Show Used Space In Titles` - appends `(fill/size)` to every inventory
  panel's title: player, tool belt, chests, bags, all of it. One patch on
  the single method every panel header goes through, so it covers
  everything uniformly.
- `Show World Zone In Titles` - appends the world zone's name to a chest's
  title, using the same `"wz_" + zone id` display-name convention vanilla's
  own map zone label uses.
- `Hide Unavailable Items` - in the player's own inventory/bag panels, an
  item the open window won't accept (can't be sold to the open vendor,
  can't go in the open bag) is hidden entirely instead of just showing
  grayed out. Vanilla already computes per-item availability for both of
  those cases; this reuses the same hide mechanism vanilla itself uses
  elsewhere, just applied to that existing check.

Disabling inventory-panel dimming, removing gaps between sections, forcing
bags onto a 5-column layout, and hiding invalid slots in filtered pickers
aren't implemented yet - see `TASKS.md` for why (one needs more research,
three depend on Unity values not visible in a code-only decompile). Hiding
always-empty stockpile/tavern/soul/warehouse-shop widget rows has no GK2
equivalent at all and isn't planned.

### Gameplay conveniences (Phase 3, partial)

- `Allow Hand Tool Destroy` - let hand tools (axe, shovel, pickaxe, hammer,
  fishing rod) be destroyed from the inventory context menu. Turns out GK2
  doesn't block this with an "is this tool currently equipped" runtime check
  the way GK1 did - it's gated by the same flat per-item
  `ItemDef.CanNotBeDestroyed` flag any other undiscardable item uses, so
  this just forces that flag off for tools when the toggle is on.

Drop collection (auto-collect ground drops on load, or relocate them near
the Keeper's house) and the loot-magnet pickup range aren't implemented yet
- both have an open question that decomp reading alone couldn't resolve
  (see `TASKS.md`).

### Shared inventory pool (Phase 2)

GK2 already pools every eligible container in a zone for chests, craft
desks, the Builder desk and town building - that's vanilla behavior, not
something this mod adds. What this mod adds is control over that existing
pool:

- `Shared Inventory` - master toggle. Off restores vanilla's
  per-container-only behavior everywhere the pool would otherwise apply.
- `Sort By Distance From Crafter` - orders the pool's containers nearest
  first, instead of vanilla's fuel-container-priority order. Entries this
  mod can't place (the desk's own craft buffer, a worker's carried
  inventory, the interacting player's own inventory) sort first, ahead of
  every pooled container - they're always "closer" than anything pooled
  from elsewhere in the zone.
- `Exclude Wells From Shared Inventory` / `Exclude Quarry From Shared
  Inventory` - don't pool a zone's containers when the zone is a well or
  the mine/quarry.
- `Allow Zombies Access To Shared Inventory` - off restricts a zombie
  worker at a craft desk to its own carried inventory instead of the zone's
  pooled containers (zombies get pool access by default in vanilla, same as
  a player worker would).

Not covered: a "zombie mill" exclusion (GK1's third exclusion category -
no matching GK2 zone id was found, see `TASKS.md`), wilderness containers
outside any zone, a vendor "personal inventory only" override, and
zombie-specific/conveyor-belt crafting (`ZombieWgoData`/`ConveyorWgoData`
override the method this mod patches, so their own crafting isn't
restricted or distance-sorted yet). See `TASKS.md` for the full breakdown.

### Capacity + stacking (Phase 1)

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

Drop collection and the loot-magnet range (rest of Phase 3/Tier 4) are
researched but not built, same for the rest of Phase 4/Tier 3's QoL/UI
toggles noted above. See `TASKS.md` for the full breakdown, including the
shared-inventory-pool gaps noted above.

## Config

`BepInEx/config/kupie.gk2.wheresmastorage.cfg` after the first run, under
`Capacity`, `Item Stacking`, `Shared Inventory`, `Gameplay` and `UI`.

## Compatibility

Player/tool-belt capacity is the same technique - and touches the same
fields - as this solution's `MoreInventorySlots` mod. Running both at once
means whichever mod's `OnGameStarted`/config-change handler runs last wins
for those two values; they aren't additive. Not an issue if you only run
one of the two for player/tool-belt sizing.
