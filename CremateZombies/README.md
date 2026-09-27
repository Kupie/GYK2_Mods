# Cremate Zombies (GK2)

Lets you put a zombie you're carrying into a crematorium, the same way you
would a corpse. Walk up to an idle crematorium with a zombie overhead and use
the normal "Cremate body" interaction.

What happens:

- **Same output as a corpse.** The zombie's body parts are turned back into a
  plain `body_corpse` item, which goes into the crematorium, and the
  crematorium's own burn craft starts. You get whatever burning a normal
  corpse gives you.
- **Collar comes back.** The zombie's equipped collar goes into your
  inventory, or drops at your feet if your inventory is full. An equipped
  weapon or armor comes back the same way, so gear you gave the zombie isn't
  lost.
- **The zombie is deleted.** Its `ZombieWgoData` is removed from the game's
  zombie store (`ZombieSystemData`), and that store is the only place its
  talent tree, perks, tech points and name are kept. Once it's removed,
  nothing about the zombie is written to the save anymore. Its name also
  goes back into the pool of free zombie names.
- **Zombie count drops by 1.** `cur_zombies_count` goes down by one, and the
  "too many zombies" debuff is re-checked in case you're now back under the
  limit.

## Notes

- Only zombies you're **carrying** can be cremated. To cremate one that's
  working at a station, pick it up first, as you would to move it.
- If you're carrying a normal corpse and a zombie at the same time, the
  corpse goes in first, as in vanilla.
- Bodies with a demon inside are refused, the same as vanilla does for
  corpses. Wild zombies (`body_wild_zombie`) aren't affected.
- The morgue body count (`cur_bodies_count`) is left alone. A zombie isn't
  counted as a body, so cremating one shouldn't lower that count.

Built against `Kupie/gyk2_decomp`. Not compiled or tested in-game yet.
