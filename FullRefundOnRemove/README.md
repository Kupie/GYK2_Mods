# Full Refund On Remove (GK2)

Vanilla's build-mode Remove tool only refunds a building's `removableWgos`
`outputItems` list (e.g. a handful of planks/parts) instead of what the
building actually cost to place (`buildableWgos.needItems`). This mod makes
Remove refund the full build cost instead, for any Wgo that was actually
built by the player.

Wgos with no build cost (trees, rubble, decorative clutter that only has a
`removableWgos` entry and no matching `buildableWgos` entry) are untouched -
they keep vanilla's own `outputItems` refund, since there's nothing to
"fully" refund in the first place.

## How it works

Patches `Wgo.DoBuildRemove()` (via Harmony, full Prefix + skip original) -
the point where the game decides what to hand back to the player, whether
the removal is instant or a deconstruct-over-time craft. See `Plugin.cs` for
the per-branch reasoning; it mirrors vanilla's own logic exactly except for
where the refund item list comes from.

Built against `Kupie/gyk2_decomp`.
