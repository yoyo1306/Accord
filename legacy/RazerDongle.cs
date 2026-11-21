using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoAudioSwitch
{
    public enum RazerHeadsetState
    {
        Disconnected = 0,
        Connected = 1,
        Unknown = 2
    }

    // BlackShark V3 Pro HyperSpeed dongle (VID 1532 / PID 0577)
    // Same query as Synapse getWirelessConnectionStatus.
    public static class RazerDongle
    {
        private const int ReportLength = 64;
        private const int IoTimeoutMs = 450;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        public static RazerHeadsetState GetWirelessState()
        {
            try
            {
                string path = FindDongleControlPath();
                if (string.IsNullOrEmpty(path))
                {
                    return RazerHeadsetState.Disconnected;
                }

                return QueryPowerState(path);
            }
            catch
            {
                return RazerHeadsetState.Unknown;
            }
        }

        private static byte[] CreatePowerStatusQuery()
        {
            byte[] report = new byte[ReportLength];
            report[0] = 2;
            report[2] = 0x60;
            report[6] = 4;
            report[10] = 0x20;
            report[ReportLength - 2] = CalculateChecksum(report);
            return report;
        }

        private static RazerHeadsetState ParsePowerStatusResponse(byte[] response, int count)
        {
            if (count < ReportLength ||
                response[0] != 2 ||
                response[1] != 2 ||
                response[2] != 0x60 ||
                response[10] != 0x20 ||
                response[11] != 1 ||
                response[ReportLength - 2] != CalculateChecksum(response))
            {
                return RazerHeadsetState.Unknown;
            }

            if (response[13] == 1) return RazerHeadsetState.Connected;
            if (response[13] == 0) return RazerHeadsetState.Disconnected;
            return RazerHeadsetState.Unknown;
        }

        private static RazerHeadsetState QueryPowerState(string path)
        {
            SafeFileHandle handle = CreateFile(
                path,
                0x80000000u | 0x40000000u,
                0x00000001u | 0x00000002u,
                IntPtr.Zero,
                3u,
                0x40000000u,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return RazerHeadsetState.Unknown;
            }

            using (handle)
            using (FileStream stream = new FileStream(handle, FileAccess.ReadWrite, ReportLength, true))
            {
                byte[] query = CreatePowerStatusQuery();
                IAsyncResult write = stream.BeginWrite(query, 0, query.Length, null, null);
                if (!write.AsyncWaitHandle.WaitOne(IoTimeoutMs))
                {
                    return RazerHeadsetState.Unknown;
                }
                stream.EndWrite(write);

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(IoTimeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    byte[] response = new byte[ReportLength];
                    IAsyncResult read = stream.BeginRead(response, 0, response.Length, null, null);
                    int remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (!read.AsyncWaitHandle.WaitOne(remaining))
                    {
                        return RazerHeadsetState.Unknown;
                    }

                    int count = stream.EndRead(read);
                    RazerHeadsetState state = ParsePowerStatusResponse(response, count);
                    if (state != RazerHeadsetState.Unknown)
                    {
                        return state;
                    }
                }

                return RazerHeadsetState.Unknown;
            }
        }

        private static byte CalculateChecksum(byte[] report)
        {
            byte checksum = 0;
            for (int index = 0; index < ReportLength - 2; index++)
            {
                checksum ^= report[index];
            }
            return checksum;
        }

        private static string FindDongleControlPath()
        {
            foreach (string path in EnumerateHidPaths())
            {
                if (path.IndexOf("vid_1532", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    path.IndexOf("pid_0577", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    path.IndexOf("col04", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return path;
                }
            }
            return null;
        }

        private static IEnumerable<string> EnumerateHidPaths()
        {
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr infoSet = SetupDiGetClassDevs(
                ref hidGuid,
                IntPtr.Zero,
                IntPtr.Zero,
                0x00000002u | 0x00000010u);
            if (infoSet == InvalidHandleValue)
            {
                yield break;
            }

            try
            {
                uint index = 0;
                while (true)
                {
                    SP_DEVICE_INTERFACE_DATA interfaceData = new SP_DEVICE_INTERFACE_DATA();
                    interfaceData.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(
                        infoSet,
                        IntPtr.Zero,
                        ref hidGuid,
                        index++,
                        ref interfaceData))
                    {
                        if (Marshal.GetLastWin32Error() == 259)
                        {
                            break;
                        }
                        continue;
                    }

                    uint requiredSize;
                    SetupDiGetDeviceInterfaceDetail(
                        infoSet,
                        ref interfaceData,
                        IntPtr.Zero,
                        0,
                        out requiredSize,
                        IntPtr.Zero);
                    if (requiredSize == 0)
                    {
                        continue;
                    }

                    IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(
                            infoSet,
                            ref interfaceData,
                            detail,
                            requiredSize,
                            out requiredSize,
                            IntPtr.Zero))
                        {
                            string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                            if (!string.IsNullOrEmpty(path))
                            {
                                yield return path;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(infoSet);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid interfaceClassGuid;
            public int flags;
            public UIntPtr reserved;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            IntPtr enumerator,
            IntPtr parent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr infoSet,
            IntPtr deviceInfo,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SP_DEVICE_INTERFACE_DATA interfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr infoSet,
            ref SP_DEVICE_INTERFACE_DATA interfaceData,
            IntPtr detailData,
            uint detailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr infoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string path,
            uint access,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flags,
            IntPtr templateFile);
    }
}
