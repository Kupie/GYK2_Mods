# GK2Mods

BepInEx 5 (Mono) mods for Graveyard Keeper 2, laid out the same way as
[Kupie_OniMods](https://github.com/Kupie/Kupie_OniMods): one solution, one
csproj per mod, shared build settings in `Directory.Build.props` /
`Directory.Packages.props` so adding another mod is a new folder, a new
minimal csproj, and one more line in the .sln.

## Prerequisites

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx) (Mono/x64 build)
   into your Graveyard Keeper 2 install folder, and run the game once so it
   generates the `BepInEx/` folder structure.
2. Copy `Directory.Build.local.props.example` to `Directory.Build.local.props`
   and point `Gyk2ManagedPath` at your `Graveyard Keeper 2_Data\Managed`
   folder if the auto-detection in `Directory.Build.props` doesn't find it
   (the Steam folder name there is a guess made before GK2's full release,
   so treat auto-detection as unreliable until confirmed).
3. `dotnet restore` / open `GK2Mods.sln` and build. Each mod's output lands
   in its own `bin\<Configuration>\net48\` - copy the built DLL into
   `BepInEx/plugins/<ModName>/` in the game folder to test it.

## Mods

- **MoreInventorySlots** - overrides the player's base inventory and tool
  belt sizes. See its own README for exactly what it covers and what it
  doesn't (yet).
