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

Big items (corpses, logs, zombie bodies, etc.) can't go in the inventory. If
**RelocateBigItems** is on (it's off by default) they're moved instead: each
one is repositioned in a spiral just above the ground around the player, and
the game's own drop physics settles them and pushes overlapping ones apart.

Resource drops (tech points and similar, `game_res_*`) aren't inventory items,
so those are handed to the game's own `DropSystem.CollectAllGameResDropsToPlayer`.

## Config

`BepInEx/config/kupie.gk2.collectlooseitems.cfg`:

- **CollectKey** (default `Ctrl+Shift+P`): the pickup hotkey.
- **RelocateBigItems** (default `false`): also move every big item in the
  player's current scene next to the player when you pick up. Off = big items
  are left where they are.
- **ToggleBigItemsKey** (default `Ctrl+Shift+B`): flips RelocateBigItems in game
  (the new state is written to the BepInEx log).

## Notes

- Relocation only applies to big items in the scene the player is standing in
  (the whole map, in practice). Big items in any other scene are left alone and
  counted in the log.
- Relocation is not selective: *every* big item in the scene comes to you,
  including ones you placed on purpose (e.g. corpses laid out somewhere).
- Small items that don't fit in your inventory stay where they are.
- If your inventory fills up, collection stops, the usual "inventory full"
  notice shows, and whatever is left stays on the ground.
- Nothing is collected while the game is paused.
- A summary of each pickup is written to the BepInEx log
  (`BepInEx/LogOutput.log`).

Built against `Kupie/gyk2_decomp`.
