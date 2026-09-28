# Deconstruct To Player (GK2)

When you instantly remove something with build mode's Remove tool (conveyors,
buildings, etc.), everything it gives back - the removal refund and anything
stored inside it - drops at the player's feet instead of at the removed
object. The player's pickup magnet then grabs it right away (after the
game's normal ~0.6s spawn collect-delay), so removing and immediately
re-placing things doesn't leave piles behind.

One patch: a postfix on `WgoData.GetDropPos()` that returns the player's
position while the Remove tool is open (`BuildController.IsRemoveMode`).

## Notes

- Only instant removal is affected. Deconstruct-over-time drops land where
  they normally do.
- Big items (`ItemSize.Big`) are never magnet-collected by the game, so
  those still need a manual pickup - they just land next to you.
- If your inventory is full, items stay on the ground at your feet.
- If the player isn't in the same scene as the removed object, vanilla drop
  positions are used.

Built against `Kupie/gyk2_decomp`.
