# Alchemy Folio Button (GK2)

Adds a **Folio** button to the top-right of the alchemy lab window. Click it
to open the alchemy folio (the formula / rune book you'd normally read at the
recipe book) on top of the lab. You don't have to walk over to the book to
check a recipe mid-mix. Close the folio (Esc / Back) and you're back at the
lab with your ingredients still in their slots.

By default the button is added to:

- **Alchemy Laboratory 1** (`alchemy_mix`, 2 ingredient slots)
- **Alchemy Laboratory 2** (`alchemy_mix_2`, 3 ingredient slots)

## How it works

- Postfix on `UIAlchemyWindow.Open` adds a `LazyButton` under the lab
  window's root, anchored top-right. The button is part of the lab window, so
  it opens, closes and layers with it. It's created once and reused.
- Clicking it runs the same code the recipe book's `Flow_OpenFolioWindow`
  node runs: `new UIAlchemyFolioWindowData().FillFromGaveSave(...)`, then
  `LazyUI.GetWindow<UIAlchemyFolioWindow>().Open(...)`. The folio shows
  the same formulas and known rune items as it does from the book.
- The button copies its sprite, hover/press transitions, sounds and text
  styles from the lab's own start-mix button, so it should look like part of
  the game's UI. If any of those are missing it falls back to a plain dark
  panel.
- If the window's close (X) button is where the Folio button would go, the
  Folio button moves left of it.
- It gets a `GamepadNavigationItem`, the same way the game's own
  runtime-built pause-menu button does, so gamepad navigation can focus and
  press it.

## Config

`BepInEx/config/kupie.gk2.alchemyfoliobutton.cfg`. Every setting applies the
next time a lab window opens.

| Setting | Default | |
|---|---|---|
| `General.StationIds` | `alchemy_mix,alchemy_mix_2` | Stations that get the button. Empty = every station that opens the alchemy window. |
| `General.ButtonLabel` | `Folio` | Button text. |
| `Layout.ButtonWidth` / `ButtonHeight` | `180` / `56` | Button size in UI units. |
| `Layout.ButtonOffsetX` / `ButtonOffsetY` | `-24` / `-24` | Offset of the button's top-right corner from the window's top-right corner. |

## Notes

- A third station, `alchemy_mix_3`, also exists in the game data and uses the
  same window. It's left out by default. Add it to `StationIds` if you want
  the button there too.
- Clicking the button while another picker is stacked over the lab (the
  ingredient or boost picker) does nothing. Close the picker first.

Built against `Kupie/gyk2_decomp`. Not compiled or tested in-game yet. The
button's exact placement depends on the lab window's prefab layout, which
isn't visible from the decomp. If it lands somewhere odd, adjust the
`Layout` offsets.
