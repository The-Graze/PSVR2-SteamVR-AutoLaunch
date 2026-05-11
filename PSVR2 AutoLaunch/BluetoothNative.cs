using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PSVR2_AutoLaunch
{
    // Minimal P/Invoke surface over the Win32 Bluetooth API (BluetoothApis.h).
    // Exported by bthprops.cpl on every supported Windows version.
    internal static class BluetoothNative
    {
        private const string BthDll = "bthprops.cpl";

        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_ADDRESS
        {
            public ulong ullLong;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct BLUETOOTH_DEVICE_INFO
        {
            public uint dwSize;
            public BLUETOOTH_ADDRESS Address;
            public uint ulClassofDevice;
            [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string szName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public uint dwSize;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public uint dwSize;
        }

        // BLUETOOTH_AUTHENTICATION_REQUIREMENTS
        public const uint MITMProtectionNotRequired              = 0;
        public const uint MITMProtectionRequired                 = 1;
        public const uint MITMProtectionNotRequiredBonding       = 2;
        public const uint MITMProtectionRequiredBonding          = 3;
        public const uint MITMProtectionNotRequiredGeneralBonding = 4;
        public const uint MITMProtectionRequiredGeneralBonding   = 5;
        public const uint MITMProtectionNotDefined               = 6;

        // BLUETOOTH_AUTHENTICATION_METHOD
        public const uint BLUETOOTH_AUTHENTICATION_METHOD_LEGACY               = 0x1;
        public const uint BLUETOOTH_AUTHENTICATION_METHOD_OOB                  = 0x2;
        public const uint BLUETOOTH_AUTHENTICATION_METHOD_NUMERIC_COMPARISON   = 0x3;
        public const uint BLUETOOTH_AUTHENTICATION_METHOD_PASSKEY_NOTIFICATION = 0x4;
        public const uint BLUETOOTH_AUTHENTICATION_METHOD_PASSKEY              = 0x5;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS
        {
            public BLUETOOTH_DEVICE_INFO deviceInfo;
            public uint authenticationMethod; // BLUETOOTH_AUTHENTICATION_METHOD
            public uint ioCapability;
            public uint authenticationRequirements;
            public uint Numeric_Value_Or_Passkey;
        }

        // Response struct with the union laid out as raw bytes. For NUMERIC_COMPARISON
        // / PASSKEY_NOTIFICATION (the Just Works case for HID NoInputNoOutput devices)
        // we just need to set negativeResponse=0; the OS doesn't care about the union
        // payload in that case.
        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_AUTHENTICATE_RESPONSE
        {
            public BLUETOOTH_ADDRESS bthAddressRemote;
            public uint authMethod; // BLUETOOTH_AUTHENTICATION_METHOD
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] union_pinInfo_oobInfo_etc; // sized to BLUETOOTH_OOB_DATA_INFO (the largest union member)
            public byte negativeResponse;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public delegate bool PFN_AUTHENTICATION_CALLBACK_EX(
            IntPtr pvParam,
            ref BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS pAuthCallbackParams);

        [DllImport(BthDll, CharSet = CharSet.Unicode)]
        public static extern uint BluetoothRegisterForAuthenticationEx(
            IntPtr pbtdiIn, // BLUETOOTH_DEVICE_INFO* - NULL = all devices
            out IntPtr phRegHandleOut,
            PFN_AUTHENTICATION_CALLBACK_EX pfnCallbackIn,
            IntPtr pvParam);

        [DllImport(BthDll)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BluetoothUnregisterAuthentication(IntPtr hRegHandle);

        [DllImport(BthDll, CharSet = CharSet.Unicode)]
        public static extern uint BluetoothSendAuthenticationResponseEx(
            IntPtr hRadioIn, // NULL = any radio
            ref BLUETOOTH_AUTHENTICATE_RESPONSE pauthResponse);

        // Service-state binding (e.g. attach the HID profile to a freshly paired device).
        public const uint BLUETOOTH_SERVICE_DISABLE = 0x00;
        public const uint BLUETOOTH_SERVICE_ENABLE  = 0x01;

        // {00001124-0000-1000-8000-00805F9B34FB} - HumanInterfaceDeviceServiceClass_UUID
        public static readonly Guid HidServiceClassGuid =
            new Guid("00001124-0000-1000-8000-00805F9B34FB");

        [DllImport(BthDll, CharSet = CharSet.Unicode)]
        public static extern uint BluetoothSetServiceState(
            IntPtr hRadio, // NULL = any radio
            ref BLUETOOTH_DEVICE_INFO pbtdi,
            ref Guid pGuidService,
            uint dwServiceFlags);

        [DllImport(BthDll, CharSet = CharSet.Unicode)]
        public static extern IntPtr BluetoothFindFirstRadio(
            ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp,
            out IntPtr phRadio);

        [DllImport(BthDll)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport(BthDll, CharSet = CharSet.Unicode)]
        public static extern IntPtr BluetoothFindFirstDevice(
            ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp,
            ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport(BthDll, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport(BthDll)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BluetoothFindDeviceClose(IntPtr hFind);

        [DllImport(BthDll)]
        public static extern uint BluetoothRemoveDevice(ref BLUETOOTH_ADDRESS pAddress);

        [DllImport(BthDll, CharSet = CharSet.Unicode)]
        public static extern uint BluetoothAuthenticateDeviceEx(
            IntPtr hwndParentIn,
            IntPtr hRadioIn,
            ref BLUETOOTH_DEVICE_INFO pbtdiInout,
            IntPtr pbtOobData,
            uint authenticationRequirement);

        public static string AddressToString(BLUETOOTH_ADDRESS addr)
        {
            return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                (addr.ullLong >> 40) & 0xFF,
                (addr.ullLong >> 32) & 0xFF,
                (addr.ullLong >> 24) & 0xFF,
                (addr.ullLong >> 16) & 0xFF,
                (addr.ullLong >> 8) & 0xFF,
                addr.ullLong & 0xFF);
        }

        // Enumerate devices currently known to the BT stack.
        // Authenticated/Remembered = previously paired (may or may not be connected now).
        // Unknown = unpaired devices currently discoverable (only meaningful if fIssueInquiry is set).
        public static IEnumerable<BLUETOOTH_DEVICE_INFO> EnumerateDevices(
            bool authenticated = true,
            bool remembered    = true,
            bool unknown       = false,
            bool connected     = true,
            bool issueInquiry  = false,
            byte timeoutMultiplier = 2)
        {
            var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
            {
                dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS)),
                fReturnAuthenticated = authenticated,
                fReturnRemembered    = remembered,
                fReturnUnknown       = unknown,
                fReturnConnected     = connected,
                fIssueInquiry        = issueInquiry,
                cTimeoutMultiplier   = timeoutMultiplier,
                hRadio               = IntPtr.Zero,
            };
            var info = new BLUETOOTH_DEVICE_INFO
            {
                dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO)),
            };

            IntPtr hFind = BluetoothFindFirstDevice(ref search, ref info);
            if (hFind == IntPtr.Zero) yield break;

            try
            {
                do
                {
                    yield return info;
                    info = new BLUETOOTH_DEVICE_INFO
                    {
                        dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO)),
                    };
                }
                while (BluetoothFindNextDevice(hFind, ref info));
            }
            finally
            {
                BluetoothFindDeviceClose(hFind);
            }
        }
    }
}
