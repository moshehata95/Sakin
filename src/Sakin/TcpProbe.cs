using System.Runtime.InteropServices;

namespace Sakin;

/// <summary>
/// Local TCP connections between two processes on this PC. Claude's tools talk to apps this way
/// (an MCP bridge inside Premiere, a local server, a debugging port), so a live connection between
/// Claude and an app means Claude is using that app right now.
/// </summary>
internal static class TcpProbe
{
    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int cls, int reserved);
    const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    const int ESTABLISHED = 5;

    /// <summary>Pairs (pid, pid) at the two ends of every established loopback connection.</summary>
    public static List<(int a, int b)> LoopbackPairs()
    {
        var pairs = new List<(int, int)>();
        Scan(2, pairs);    // IPv4
        Scan(23, pairs);   // IPv6
        return pairs;
    }

    static void Scan(int af, List<(int, int)> pairs)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            int sz = size + 4096;
            IntPtr buf = Marshal.AllocHGlobal(sz);
            try
            {
                uint rc = GetExtendedTcpTable(buf, ref sz, false, af, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
                if (rc == 122) { size = sz; continue; }   // the table grew meanwhile
                if (rc != 0) return;
                int n = Marshal.ReadInt32(buf);
                var byPort = new Dictionary<int, int>();
                var conns = new List<(int rport, int pid)>();
                for (int i = 0; i < n; i++)
                {
                    int state, lport, rport, pid; bool loop;
                    if (af == 2)
                    {
                        IntPtr row = buf + 4 + i * 24;   // MIB_TCPROW_OWNER_PID
                        state = Marshal.ReadInt32(row, 0);
                        int la = Marshal.ReadInt32(row, 4), ra = Marshal.ReadInt32(row, 12);
                        lport = Port(Marshal.ReadInt32(row, 8));
                        rport = Port(Marshal.ReadInt32(row, 16));
                        pid = Marshal.ReadInt32(row, 20);
                        loop = (la & 0xFF) == 127 && (ra & 0xFF) == 127;
                    }
                    else
                    {
                        IntPtr row = buf + 4 + i * 56;   // MIB_TCP6ROW_OWNER_PID
                        loop = IsV6Loopback(row) && IsV6Loopback(row + 24);
                        lport = Port(Marshal.ReadInt32(row, 20));
                        rport = Port(Marshal.ReadInt32(row, 44));
                        state = Marshal.ReadInt32(row, 48);
                        pid = Marshal.ReadInt32(row, 52);
                    }
                    if (state != ESTABLISHED || !loop || pid == 0) continue;
                    byPort[lport] = pid;
                    conns.Add((rport, pid));
                }
                foreach (var (rport, pid) in conns)
                    if (byPort.TryGetValue(rport, out int other) && other != pid) pairs.Add((pid, other));
                return;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    static bool IsV6Loopback(IntPtr addr)
    {
        for (int i = 0; i < 15; i++) if (Marshal.ReadByte(addr, i) != 0) return false;
        return Marshal.ReadByte(addr, 15) == 1;
    }
}
