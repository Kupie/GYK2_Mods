# ShowBuyers (GK2)

A small BepInEx mod that adds a "Buyer" section to item tooltips: which NPC
vendors buy that item, the tier each one needs to reach first (if not their
base tier), and the base price they pay for it.

```
Buyer: Smithy (II)
12g 5s
Buyer: Innkeeper
3s
```

## Grounding

GK2 doesn't have a per-townsfolk gift/wishlist system for trade purposes -
what looks like "which NPC buys this" is actually the `Vendor` system
(`Vendor.cs`, `VendorDef.cs`, `VendorTierData.cs`, `VendorProductData.cs`).
Each `Vendor` instance is keyed by an id that is itself an NPC id (e.g.
`npc_herm` - confirmed by `UIVendorWindow.Open`, which resolves the shop
header via `LLBase.L(data.Vendor.Definition.id)`, and by
`UITooltip.TestDraw`, which opens the vendor window with
`trading.FillVendorWindowData(uivendorWindowData, "npc_herm", null)`).
`VendorDef.tierDataList` (confirmed via decomp) holds one `VendorTierData`
per tier, each with its own `vendorProducts` list and `notBuying` id list
(`VendorTierData.IsBuyingProduct`) - together these say whether a given
vendor buys a given item once they reach that tier, and `VendorTierData`
entries are indexed 1-based elsewhere in the game's own UI
(`UIVendorOrderWidget` reads `tierIcons[VendorOrderData.Tier - 1]`), which
is the numbering this mod's tier display matches.

Price comes from `Vendor.CurBasePrice(VendorProductData)`
(`basePrice + a live global price modifier + that tier's priceMod`) - the
same base price the trading window itself is built from, before the
"cheaper the more you've already sold" quantity adjustment
`Vendor.CurPrice` applies on top. It's formatted with `Trading.FormatMoney`,
the same gold/silver/bronze coin-icon formatter `UITooltip.AddItemWidgets`
already uses elsewhere in its own file for other money amounts.

The tooltip side patches `UITooltip.AddItemWidgets` (private static,
Harmony-patchable), the single method every item tooltip - inventory,
containers, craft results, vendor windows - builds its widget list through.
A postfix appends one more `UITooltipTextWidgetData`, so no other tooltip
source needs its own patch.

## What this covers

- Any item tooltip that goes through `UITooltip.AddItemWidgets`.
- Every vendor who buys the item at any tier, not just their current one -
  a vendor who doesn't buy something yet still shows up, with the tier
  they need first, e.g. `Buyer: Smithy (II)`. No tier suffix means their
  base tier already buys it.
- Nothing is shown for items nobody buys at any tier - no empty "Buyer"
  section.

## Caching

Which vendor buys what at which tier comes from `VendorDef.tierDataList` -
static balance data, not per-save state - so `BuyerCache` builds one item
id -> buyer/tier list lazily and reuses it across every tooltip hover
instead of rescanning every vendor's tier list each time. It only needs
rebuilding when a new or loaded save can hand it a different vendor list,
so it's invalidated on `MainGame.OnGameStarted`. The price line is still
computed live on every hover (not cached), since `CurBasePrice` reads a
global price modifier that can change mid-game.

## Config

`BepInEx/config/kupie.gk2.showbuyers.cfg` after the first run:

- `General` / `ShowBuyers` (default `true`) - turn off if this ever
  conflicts with another tooltip mod.
