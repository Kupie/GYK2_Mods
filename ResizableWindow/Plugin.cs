using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx;
using UnityEngine;

namespace ResizableWindow
{
	[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
	public class Plugin : BaseUnityPlugin
	{
		[DllImport("user32.dll")]
		private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

		[DllImport("user32.dll")]
		private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

		[DllImport("user32.dll")]
		private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

		private const int GWL_STYLE = -16;
		private const int WS_SIZEBOX = 0x00040000;
		private const int WS_MAXIMIZEBOX = 0x00010000;
		private const uint SWP_NOMOVE = 0x0002;
		private const uint SWP_NOSIZE = 0x0001;
		private const uint SWP_NOZORDER = 0x0004;
		private const uint SWP_FRAMECHANGED = 0x0020;

		private bool resizableApplied;

		// Keeps retrying every frame until the native window handle exists, since a
		// BepInEx plugin's own lifecycle can run before Unity has finished creating it.
		private void Update()
		{
			if (!resizableApplied)
			{
				resizableApplied = MakeWindowResizable();
			}

			bool altHeld = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
			bool enterPressed = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);

			if (altHeld && enterPressed)
			{
				ToggleFullscreen();
			}
		}

		// Uses the game's own settings pipeline rather than Screen.fullScreenMode directly,
		// so the change sticks and the settings menu doesn't show stale info afterward.
		private void ToggleFullscreen()
		{
			GameSettings settings = GameSettings.Instance;
			settings.screenMode = settings.screenMode == ScreenMode.Windowed ? ScreenMode.FullScreen : ScreenMode.Windowed;
			settings.ApplyScreenSettings();
			SaveSystem.SaveGameSettings();

			// Fullscreen mode changes can make Unity re-touch the native window, undoing
			// the resizable style below - reapply it every time, not just once at startup.
			MakeWindowResizable();
		}

		private bool MakeWindowResizable()
		{
			using (Process process = Process.GetCurrentProcess())
			{
				process.Refresh();
				IntPtr handle = process.MainWindowHandle;
				if (handle == IntPtr.Zero)
				{
					return false;
				}

				int style = GetWindowLong(handle, GWL_STYLE);
				SetWindowLong(handle, GWL_STYLE, style | WS_SIZEBOX | WS_MAXIMIZEBOX);
				SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
				return true;
			}
		}
	}
}