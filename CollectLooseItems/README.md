# Collect Loose Items (GK2)

Press **Ctrl+Shift+P** to pick up every loose item in the whole world into your
inventory, wherever it is - including items that fell through floors or out of
bounds and that the normal pickup magnet can never reach.

## How it works

Every dropped item is stored as a `DropData` in its scene's
`GameSceneData.droppedItems` (or `queuedDrops` for a scene that isn't loaded).
The visible `DropView` is only its representation while the area is loaded, and
the pickup magnet only sees drops whose collider overlaps the player. This mod
skips all of that and walks the saved drop lists of every scene directly, adding
each stack to the player's inventory the same way `PlayerData.CollectDrop`
does (including the item's `onDropCollected` expressions), then removing the
drop so its view despawns.

Resource drops (tech points and similar, `game_res_*`) aren't inventory items,
so those are handed to the game's own `DropSystem.CollectAllGameResDropsToPlayer`.

## Config

`BepInEx/config/kupie.gk2.collectlooseitems.cfg`:

- **CollectKey** (default `Ctrl+Shift+P`): the pickup hotkey.

## Notes

- Big items (`ItemSize.Big`: corpses, logs, etc.) and items linked to a wgo are
  skipped - the game never lets those go into the inventory, even with the
  magnet. The count left behind is written to the BepInEx log.
- If your inventory fills up, collection stops, the usual "inventory full"
  notice shows, and whatever is left stays on the ground.
- Nothing is collected while the game is paused.
- A summary of each pickup is written to the BepInEx log
  (`BepInEx/LogOutput.log`).

Built against `Kupie/gyk2_decomp`.
