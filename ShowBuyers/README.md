# ShowBuyers (GK2)

A small BepInEx mod that adds a "Buyer" line to item tooltips: which NPC
vendors buy that item and the tier each one needs to reach first, followed
by the item's base sell price.

```
Buyer: Smithy (II), Innkeeper (I)
12g 5s
```

## Grounding

GK2 doesn't have a per-townsfolk gift/wishlist system for trade purposes -
what looks like "which NPC buys this" is actually the `Vendor` system
(`Vendor.cs`, `VendorDef.cs`, `VendorTierData.cs`, `VendorProductData.cs`).
Each `Vendor` instance is keyed by an id that is itself an NPC or town
building id (e.g. `npc_herm`, `t_b_blacksmith` - confirmed by
`UIVendorWindow.Open`, which resolves the shop header via
`LLBase.L(data.Vendor.Definition.id)`).

### Why not `Vendor.CurrentTierData`

The first version listed only vendors whose current tier buys the item,
via `Vendor.CurrentTierData`. That can never show upper-tier buyers:
`CurrentTierData` is just `tierDataList[curTier - 1]`, so a next-tier item
isn't in it at all. The Hide Unavailable Items mod hit the same wall.

What the vendor window itself does (`Trading.FillVendorWindowData`) is draw
the current stock plus "Tier N" previews for the next two tiers, built by
its local function `TryFormFakeInventoryForTier`. That function's body is
only in the IL; the decompiled `.cs` drops it. Disassembled, it reads
`VendorDef.tierDataList[N - 1]` directly and shows that tier's
`vendorProducts` that are also in its `newProducts` (filled at load by
`GameBalance.CreateNewVendorProductsCache` with the ids no earlier tier
lists) and not already in the vendor's stock.

So everything needed is in `VendorDef.tierDataList`, which is balance data
and not save state. Each tier's `vendorProducts` is that tier's full list,
and its `notBuying` is per tier too. This mod applies the same test as
`Vendor.CanBuyItemFromPlayer` ("in `vendorProducts`, not in `notBuying`")
to every tier from the vendor's `startTier` up, and shows the lowest one
that passes. `newProducts` alone isn't enough, because an item can be
refused at the tier it's introduced and bought from a later one: the
blacksmith lists `hammer_1` from tier 1 but only drops it from `notBuying`
at tier 2.

Checked against the DataDumper output: 159 of the 325 vendor/item buyer
pairs only start at tier II or III, e.g. `nails_iron` is bought by
`t_b_furniturer`, `t_b_builder` and `t_b_blacksmith`, all from tier II.

### Price

Price is `ItemDef.basePrice` - the same base price for every buyer,
regardless of any per-vendor `VendorProductData.priceMod` - formatted with
`Trading.FormatMoney`, the same gold/silver/bronze coin-icon formatter
`UITooltip.AddItemWidgets` already uses elsewhere in its own file for other
money amounts.

### Tooltip patch

The tooltip side patches `UITooltip.AddItemWidgets` (private static,
Harmony-patchable), the single method every item tooltip - inventory,
containers, craft results, vendor windows - builds its widget list through.
A postfix appends one more `UITooltipTextWidgetData`, so no other tooltip
source needs its own patch.

## What this covers

- Any item tooltip that goes through `UITooltip.AddItemWidgets`.
- Every vendor who buys the item at any tier, not just their current one,
  with the lowest tier they buy it at, e.g. `Buyer: Smithy (II)`.
- Test vendors in the balance data (`test2`, `test_town_vendor`) are
  skipped.
- Nothing is shown for items nobody buys at any tier - no empty "Buyer"
  line.

## Known gaps

- The tier shown is the first tier that buys the item. A few items stop
  being bought at a later tier (the alchemist drops `weak_growing_elixir`
  at tier III); the tooltip doesn't say so.
- `npc_bishop` and `npc_woodcarver` have tier I product lists in the
  balance data (bronze scrap, iron ore) that look like placeholder data.
  NPC vendors are opened from flow graphs by id string, which the dumped
  data doesn't include, so there's no way to confirm from here whether
  those two can actually be traded with. If they show up as buyers but
  can't be, add them to the skip list next to the test vendors.

## Caching

Which vendor buys what at which tier comes from `VendorDef.tierDataList`,
so `BuyerCache` builds one item id -> buyer/tier list lazily and reuses it
across every tooltip hover instead of rescanning every vendor's tier list
each time. It only needs rebuilding when a new or loaded save can hand it
a different vendor list, so it's invalidated on `MainGame.OnGameStarted`.

## Config

`BepInEx/config/kupie.gk2.showbuyers.cfg` after the first run:

- `General` / `ShowBuyers` (default `true`) - turn off if this ever
  conflicts with another tooltip mod.
