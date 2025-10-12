using System.Runtime.InteropServices;
using System.Windows;

namespace EveOPreview.Services.Interop
{
	[StructLayout(LayoutKind.Sequential)]
	class DWM_THUMBNAIL_PROPERTIES
	{
		public uint dwFlags;
		public Rect rcDestination;
		public Rect rcSource;
		public byte opacity;
		[MarshalAs(UnmanagedType.Bool)]
		public bool fVisible;
		[MarshalAs(UnmanagedType.Bool)]
		public bool fSourceClientAreaOnly;
	}
}