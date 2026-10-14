using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Sample.WPF
{
    /// <summary>
    /// Win32 helpers to find and hide windows by title (e.g. scanner driver "scan status" dialogs).
    /// </summary>
    public static class NativeWindowHelper
    {
        public const int SW_HIDE = 0;

        public static readonly string[] ScanStatusWindowTitleSubstrings = new[]
        {
            "扫描状态",
            "Scan Status"
        };

        public static void HideScanStatusWindows()
        {
            NativeMethods.EnumWindows(EnumCallback, IntPtr.Zero);
        }

        private static bool EnumCallback(IntPtr hWnd, IntPtr lParam)
        {
            var sb = new StringBuilder(256);
            if (NativeMethods.GetWindowText(hWnd, sb, sb.Capacity) <= 0)
                return true;

            var title = sb.ToString();
            foreach (var substring in ScanStatusWindowTitleSubstrings)
            {
                if (title.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    NativeMethods.ShowWindow(hWnd, SW_HIDE);
                    break;
                }
            }

            return true;
        }

        public static class NativeMethods
        {
            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        }
    }
}
