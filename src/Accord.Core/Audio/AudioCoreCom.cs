using System.Runtime.InteropServices;

namespace Accord.Core.Audio;

public sealed class LiveAudioEndpoint
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Flow { get; init; } = "Playback"; // "Playback" | "Recording"
}

/// <summary>
/// Direct Core Audio COM wrapper (IMMDeviceEnumerator + IPolicyConfig) with zero PowerShell or external modules.
/// </summary>
public static class AudioCoreCom
{
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid ClsidPolicyConfig = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid IidIPolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");

    private static readonly PropertyKey PkeyDeviceFriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    private static readonly PropertyKey PkeyDeviceInterfaceFriendlyName = new("b3f8fa53-0004-438e-9003-51a46e139bfc", 6);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumEndpointsDelegate(IntPtr self, int dataFlow, uint stateMask, out IntPtr collection);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDefaultDelegate(IntPtr self, int dataFlow, int role, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountDelegate(IntPtr self, out int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ItemDelegate(IntPtr self, int index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenStoreDelegate(IntPtr self, uint access, out IntPtr store);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIdDelegate(IntPtr self, out IntPtr id);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetValueDelegate(IntPtr self, ref PropertyKey key, IntPtr pv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDefaultDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, int role);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPropDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, int fx, ref PropertyKey key, IntPtr pv);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FmtId;
        public uint Pid;

        public PropertyKey(string guid, uint pid)
        {
            FmtId = new Guid(guid);
            Pid = pid;
        }
    }

    private static void EnsureComInitialized()
    {
        // COINIT_APARTMENTTHREADED = 0x2, COINIT_MULTITHREADED = 0x0.
        // If already initialized in another mode (RPC_E_CHANGED_MODE = 0x80010106), both STA and MTA work for MMDeviceEnumerator/IPolicyConfig.
        _ = CoInitializeEx(IntPtr.Zero, 0x0);
    }

    private static IntPtr CreateEnumerator()
    {
        EnsureComInitialized();
        Guid clsid = ClsidMMDeviceEnumerator;
        Guid iid = IidIMMDeviceEnumerator;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out IntPtr obj);
        if (hr < 0 || obj == IntPtr.Zero)
        {
            throw new InvalidOperationException($"IMMDeviceEnumerator init failed: 0x{hr:X8}");
        }
        return obj;
    }

    private static IntPtr CreatePolicyConfig()
    {
        EnsureComInitialized();
        Guid clsid = ClsidPolicyConfig;
        Guid iid = IidIPolicyConfig;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out IntPtr obj);
        if (hr < 0 || obj == IntPtr.Zero)
        {
            throw new InvalidOperationException($"IPolicyConfig init failed: 0x{hr:X8}");
        }
        return obj;
    }

    public static List<LiveAudioEndpoint> ListActiveEndpoints()
    {
        var list = new List<LiveAudioEndpoint>();
        IntPtr enumerator = CreateEnumerator();
        try
        {
            Collect(enumerator, list, 0, "Playback");
            Collect(enumerator, list, 1, "Recording");
        }
        finally
        {
            Release(enumerator);
        }
        return list;
    }

    public static LiveAudioEndpoint? GetDefaultEndpoint(int dataFlow, int role = 1)
    {
        IntPtr enumerator = CreateEnumerator();
        try
        {
            GetDefaultDelegate getFn = Vtable<GetDefaultDelegate>(enumerator, 4);
            int hr = getFn(enumerator, dataFlow, role, out IntPtr device);
            if (hr < 0 || device == IntPtr.Zero) return null;
            try
            {
                return ReadEndpoint(device, dataFlow == 0 ? "Playback" : "Recording");
            }
            finally
            {
                Release(device);
            }
        }
        finally
        {
            Release(enumerator);
        }
    }

    public static void SetDefaultEndpoint(string endpointId, int role)
    {
        IntPtr policy = CreatePolicyConfig();
        try
        {
            SetDefaultDelegate setDefault = Vtable<SetDefaultDelegate>(policy, 13);
            int hr = setDefault(policy, endpointId, role);
            if (hr < 0)
            {
                throw new InvalidOperationException($"SetDefaultEndpoint failed: 0x{hr:X8}");
            }
        }
        finally
        {
            Release(policy);
        }
    }

    public static void SetDefaultAllRoles(string endpointId)
    {
        IntPtr policy = CreatePolicyConfig();
        try
        {
            SetDefaultDelegate setDefault = Vtable<SetDefaultDelegate>(policy, 13);
            // 0 = eConsole, 1 = eMultimedia, 2 = eCommunications
            int hr1 = setDefault(policy, endpointId, 0);
            int hr2 = setDefault(policy, endpointId, 1);
            int hr3 = setDefault(policy, endpointId, 2);
            if (hr1 < 0 && hr2 < 0 && hr3 < 0)
            {
                throw new InvalidOperationException($"SetDefaultAllRoles failed: 0x{hr1:X8}");
            }
        }
        finally
        {
            Release(policy);
        }
    }

    /// <summary>
    /// Updates PKEY_DeviceInterface_FriendlyName via IPolicyConfig::SetPropertyValue so mmsys.cpl reflects it immediately.
    /// </summary>
    public static void SetInterfaceName(string endpointId, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Le nom d'interface est vide.", nameof(value));
        }

        IntPtr policy = CreatePolicyConfig();
        try
        {
            SetPropDelegate setProp = Vtable<SetPropDelegate>(policy, 12);
            PropertyKey key = PkeyDeviceInterfaceFriendlyName;
            IntPtr pv = Marshal.AllocHGlobal(24);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 31); // VT_LPWSTR
                IntPtr strPtr = Marshal.StringToHGlobalUni(value);
                Marshal.WriteIntPtr(pv, 8, strPtr);
                try
                {
                    int hr = setProp(policy, endpointId, 0, ref key, pv);
                    if (hr < 0)
                    {
                        throw new InvalidOperationException($"SetInterfaceName failed: 0x{hr:X8}");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(strPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pv);
            }
        }
        finally
        {
            Release(policy);
        }
    }

    private static void Collect(IntPtr enumerator, List<LiveAudioEndpoint> list, int dataFlow, string flowLabel)
    {
        EnumEndpointsDelegate enumFn = Vtable<EnumEndpointsDelegate>(enumerator, 3);
        int hr = enumFn(enumerator, dataFlow, 1 /* DEVICE_STATE_ACTIVE */, out IntPtr collection);
        if (hr < 0 || collection == IntPtr.Zero) return;
        try
        {
            GetCountDelegate countFn = Vtable<GetCountDelegate>(collection, 3);
            if (countFn(collection, out int count) < 0) return;

            ItemDelegate itemFn = Vtable<ItemDelegate>(collection, 4);
            for (int i = 0; i < count; i++)
            {
                if (itemFn(collection, i, out IntPtr device) < 0 || device == IntPtr.Zero) continue;
                try
                {
                    LiveAudioEndpoint? parsed = ReadEndpoint(device, flowLabel);
                    if (parsed is not null) list.Add(parsed);
                }
                finally
                {
                    Release(device);
                }
            }
        }
        finally
        {
            Release(collection);
        }
    }

    private static LiveAudioEndpoint? ReadEndpoint(IntPtr device, string flowLabel)
    {
        GetIdDelegate getId = Vtable<GetIdDelegate>(device, 5);
        if (getId(device, out IntPtr idPtr) < 0 || idPtr == IntPtr.Zero) return null;

        string id = Marshal.PtrToStringUni(idPtr) ?? string.Empty;
        Marshal.FreeCoTaskMem(idPtr);
        if (string.IsNullOrEmpty(id)) return null;

        return new LiveAudioEndpoint
        {
            Id = id,
            Flow = flowLabel,
            Name = ReadFriendlyName(device)
        };
    }

    private static string ReadFriendlyName(IntPtr device)
    {
        OpenStoreDelegate openStore = Vtable<OpenStoreDelegate>(device, 4);
        if (openStore(device, 0 /* STGM_READ */, out IntPtr store) < 0 || store == IntPtr.Zero)
        {
            return "Inconnu";
        }

        try
        {
            PropertyKey key = PkeyDeviceFriendlyName;
            IntPtr pv = Marshal.AllocHGlobal(24);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                GetValueDelegate getValue = Vtable<GetValueDelegate>(store, 5);
                if (getValue(store, ref key, pv) < 0) return "Inconnu";

                short vt = Marshal.ReadInt16(pv);
                if (vt != 31)
                {
                    PropVariantClear(pv);
                    return "Inconnu";
                }

                IntPtr strPtr = Marshal.ReadIntPtr(pv, 8);
                string? value = strPtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(strPtr);
                PropVariantClear(pv);
                return string.IsNullOrWhiteSpace(value) ? "Inconnu" : value;
            }
            finally
            {
                Marshal.FreeHGlobal(pv);
            }
        }
        finally
        {
            Release(store);
        }
    }

    private static T Vtable<T>(IntPtr comPtr, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comPtr);
        IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static void Release(IntPtr comPtr)
    {
        if (comPtr == IntPtr.Zero) return;
        ReleaseDelegate rel = Vtable<ReleaseDelegate>(comPtr, 2);
        rel(comPtr);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr pv);
}
