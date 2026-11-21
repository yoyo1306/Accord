using System.Runtime.InteropServices;
using System.Text;

namespace Accord.Core.Hardware;

/// <summary>
/// Checks live Bluetooth device presence via SetupAPI + CfgMgr32 (e.g. Pixel Buds).
/// </summary>
public static class BluetoothDetector
{
    private static readonly Guid BluetoothClassGuid = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");

    public static bool IsDeviceConnected(string nameContains = "Pixel Buds")
    {
        Guid bt = BluetoothClassGuid;
        IntPtr info = SetupDiGetClassDevs(ref bt, null, IntPtr.Zero, 2 /* DIGCF_PRESENT */);
        if (info == new IntPtr(-1)) return false;

        try
        {
            uint index = 0;
            while (true)
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(info, index++, ref data)) break;

                if (CM_Get_DevNode_Status(out _, out uint problem, data.devInst, 0) != 0 || problem != 0)
                {
                    continue;
                }

                string name = ReadName(info, ref data);
                if (name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(info);
        }

        return false;
    }

    private static string ReadName(IntPtr info, ref SP_DEVINFO_DATA data)
    {
        string friendly = ReadProp(info, ref data, 12 /* SPDRP_FRIENDLYNAME */);
        if (!string.IsNullOrEmpty(friendly)) return friendly;
        return ReadProp(info, ref data, 0 /* SPDRP_DEVICEDESC */);
    }

    private static string ReadProp(IntPtr info, ref SP_DEVINFO_DATA data, uint property)
    {
        byte[] buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryProperty(info, ref data, property, out _, buffer, (uint)buffer.Length, out uint required))
        {
            return string.Empty;
        }

        int len = (int)Math.Min(required, (uint)buffer.Length);
        return Encoding.Unicode.GetString(buffer, 0, len).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid classGuid;
        public uint devInst;
        public IntPtr reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr infoSet, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr infoSet,
        ref SP_DEVINFO_DATA data,
        uint property,
        out uint regType,
        byte[] buffer,
        uint size,
        out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr infoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
