using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using EveOPreview.Configuration;
using EveOPreview.Services.Interop;

namespace EveOPreview.Services.Implementation
{
	public class WindowManager : IWindowManager
	{
		#region Private constants
		private const int WINDOW_SIZE_THRESHOLD = 300;
		private const int NO_ANIMATION = 0;
		#endregion

		#region Private fields
		private readonly bool _enableWineCompatabilityMode;
		private string _bashLocation;
		private string _wmctrlLocation;
		private const string EXCEPTION_DUMP_FILE_NAME = "EVE-O-Preview.log";
		#endregion

		Timer timer;


		public WindowManager(IThumbnailConfiguration configuration)
		{
#if LINUX
			this._enableWineCompatabilityMode = configuration.EnableWineCompatibilityMode;
			this._bashLocation = FindLinuxBinLocation("bash");
			this._wmctrlLocation = FindLinuxBinLocation("wmctrl");
#endif
			// Composition is always enabled for Windows 8+
			this.IsCompositionEnabled =
				((Environment.OSVersion.Version.Major == 6) && (Environment.OSVersion.Version.Minor >= 2)) // Win 8 and Win 8.1
				|| (Environment.OSVersion.Version.Major >= 10) // Win 10
				|| DwmNativeMethods.DwmIsCompositionEnabled(); // In case of Win 7 an API call is requiredWin 7
			_animationParam.cbSize = (System.UInt32)Marshal.SizeOf(typeof(ANIMATIONINFO));
			
			timer = new Timer(OnTimerEvent, null, 0, 1000);
		}
#if LINUX
		private string FindLinuxBinLocation(string command)
		{
			// Check common paths for command
			string[] paths = { "/run/host/usr/bin", "/bin", "/usr/bin" };
			foreach (var path in paths)
			{
			    string locationToCheck = $"{path}/{command}";
				if (System.IO.File.Exists(locationToCheck))
				{
					string binLocation = System.IO.Path.GetDirectoryName(locationToCheck);
					string binLocationUnixStyle = binLocation.Replace("\\", "/");

					return binLocationUnixStyle;
				}
			}

			WriteToLog($"[{DateTime.Now}] Error: {command} not found in expected locations.");
			return null;
		}
#endif

		private void WriteToLog(string message)
		{
			try
			{
				System.IO.File.AppendAllText(EXCEPTION_DUMP_FILE_NAME, message + Environment.NewLine);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Failed to write to log file: {ex.Message}");
			}
		}

		private int? _currentAnimationSetting = null;
		private ANIMATIONINFO _animationParam = new ANIMATIONINFO();

		public bool IsCompositionEnabled { get; }

		public IntPtr GetForegroundWindowHandle()
		{
			return User32NativeMethods.GetForegroundWindow();
		}

		private void OnTimerEvent(object stateInfo)
		{
			IntPtr hProc;
			IntPtr hWnd;

			hProc = Process.GetCurrentProcess().Handle;
			hWnd = Process.GetCurrentProcess().MainWindowHandle;
			

			var count = User32NativeMethods.GetGuiResourcesGDICount(hProc);
			var peak = User32NativeMethods.GetGuiResourcesGDICountPeak(hProc);

			WriteToLog($"[{DateTime.Now}] Process - GDIs: {count}. GDI peak: {peak}");

			count = User32NativeMethods.GetGuiResourcesGDICount(hWnd);
			peak = User32NativeMethods.GetGuiResourcesGDICountPeak(hWnd);
			WriteToLog($"[{DateTime.Now}] MainWindow -  GDIs: {count} peak: {peak}");

			var handleCount = Process.GetCurrentProcess().HandleCount;
			WriteToLog($"[{DateTime.Now}] Handle count: {handleCount}");
		}

		private void TurnOffAnimation()
		{
			var currentAnimationSetup = User32NativeMethods.SystemParametersInfo(User32NativeMethods.SPI_GETANIMATION, (System.Int32)Marshal.SizeOf(typeof(ANIMATIONINFO)), ref _animationParam, 0);
			if (_currentAnimationSetting == null)
			{
				// Store the current Animation Setting
				_currentAnimationSetting = _animationParam.iMinAnimate;
			}

			if (currentAnimationSetup != NO_ANIMATION)
			{
				// Turn off Animation
				_animationParam.iMinAnimate = NO_ANIMATION;
				var animationOffReturn = User32NativeMethods.SystemParametersInfo(User32NativeMethods.SPI_SETANIMATION, (System.Int32)Marshal.SizeOf(typeof(ANIMATIONINFO)), ref _animationParam, 0);
			}
		}

		private void RestoreAnimation()
		{
			var currentAnimationSetup = User32NativeMethods.SystemParametersInfo(User32NativeMethods.SPI_GETANIMATION, (System.Int32)Marshal.SizeOf(typeof(ANIMATIONINFO)), ref _animationParam, 0);
			// Restore current Animation Settings
			if (_animationParam.iMinAnimate != (int)_currentAnimationSetting)
			{
				_animationParam.iMinAnimate = (int)_currentAnimationSetting;
				var animationResetReturn = User32NativeMethods.SystemParametersInfo(User32NativeMethods.SPI_SETANIMATION, (System.Int32)Marshal.SizeOf(typeof(ANIMATIONINFO)), ref _animationParam, 0);
			}
		}

		// if building for LINUX the window handling is slightly different
#if LINUX
		private void WindowsActivateWindow(IntPtr handle)
		{
			User32NativeMethods.SetForegroundWindow(handle);
			User32NativeMethods.SetFocus(handle);

			int style = User32NativeMethods.GetWindowLong(handle, InteropConstants.GWL_STYLE);

			if ((style & InteropConstants.WS_MINIMIZE) == InteropConstants.WS_MINIMIZE)
			{
				User32NativeMethods.ShowWindowAsync(handle, InteropConstants.SW_RESTORE);
			}
		}

		private void WineActivateWindow(string windowName)
		{
			// On Wine it is not possible to manipulate windows directly.
			// They are managed by native Window Manager
			// So a separate command-line utility is used
			if (string.IsNullOrEmpty(windowName))
			{
				return;
			}

            string cmd = "";
			try
			{
                // If we are in a flatpak, then use flatpak-spawn to run wmctrl outside the sandbox
                if (Environment.GetEnvironmentVariable("container") == "flatpak")
                {
                    cmd = $"-c \"flatpak-spawn --host wmctrl -a \"\"" + windowName + "\"\"\"";
                } 
                else 
                {
                    cmd = $"-c \"wmctrl -a \"\"" + windowName + "\"\"\"";
                }

				// Configure and start the process
				var info = new System.Diagnostics.ProcessStartInfo
				{
					FileName = "/bin/sh",
					Arguments = cmd,
					UseShellExecute = false,
					CreateNoWindow = false,
					RedirectStandardOutput = true
				};
				var pathext = System.Environment.GetEnvironmentVariable("PATHEXT");
				info.EnvironmentVariables["PATHEXT"] = $"{pathext};.";

				using(var proc = new System.Diagnostics.Process())
				{
					proc.StartInfo = info;
					proc.Start();
					// Wine doesn't implement WindowsAPI to wait or use pipes with CreateProcess, see: https://stackoverflow.com/questions/6004070/execute-shell-commands-from-program-running-in-wine
					while(proc.StandardOutput.Read() >= 0);	
				}
			}
			catch (Exception ex)
			{
				WriteToLog($"[{DateTime.Now}] executing wmctrl - Exception: {ex.Message}");
			}
		}

        public void ActivateWindow(IntPtr handle, string windowName)
        {
            if (this._enableWineCompatabilityMode)
            {
                this.WineActivateWindow(windowName);
            }
            else
            {
                this.WindowsActivateWindow(handle);
            }
        }

        public void MinimizeWindow(IntPtr handle, bool enableAnimation)
		{
			if (enableAnimation)
			{
				User32NativeMethods.SendMessage(handle, InteropConstants.WM_SYSCOMMAND, InteropConstants.SC_MINIMIZE, 0);
			}
			else
			{
				WINDOWPLACEMENT param = new WINDOWPLACEMENT();
				param.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
				User32NativeMethods.GetWindowPlacement(handle, ref param);
				param.showCmd = WINDOWPLACEMENT.SW_MINIMIZE;
				User32NativeMethods.SetWindowPlacement(handle, ref param);
			}
		}

#endif

#if WINDOWS
		public void ActivateWindow(IntPtr handle, AnimationStyle animation)
		{
			User32NativeMethods.SetForegroundWindow(handle);
			User32NativeMethods.SetFocus(handle);

			int style = User32NativeMethods.GetWindowLong(handle, InteropConstants.GWL_STYLE);

			if ((style & InteropConstants.WS_MINIMIZE) == InteropConstants.WS_MINIMIZE)
			{
				switch (animation)
				{
					case AnimationStyle.OriginalAnimation:
						User32NativeMethods.ShowWindowAsync(handle, InteropConstants.SW_RESTORE);
						break;
					case AnimationStyle.NoAnimation:
						TurnOffAnimation();
						User32NativeMethods.ShowWindowAsync(handle, InteropConstants.SW_RESTORE);
						RestoreAnimation();
						break;
				}
			}
		}

		public void MinimizeWindow(IntPtr handle, AnimationStyle animation, bool enableAnimation)
		{
			if (enableAnimation)
			{
				switch (animation)
				{
					case AnimationStyle.OriginalAnimation:
						User32NativeMethods.SendMessage(handle, InteropConstants.WM_SYSCOMMAND, InteropConstants.SC_MINIMIZE, 0);
						break;
					case AnimationStyle.NoAnimation:
						TurnOffAnimation();
						User32NativeMethods.SendMessage(handle, InteropConstants.WM_SYSCOMMAND, InteropConstants.SC_MINIMIZE, 0);
						RestoreAnimation();
						break;
				}
			}
			else
			{
				switch (animation)
				{
					case AnimationStyle.OriginalAnimation:
						WINDOWPLACEMENT param = new WINDOWPLACEMENT();
						param.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));

						if (!User32NativeMethods.GetWindowPlacement(handle, ref param))
						{
							WriteToLog($"[{DateTime.Now}] {nameof(MinimizeWindow)} - {nameof(User32NativeMethods.GetWindowPlacement)} returned NULL");

							return;
						}

						param.showCmd = WINDOWPLACEMENT.SW_MINIMIZE;
						
						if (!User32NativeMethods.SetWindowPlacement(handle, ref param))
						{
							WriteToLog($"[{DateTime.Now}] {nameof(MinimizeWindow)} - {nameof(User32NativeMethods.SetWindowPlacement)} returned NULL");

							return;
						}

						break;
					case AnimationStyle.NoAnimation:
						TurnOffAnimation();
						User32NativeMethods.SendMessage(handle, InteropConstants.WM_SYSCOMMAND, InteropConstants.SC_MINIMIZE, 0);
						RestoreAnimation();
						break;
				}
			}
		}
#endif

		public void MoveWindow(IntPtr handle, int left, int top, int width, int height)
		{
			User32NativeMethods.MoveWindow(handle, left, top, width, height, true);
		}

		public void MaximizeWindow(IntPtr handle)
		{
			User32NativeMethods.ShowWindowAsync(handle, InteropConstants.SW_SHOWMAXIMIZED);
        }

		public (int Left, int Top, int Right, int Bottom) GetWindowPosition(IntPtr handle)
		{
			IntPtr res = User32NativeMethods.GetWindowRect(handle, out RECT windowRectangle);
			if (res == IntPtr.Zero)
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetWindowPosition)} - {nameof(User32NativeMethods.GetWindowRect)} returned NULL");

				return (0, 0, 0, 0);
			}

			return (windowRectangle.Left, windowRectangle.Top, windowRectangle.Right, windowRectangle.Bottom);
		}

		public bool IsWindowMaximized(IntPtr handle)
		{
			return User32NativeMethods.IsZoomed(handle);
		}

		public bool IsWindowMinimized(IntPtr handle)
		{
			return User32NativeMethods.IsIconic(handle);
		}

		public IDwmThumbnail GetLiveThumbnail(IntPtr destination, IntPtr source)
		{
			IDwmThumbnail thumbnail = new DwmThumbnail(this);
			thumbnail.Register(destination, source);

			return thumbnail;
		}

		public Image GetStaticThumbnail(IntPtr source)
		{
			const int HGDI_ERROR = 65535;

			IntPtr sourceContext = User32NativeMethods.GetDC(source);
			if (sourceContext == IntPtr.Zero)
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(User32NativeMethods.GetDC)} returned NULL");

				return null;
			}

			if (!User32NativeMethods.GetClientRect(source, out RECT windowRect))
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(User32NativeMethods.GetClientRect)} failed");

				return null;
			}

			var width = windowRect.Right - windowRect.Left;
			var height = windowRect.Bottom - windowRect.Top;

			// Check if there is anything to make thumbnail of
			if ((width < WINDOW_SIZE_THRESHOLD) || (height < WINDOW_SIZE_THRESHOLD))
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - Nothing to draw: (w: {width}) h: {height}");

				return null;
			}

			IntPtr destContext = Gdi32NativeMethods.CreateCompatibleDC(sourceContext);
			if (destContext == IntPtr.Zero)
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(Gdi32NativeMethods.CreateCompatibleDC)} returned NULL");

				return null;
			}

			IntPtr bitmap = Gdi32NativeMethods.CreateCompatibleBitmap(sourceContext, width, height);
			if (bitmap == IntPtr.Zero)
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(Gdi32NativeMethods.CreateCompatibleBitmap)} returned NULL");

				return null;
			}

			IntPtr oldBitmap = Gdi32NativeMethods.SelectObject(destContext, bitmap);
			if (oldBitmap == IntPtr.Zero)
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(Gdi32NativeMethods.SelectObject)} returned NULL");

				return null;
			}

			if (!Gdi32NativeMethods.BitBlt(destContext, 0, 0, width, height, sourceContext, 0, 0, Gdi32NativeMethods.SRCCOPY))
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(Gdi32NativeMethods.BitBlt)} failed");

				return null;
			}

			IntPtr bla = Gdi32NativeMethods.SelectObject(destContext, oldBitmap);
			if (bla == IntPtr.Zero || bla == HGDI_ERROR)
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(Gdi32NativeMethods.SelectObject)} returned {bla}");

				return null;
			}

			if(!Gdi32NativeMethods.DeleteDC(destContext))
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(Gdi32NativeMethods.DeleteDC)} failed");

				return null;
			}


			IntPtr bla2 = User32NativeMethods.ReleaseDC(source, sourceContext);
			if (bla2 == IntPtr.Zero)
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(User32NativeMethods.ReleaseDC)} returned {bla}");

				return null;
			}

			Image image = Image.FromHbitmap(bitmap);

			// ToDo: Use DeleteObject as advised by https://learn.microsoft.com/de-de/windows/win32/api/wingdi/nf-wingdi-deleteobject
			if (!Gdi32NativeMethods.DeleteDC(bitmap))
			{
				WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(Gdi32NativeMethods.DeleteDC)} failed");

				if (!Gdi32NativeMethods.DeleteObject(bitmap))
				{
					WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(Gdi32NativeMethods.DeleteObject)} failed");
				}
				else
				{
					WriteToLog($"[{DateTime.Now}] {nameof(GetStaticThumbnail)} - {nameof(Gdi32NativeMethods.DeleteObject)} succeeded :3");
				}
			}

			return image;
		}
	}
}