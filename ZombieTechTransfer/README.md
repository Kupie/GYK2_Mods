# Zombie Tech Transfer (GK2)

Lets you move red, green and blue tech points between yourself and a zombie,
and raises your own tech point cap from 999 to 99999.

Open a zombie's window. On the **Character** tab, a column of buttons sits
over the zombie silhouette, just left of the equipment slots. There's one
group for each color (red, green, blue), each with the sphere's icon over two
buttons:

- **+50** takes 50 of that color from you and gives it to the zombie.
- **-50** takes 50 of that color from the zombie and gives it to you.

Hold **Shift** and the buttons switch to **-200 / +200**: the labels change
while Shift is down, and a click moves 200 instead of 50.

That's six buttons in all. The zombie's current counts are the ones already
shown in the window's header. Your own counts are shown by the HUD's tech
point panel, which the mod keeps on screen for as long as the zombie window
is open (normally it only slides in briefly when you gain or spend points).

How transfers work:

- **Nothing is created or lost.** Every point the zombie gains comes out of
  your pool, and every point it loses goes into your pool.
- **Partial transfers.** If the side giving the points has fewer than 50
  (or 200 with Shift), whatever it has is moved instead. A button is greyed out when the side it
  takes from has none of that color.
- **Your cap is respected.** Your tech points go through the game's normal
  resource system, which caps them. If you're at that cap, only the points
  that fit are taken from the zombie.
- **Everything refreshes right away.** The zombie's counters, the perk list
  (which perks it can now afford) and your HUD tech point display all update
  on the click.

## Config

`BepInEx/config/kupie.gk2.zombietechtransfer.cfg`:

- **Transfer Amount** (default `50`): how many points each button moves. The
  button labels follow it (`-100` / `+100` and so on). Takes effect
  immediately, no restart needed.
- **Shift Transfer Amount** (default `200`): how many points each button
  moves while Shift is held. Takes effect immediately.
- **Tech Point Cap** (default `99999`): the most red, green or blue tech
  points you can hold at once. Vanilla caps each color at 999. This only ever
  raises the cap: a value at or below the game's own cap, or `0`, leaves the
  game's cap alone. Takes effect immediately.
- **Show Your Tech Points** (default `true`): keeps the HUD panel with your
  own tech points on screen while a zombie's window is open, on either tab.
  It slides away as usual a few seconds after the window closes. Takes
  effect immediately.
- **Button Offset X** / **Button Offset Y** (defaults `6` / `-25`): nudges
  the button column left/right or down/up. `0` / `0` puts it just left of the
  equipment slots and centered on them. Use this if the
  buttons overlap something. Takes effect immediately.

## Notes

- Works alongside Timbn's Vanilla Tweaks, which can also raise the tech point
  cap. Whichever of the two sets the higher cap wins.

- The buttons are mouse-only. Gamepad navigation in the zombie window doesn't
  reach them.
- The column is positioned and sized from the equipment slots, so it should
  follow the window at any UI scale. If it still lands in an awkward spot,
  use the offset settings above.
- Once the game has set up the zombie window (at the latest, the first time
  it opens), the BepInEx log should show "Added tech point buttons to the
  zombie window." If that line is missing, the mod didn't hook the window.

Built against `Kupie/gyk2_decomp`. Not compiled or tested in-game yet.
