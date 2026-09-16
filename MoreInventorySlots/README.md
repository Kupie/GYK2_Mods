# MoreInventorySlots (GK2)

A small BepInEx mod that adds a configurable number of bonus slots to the
player's base inventory and tool belt. Not a port of the GK1 mod of the same
concept - GK2's inventory system doesn't share the same hooks (see the
earlier discussion in this repo's history), this is a fresh, minimal
implementation of "add +X slots" against GK2's actual API.

## Why this doesn't patch the InventorySize getter

`Item.InventorySize` is a plain property backed by a private field:

```csharp
public int InventorySize
{
    get { return this.inventorySize; }
    set { this.inventorySize = value; }
}
```

It looks like an obvious Harmony postfix target ("add the bonus every time
it's read"), but several of Item's own capacity checks
(`CanAddItemToInventory`, `CanAddItemCountToInventory`, and the free-space
math inside them) read the private `inventorySize` field directly rather
than going through this property. A getter patch would only affect callers
outside `Item.cs` that go through the property - the UI would show a bigger
number, but the actual "is there room for this" checks would still be
working off the real, un-boosted field, so you wouldn't actually be able to
hold more items.

Since there's only one storage location either way, the fix is to write the
field (via the public setter) rather than intercept reads of it. That's what
`InventoryBonus.Apply()` does.

## What this covers

- Player base inventory (vanilla default: 20)
- Tool belt (vanilla default: 14)

Both are public fields on `PlayerData` (`inventory` / `toolBeltInventory`),
each wrapping an `Item` whose `InventorySize` has a public setter - no
Harmony patch needed, just a plain field write.

## When it applies

- `MainGame.OnGameStarted` - fires for a new game and a loaded save alike,
  unlike `PlayerData.Init()`, which only ever runs for a brand-new character.
- Immediately on a config value change (`ConfigEntry.SettingChanged`), so
  editing the bonus in-game via a tool like Configuration Manager takes
  effect without needing to reload.

Each application recovers the container's real base size by subtracting
whatever bonus it last applied, then adds the current configured bonus. That
keeps repeated calls (a config edit, a second `OnGameStarted`, a different
save with a different base size) from stacking on top of an earlier bonus.

## What this does NOT cover

Bags, chests, and racks aren't touched. Some of those get their size from
`ItemDef.inventorySize` / `ItemDef.bagSize` at construction time rather than
from a long-lived `Item` you can reach through `PlayerData`, so boosting them
needs a different hook (a postfix on the `Item(string, int)` constructor,
keyed by item id) once their real GK2 ids are known.

## Config

`BepInEx/config/kupie.gk2.moreinventoryslots.cfg` after the first run:

- `General` / `Player Inventory Bonus Slots` (default 10)
- `General` / `Tool Belt Bonus Slots` (default 0)

Set either to 0 to disable that bonus.
