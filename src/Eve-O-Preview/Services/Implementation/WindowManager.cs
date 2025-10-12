using EveOPreview.Configuration;
using EveOPreview.Services.Interop;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Media3D;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

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

		public static void WriteToLog(string message)
		{
			try
			{
				Trace.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] " + message);
				File.AppendAllText(EXCEPTION_DUMP_FILE_NAME, $"[{DateTime.Now}] " + message + Environment.NewLine);
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
			return PInvoke.GetForegroundWindow();
		}

		private void OnTimerEvent(object stateInfo)
		{
			IntPtr hProc;
			IntPtr hWnd;

			//hProc = Process.GetCurrentProcess().Handle;
			//hWnd = Process.GetCurrentProcess().MainWindowHandle;


			//var count = PInvoke.GetGuiResourcesGDICount(hProc);
			//var peak = PInvoke.GetGuiResourcesGDICountPeak(hProc);

			//WriteToLog($"Process - GDIs: {count}. GDI peak: {peak}");

			//count = PInvoke.GetGuiResourcesGDICount(hWnd);
			//peak = PInvoke.GetGuiResourcesGDICountPeak(hWnd);
		 //   // WriteToLog($"MainWindow -  GDIs: {count} peak: {peak}");

			var handleCount = Process.GetCurrentProcess().HandleCount;
			WriteToLog($"Handle count: {handleCount}");
		}

		private unsafe void TurnOffAnimation()
		{
			var info = new ANIMATIONINFO();

			var res = PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETANIMATION, _animationParam.cbSize, Unsafe.AsPointer(ref _animationParam), 0);
            if (!res)
            {
                WindowManager.WriteToLog($"{nameof(RestoreAnimation)} - ERR: SystemParametersInfo with SPI_SETANMIATION failed");
            }

            if (_currentAnimationSetting == null)
			{
				// Store the current Animation Setting
				_currentAnimationSetting = _animationParam.iMinAnimate;
                
            }

			if (_animationParam.iMinAnimate != NO_ANIMATION)
			{
				// Turn off Animation
				_animationParam.iMinAnimate = NO_ANIMATION;
				res = PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETANIMATION, _animationParam.cbSize, Unsafe.AsPointer(ref _animationParam), 0);
                if (!res)
                {
                    WindowManager.WriteToLog($"{nameof(RestoreAnimation)} - ERR: SystemParametersInfo with SPI_SETANMIATION failed");
                }
            }
		}

		private unsafe void RestoreAnimation()
		{
			var currentAnimationSetup = PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETANIMATION, _animationParam.cbSize, Unsafe.AsPointer(ref _animationParam), 0);
			// Restore current Animation Settings
			if (_animationParam.iMinAnimate != (int)_currentAnimationSetting)
			{
				_animationParam.iMinAnimate = (int)_currentAnimationSetting;
				var res = PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETANIMATION, _animationParam.cbSize, Unsafe.AsPointer(ref _animationParam), 0);
				if(!res)
				{
					WindowManager.WriteToLog($"{nameof(RestoreAnimation)} - ERR: SystemParametersInfo with SPI_SETANMIATION failed");
				}
			}
		}

		// if building for LINUX the window handling is slightly different
#if LINUX
		private void WindowsActivateWindow(IntPtr handle)
		{
			PInvoke.SetForegroundWindow((HWND)handle);
            PInvoke.SetFocus((HWND)handle);

			int style = PInvoke.GetWindowLong((HWND)handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE);

			if ((style & InteropConstants.WS_MINIMIZE) == InteropConstants.WS_MINIMIZE)
			{
                PInvoke.ShowWindowAsync((HWND)handle, SHOW_WINDOW_CMD.SW_RESTORE);
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

        public unsafe void MinimizeWindow(IntPtr handle, bool enableAnimation)
		{
			if (enableAnimation)
			{
                PInvoke.SendMessage((HWND)handle, InteropConstants.WM_SYSCOMMAND, InteropConstants.SC_MINIMIZE, 0);
			}
			else
			{
				WINDOWPLACEMENT param = new WINDOWPLACEMENT();
				param.length = (uint)Marshal.SizeOf(param);
                PInvoke.GetWindowPlacement((HWND)handle, ref param);
				param.showCmd = SHOW_WINDOW_CMD.SW_MINIMIZE;
                PInvoke.SetWindowPlacement((HWND)handle, in param);
			}
		}

#endif

#if WINDOWS
		public void ActivateWindow(IntPtr handle, AnimationStyle animation)
		{
            PInvoke.SetForegroundWindow(new(handle));
            PInvoke.SetFocus(new(handle));

			int style = PInvoke.GetWindowLong(new(handle), WINDOW_LONG_PTR_INDEX.GWL_STYLE);

			if ((style & InteropConstants.WS_MINIMIZE) == InteropConstants.WS_MINIMIZE)
			{
				switch (animation)
				{
					case AnimationStyle.OriginalAnimation:
                        PInvoke.ShowWindowAsync(new(handle), SHOW_WINDOW_CMD.SW_RESTORE);
						break;
					case AnimationStyle.NoAnimation:
						TurnOffAnimation();
                        PInvoke.ShowWindowAsync(new(handle), SHOW_WINDOW_CMD.SW_RESTORE);
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
                        PInvoke.SendMessage(new(handle), InteropConstants.WM_SYSCOMMAND, InteropConstants.SC_MINIMIZE, 0);
						break;
					case AnimationStyle.NoAnimation:
						TurnOffAnimation();
                        PInvoke.SendMessage(new(handle), InteropConstants.WM_SYSCOMMAND, InteropConstants.SC_MINIMIZE, 0);
						RestoreAnimation();
						break;
				}
			}
			else
			{
				switch (animation)
				{
					case AnimationStyle.OriginalAnimation:
						WINDOWPLACEMENT param = new();

						if (!PInvoke.GetWindowPlacement(new(handle), ref param))
						{
							WriteToLog($"{nameof(MinimizeWindow)} - {nameof(PInvoke.GetWindowPlacement)} returned NULL");

							return;
						}

						param.showCmd = SHOW_WINDOW_CMD.SW_MINIMIZE;
						
						if (!PInvoke.SetWindowPlacement(new(handle), ref param))
						{
							WriteToLog($"{nameof(MinimizeWindow)} - {nameof(PInvoke.SetWindowPlacement)} returned NULL");

							return;
						}

						break;
					case AnimationStyle.NoAnimation:
						TurnOffAnimation();
                        PInvoke.SendMessage(new(handle), InteropConstants.WM_SYSCOMMAND, InteropConstants.SC_MINIMIZE, 0);
						RestoreAnimation();
						break;
				}
			}
		}
#endif

		public void MoveWindow(IntPtr handle, int left, int top, int width, int height)
		{
            PInvoke.MoveWindow(new(handle), left, top, width, height, true);
		}

		public void MaximizeWindow(IntPtr handle)
		{
            PInvoke.ShowWindowAsync(new(handle), Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED);
        }

		public (int Left, int Top, int Right, int Bottom) GetWindowPosition(IntPtr handle)
		{
			IntPtr res = PInvoke.GetWindowRect(new(handle), out RECT windowRectangle);
			if (res == IntPtr.Zero)
			{
				WriteToLog($"{nameof(GetWindowPosition)} - {nameof(PInvoke.GetWindowRect)} returned NULL");

				return (0, 0, 0, 0);
			}

			return (windowRectangle.left, windowRectangle.top, windowRectangle.right, windowRectangle.bottom);
		}

		public bool IsWindowMaximized(IntPtr handle)
		{
			return PInvoke.IsZoomed(new(handle));
		}

		public bool IsWindowMinimized(IntPtr handle)
		{
			return PInvoke.IsIconic(new(handle));
		}

		public IDwmThumbnail GetLiveThumbnail(IntPtr destination, IntPtr source)
		{
			IDwmThumbnail thumbnail = new DwmThumbnail(this);
			thumbnail.Register(destination, source);

			return thumbnail;
		}

		public Image GetStaticThumbnail(IntPtr source)
		{
			// https://stackoverflow.com/questions/15870591/why-does-createcompatiblebitmap-fail-after-about-a-thousand-executions
			HWND hWnd = new(source);
			var hWindowDC = PInvoke.GetDC(hWnd);

			PInvoke.GetClientRect(hWnd, out RECT rect);

            var width = rect.right - rect.left;
            var height = rect.bottom - rect.top;

            // Check if there is anything to make thumbnail of
            if ((width < WINDOW_SIZE_THRESHOLD) || (height < WINDOW_SIZE_THRESHOLD))
            {
                return null;
            }

            var hWindowCompDC = PInvoke.CreateCompatibleDC(hWindowDC);
            // Gdi32NativeMethods.SetStretchBltMode(hWindowCompDC, COLORONCOLOR);

			// Create bitmap
            var hBmp = PInvoke.CreateCompatibleBitmap(hWindowDC, width, height);

			// Save old bitmap
            var hOldBmp = PInvoke.SelectObject(hWindowCompDC, hBmp); //copy from hwindowCompatibleDC to hbwindow
            if(!PInvoke.BitBlt(hWindowCompDC, 0, 0, width, height, hWindowDC, 0, 0, ROP_CODE.SRCCOPY))
			{
				WriteToLog($"{GetStaticThumbnail} - BitBlt failed");
			}

            // Restore old bitmap
            PInvoke.SelectObject(hWindowCompDC, hOldBmp);
            PInvoke.DeleteDC(hWindowCompDC);
            PInvoke.ReleaseDC(hWnd, hWindowDC);

            Image image = Image.FromHbitmap(hBmp);
            PInvoke.DeleteObject(hBmp);

			// image.Save("asdf.bmp");

            return image;
        }
	}
}