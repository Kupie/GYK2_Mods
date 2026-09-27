# Zombie Tech Transfer (GK2)

Lets you move red, green and blue tech points between yourself and a zombie.

Open a zombie's window and switch to the **Perks** tab. Under each of the
zombie's three tech point counters there's a pair of buttons:

- **+50** takes 50 of that color from you and gives it to the zombie.
- **-50** takes 50 of that color from the zombie and gives it to you.

That's six buttons in all. The buttons only show on the Perks tab, not on the
Character tab.

How transfers work:

- **Nothing is created or lost.** Every point the zombie gains comes out of
  your pool, and every point it loses goes into your pool.
- **Partial transfers.** If the side giving the points has fewer than 50,
  whatever it has is moved instead. A button is greyed out when the side it
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
- **Button Offset X** / **Button Offset Y** (defaults `0` / `-2`): nudges
  every button pair left/right or up/down from its default spot, which is
  centered just under its counter. Use this if the buttons overlap something
  in the window. Takes effect immediately.

## Notes

- The buttons are mouse-only. Gamepad navigation in the zombie window doesn't
  reach them.
- The button placement was worked out from the decompiled window code alone,
  without seeing the window's layout in the game. If the buttons land in an
  awkward spot, use the offset settings above.

Built against `Kupie/gyk2_decomp`. Not compiled or tested in-game yet.
