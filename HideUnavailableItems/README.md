# Hide Unavailable Items (GK2)

A standalone BepInEx mod for Graveyard Keeper 2.

In the player's own inventory/bag panels, an item the currently open window
won't accept (can't be sold to the open vendor, can't go in the open bag) is
hidden entirely instead of just showing grayed out (vanilla's default
behavior).

Vanilla already computes per-item availability for both of those cases; this
mod reuses the same hide mechanism vanilla itself uses elsewhere, just
applied to that existing check.

This is the single feature originally implemented as part of the WheresMaStorage
mod, split out into its own standalone mod for simplicity.

## Config

- `Hide Unavailable Items` (default: on) - master toggle.
- `Hide Blank Slots` (default: on) - also hides the player's own empty
  slots while a vendor or bag window is open, instead of leaving blank
  placeholder tiles behind.

## Scope

Covers `InventoryWidget` and `BagInventoryWidget` (both share one `Redraw`
implementation). `ToolBeltInventoryWidget`, `BodyOrgansInventoryWidget`/
`BodyPocketInventoryWidget` and `VendorDealInventoryWidget` each have their
own separate `Redraw` implementation and are not covered.

Only hides items on the player's own side. The vendor trade window sets the
same `CustomItemsAvailableCondition` on both the player's listing and the
vendor's listing, so hiding on that predicate alone hid the vendor's items
too - the patch now also checks which callback the predicate points to,
matching only the two confirmed player-side checks (can this be sold to the
open vendor, can this go in the open bag) and leaving the vendor's own
reverse-direction check untouched.
