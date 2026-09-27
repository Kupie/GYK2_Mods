# Deconstruct To Player (GK2)

When a building is removed, everything it gives back - the removal refund,
anything stored inside it, and the inputs of crafts still queued on it -
drops at the player's feet instead of at the building. The player's pickup
magnet then grabs it right away (after the game's normal ~0.6s spawn
collect-delay), so there's no walking over to scoop up the pile.

Works for both kinds of removal:

- **Instant removal** (build mode's Remove tool) - patched at
  `Wgo.DoBuildRemove()`.
- **Deconstruct-over-time** (Remove queues a destroy craft you then work on) -
  patched at `CraftComponent.Finish()` / `CraftComponent.HandleOutput()`
  while the destroy craft is the one finishing.

Only the drop *position* changes: `WgoData.GetDropPos()` returns the
player's position while (and only while) that specific building is being
deconstructed. Normal crafting output, harvests and so on are untouched.

## Notes

- Big items (`ItemSize.Big`) are never magnet-collected by the game, so
  those still need a manual pickup - they just land next to you instead of
  at the building.
- If your inventory is full, items stay on the ground at your feet, same as
  any other drop.
- If the player isn't in the same scene as the building when its items
  drop, vanilla drop positions are used.
- Runs its `DoBuildRemove` prefix at `Priority.First`, so it should also
  redirect drops from a refund mod that replaces `DoBuildRemove` - as long
  as that mod still drops through `WgoData.MakeDrop` / `GetDropPos`.

Built against `Kupie/gyk2_decomp`. Not compiled or tested in-game yet.
