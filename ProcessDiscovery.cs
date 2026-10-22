using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodexProxyMask
{
    internal static class TargetProcessDiscovery
    {
        private static readonly string[] TargetNames = { "ChatGPT", "codex" };

        private static readonly HashSet<string> TestCommandNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "cmd", "powershell", "pwsh", "bash", "sh", "wsl",
                "python", "python3", "py", "node", "npm", "npx", "yarn", "pnpm",
                "deno", "bun", "dotnet", "java", "mvn", "gradle", "cargo", "go",
                "ruby", "perl", "php", "pytest", "jest", "vitest", "mocha",
                "msbuild", "vstest.console", "git", "make", "nmake", "gcc", "g++",
                "cl", "csc"
            };

        public static List<ProcessRecord> GetProcessRecords()
        {
            var records = new List<ProcessRecord>();
            var seen = new HashSet<int>();

            foreach (var name in TargetNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                foreach (var process in processes)
                {
                    try
                    {
                        if (!seen.Add(process.Id))
                        {
                            continue;
                        }

                        records.Add(ReadProcessRecord(
                            process, name, true, false, 0));
                    }
                    catch
                    {
                        // A process can exit between enumeration and inspection.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            AddTestProcessRecords(records, seen);
            return records;
        }

        public static HashSet<int> GetTargetProcessIds(IEnumerable<ProcessRecord> records)
        {
            return new HashSet<int>(records
                .Where(record => record.IsTargetProcess)
                .Select(record => record.ProcessId));
        }

        public static HashSet<int> GetRunningTargetProcessIds()
        {
            var result = new HashSet<int>();
            foreach (var name in TargetNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                foreach (var process in processes)
                {
                    try
                    {
                        result.Add(process.Id);
                    }
                    catch
                    {
                        // A process can exit during enumeration.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            return result;
        }

        public static List<string> DiscoverProgramPaths(IEnumerable<ProcessRecord> activeRecords)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in activeRecords)
            {
                AddExistingPath(paths, record.Path);
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var windowsApps = Path.Combine(programFiles, "WindowsApps");
            try
            {
                if (Directory.Exists(windowsApps))
                {
                    foreach (var directory in Directory.GetDirectories(windowsApps, "OpenAI.Codex_*"))
                    {
                        AddExistingPath(paths, Path.Combine(directory, "app", "ChatGPT.exe"));
                    }
                }
            }
            catch
            {
                // The active process path is still enough when WindowsApps is protected.
            }

            var localCodexBin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(localCodexBin))
                {
                    foreach (var path in Directory.GetFiles(localCodexBin, "codex.exe", SearchOption.AllDirectories))
                    {
                        AddExistingPath(paths, path);
                    }
                }
            }
            catch
            {
                // Discovery is best effort; it never broadens a firewall rule.
            }

            return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static ProcessRecord ReadProcessRecord(
            Process process,
            string name,
            bool isTargetProcess,
            bool isTestProcess,
            int parentProcessId)
        {
            var record = new ProcessRecord
            {
                ProcessId = process.Id,
                ParentProcessId = parentProcessId,
                IsTargetProcess = isTargetProcess,
                IsTestProcess = isTestProcess,
                Name = name,
                Path = TryGetPath(process),
                StartTime = TryGetStartTime(process),
                MainWindowHandle = TryGetMainWindowHandle(process)
            };

            try
            {
                record.TotalProcessorTime = process.TotalProcessorTime;
                record.HasProcessorTime = true;
            }
            catch
            {
                record.HasProcessorTime = false;
            }

            try
            {
                NativeMethods.IoCounters counters;
                if (NativeMethods.GetProcessIoCounters(process.Handle, out counters))
                {
                    record.ReadTransferBytes = counters.ReadTransferCount;
                    record.WriteTransferBytes = counters.WriteTransferCount;
                    record.HasIoCounters = true;
                }
            }
            catch
            {
                record.HasIoCounters = false;
            }

            return record;
        }

        private static void AddTestProcessRecords(List<ProcessRecord> records, ISet<int> seen)
        {
            var codexProcessIds = new HashSet<int>(records
                .Where(record => record.IsTargetProcess
                    && string.Equals(record.Name, "codex", StringComparison.OrdinalIgnoreCase))
                .Select(record => record.ProcessId));
            if (codexProcessIds.Count == 0)
            {
                return;
            }

            var entries = EnumerateProcessEntries();
            var parentByProcessId = entries.ToDictionary(
                entry => entry.ProcessId, entry => entry.ParentProcessId);
            var commandRunnerIds = new HashSet<int>(entries
                .Where(entry => IsCommandRunnerExecutable(entry.ExecutableName))
                .Select(entry => entry.ProcessId));

            foreach (var entry in entries)
            {
                if (seen.Contains(entry.ProcessId)
                    || !IsTestProcessExecutable(entry.ExecutableName)
                    || !IsDescendant(entry.ProcessId, codexProcessIds, parentByProcessId))
                {
                    continue;
                }

                Process process = null;
                try
                {
                    process = Process.GetProcessById(entry.ProcessId);
                    var name = Path.GetFileNameWithoutExtension(entry.ExecutableName);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = entry.ExecutableName;
                    }

                    var record = ReadProcessRecord(
                        process, name, false, true, entry.ParentProcessId);
                    record.IsCommandRunnerProcess = commandRunnerIds.Contains(entry.ProcessId);
                    record.IsCommandProcess = !record.IsCommandRunnerProcess
                        && IsDescendant(entry.ProcessId, commandRunnerIds, parentByProcessId);
                    records.Add(record);
                    seen.Add(entry.ProcessId);
                }
                catch
                {
                    // A child process can exit while the process tree is being read.
                }
                finally
                {
                    if (process != null)
                    {
                        process.Dispose();
                    }
                }
            }
        }

        private static bool IsTestProcessExecutable(string executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return false;
            }

            var baseName = Path.GetFileNameWithoutExtension(executableName);
            if (TestCommandNames.Contains(baseName))
            {
                return true;
            }

            return baseName.IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0
                || baseName.IndexOf("runner", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCommandRunnerExecutable(string executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return false;
            }

            var baseName = Path.GetFileNameWithoutExtension(executableName);
            return baseName.StartsWith(
                "codex-command-runner", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDescendant(
            int processId,
            ISet<int> roots,
            IDictionary<int, int> parentByProcessId)
        {
            var visited = new HashSet<int>();
            var current = processId;
            int parent;
            while (parentByProcessId.TryGetValue(current, out parent)
                && parent != 0
                && visited.Add(current))
            {
                if (roots.Contains(parent))
                {
                    return true;
                }

                current = parent;
            }

            return false;
        }

        private static List<ProcessEntry> EnumerateProcessEntries()
        {
            var result = new List<ProcessEntry>();
            var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return result;
            }

            try
            {
                var nativeEntry = new ProcessEntryNative
                {
                    Size = (uint)Marshal.SizeOf(typeof(ProcessEntryNative))
                };
                if (!Process32First(snapshot, ref nativeEntry))
                {
                    return result;
                }

                do
                {
                    result.Add(new ProcessEntry
                    {
                        ProcessId = (int)nativeEntry.ProcessId,
                        ParentProcessId = (int)nativeEntry.ParentProcessId,
                        ExecutableName = nativeEntry.ExecutableName
                    });
                    nativeEntry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntryNative));
                }
                while (Process32Next(snapshot, ref nativeEntry));
            }
            catch
            {
                // Process enumeration is best effort; target processes remain available.
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return result;
        }

        private static void AddExistingPath(ISet<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath) && IsTargetExecutable(fullPath))
                {
                    paths.Add(fullPath);
                }
            }
            catch
            {
                // Ignore malformed or disappearing paths.
            }
        }

        private static bool IsTargetExecutable(string path)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                && path.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string TryGetPath(Process process)
        {
            try
            {
                return process.MainModule.FileName;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static DateTime? TryGetStartTime(Process process)
        {
            try
            {
                return process.StartTime;
            }
            catch
            {
                return null;
            }
        }

        private static IntPtr TryGetMainWindowHandle(Process process)
        {
            try
            {
                return process.MainWindowHandle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private const uint Th32csSnapProcess = 0x00000002;

        private sealed class ProcessEntry
        {
            public int ProcessId;
            public int ParentProcessId;
            public string ExecutableName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntryNative
        {
            public uint Size;
            public uint UsageCount;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint ThreadCount;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutableName;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32First(
            IntPtr snapshot, ref ProcessEntryNative entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32Next(
            IntPtr snapshot, ref ProcessEntryNative entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetProcessIoCounters(
            IntPtr processHandle,
            out IoCounters counters);
    }
}
