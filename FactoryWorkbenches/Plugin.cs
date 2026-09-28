using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace FactoryWorkbenches
{
	public enum OutputPreferenceMode
	{
		BeltFirst,
		BeltOnly,
		NoBelts,
	}

	// Lets the underground factory workbenches (the ConveyorElementType.Workbench wgos) be run by
	// ordinary Crafter zombies fed by the Zombie Supplier Station's caretaker, and lets the
	// conveyor zone's builder desk build that station. Nothing is added to or changed in the
	// game's balance data and nothing new is saved: every patch here decides at run time, so the
	// save only ever contains vanilla ids and vanilla types. See README.md for the full trace.
	[BepInPlugin("kupie.gk2.factoryworkbenches", "Factory Workbenches", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ManualLogSource Log;

		internal static ConfigEntry<bool> Enabled;
		internal static ConfigEntry<bool> VerboseLogging;
		internal static ConfigEntry<bool> AddNormalChests;
		internal static ConfigEntry<string> ConvertedBenchIds;
		internal static ConfigEntry<bool> BeltInputsAllowed;
		internal static ConfigEntry<OutputPreferenceMode> OutputPreference;
		internal static ConfigEntry<float> BeltFirstDelaySeconds;
		internal static ConfigEntry<bool> LiftSingleRecipeQueueLimit;
		internal static ConfigEntry<bool> PrepareForUninstall;

		// Factory benches converted by default. conveyor_woodworkbench is left out: it has no crafts
		// in the game data. conveyor_bioreactor is never touched by this mod (it is an auto crafter
		// that belts already serve), so it is not listed and is ignored if someone lists it.
		internal const string DefaultConvertedBenchIds =
			"conveyor_assemblybench_t1,conveyor_assemblybench_t2,conveyor_assemblybench_t3," +
			"conveyor_furnace_t1,conveyor_furnace_t2," +
			"conveyor_kitchen_t1,conveyor_kitchen_t2";

		// The conveyor zone's builder desk. The supplier station is always added to it (the rest of
		// the mod depends on the station), so this is not configurable.
		internal const string ConveyorDeskId = "builder_conveyor";

		private Harmony harmony;

		private void Awake()
		{
			Log = Logger;

			Enabled = Config.Bind("General", "Enabled", true,
				"Master switch. When false every patch in this mod does nothing.");

			AddNormalChests = Config.Bind("General", "Add Normal Chests to building", true,
				"Let the conveyor zone's builder desk build simple chest, chest, large chest. " +
				"Suppliers and converted benches use them like the conveyor chests, but belts do not connect to them.");

			ConvertedBenchIds = Config.Bind("Workbenches", "Converted Bench IDs", DefaultConvertedBenchIds,
				"Comma separated IDs of the factory benches to convert to work like normal workbenches. " +
				"All factory benches: conveyor_assemblybench_t1, conveyor_assemblybench_t2, conveyor_assemblybench_t3, " +
				"conveyor_furnace_t1, conveyor_furnace_t2, conveyor_kitchen_t1, conveyor_kitchen_t2. " +
				"IDs that are not factory benches are ignored.");

			BeltInputsAllowed = Config.Bind("Workbenches", "Belt Inputs Allowed", true,
				"When true, items arriving on input belts still feed a converted bench. " +
				"When false, belts never feed converted benches and the suppliers do all the delivering. Outputs are not affected, see Output Preference.");

			OutputPreference = Config.Bind("Workbenches", "Output Preference", OutputPreferenceMode.BeltFirst,
				"Belt First: outputs go onto the output belt, and a supplier only takes them if they are still waiting after Belt First Delay Seconds. " +
				"Belt Only: suppliers never take outputs from a bench that has a working output belt (if the belt jams, outputs wait). " +
				"No Belts: outputs are never put on belts.");

			BeltFirstDelaySeconds = Config.Bind("Workbenches", "Belt First Delay Seconds", 5f,
				new ConfigDescription(
					"Only used if Output Preference is Belt First. Seconds a finished output waits for the output belt before a supplier may take it instead. The wait restarts every time the belt moves an output.",
					new AcceptableValueRange<float>(1f, 600f)));

			LiftSingleRecipeQueueLimit = Config.Bind("Workbenches", "LiftSingleRecipeQueueLimit", true,
				"Let converted benches queue several different recipes at once, like normal workbenches.");

			VerboseLogging = Config.Bind("Maintenance", "Debug Logging", false,
				"Enable Debug Logging, only for bug reporting.");

			PrepareForUninstall = Config.Bind("Maintenance", "PrepareForUninstall", false,
				"One shot, for before removing the mod. On the next save load, zombies working on converted benches are taken off them and their orders cleared. Turns itself off afterwards.");

			Factory.Init();
			ConvertedBenchIds.SettingChanged += (_, __) => Factory.Init();
			Enabled.SettingChanged += (_, __) => Factory.Init();

			harmony = new Harmony("kupie.gk2.factoryworkbenches");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}

		internal static void Verbose(string message)
		{
			if (VerboseLogging != null && VerboseLogging.Value)
			{
				Log.LogInfo(message);
			}
		}
	}

	// The shared "how is this bench being run right now" decisions.
	internal static class Factory
	{
		private static readonly HashSet<string> configured = new HashSet<string>(StringComparer.Ordinal);
		private static readonly HashSet<string> regular = new HashSet<string>(StringComparer.Ordinal);
		private static bool validated;

		internal static void Init()
		{
			configured.Clear();
			regular.Clear();
			validated = false;

			string raw = Plugin.ConvertedBenchIds.Value ?? string.Empty;
			foreach (string part in raw.Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
			{
				configured.Add(part.Trim());
			}
			Plugin.Verbose("Configured converted bench ids: " + string.Join(", ", configured));
		}

		// The configured ids can only be checked against the game data once it is loaded, so this
		// runs the first time a bench is asked about.
		private static void Validate()
		{
			GameBalance balance = GameBalance.Me;
			if (balance == null)
			{
				return;
			}

			validated = true;
			regular.Clear();
			foreach (string id in configured)
			{
				WGODef def = balance.GetDataOrNull<WGODef>(id);
				if (def == null)
				{
					Plugin.Log.LogWarning("Converted Bench IDs: '" + id + "' is not a known wgo id, ignored.");
				}
				else if (def.conveyorType != ConveyorElementType.Workbench)
				{
					Plugin.Log.LogWarning("Converted Bench IDs: '" + id + "' is not a factory bench (conveyorType " + def.conveyorType + "), ignored.");
				}
				else if (def.isAutoCrafter)
				{
					Plugin.Log.LogWarning("Converted Bench IDs: '" + id + "' is an auto crafter, which this mod leaves alone, ignored.");
				}
				else
				{
					regular.Add(id);
				}
			}
			Plugin.Verbose("Converted benches in effect: " + string.Join(", ", regular));
		}

		private static bool Ready()
		{
			if (!Plugin.Enabled.Value)
			{
				return false;
			}
			if (!validated)
			{
				Validate();
			}
			return validated;
		}

		// True when this wgo is a converted factory bench that should currently act as a regular
		// workbench (CraftableType Regular, normal queue and storage rules). A bench that still holds
		// a ConveyorCrafter zombie (placed before this mod was installed) answers false, so that
		// zombie keeps working exactly the way vanilla does until the player picks it up and puts it
		// back.
		internal static bool IsRegularMode(WgoData bench)
		{
			return bench != null && Ready() && regular.Contains(bench.id) && !HoldsLegacyConveyorCrafter(bench);
		}

		internal static bool HoldsLegacyConveyorCrafter(WgoData bench)
		{
			if (bench.workerId == null || bench.workerId.IsEmpty)
			{
				return false;
			}
			MainGame main = MainGame.Instance;
			if (main == null || main.GameSave == null)
			{
				return false;
			}
			ZombieWgoData zombie = bench.Worker as ZombieWgoData;
			return zombie != null && zombie.ZombieType == ZombieType.ConveyorCrafter;
		}
	}
}
