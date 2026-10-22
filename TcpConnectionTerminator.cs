using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace CodexProxyMask
{
    internal static class TcpConnectionTerminator
    {
        private const int AddressFamilyIpv4 = 2;
        private const int TcpTableOwnerPidAll = 5;
        private const uint DeleteTcb = 12;
        private const uint ErrorInsufficientBuffer = 122;
        private const uint LoopbackAddress = 0x0100007F;

        public static TcpTerminationResult Terminate(
            ISet<int> targetProcessIds,
            int remotePort)
        {
            return ProcessConnections(targetProcessIds, remotePort, true);
        }

        public static TcpTerminationResult Inspect(
            ISet<int> targetProcessIds,
            int remotePort)
        {
            return ProcessConnections(targetProcessIds, remotePort, false);
        }

        private static TcpTerminationResult ProcessConnections(
            ISet<int> targetProcessIds,
            int remotePort,
            bool terminate)
        {
            var result = new TcpTerminationResult();
            if (targetProcessIds == null || targetProcessIds.Count == 0)
            {
                result.Success = true;
                result.Message = "没有需要断开的目标连接。";
                return result;
            }

            List<MibTcpRowOwnerPid> rows;
            try
            {
                rows = GetRows();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "无法读取现有 TCP 连接: " + ex.Message;
                return result;
            }

            var errors = new List<string>();
            foreach (var row in rows)
            {
                if (!targetProcessIds.Contains((int)row.OwningPid)
                    || row.RemoteAddress != LoopbackAddress
                    || GetPort(row.RemotePort) != remotePort)
                {
                    continue;
                }

                result.FoundCount++;
                if (!terminate)
                {
                    continue;
                }

                var deleteRow = new MibTcpRow
                {
                    State = DeleteTcb,
                    LocalAddress = row.LocalAddress,
                    LocalPort = row.LocalPort,
                    RemoteAddress = row.RemoteAddress,
                    RemotePort = row.RemotePort
                };

                var status = SetTcpEntry(ref deleteRow);
                if (status == 0)
                {
                    result.TerminatedCount++;
                }
                else
                {
                    errors.Add("PID " + row.OwningPid.ToString()
                        + " 返回错误码 " + status.ToString());
                }
            }

            result.Success = errors.Count == 0;
            result.Message = terminate
                ? "发现 " + result.FoundCount.ToString()
                    + " 条目标连接，已断开 " + result.TerminatedCount.ToString() + " 条。"
                : "发现 " + result.FoundCount.ToString() + " 条目标连接。";
            if (errors.Count > 0)
            {
                result.Message += "未能断开的连接: " + string.Join("; ", errors);
            }

            return result;
        }

        private static List<MibTcpRowOwnerPid> GetRows()
        {
            var size = 0;
            var status = GetExtendedTcpTable(
                IntPtr.Zero, ref size, true, AddressFamilyIpv4,
                TcpTableOwnerPidAll, 0);
            if (status != ErrorInsufficientBuffer && status != 0)
            {
                throw new InvalidOperationException("GetExtendedTcpTable 错误码 " + status.ToString());
            }

            if (size <= 0)
            {
                return new List<MibTcpRowOwnerPid>();
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                status = GetExtendedTcpTable(
                    buffer, ref size, true, AddressFamilyIpv4,
                    TcpTableOwnerPidAll, 0);
                if (status != 0)
                {
                    throw new InvalidOperationException("GetExtendedTcpTable 错误码 " + status.ToString());
                }

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
                var rows = new List<MibTcpRowOwnerPid>(count);
                var rowPointer = new IntPtr(buffer.ToInt64() + sizeof(int));
                for (var index = 0; index < count; index++)
                {
                    rows.Add((MibTcpRowOwnerPid)Marshal.PtrToStructure(
                        rowPointer, typeof(MibTcpRowOwnerPid)));
                    rowPointer = new IntPtr(rowPointer.ToInt64() + rowSize);
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static int GetPort(uint networkOrderPort)
        {
            return (ushort)IPAddress.NetworkToHostOrder((short)networkOrderPort);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public uint State;
            public uint LocalAddress;
            public uint LocalPort;
            public uint RemoteAddress;
            public uint RemotePort;
            public uint OwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRow
        {
            public uint State;
            public uint LocalAddress;
            public uint LocalPort;
            public uint RemoteAddress;
            public uint RemotePort;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr tcpTable,
            ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool sort,
            int ipVersion,
            int tableClass,
            uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint SetTcpEntry(ref MibTcpRow row);
    }

    internal sealed class TcpTerminationResult
    {
        public bool Success;
        public int FoundCount;
        public int TerminatedCount;
        public string Message;
    }
}
