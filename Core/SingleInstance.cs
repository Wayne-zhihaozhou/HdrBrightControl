using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HdrBrightControl
{
    /// <summary>
    /// Keeps a single copy of the application alive. The first process owns the mutex and listens for activation
    /// requests; every later process asks that copy to show its window and then exits without creating any UI.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        private const string MutexName = @"Local\HdrBrightControl.SingleInstance";
        private const string ActivationEventName = @"Local\HdrBrightControl.Activate";
        private const int ActivationSignalIndex = 0;
        private const int AnyProcessId = -1;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int processId);

        private readonly Mutex _mutex;
        private readonly EventWaitHandle _activationSignal;
        private readonly ManualResetEvent _shutdownSignal = new(false);

        private Control? _dispatcher;
        private Action? _showWindow;
        private Thread? _listener;

        private SingleInstance(Mutex mutex, EventWaitHandle activationSignal)
        {
            _mutex = mutex;
            _activationSignal = activationSignal;
        }

        /// <summary>
        /// Returns the running copy of the application, or <c>null</c> when another copy already holds the mutex.
        /// In that case the running copy has been asked to show its window and the caller must exit immediately.
        /// </summary>
        public static SingleInstance? TryStartPrimary()
        {
            var mutex = new Mutex(false, MutexName);
            if (!TryTakeOwnership(mutex))
            {
                mutex.Dispose();
                RequestShowWindowFromRunningCopy();
                return null;
            }

            return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName));
        }

        /// <summary>
        /// Runs <paramref name="showWindow"/> on the UI thread of <paramref name="dispatcher"/> whenever another copy
        /// of the executable is launched. Call once on the UI thread, before the message loop starts pumping.
        /// </summary>
        public void ListenForActivationRequests(Control dispatcher, Action showWindow)
        {
            _dispatcher = dispatcher;
            _showWindow = showWindow;

            _listener = new Thread(Listen) { IsBackground = true, Name = "ActivationListener" };
            _listener.Start();
        }

        private static bool TryTakeOwnership(Mutex mutex)
        {
            try
            {
                return mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        private static void RequestShowWindowFromRunningCopy()
        {
            AllowSetForegroundWindow(AnyProcessId);

            try
            {
                using var signal = EventWaitHandle.OpenExisting(ActivationEventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return;
            }
        }

        private void Listen()
        {
            var signals = new WaitHandle[] { _activationSignal, _shutdownSignal };

            while (WaitHandle.WaitAny(signals) == ActivationSignalIndex)
                DispatchShowWindow();
        }

        /// <summary>
        /// Posts the activation to the UI thread. A launch can race with the exit of the running copy, and then the
        /// dispatcher window is already gone; the request is dropped because the launching process takes over as primary.
        /// </summary>
        private void DispatchShowWindow()
        {
            Control? dispatcher = _dispatcher;
            Action? showWindow = _showWindow;
            if (dispatcher is null || showWindow is null) return;

            try
            {
                if (dispatcher.IsHandleCreated && !dispatcher.IsDisposed)
                    dispatcher.BeginInvoke(showWindow);
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        public void Dispose()
        {
            _shutdownSignal.Set();
            _listener?.Join();

            _activationSignal.Dispose();
            _shutdownSignal.Dispose();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
