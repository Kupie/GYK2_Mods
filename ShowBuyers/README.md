# ShowBuyers (GK2)

A small BepInEx mod that adds a "Buyers" line to item tooltips listing the
NPC vendors who will currently buy that item from the player.

## Grounding

GK2 doesn't have a per-townsfolk gift/wishlist system for trade purposes -
what looks like "which NPC buys this" is actually the `Vendor` system
(`Vendor.cs`, `VendorDef.cs`, `VendorTierData.cs`, `VendorProductData.cs`).
Each `Vendor` instance is keyed by an id that is itself an NPC id (e.g.
`npc_herm` - confirmed by `UIVendorWindow.Open`, which resolves the shop
header via `LLBase.L(data.Vendor.Definition.id)`, and by
`UITooltip.TestDraw`, which opens the vendor window with
`trading.FillVendorWindowData(uivendorWindowData, "npc_herm", null)`). Each
vendor's current tier (`Vendor.CurrentTierData`) carries the list of
products it stocks (`vendorProducts`) and a `notBuying` id list
(`VendorTierData.IsBuyingProduct`) - together these say whether a given
vendor will buy a given item right now, the same check `Vendor
.CanBuyItemFromPlayer` already does for the trading UI.

The tooltip side patches `UITooltip.AddItemWidgets` (private static,
Harmony-patchable), the single method every item tooltip - inventory,
containers, craft results, vendor windows - builds its widget list through.
A postfix appends one more `UITooltipTextWidgetData` line, so no other
tooltip source needs its own patch.

## What this covers

- Any item tooltip that goes through `UITooltip.AddItemWidgets`.
- Only vendors currently willing to buy the item (tier and `notBuying` are
  both checked) - a vendor who used to buy something before leveling up, or
  who doesn't carry it yet, won't be listed.
- Nothing is shown for items nobody currently buys - no empty "Buyers:"
  line.

## Caching

Vendor product lists don't change every frame, so `BuyerCache` builds one
item id -> buyer id dictionary lazily and reuses it across every tooltip
hover, instead of rescanning every vendor's product list each time. It's
rebuilt on `MainGame.OnGameStarted` (new game and loaded save alike) and
invalidated again whenever a vendor levels up (`Vendor.ForceLevelUp`,
patched separately), since that's what actually changes which items a
vendor buys mid-game.

## Config

`BepInEx/config/kupie.gk2.showbuyers.cfg` after the first run:

- `General` / `ShowBuyers` (default `true`) - turn off if this ever
  conflicts with another tooltip mod.
