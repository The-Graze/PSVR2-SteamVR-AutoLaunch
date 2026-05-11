using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using static PSVR2_AutoLaunch.BluetoothNative;

namespace PSVR2_AutoLaunch
{
    internal enum SenseSide { Left, Right }

    // Auto re-pair flow for PSVR2 Sense controllers.
    //
    // Trigger: a Sense is plugged in over USB.
    // Action : drop the existing Windows BT pairing for that side, then watch for
    //          ~60 s for the controller to advertise in pair mode (user long-presses
    //          PS + Options on it after unplugging) and silently re-authenticate.
    //
    // Rationale: USB plug-in always severs the live BT link on Sense, so there's
    // no reliable way to distinguish "stale pairing" from "currently USB-attached"
    // at plug-in time. We treat any plug-in as the user explicitly asking to
    // re-pair. The tray toggle lets them disable the feature if they only ever
    // plug in to charge.
    internal class SenseAutoRepair
    {
        public const string VendorIdSony    = "054C";
        public const string ProductIdSenseL = "0E45";
        public const string ProductIdSenseR = "0E46";

        // Windows shows these as "PlayStation VR2 Sense Controller (L)" / "(R)" in the
        // Bluetooth stack (confirmed via HKLM\SYSTEM\...\BTHPORT\Parameters\Devices).
        // Match a stable substring of that name.
        private const string BtNamePrefix = "VR2 Sense";

        private static readonly TimeSpan WatchTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ScanGap     = TimeSpan.FromSeconds(2);
        private const byte InquiryTimeoutMultiplier = 4; // 4 * 1.28 s ~= 5 s per inquiry pass

        // Per-side state. The flow has two phases:
        //   PendingUnplug -- USB plug-in seen, BT pair removed, waiting for user to unplug.
        //   Watching      -- USB removal seen, scanner running, looking for pair mode for
        //                    up to WatchTimeout.
        private enum Phase { PendingUnplug, Watching }
        private class SideState
        {
            public Phase Phase;
            public DateTime Deadline; // only meaningful when Phase == Watching
        }

        private readonly NotifyIcon trayIcon;
        private readonly object gate = new object();
        private readonly Dictionary<SenseSide, SideState> states = new Dictionary<SenseSide, SideState>();
        private Thread scannerThread;
        private CancellationTokenSource scannerCts;
        private volatile bool enabled = true;

        // Auth callback: kept as a field so the delegate isn't GC'd while pinned native-side.
        private readonly PFN_AUTHENTICATION_CALLBACK_EX authCallbackDelegate;
        private IntPtr authRegHandle = IntPtr.Zero;

        public SenseAutoRepair(NotifyIcon trayIcon)
        {
            this.trayIcon = trayIcon;
            authCallbackDelegate = OnAuthCallback;
            BluetoothRegisterForAuthenticationEx(IntPtr.Zero, out authRegHandle, authCallbackDelegate, IntPtr.Zero);
        }

        // Called by Windows when an SSP pairing event needs confirmation. Returns true
        // if we handled it (Windows skips its own UI), false to let Windows handle it.
        // We auto-accept for any device whose name matches our Sense pattern; everything
        // else is left to the default Windows flow.
        private bool OnAuthCallback(IntPtr pvParam, ref BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS p)
        {
            if (p.deviceInfo.szName == null) return false;
            if (p.deviceInfo.szName.IndexOf(BtNamePrefix, StringComparison.OrdinalIgnoreCase) < 0) return false;

            var response = new BLUETOOTH_AUTHENTICATE_RESPONSE
            {
                bthAddressRemote = p.deviceInfo.Address,
                authMethod = p.authenticationMethod,
                union_pinInfo_oobInfo_etc = new byte[32],
                negativeResponse = 0, // accept
            };

            BluetoothSendAuthenticationResponseEx(IntPtr.Zero, ref response);
            return true;
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                bool wasEnabled;
                lock (gate)
                {
                    wasEnabled = enabled;
                    enabled = value;
                    if (wasEnabled && !value)
                    {
                        states.Clear();
                        scannerCts?.Cancel();
                    }
                }
            }
        }

        // Returns true and sets `side` when `pnpDeviceId` is a PSVR2 Sense USB path.
        // Expected form: "USB\VID_054C&PID_0E45\..." (case may vary).
        public static bool TryParseSenseSide(string pnpDeviceId, out SenseSide side)
        {
            if (string.IsNullOrEmpty(pnpDeviceId)) { side = default(SenseSide); return false; }
            string upper = pnpDeviceId.ToUpperInvariant();
            if (upper.IndexOf("VID_" + VendorIdSony, StringComparison.Ordinal) < 0)
            {
                side = default(SenseSide);
                return false;
            }
            if (upper.IndexOf("PID_" + ProductIdSenseL, StringComparison.Ordinal) >= 0) { side = SenseSide.Left;  return true; }
            if (upper.IndexOf("PID_" + ProductIdSenseR, StringComparison.Ordinal) >= 0) { side = SenseSide.Right; return true; }
            side = default(SenseSide);
            return false;
        }

        public void OnUsbArrival(SenseSide side)
        {
            // WMI fires plug events once per USB interface (~4 per plug for a Sense
            // composite device). We deduplicate inside the lock so only the first
            // arrival in a chain does the BT work and shows the toast. Re-plug after
            // an unplug (state was Watching) is treated as a new arrival.
            bool firstArrival;
            lock (gate)
            {
                if (!enabled) return;
                if (states.TryGetValue(side, out SideState existingState)
                    && existingState.Phase == Phase.PendingUnplug)
                {
                    return; // duplicate WMI event for the same in-progress plug-in
                }
                states[side] = new SideState
                {
                    Phase = Phase.PendingUnplug,
                    Deadline = DateTime.MaxValue, // sentinel; only set when transitioning to Watching
                };
                firstArrival = true;
            }

            if (!firstArrival) return;

            // Best-effort: drop the existing Windows BT pairing for this side. The
            // 60 s pair-mode watch does not start here - it starts on USB removal.
            var existing = FindPairedSense(side);
            if (existing.HasValue)
            {
                var addr = existing.Value.Address;
                BluetoothRemoveDevice(ref addr);
            }

            ShowToast(
                "PSVR2 Sense",
                "Clearing " + Label(side) + " Sense controller. Unplug USB to kick off auto pairing...",
                ToolTipIcon.Info);
        }

        public void OnUsbDeparture(SenseSide side)
        {
            if (!Enabled) return;

            bool startScanner = false;
            lock (gate)
            {
                if (!states.TryGetValue(side, out SideState state)) return; // never saw a plug-in for this side
                if (state.Phase == Phase.Watching) return; // already watching

                state.Phase = Phase.Watching;
                state.Deadline = DateTime.UtcNow + WatchTimeout;
                startScanner = true;
            }

            if (startScanner)
            {
                // Pair-mode combo differs per side on the PSVR2 Sense:
                //   Left  : PS + Create
                //   Right : PS + Options
                string combo = side == SenseSide.Left ? "PS + Create" : "PS + Options";
                ShowToast(
                    "PSVR2 Sense",
                    "Auto pairing " + Label(side) + " Sense controller for 60 seconds. " +
                    "Long-press " + combo + " until the LEDs pulse blue.",
                    ToolTipIcon.Info);
                EnsureScannerRunning();
            }
        }

        public void Shutdown()
        {
            CancellationTokenSource toDispose = null;
            lock (gate)
            {
                states.Clear();
                if (scannerCts != null)
                {
                    scannerCts.Cancel();
                    toDispose = scannerCts;
                    scannerCts = null;
                }
            }
            toDispose?.Dispose();

            if (authRegHandle != IntPtr.Zero)
            {
                BluetoothUnregisterAuthentication(authRegHandle);
                authRegHandle = IntPtr.Zero;
            }
        }

        private void EnsureScannerRunning()
        {
            CancellationTokenSource toDispose = null;
            lock (gate)
            {
                if (scannerThread != null && scannerThread.IsAlive) return;
                // Previous scanner exited (or never ran). Dispose its CTS if still
                // around to avoid leaking the underlying kernel event handle.
                toDispose = scannerCts;
                scannerCts = new CancellationTokenSource();
                var token = scannerCts.Token;
                scannerThread = new Thread(() => ScannerLoop(token))
                {
                    IsBackground = true,
                    Name = "SenseAutoRepair-Scanner",
                };
                scannerThread.Start();
            }
            toDispose?.Dispose();
        }

        private void ScannerLoop(CancellationToken token)
        {
            try
            {
                ScannerLoopBody(token);
            }
            finally
            {
                // Clear ownership of scannerThread only when this thread is the
                // one currently registered. Avoids racing with EnsureScannerRunning
                // when it has already replaced us.
                lock (gate)
                {
                    if (scannerThread == Thread.CurrentThread)
                        scannerThread = null;
                }
            }
        }

        private void ScannerLoopBody(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // Expire any Watching deadlines that have elapsed and snapshot the
                // currently-watching sides. Sides in PendingUnplug aren't scanned.
                List<SenseSide> expired = new List<SenseSide>();
                List<SenseSide> activeList = new List<SenseSide>();
                lock (gate)
                {
                    foreach (var kv in states)
                    {
                        if (kv.Value.Phase != Phase.Watching) continue;
                        if (DateTime.UtcNow >= kv.Value.Deadline)
                            expired.Add(kv.Key);
                        else
                            activeList.Add(kv.Key);
                    }
                    foreach (var s in expired)
                        states.Remove(s);
                }
                SenseSide[] activeSides = activeList.ToArray();

                foreach (var s in expired)
                {
                    ShowToast(
                        "PSVR2 Sense",
                        "Timed out auto pairing " + Label(s) + " Sense controller. Plug back in to retry.",
                        ToolTipIcon.Warning);
                }

                if (activeSides.Length == 0) return;

                // Inquiry-scan for discoverable BT devices. Blocks ~5 s.
                BLUETOOTH_DEVICE_INFO? leftFound = null;
                BLUETOOTH_DEVICE_INFO? rightFound = null;
                foreach (var d in EnumerateDevices(
                    authenticated: false, remembered: false, unknown: true, connected: false,
                    issueInquiry: true, timeoutMultiplier: InquiryTimeoutMultiplier))
                {
                    if (d.szName == null) continue;
                    if (d.szName.IndexOf(BtNamePrefix, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    if (d.szName.EndsWith("(L)", StringComparison.Ordinal))
                        leftFound = d;
                    else if (d.szName.EndsWith("(R)", StringComparison.Ordinal))
                        rightFound = d;
                }

                if (token.IsCancellationRequested) return;

                // Authenticate any matched-and-watched sides.
                foreach (var side in activeSides)
                {
                    BLUETOOTH_DEVICE_INFO? candidate = side == SenseSide.Left ? leftFound : rightFound;
                    if (!candidate.HasValue) continue;
                    // Double-check: side may have been cleared by Enabled = false or by
                    // a fresh USB plug-in moving it back to PendingUnplug in the gap.
                    lock (gate)
                    {
                        if (!states.TryGetValue(side, out SideState st) || st.Phase != Phase.Watching)
                            continue;
                    }

                    var deviceInfo = candidate.Value;
                    uint err = BluetoothAuthenticateDeviceEx(
                        IntPtr.Zero, IntPtr.Zero, ref deviceInfo, IntPtr.Zero, MITMProtectionNotRequired);
                    string label = Label(side);
                    if (err == 0)
                    {
                        // Bind the HID profile so Windows actually treats this as a gamepad.
                        // Without this, the device appears under "Other devices" and won't
                        // stay connected.
                        var hidGuid = HidServiceClassGuid;
                        BluetoothSetServiceState(
                            IntPtr.Zero, ref deviceInfo, ref hidGuid, BLUETOOTH_SERVICE_ENABLE);

                        lock (gate) { states.Remove(side); }
                        ShowToast(
                            "PSVR2 Sense",
                            "Paired " + label + " Sense controller.",
                            ToolTipIcon.Info);
                    }
                    // err != 0: leave in watch state, retry on the next pass.
                }

                // Pause briefly before the next inquiry pass.
                if (token.WaitHandle.WaitOne(ScanGap)) return;
            }
        }

        private static BLUETOOTH_DEVICE_INFO? FindPairedSense(SenseSide side)
        {
            string suffix = side == SenseSide.Left ? "(L)" : "(R)";
            foreach (var d in EnumerateDevices(
                authenticated: true, remembered: true, unknown: false, connected: true))
            {
                if (d.szName != null
                    && d.szName.IndexOf(BtNamePrefix, StringComparison.OrdinalIgnoreCase) >= 0
                    && d.szName.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return d;
                }
            }
            return null;
        }

        private void ShowToast(string title, string body, ToolTipIcon icon)
        {
            try
            {
                trayIcon.BalloonTipTitle = title;
                trayIcon.BalloonTipText  = body;
                trayIcon.BalloonTipIcon  = icon;
                trayIcon.ShowBalloonTip(7000);
            }
            catch
            {
                // tray icon being disposed during shutdown - swallow
            }
        }

        private static string Label(SenseSide s)
        {
            return s == SenseSide.Left ? "(L)" : "(R)";
        }
    }
}
