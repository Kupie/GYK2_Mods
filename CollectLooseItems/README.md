# Collect Loose Items (GK2)

Press **Ctrl+Shift+P** to bring every loose item in the whole world to you,
wherever it is - including items that fell through floors or out of bounds and
that the normal pickup magnet can never reach. The game's pickup magnet then
picks them up.

## How it works

Every dropped item is stored as a `DropData` in its scene's
`GameSceneData.droppedItems` (or `queuedDrops` for a scene that isn't loaded).
The visible `DropView` is only its representation while the area is loaded, and
the pickup magnet only sees drops whose collider overlaps the player - so an
item that fell through the floor or out of bounds is never picked up.

This mod walks the drop lists of every scene directly and moves each item
(the data and, if loaded, its view) into a tight spiral around the player, just
above the ground. From there the game's own pickup magnet collects it with all
its normal rules: inventory space, stacking, pickup sounds and notifications,
and the "inventory full" kick-back. Anything that overlaps is pushed apart by
the game's drop physics.

Resource drops (tech points and similar, `game_res_*`) aren't physical pickups,
so those are handed to the game's own `DropSystem.CollectAllGameResDropsToPlayer`.

## Config

`BepInEx/config/kupie.gk2.collectlooseitems.cfg`:

- **CollectKey** (default `Ctrl+Shift+P`): the hotkey.
- **RelocateBigItems** (default `false`): also move big items (corpses, logs,
  zombie bodies, etc.) next to the player. The magnet never collects those, so
  they just end up sitting around you. Off = big items stay where they are.

## Notes

- Items are only moved in the scene the player is standing in (the whole map, in
  practice). Drops in any other scene are left alone and counted in the log.
- Items you've placed on purpose (e.g. corpses laid out somewhere, if
  RelocateBigItems is on) come to you too - it isn't selective.
- Small items are packed within a few units of the player so the magnet reaches
  all of them. If your inventory is full, the game's usual kick-back and
  "inventory full" notice fire per item and the rest stay on the ground.
- The magnet needs a moment: items are collected as the game's pickup logic
  processes them, not instantly on the keypress.
- Nothing happens while the game is paused.
- A summary of each use is written to the BepInEx log
  (`BepInEx/LogOutput.log`).

Built against `Kupie/gyk2_decomp`.
