using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace FactoryWorkbenches
{
	public enum BeltIOMode
	{
		Disabled,
		Hybrid,
	}

	public enum OutputPreferenceMode
	{
		BeltFirst,
		CaretakerOnly,
		BeltOnly,
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
		internal static ConfigEntry<bool> SupplierStationOnConveyorDesk;
		internal static ConfigEntry<bool> SupplierStationMiniOnConveyorDesk;
		internal static ConfigEntry<string> ConveyorDeskId;
		internal static ConfigEntry<string> ConvertedBenchIds;
		internal static ConfigEntry<BeltIOMode> BeltIO;
		internal static ConfigEntry<OutputPreferenceMode> OutputPreference;
		internal static ConfigEntry<bool> LiftSingleRecipeQueueLimit;
		internal static ConfigEntry<bool> PrepareForUninstall;

		// Factory benches that behave like normal workbenches by default. conveyor_bioreactor is
		// deliberately not in this list: it is an isAutoCrafter bench that cannot take a zombie
		// (canInsertZombie is false), so there is nothing for a Crafter zombie to do there.
		// conveyor_woodworkbench is also left out: it has no crafts in the game data.
		internal const string DefaultConvertedBenchIds =
			"conveyor_assemblybench_t1,conveyor_assemblybench_t2,conveyor_assemblybench_t3," +
			"conveyor_furnace_t1,conveyor_furnace_t2," +
			"conveyor_kitchen_t1,conveyor_kitchen_t2";

		private Harmony harmony;

		private void Awake()
		{
			Log = Logger;

			Enabled = Config.Bind("General", "Enabled", true,
				"Master switch. When false every patch in this mod does nothing (the patches stay installed but pass straight through to the vanilla behaviour).");
			VerboseLogging = Config.Bind("General", "VerboseLogging", false,
				"Log the decisions this mod makes (which benches are treated as normal workbenches, what was added to the conveyor desk, and so on).");

			SupplierStationOnConveyorDesk = Config.Bind("SupplierStation", "OnConveyorDesk", true,
				"Let the conveyor zone's builder desk build the regular Zombie Supplier Station (zombie_supplier_station_p) at its normal cost.");
			SupplierStationMiniOnConveyorDesk = Config.Bind("SupplierStation", "AlsoMiniVariant", false,
				"Also offer zombie_supplier_station_mini on the conveyor desk. The game data has no BuildingDef for the mini station, so this only does something if a BuildingDef for it exists.");
			ConveyorDeskId = Config.Bind("SupplierStation", "DeskId", "builder_conveyor",
				"Wgo id of the builder desk that should offer the supplier station.");

			ConvertedBenchIds = Config.Bind("Workbenches", "ConvertedBenchIds", DefaultConvertedBenchIds,
				"Comma separated wgo ids of the factory workbenches that behave like normal workbenches. " +
				"Every ConveyorElementType.Workbench wgo in the game data: conveyor_assemblybench_t1, conveyor_assemblybench_t2, conveyor_assemblybench_t3, " +
				"conveyor_furnace_t1, conveyor_furnace_t2, conveyor_kitchen_t1, conveyor_kitchen_t2, conveyor_bioreactor (auto crafter, no zombie, ignored if listed), " +
				"conveyor_woodworkbench (no crafts). Ids that are not factory workbenches, or that are auto crafters, are ignored with a warning.");
			BeltIO = Config.Bind("Workbenches", "BeltIO", BeltIOMode.Hybrid,
				"Hybrid: converted benches keep the normal Crafter zombie + caretaker flow, and belts still feed them and can carry finished outputs away (see OutputPreference). " +
				"Disabled: belts neither feed nor drain converted benches, so the caretaker does all the hauling.");
			OutputPreference = Config.Bind("Workbenches", "OutputPreference", OutputPreferenceMode.BeltFirst,
				"Only used when BeltIO is Hybrid, and only for benches with an output belt connected. " +
				"BeltFirst: finished outputs go onto the output belt, and a caretaker only takes them if they are still waiting after a short grace period. " +
				"CaretakerOnly: outputs are never put on belts. " +
				"BeltOnly: caretakers never take outputs from a bench that has a working output belt (if the belt jams, outputs wait).");
			LiftSingleRecipeQueueLimit = Config.Bind("Workbenches", "LiftSingleRecipeQueueLimit", true,
				"Vanilla only lets a factory bench queue one recipe id at a time. When true, converted benches can queue several different recipes like a normal workbench.");

			PrepareForUninstall = Config.Bind("Maintenance", "PrepareForUninstall", false,
				"One shot. On the next save load, every Crafter zombie standing on a converted bench is taken off it (its body is dropped next to the bench, like a bench deconstruction does) and the orders aimed at it are cleared, so the save has no mod-made state left before you remove this mod. The option turns itself off afterwards.");

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

	// The shared "is this bench behaving like a normal workbench right now" decision.
	internal static class Factory
	{
		private static readonly HashSet<string> configured = new HashSet<string>(StringComparer.Ordinal);
		private static readonly HashSet<string> effective = new HashSet<string>(StringComparer.Ordinal);
		private static bool validated;

		internal static void Init()
		{
			configured.Clear();
			effective.Clear();
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
			effective.Clear();
			foreach (string id in configured)
			{
				WGODef def = balance.GetDataOrNull<WGODef>(id);
				if (def == null)
				{
					Plugin.Log.LogWarning("ConvertedBenchIds: '" + id + "' is not a known wgo id, ignored.");
				}
				else if (def.conveyorType != ConveyorElementType.Workbench)
				{
					Plugin.Log.LogWarning("ConvertedBenchIds: '" + id + "' is not a factory workbench (conveyorType " + def.conveyorType + "), ignored.");
				}
				else if (def.isAutoCrafter)
				{
					Plugin.Log.LogWarning("ConvertedBenchIds: '" + id + "' is an auto crafter, which cannot take a Crafter zombie, ignored.");
				}
				else
				{
					effective.Add(id);
				}
			}
			Plugin.Verbose("Converted benches in effect: " + string.Join(", ", effective));
		}

		// True when this wgo is a converted factory bench that should currently act as a regular
		// workbench. A bench that still holds a ConveyorCrafter zombie (placed before this mod was
		// installed) answers false, so that zombie keeps working exactly the way vanilla does
		// until the player picks it up and puts it back.
		internal static bool IsRegularMode(WgoData bench)
		{
			if (bench == null || !Plugin.Enabled.Value)
			{
				return false;
			}
			if (!validated)
			{
				Validate();
				if (!validated)
				{
					return false;
				}
			}
			if (!effective.Contains(bench.id))
			{
				return false;
			}
			return !HoldsLegacyConveyorCrafter(bench);
		}

		internal static bool IsConvertedId(string wgoId)
		{
			if (!validated)
			{
				Validate();
			}
			return validated && effective.Contains(wgoId);
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
