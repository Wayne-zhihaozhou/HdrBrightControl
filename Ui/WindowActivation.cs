using System;
using System.Runtime.InteropServices;

namespace HdrBrightControl
{
    /// <summary>
    /// Brings a window to the foreground even when another process currently owns the foreground role.
    /// Show/Activate alone is not enough in that case, so the calling thread temporarily attaches to the input queue
    /// of the foreground thread, which makes the activation request acceptable to Windows.
    /// </summary>
    internal static class WindowActivation
    {
        private const int ShowRestored = 9;
        private const uint UnknownThreadId = 0;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public static void BringToForeground(IntPtr window)
        {
            if (window == IntPtr.Zero) return;

            if (IsIconic(window)) ShowWindow(window, ShowRestored);

            uint currentThread = GetCurrentThreadId();
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);

            if (foregroundThread == UnknownThreadId || foregroundThread == currentThread)
            {
                SetForegroundWindow(window);
                return;
            }

            AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
            finally
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }
}
