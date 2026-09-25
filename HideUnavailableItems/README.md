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
too - the patch excludes only the one confirmed vendor-side callback
(`Vendor.CanSellItemToPlayer`, matched by its declaring type/method name)
rather than allow-listing player-side method names. An earlier version
allow-listed the two known player-side methods instead, which broke hiding
in every other filtered picker (prayer slot, organ slot, etc.) since each
wires its own differently-named condition - those all work again now, since
anything not declared on the vendor is treated as a player-side panel.

### Tier-gated vendors

A vendor whose trade level gates which items it currently deals in (e.g. a
smithy that only buys bronze bars at reputation level 1, with iron and steel
bars shown greyed - not hidden - on its own side until level 2) is handled
the same way on the player's side: if the vendor's own panel still shows that
item's category at all (greyed or not), the matching item in the player's
panel is left greyed too instead of being hidden. It's only hidden if the
vendor's panel has nothing of that category at all.

This uses the same vendor-product API the sibling `ShowBuyers` mod already
relies on: `Vendor.CurrentTierData.vendorProducts` lists every item id the
vendor deals in, even ones `Vendor.CurrentTierData.IsBuyingProduct(itemId)`
currently rejects for tier reasons - so membership in that list (not the
buy-right-now check) is what decides grey-vs-hide. The vendor instance itself
comes from the vendor-side condition delegate's `Target` (since
`Vendor.CanSellItemToPlayer` is an instance method, its bound delegate's
target is the open vendor) - no separate "currently open vendor" tracking was
needed. Worth double-checking in-game across a few tier boundaries.
