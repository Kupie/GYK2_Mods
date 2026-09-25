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

Only hides items on the player's own side. Confirmed against the actual
decompiled source (`Trading.cs`'s `FillVendorWindowData`): the vendor's own
buy-panel sets its own condition, `Trading.VendorItemsAvailableCondition`
(-> `vendor.CanSellItemToPlayer`), plus an explicit not-show condition,
`Trading.VendorItemsNotShowCondition` (-> `!vendor.CurrentTierData.HasProduct(itemId)`),
which vanilla's own hide pass in `InventoryWidget.Redraw` already applies -
so the vendor's panel is entirely handled by vanilla itself. The patch detects
it structurally instead of by method name: the player's own panel is built
via `InventoryWidgetDataHelper.GetWidgetsDataForInventory`, which has no
parameter for a not-show condition at all, so it's always `null` there - the
vendor's panel is the only one routed through this `Redraw` with a non-null
`CustomItemsNotShowCondition`, and that alone is enough to leave a panel
completely untouched. Earlier versions tried matching the vendor condition's
method name (didn't reliably catch it in practice), a
`CanSellItemToPlayer`/"Vendor"-declaring-type guess (wrong - that's a
different method the real condition calls internally), and before that an
allow-list of player-side method names (which broke hiding in every other
filtered picker, like the prayer slot, since each wires its own
differently-named condition).

### Tier-gated vendors

A vendor whose trade level gates which items it currently deals in (e.g. a
smithy that only buys bronze bars at reputation level 1, with iron and steel
bars shown greyed - not hidden - on its own side until level 2) is handled
the same way on the player's side: if the vendor's own panel would still show
that item at all (greyed or not), the matching item in the player's panel is
left greyed too instead of being hidden.

This mirrors vanilla's own `VendorItemsNotShowCondition` directly:
`vendor.CurrentTierData.HasProduct(itemId)`. An earlier version instead
checked membership in `Vendor.CurrentTierData.vendorProducts` (the same API
the sibling `ShowBuyers` mod uses) - that turned out wrong, since each tier
has its own distinct product list rather than an accumulating one, so it
could never see a next-tier item at all. `HasProduct` still resolves true for
an upcoming-tier item because `Trading.FillVendorWindowData` seeds the
vendor's actual `Inventory` with placeholder items for the next tier or two
before the window ever draws - the same thing that makes the vendor's own
panel show them greyed rather than absent.

Getting the `Vendor` instance needs no cross-widget caching: the player's own
condition (`Trading.PlayerItemsAvailableCondition`) is bound to the same
`Trading` object that owns the trade window, which has a private
`cachedWindowData` field (type `UIVendorWindowData`) whose public `Vendor`
property is exactly what's needed - read via reflection on the condition
delegate's own `Target`, fresh every time, with no dependency on redraw order
between panels.
