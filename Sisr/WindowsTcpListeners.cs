using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SBridge.Sisr;

[SupportedOSPlatform("windows")]
internal static class WindowsTcpListeners
{
    private const uint InsufficientBuffer = 122;

    public static IReadOnlyList<IPEndPoint> ForProcess(int processId)
    {
        var endpoints = new List<IPEndPoint>();
        ReadTable(2, 24, processId, endpoints); // AF_INET, MIB_TCPROW_OWNER_PID
        ReadTable(23, 56, processId, endpoints); // AF_INET6, MIB_TCP6ROW_OWNER_PID
        return endpoints;
    }

    public static bool IsOwned(int processId, IPEndPoint endpoint)
    {
        foreach (var candidate in ForProcess(processId))
            if (candidate.Equals(endpoint)) return true;
        return false;
    }

    private static void ReadTable(int family, int rowSize, int processId, List<IPEndPoint> endpoints)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int size = 0;
            uint error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (error != 0 && error != InsufficientBuffer) throw new Win32Exception((int)error);
            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                error = GetExtendedTcpTable(table, ref size, false, family, 3, 0);
                if (error == InsufficientBuffer) continue;
                if (error != 0) throw new Win32Exception((int)error);
                int count = Marshal.ReadInt32(table);
                if (count < 0 || 4L + (long)count * rowSize > size)
                    throw new InvalidOperationException("Invalid Windows TCP listener table.");
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(table, 4 + i * rowSize);
                    int pidOffset = family == 2 ? 20 : 52;
                    if (Marshal.ReadInt32(row, pidOffset) != processId) continue;
                    int addressOffset = family == 2 ? 4 : 0;
                    byte[] addressBytes = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(IntPtr.Add(row, addressOffset), addressBytes, 0, addressBytes.Length);
                    IPAddress address = family == 2 ? new IPAddress(addressBytes)
                        : new IPAddress(addressBytes, unchecked((uint)Marshal.ReadInt32(row, 16)));
                    if (!IPAddress.IsLoopback(address)) continue;
                    int portOffset = family == 2 ? 8 : 20;
                    int port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                    if (port != 0) endpoints.Add(new IPEndPoint(address, port));
                }
                return;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw new InvalidOperationException("Windows TCP listeners changed repeatedly during discovery.");
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
}
