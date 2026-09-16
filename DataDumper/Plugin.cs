using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace DataDumper
{
	// Dumps every public def/data collection on GameBalance (techs, items, crafts, wgos,
	// talents, and everything else it holds) to individual JSON files - all the
	// data-table-driven content a static decompile can't see, since it's loaded from a
	// ScriptableObject asset at runtime rather than living in code.
	[BepInPlugin("kupie.gk2.datadumper", "Data Dumper", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		private const int MaxDepth = 6;
		private const int MaxCollectionItems = 500;

		internal static ManualLogSource Log;
		internal static ConfigEntry<bool> DumpOnStartup;
		internal static ConfigEntry<KeyboardShortcut> DumpKey;

		private bool dumpedOnStartup;

		private void Awake()
		{
			Log = Logger;

			DumpOnStartup = Config.Bind(
				"General",
				"DumpOnStartup",
				true,
				"Dump everything once automatically shortly after the game launches. GameBalance loads its data lazily from a Resources asset independent of any save, so this works from the main menu - no need to start or load a game first.");

			DumpKey = Config.Bind(
				"General",
				"DumpKey",
				new KeyboardShortcut(KeyCode.F10),
				"Key that re-runs the dump on demand (e.g. to pick up runtime caches that only populate after some play).");
		}

		private void Update()
		{
			if (DumpOnStartup.Value && !dumpedOnStartup)
			{
				dumpedOnStartup = true;
				DumpAll();
			}

			if (DumpKey.Value.IsDown())
			{
				DumpAll();
			}
		}

		private static void DumpAll()
		{
			GameBalance balance = GameBalance.Me;
			if (balance == null)
			{
				Log.LogWarning("DataDumper: GameBalance.Me was null - nothing to dump.");
				return;
			}

			// BepInExRootPath rather than GameRootPath - BepInEx already needs write access
			// here to function at all, so this avoids Program Files-style permission issues,
			// while still sitting somewhere easy to find (a sibling of config/plugins).
			string outputDir = Path.Combine(Paths.BepInExRootPath, "DataDumper_Output");

			try
			{
				Directory.CreateDirectory(outputDir);
			}
			catch (Exception ex)
			{
				Log.LogError($"DataDumper: couldn't create output folder '{outputDir}': {ex.Message}");
				return;
			}

			JsonSerializerSettings settings = new JsonSerializerSettings
			{
				Formatting = Formatting.Indented,
				ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
			};

			int written = 0;

			// Every public IEnumerable field on GameBalance - itemDefs, techDefs, craftDefs,
			// wgoDefs, talentDefs, and dozens more - gets its own file named after the field,
			// rather than hand-listing which ones matter. New def types the game adds later
			// get picked up automatically without this mod needing an update.
			foreach (FieldInfo field in typeof(GameBalance).GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				if (field.FieldType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(field.FieldType))
				{
					continue;
				}

				try
				{
					object value = field.GetValue(balance);
					object flattened = Flatten(value, 0);
					string json = JsonConvert.SerializeObject(flattened, settings);
					File.WriteAllText(Path.Combine(outputDir, field.Name + ".json"), json);
					written++;
				}
				catch (Exception ex)
				{
					Log.LogError($"DataDumper: failed to dump '{field.Name}': {ex.Message}");
				}
			}

			Log.LogInfo($"DataDumper: wrote {written} file(s) to {outputDir}");
		}

		// Reflects public fields/properties into plain dictionaries and lists so Newtonsoft
		// never has to deal with live Unity objects, circular references, or unbounded
		// collections directly. Unity object references (sprites, prefabs, components) are
		// recorded as a short marker instead of recursed into - their own internals aren't
		// useful balance data and can otherwise pull in huge unrelated object graphs.
		//
		// This walks properties as well as fields, and a property getter could in principle
		// have side effects or be expensive - for these def/balance types that's not expected
		// to matter, but if a dump ever seems to hang or misbehave, that's the first thing to
		// suspect, and switching to fields-only is the fix.
		private static object Flatten(object value, int depth)
		{
			if (value == null)
			{
				return null;
			}

			Type type = value.GetType();

			if (type.IsPrimitive || value is string || value is decimal || type.IsEnum)
			{
				return value;
			}

			if (depth >= MaxDepth)
			{
				return $"<max depth reached: {type.Name}>";
			}

			if (typeof(UnityEngine.Object).IsAssignableFrom(type))
			{
				return $"<UnityObject:{type.Name}>";
			}

			if (value is IDictionary dictionary)
			{
				Dictionary<string, object> result = new Dictionary<string, object>();
				foreach (DictionaryEntry entry in dictionary)
				{
					result[Convert.ToString(entry.Key)] = Flatten(entry.Value, depth + 1);
				}
				return result;
			}

			if (value is IEnumerable enumerable)
			{
				List<object> list = new List<object>();
				int count = 0;
				foreach (object item in enumerable)
				{
					if (count++ >= MaxCollectionItems)
					{
						list.Add("<truncated>");
						break;
					}
					list.Add(Flatten(item, depth + 1));
				}
				return list;
			}

			Dictionary<string, object> obj = new Dictionary<string, object>();

			foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				try
				{
					obj[field.Name] = Flatten(field.GetValue(value), depth + 1);
				}
				catch (Exception ex)
				{
					obj[field.Name] = $"<error reading field: {ex.Message}>";
				}
			}

			foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (!property.CanRead || property.GetIndexParameters().Length > 0)
				{
					continue;
				}

				try
				{
					obj[property.Name] = Flatten(property.GetValue(value), depth + 1);
				}
				catch (Exception ex)
				{
					obj[property.Name] = $"<error reading property: {ex.Message}>";
				}
			}

			return obj;
		}
	}
}