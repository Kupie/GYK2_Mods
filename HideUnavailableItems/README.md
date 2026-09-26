# Hide Unavailable Items (GK2)

A standalone BepInEx mod for Graveyard Keeper 2.

In the player's own inventory/bag panels, an item the currently open window
won't accept (can't be sold to the open vendor, can't go in the open bag,
isn't valid for the prayer slot, etc.) is hidden entirely instead of just
showing greyed out (vanilla's default behavior).

Vanilla already computes per-item availability for all of those cases; this
mod reuses the same hide mechanism vanilla itself uses elsewhere, just
applied to that existing check.

This is the single feature originally implemented as part of the WheresMaStorage
mod, split out into its own standalone mod for simplicity.

## Config

- `Hide Unavailable Items` (default: on) - master toggle.

## Scope

Covers `InventoryWidget` and `BagInventoryWidget` (both share one `Redraw`
implementation). `ToolBeltInventoryWidget`, `BodyOrgansInventoryWidget`/
`BodyPocketInventoryWidget` and `VendorDealInventoryWidget` each have their
own separate `Redraw` implementation and are not covered.

### The vendor's side is never touched

In the trade window, nothing on the vendor's side is ever hidden. The vendor's
side is more than its stock panel: `Trading.FillVendorWindowData` also adds
"Tier N" preview sections for the vendor's next two tiers (built by its local
function `TryFormFakeInventoryForTier`, whose body only shows up in the IL -
the decompiled `.cs` omits it). Each preview is a fake inventory whose
availability condition always returns `false`, which is how vanilla greys the
whole section. Earlier versions of this mod tried to recognise the vendor's
side by condition method name or by the stock panel's not-show condition,
neither of which matches those previews, so their items kept getting hidden.

The mod now patches `Trading.FillVendorWindowData` to remember the window's
`VendorMultiInventoryWidgetData` and `PlayerMultiInventoryWidgetData`, and
skips any panel whose data is in the vendor list. The window draws exactly
those data objects, so this covers the stock panel, both tier previews, and
anything else the game puts on that side.

Every other panel that has an availability condition - the player's
inventory and bags in the trade window, the open-bag view, and pickers like
the prayer slot (which also include your zone's chests) - hides unavailable
items as before.

### Tier-gated vendors

A vendor whose trade level gates what it deals in (e.g. a smithy that only
buys bronze bars at reputation level 1, with iron and steel bars shown greyed
in its Tier 2/3 previews) is mirrored on the player's side: if an item the
vendor won't take right now is still visible anywhere on the vendor's side
(stock panel or tier preview, greyed or not), the matching item in the
player's panel stays greyed instead of being hidden. Items the vendor doesn't
show at all are hidden.

"Visible on the vendor's side" is read from the vendor panels' data rather
than their UI cells: every non-empty item in each vendor-side inventory,
minus anything that panel's own not-show condition hides. That makes it
independent of which panel redraws first.
