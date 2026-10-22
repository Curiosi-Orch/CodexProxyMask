using System;

namespace CodexProxyMask
{
    internal enum MaskMode
    {
        None,
        Automatic,
        Manual,
        Forced
    }

    internal static class MaskAutomationPolicy
    {
        public static bool ShouldApplyAutomatic(
            MaskMode mode, bool enabled, int targetProcessCount,
            DateTime nowUtc, DateTime lastActivityUtc, int idleMinutes)
        {
            return mode == MaskMode.None
                && enabled
                && targetProcessCount > 0
                && nowUtc - lastActivityUtc >= TimeSpan.FromMinutes(idleMinutes);
        }
    }

    internal sealed class AppSettings
    {
        public bool AutoMaskEnabled = true;
        public bool AutoUnmaskEnabled = true;
        public int IdleMinutes = 5;
        public int ProxyPort = 7078;
    }

    internal sealed class HistoryEntry
    {
        public DateTimeOffset Timestamp;
        public string Action;
        public string Reason;
        public string Detail;
    }

    internal sealed class ProcessRecord
    {
        public int ProcessId;
        public int ParentProcessId;
        public bool IsTargetProcess;
        public bool IsTestProcess;
        public bool IsCommandRunnerProcess;
        public bool IsCommandProcess;
        public string Name;
        public string Path;
        public DateTime? StartTime;
        public TimeSpan TotalProcessorTime;
        public bool HasProcessorTime;
        public ulong ReadTransferBytes;
        public ulong WriteTransferBytes;
        public bool HasIoCounters;
        public IntPtr MainWindowHandle;
    }

    internal sealed class ActivitySnapshot
    {
        public DateTimeOffset Timestamp;
        public int TargetProcessCount;
        public int TestProcessCount;
        public int ActiveTaskCount;
        public bool UserActivity;
        public bool WorkActivity;
        public int ProxySocketCount;
        public string Signals;
    }

    internal sealed class FirewallOperationResult
    {
        public bool Success;
        public string Message;
        public int RuleCount;
        public int TargetPathCount;
    }
}
