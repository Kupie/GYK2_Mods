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

## Scope

Covers `InventoryWidget` and `BagInventoryWidget` (both share one `Redraw`
implementation). `ToolBeltInventoryWidget`, `BodyOrgansInventoryWidget`/
`BodyPocketInventoryWidget` and `VendorDealInventoryWidget` each have their
own separate `Redraw` implementation and are not covered.
