using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AlchemyFolioButton
{
	// Adds a "Folio" button to the top-right of the alchemy lab window (UIAlchemyWindow, what
	// AlchemyInteractionHandler opens for Alchemy Laboratory 1/2) that opens the alchemy folio
	// (UIAlchemyFolioWindow) on top of it, so formulas and known rune items can be checked
	// without walking over to the recipe book.
	//
	// Vanilla only opens the folio from the recipe book's FlowCanvas graph (Flow_OpenFolioWindow),
	// and that node's whole open call is public API:
	//
	//   var data = new UIAlchemyFolioWindowData();
	//   data.FillFromGaveSave(base.SelfWgoData);
	//   LazyUI.GetWindow<UIAlchemyFolioWindow>().Open(data);
	//
	// The WgoData argument is only stored on the data object, nothing in the folio reads it back,
	// so passing the lab's own WgoData instead of the book's changes nothing. Windows stack
	// (LazyWindowsStackController), so the lab window stays open underneath and is back on top
	// once the folio is closed - same as the lab's own boost/ingredient pickers.
	[BepInPlugin("kupie.gk2.alchemyfoliobutton", "Alchemy Folio Button", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<string> StationIds;
		internal static ConfigEntry<string> ButtonLabel;
		internal static ConfigEntry<float> ButtonWidth;
		internal static ConfigEntry<float> ButtonHeight;
		internal static ConfigEntry<float> ButtonOffsetX;
		internal static ConfigEntry<float> ButtonOffsetY;

		private Harmony harmony;

		private void Awake()
		{
			StationIds = Config.Bind(
				"General",
				"StationIds",
				"alchemy_mix,alchemy_mix_2",
				"Comma-separated ids of the alchemy stations that get the Folio button. alchemy_mix = Alchemy Laboratory 1, alchemy_mix_2 = Alchemy Laboratory 2. Leave empty to show it on every station that opens the alchemy mixing window. Applied the next time a lab window is opened.");

			ButtonLabel = Config.Bind(
				"General",
				"ButtonLabel",
				"Folio",
				"Text shown on the button. Applied the next time a lab window is opened.");

			ButtonWidth = Config.Bind(
				"Layout",
				"ButtonWidth",
				180f,
				"Button width, in the game's UI units. Applied the next time a lab window is opened.");

			ButtonHeight = Config.Bind(
				"Layout",
				"ButtonHeight",
				56f,
				"Button height, in the game's UI units. Applied the next time a lab window is opened.");

			ButtonOffsetX = Config.Bind(
				"Layout",
				"ButtonOffsetX",
				-24f,
				"Horizontal distance of the button's top-right corner from the window's top-right corner. Negative moves it left. Applied the next time a lab window is opened.");

			ButtonOffsetY = Config.Bind(
				"Layout",
				"ButtonOffsetY",
				-24f,
				"Vertical distance of the button's top-right corner from the window's top-right corner. Negative moves it down. Applied the next time a lab window is opened.");

			harmony = new Harmony("kupie.gk2.alchemyfoliobutton");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}
	}

	[HarmonyPatch(typeof(UIAlchemyWindow), nameof(UIAlchemyWindow.Open), typeof(UIAlchemyWindowData))]
	internal static class UIAlchemyWindow_Open_Patch
	{
		private static void Postfix(UIAlchemyWindow __instance, UIAlchemyWindowData data)
		{
			FolioButton.Refresh(__instance, data);
		}
	}

	internal static class FolioButton
	{
		private const string ObjectName = "AlchemyFolioButton";

		// The button is a child of the lab window itself, so it shows/hides with it (LazyWindow
		// hides by deactivating its GameObject) and draws on the window's own Canvas - under any
		// window stacked on top of it, like the folio. Looked up by name rather than cached, so a
		// lab window that gets destroyed and re-created (scene reload) just gets a new one.
		internal static void Refresh(UIAlchemyWindow window, UIAlchemyWindowData data)
		{
			Transform existing = window.transform.Find(ObjectName);

			string stationId = data != null && data.Wgo != null && data.Wgo.Data != null ? data.Wgo.Data.id : null;
			if (!IsEnabledFor(stationId))
			{
				if (existing != null)
				{
					existing.gameObject.SetActive(false);
				}

				return;
			}

			bool created = existing == null;
			GameObject buttonObject = created ? Create(window) : existing.gameObject;
			buttonObject.SetActive(true);
			// Last sibling = drawn above everything else in the window.
			buttonObject.transform.SetAsLastSibling();

			RectTransform rect = (RectTransform)buttonObject.transform;
			rect.sizeDelta = new Vector2(Plugin.ButtonWidth.Value, Plugin.ButtonHeight.Value);
			rect.anchoredPosition = new Vector2(Plugin.ButtonOffsetX.Value, Plugin.ButtonOffsetY.Value);
			AvoidCloseButton(window, rect);

			TextMeshProUGUI label = buttonObject.GetComponentInChildren<TextMeshProUGUI>(true);
			if (label != null)
			{
				label.text = Plugin.ButtonLabel.Value;
			}

			// Bound here rather than in Create: LazyButton.Awake replaces an onClick that has no
			// persistent listeners, and Awake only runs once the button is active in the
			// hierarchy - which it is by now (the window is shown before Open's postfix runs).
			LazyButton button = buttonObject.GetComponent<LazyButton>();
			button.onClick.RemoveAllListeners();
			button.onClick.AddListener(() => OnClicked(window));

			// The lab already rebuilt its gamepad navigation list during Open (DisplayAlchemyTab),
			// before this button existed. Rebuild it once more, the same way, so a gamepad can
			// reach the button on the very first open too. Later opens pick it up on their own.
			if (created && LazyInput.IsGamepadActive)
			{
				window.GamepadNavigationController.ReinitItems(true, null, null);
			}
		}

		// If the window's own close (X) button sits where the Folio button would go, slide the
		// Folio button left until it clears it.
		private static void AvoidCloseButton(UIAlchemyWindow window, RectTransform rect)
		{
			LazyButton closeButton = window.closeButton;
			if (closeButton == null)
			{
				return;
			}

			RectTransform root = (RectTransform)window.transform;
			Rect ours = GetRectIn(root, rect);
			Rect close = GetRectIn(root, (RectTransform)closeButton.transform);
			if (!ours.Overlaps(close))
			{
				return;
			}

			rect.anchoredPosition += new Vector2(close.xMin - ours.xMax - 8f, 0f);
		}

		private static Rect GetRectIn(RectTransform space, RectTransform target)
		{
			var corners = new Vector3[4];
			target.GetWorldCorners(corners);
			Vector3 min = space.InverseTransformPoint(corners[0]);
			Vector3 max = space.InverseTransformPoint(corners[2]);
			return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
		}

		private static bool IsEnabledFor(string stationId)
		{
			string configured = Plugin.StationIds.Value;
			if (string.IsNullOrWhiteSpace(configured))
			{
				return true;
			}

			if (string.IsNullOrEmpty(stationId))
			{
				return false;
			}

			foreach (string id in configured.Split(','))
			{
				if (string.Equals(id.Trim(), stationId, StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}

		private static GameObject Create(UIAlchemyWindow window)
		{
			var buttonObject = new GameObject(ObjectName, typeof(RectTransform));
			buttonObject.layer = window.gameObject.layer;
			buttonObject.transform.SetParent(window.transform, false);

			// In case the window root has a layout group - keep it from moving/resizing the button.
			buttonObject.AddComponent<LayoutElement>().ignoreLayout = true;

			RectTransform rect = (RectTransform)buttonObject.transform;
			rect.anchorMin = new Vector2(1f, 1f);
			rect.anchorMax = new Vector2(1f, 1f);
			rect.pivot = new Vector2(1f, 1f);

			Image background = buttonObject.AddComponent<Image>();
			LazyButton button = buttonObject.AddComponent<LazyButton>();
			button.targetGraphic = background;

			// Same as the game's own runtime-built pause menu button (UIGamePauseWindow): lets the
			// window's gamepad navigation focus it and press it like any other button.
			buttonObject.AddComponent<GamepadNavigationItem>();
			button.SetCallbacksIntoGamepadNavigationItem();

			var labelObject = new GameObject("Label", typeof(RectTransform));
			labelObject.layer = buttonObject.layer;
			labelObject.transform.SetParent(buttonObject.transform, false);

			TextMeshProUGUI label = labelObject.AddComponent<TextMeshProUGUI>();
			label.alignment = TextAlignmentOptions.Center;
			label.textWrappingMode = TextWrappingModes.NoWrap;
			label.enableAutoSizing = true;
			label.fontSizeMin = 14f;
			label.fontSizeMax = 32f;
			label.raycastTarget = false;

			RectTransform labelRect = label.rectTransform;
			labelRect.anchorMin = Vector2.zero;
			labelRect.anchorMax = Vector2.one;
			labelRect.offsetMin = new Vector2(10f, 4f);
			labelRect.offsetMax = new Vector2(-10f, -4f);

			CopyLookFrom(window.createBtn, window, button, background, label);

			return buttonObject;
		}

		// Borrows the look of the lab window's own "start mix" button (createBtn) - its sprite,
		// hover/press transition, sounds and text styles - so the new button matches the game's UI
		// instead of looking bolted on. Only plain data is copied, never the button's own
		// GameObject, so none of its other components (gamepad navigation, tooltips, the
		// interactable state the lab keeps toggling on it) come along. Falls back to a plain dark
		// panel and whatever font the window uses if any of that isn't there.
		private static void CopyLookFrom(LazyButton template, UIAlchemyWindow window, LazyButton button, Image background, TextMeshProUGUI label)
		{
			Image templateImage = template != null ? template.image : null;
			if (templateImage != null && templateImage.sprite != null)
			{
				background.sprite = templateImage.sprite;
				background.type = templateImage.type;
				background.pixelsPerUnitMultiplier = templateImage.pixelsPerUnitMultiplier;
				background.color = templateImage.color;

				// Animation transitions need an Animator (and its controller) that this button
				// doesn't have - use a plain color tint instead.
				button.transition = template.transition == Selectable.Transition.Animation
					? Selectable.Transition.ColorTint
					: template.transition;
				button.colors = template.colors;
				button.spriteState = template.spriteState;
			}
			else
			{
				background.color = new Color(0.12f, 0.1f, 0.08f, 0.9f);
			}

			if (template != null)
			{
				button.onEnterSound = template.onEnterSound;
				button.onClickSound = template.onClickSound;
			}

			// Base font first, so there's readable text even if no text style gets applied below.
			TextMeshProUGUI templateLabel = FindTemplateLabel(template, window, label);
			if (templateLabel != null)
			{
				label.font = templateLabel.font;
				label.fontSharedMaterial = templateLabel.fontSharedMaterial;
				label.color = templateLabel.color;
			}
			else
			{
				label.color = Color.white;
			}

			// LazyButton restyles its label per state (normal/hover/pressed/...) through
			// TextTransitions. Reuse the start button's styles, pointed at this label - and since
			// the start button is usually non-interactable when the window opens, this is also
			// what keeps the label from inheriting its greyed-out look from the copy above.
			if (template != null && template.textTransitions != null)
			{
				foreach (TextTransition transition in template.textTransitions)
				{
					if (transition == null || transition.targetLabel == null)
					{
						continue;
					}

					button.textTransitions = new List<TextTransition>
					{
						new TextTransition
						{
							targetLabel = label,
							defaultStyle = transition.defaultStyle,
							highlightedStyle = transition.highlightedStyle,
							pressedStyle = transition.pressedStyle,
							selectedStyle = transition.selectedStyle,
							disabledStyle = transition.disabledStyle,
						},
					};
					button.RefreshTextTransitions();
					break;
				}
			}
		}

		// The start button's own label, as named by its text transition, else the first label
		// under it, else any label in the window - skipping the new button's own label, which is
		// already in the window by now.
		private static TextMeshProUGUI FindTemplateLabel(LazyButton template, UIAlchemyWindow window, TextMeshProUGUI exclude)
		{
			var candidates = new List<TextMeshProUGUI>();
			if (template != null)
			{
				if (template.textTransitions != null)
				{
					foreach (TextTransition transition in template.textTransitions)
					{
						if (transition != null && transition.targetLabel != null)
						{
							candidates.Add(transition.targetLabel);
						}
					}
				}

				candidates.AddRange(template.GetComponentsInChildren<TextMeshProUGUI>(true));
			}

			candidates.AddRange(window.GetComponentsInChildren<TextMeshProUGUI>(true));

			foreach (TextMeshProUGUI candidate in candidates)
			{
				if (candidate != null && candidate != exclude && candidate.font != null)
				{
					return candidate;
				}
			}

			return null;
		}

		private static void OnClicked(UIAlchemyWindow window)
		{
			UIAlchemyFolioWindow folio = LazyUI.GetWindow<UIAlchemyFolioWindow>();
			if (folio == null)
			{
				return;
			}

			// Second press closes it again, if the folio doesn't cover the button.
			if (folio.IsShown)
			{
				if (folio.IsTop)
				{
					folio.Close();
				}

				return;
			}

			// Something else is stacked over the lab (ingredient picker, boost picker) - a click
			// can still land here if that window doesn't cover this corner, but opening the
			// folio on top of those would be confusing.
			if (!window.IsShownAndTop)
			{
				return;
			}

			UIAlchemyWindowData data = window.data;
			var folioData = new UIAlchemyFolioWindowData();
			folioData.FillFromGaveSave(data != null && data.Wgo != null ? data.Wgo.Data : null);
			folio.Open(folioData);
		}
	}
}
