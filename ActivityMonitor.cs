using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexProxyMask
{
    internal sealed class ActivityMonitor : IDisposable
    {
        // Idle measurement of the ChatGPT desktop app shows the main process
        // writing roughly 0.6-1.1 MB of configuration/cache IO and every
        // process burning up to ~60 ms CPU per 5 s sample while nothing is
        // being requested. The old thresholds (75 ms / 32 KB) classified this
        // background noise as "processing/output" on every sample, so the
        // post-submission grace window kept extending itself forever and the
        // idle clock never started. Thresholds now sit well above measured
        // idle noise, and a sample only counts after two consecutive hits.
        private const double ProcessingCpuMillisecondsPerSample = 250;
        private const ulong ProcessingIoBytesPerSample = 4 * 1024 * 1024;
        private const int ProcessingRequiredSamples = 2;
        private const double TestProcessCpuMillisecondsPerSample = 100;
        private const ulong TestProcessIoBytesPerSample = 32 * 1024;
        private const int GenericTestProcessRequiredSamples = 2;
        private static readonly TimeSpan RecentSubmissionGracePeriod = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan TestProcessEngagementPeriod = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan TestProcessActivityGracePeriod = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ProcessingActivityGracePeriod = TimeSpan.FromSeconds(30);
        private static readonly HashSet<string> StrongTestProcessNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "pytest", "jest", "vitest", "mocha", "msbuild", "vstest.console",
                "mvn", "gradle", "cargo", "make", "nmake", "gcc", "g++", "cl", "csc"
            };

        private readonly int _proxyPort;
        private readonly SubmissionMonitor _submissionMonitor;
        private readonly CodexSessionMonitor _sessionMonitor;
        private readonly Dictionary<int, ProcessRecord> _previousProcesses =
            new Dictionary<int, ProcessRecord>();
        private DateTime _lastSubmissionUtc = DateTime.MinValue;
        private DateTime _lastConfirmedWorkUtc = DateTime.MinValue;
        private DateTime _processingActiveUntilUtc = DateTime.MinValue;
        private readonly Dictionary<int, DateTime> _activeTestProcessesUntilUtc =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, int> _testProcessActivityStreaks =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> _processingStreaks =
            new Dictionary<int, int>();

        public ActivityMonitor(int proxyPort)
        {
            _proxyPort = proxyPort;
            _submissionMonitor = new SubmissionMonitor();
            _sessionMonitor = new CodexSessionMonitor();
        }

        public ActivitySnapshot Sample()
        {
            var timestamp = DateTimeOffset.Now;
            var nowUtc = timestamp.UtcDateTime;
            var records = TargetProcessDiscovery.GetProcessRecords();
            var targetIds = TargetProcessDiscovery.GetTargetProcessIds(records);
            _submissionMonitor.UpdateTargetProcessIds(targetIds);
            var signals = new List<string>();
            var userActivity = false;
            var workActivity = false;
            var sessionSnapshot = _sessionMonitor.Sample(nowUtc);

            // A session task can only be executing while at least one target
            // process (codex.exe / ChatGPT.exe) is alive: the rollout file is
            // written by the driver process itself. A dangling task_started
            // left behind by an interrupted session is therefore not work once
            // every target process has exited, and must not pause the idle
            // clock until the 30 minute stale timeout elapses.
            if (sessionSnapshot.ActiveTaskCount > 0 && targetIds.Count > 0)
            {
                userActivity = true;
                workActivity = true;
                _lastConfirmedWorkUtc = nowUtc;
                signals.Add("Codex任务处理中:"
                    + sessionSnapshot.ActiveTaskCount.ToString());
            }

            if (_submissionMonitor.TryConsumeSubmission())
            {
                userActivity = true;
                workActivity = true;
                RecordSubmission(nowUtc);
                signals.Add("用户提交请求");
            }

            var activeTestProcessNames = GetActiveTestProcessNames(records, nowUtc);
            if (activeTestProcessNames.Count > 0)
            {
                userActivity = true;
                workActivity = true;
                signals.Add("命令/测试处理中:" + string.Join("/", activeTestProcessNames));
            }

            // The short processing grace is only an output debounce, not
            // evidence of a still-running task. Otherwise background CPU/IO
            // can continually renew the grace after a turn has ended.
            var processingEligible = HasRecentSubmission(nowUtc)
                || (sessionSnapshot.ActiveTaskCount > 0 && targetIds.Count > 0);
            var processingNames = new List<string>();
            var rawProcessingActivity = false;

            foreach (var record in records)
            {
                if (record.IsTestProcess)
                {
                    continue;
                }

                ProcessRecord previous;
                if (!_previousProcesses.TryGetValue(record.ProcessId, out previous))
                {
                    // Renderer/helper process churn is common in the desktop
                    // app. A newly observed target process is baseline only;
                    // it is not proof that a user request is running.
                    continue;
                }

                // CPU/IO only counts after a real submission or recently
                // confirmed work. This lets us use a responsive threshold
                // without treating ordinary background maintenance as work.
                var cpuProcessing = record.HasProcessorTime && previous.HasProcessorTime
                    && (record.TotalProcessorTime - previous.TotalProcessorTime).TotalMilliseconds
                        >= ProcessingCpuMillisecondsPerSample;
                var ioProcessing = record.HasIoCounters && previous.HasIoCounters
                    && record.ReadTransferBytes >= previous.ReadTransferBytes
                    && record.WriteTransferBytes >= previous.WriteTransferBytes
                    && (record.ReadTransferBytes - previous.ReadTransferBytes
                        + record.WriteTransferBytes - previous.WriteTransferBytes)
                        >= ProcessingIoBytesPerSample;

                // A single qualifying sample can still be a background blip
                // (window repaint, telemetry flush). Require consecutive
                // qualifying samples before treating the process as busy.
                int streak;
                _processingStreaks.TryGetValue(record.ProcessId, out streak);
                if (processingEligible && (cpuProcessing || ioProcessing))
                {
                    streak = Math.Min(streak + 1, ProcessingRequiredSamples);
                }
                else
                {
                    streak = 0;
                }
                _processingStreaks[record.ProcessId] = streak;

                if (streak >= ProcessingRequiredSamples)
                {
                    rawProcessingActivity = true;
                    processingNames.Add(record.Name);
                }
            }

            var endedProcessingIds = _processingStreaks.Keys
                .Where(processId => records.All(record =>
                    record.ProcessId != processId))
                .ToList();
            foreach (var processId in endedProcessingIds)
            {
                _processingStreaks.Remove(processId);
            }

            if (rawProcessingActivity)
            {
                _processingActiveUntilUtc = nowUtc + ProcessingActivityGracePeriod;
                _lastConfirmedWorkUtc = nowUtc;
            }

            // A real submission is itself evidence that Codex is waiting or
            // processing. Keep the idle clock paused during the short request
            // grace period, even when remote waiting produces no local CPU.
            var waitingAfterSubmission = HasRecentSubmission(nowUtc);
            var processingActive = rawProcessingActivity
                || _processingActiveUntilUtc >= nowUtc
                || waitingAfterSubmission;
            if (processingActive)
            {
                userActivity = true;
                workActivity = true;
                var names = processingNames.Distinct().ToList();
                signals.Add(rawProcessingActivity && names.Count > 0
                    ? "处理/输出:" + string.Join("/", names)
                    : "请求等待/处理中");
            }

            var sockets = ReadProxySockets(targetIds);

            _previousProcesses.Clear();
            foreach (var record in records)
            {
                _previousProcesses[record.ProcessId] = record;
            }

            return new ActivitySnapshot
            {
                Timestamp = timestamp,
                TargetProcessCount = targetIds.Count,
                TestProcessCount = _activeTestProcessesUntilUtc.Count,
                ActiveTaskCount = sessionSnapshot.ActiveTaskCount,
                UserActivity = userActivity,
                WorkActivity = workActivity,
                ProxySocketCount = sockets == null ? 0 : sockets.Count,
                Signals = signals.Count == 0 ? "无明显活动信号" : string.Join(", ", signals.Distinct())
            };
        }

        public void RefreshSubmissionTargets()
        {
            _submissionMonitor.UpdateTargetProcessIds(
                TargetProcessDiscovery.GetRunningTargetProcessIds());
        }

        public int GetActiveSessionTaskCount()
        {
            if (TargetProcessDiscovery.GetRunningTargetProcessIds().Count == 0)
            {
                return 0;
            }

            return _sessionMonitor.Sample(DateTime.UtcNow).ActiveTaskCount;
        }

        public bool TryConsumeImmediateSubmission()
        {
            if (!_submissionMonitor.TryConsumeSubmission())
            {
                return false;
            }

            RecordSubmission(DateTime.UtcNow);
            return true;
        }

        public bool HasRecentTargetClick(TimeSpan maximumAge)
        {
            return _submissionMonitor.HasRecentTargetClick(maximumAge);
        }

        public void ConsumeRecentTargetClick()
        {
            _submissionMonitor.ConsumeRecentTargetClick();
        }

        public void ConfirmImmediateSubmission()
        {
            RecordSubmission(DateTime.UtcNow);
        }

        private void RecordSubmission(DateTime nowUtc)
        {
            _lastSubmissionUtc = nowUtc;
            _lastConfirmedWorkUtc = nowUtc;
            _processingActiveUntilUtc = nowUtc + ProcessingActivityGracePeriod;
        }

        private bool HasRecentSubmission(DateTime nowUtc)
        {
            return _lastSubmissionUtc != DateTime.MinValue
                && nowUtc >= _lastSubmissionUtc
                && nowUtc - _lastSubmissionUtc <= RecentSubmissionGracePeriod;
        }

        private bool HasRecentConfirmedWork(DateTime nowUtc)
        {
            return _lastConfirmedWorkUtc != DateTime.MinValue
                && nowUtc >= _lastConfirmedWorkUtc
                && nowUtc - _lastConfirmedWorkUtc <= TestProcessEngagementPeriod;
        }

        private List<string> GetActiveTestProcessNames(
            IList<ProcessRecord> records,
            DateTime nowUtc)
        {
            var activeNames = new List<string>();
            var currentTestProcessIds = new HashSet<int>();
            var activeUntil = nowUtc + TestProcessActivityGracePeriod;

            foreach (var record in records.Where(item => item.IsTestProcess))
            {
                currentTestProcessIds.Add(record.ProcessId);

                // Processes spawned below codex-command-runner represent a
                // currently executing tool command. Their continued existence
                // counts as work even while they are waiting or sleeping.
                if (record.IsCommandProcess)
                {
                    _activeTestProcessesUntilUtc[record.ProcessId] = activeUntil;
                    _lastConfirmedWorkUtc = nowUtc;
                    _testProcessActivityStreaks[record.ProcessId] = 0;
                    activeNames.Add(record.Name);
                    continue;
                }

                ProcessRecord previous;
                DateTime previousActiveUntil;
                var wasRecentlyActive = _activeTestProcessesUntilUtc.TryGetValue(
                    record.ProcessId, out previousActiveUntil)
                    && previousActiveUntil >= nowUtc;
                var hasPreviousSample = _previousProcesses.TryGetValue(
                    record.ProcessId, out previous);
                var hasMeasuredActivity = hasPreviousSample
                    && HasTestProcessDelta(record, previous);
                var isStrongTestProcess = record.IsCommandRunnerProcess
                    || StrongTestProcessNames.Contains(record.Name);
                var eligible = isStrongTestProcess
                    || wasRecentlyActive
                    || HasRecentConfirmedWork(nowUtc);

                int streak;
                _testProcessActivityStreaks.TryGetValue(record.ProcessId, out streak);
                if (hasMeasuredActivity && eligible)
                {
                    streak = Math.Min(streak + 1, GenericTestProcessRequiredSamples);
                }
                else
                {
                    streak = 0;
                }
                _testProcessActivityStreaks[record.ProcessId] = streak;

                var requiredSamples = isStrongTestProcess
                    ? 1
                    : GenericTestProcessRequiredSamples;
                if (streak >= requiredSamples)
                {
                    _activeTestProcessesUntilUtc[record.ProcessId] = activeUntil;
                    _lastConfirmedWorkUtc = nowUtc;
                    wasRecentlyActive = true;
                }

                if (wasRecentlyActive)
                {
                    activeNames.Add(record.Name);
                }
            }

            var expiredIds = _activeTestProcessesUntilUtc.Keys
                .Where(processId => !currentTestProcessIds.Contains(processId)
                    || _activeTestProcessesUntilUtc[processId] < nowUtc)
                .ToList();
            foreach (var processId in expiredIds)
            {
                _activeTestProcessesUntilUtc.Remove(processId);
            }

            var endedStreakIds = _testProcessActivityStreaks.Keys
                .Where(processId => !currentTestProcessIds.Contains(processId))
                .ToList();
            foreach (var processId in endedStreakIds)
            {
                _testProcessActivityStreaks.Remove(processId);
            }

            return activeNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool HasTestProcessDelta(
            ProcessRecord record,
            ProcessRecord previous)
        {
            var cpuActive = record.HasProcessorTime && previous.HasProcessorTime
                && (record.TotalProcessorTime - previous.TotalProcessorTime).TotalMilliseconds
                    >= TestProcessCpuMillisecondsPerSample;
            var ioActive = record.HasIoCounters && previous.HasIoCounters
                && record.ReadTransferBytes >= previous.ReadTransferBytes
                && record.WriteTransferBytes >= previous.WriteTransferBytes
                && (record.ReadTransferBytes - previous.ReadTransferBytes
                    + record.WriteTransferBytes - previous.WriteTransferBytes)
                    >= TestProcessIoBytesPerSample;
            return cpuActive || ioActive;
        }

        public void Dispose()
        {
            _submissionMonitor.Dispose();
        }

        private HashSet<string> ReadProxySockets(ISet<int> targetProcessIds)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = System.IO.Path.Combine(Environment.SystemDirectory, "netstat.exe"),
                    Arguments = "-ano",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    if (!process.Start())
                    {
                        return result;
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(2500))
                    {
                        try { process.Kill(); } catch { }
                        return result;
                    }

                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var fields = Regex.Split(line.Trim(), @"\s+");
                        if (fields.Length < 5 || !fields[0].Equals("TCP", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        int pid;
                        if (!int.TryParse(fields[fields.Length - 1], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out pid) || !targetProcessIds.Contains(pid))
                        {
                            continue;
                        }

                        var remoteEndpoint = fields[2];
                        if (!IsLoopbackProxyEndpoint(remoteEndpoint))
                        {
                            continue;
                        }

                        result.Add(string.Join("|", new[]
                        {
                            fields[0], fields[1], fields[2], fields[3], pid.ToString(CultureInfo.InvariantCulture)
                        }));
                    }

                }
            }
            catch
            {
                // A failed sample reports zero sockets and never counts as activity.
            }

            return result;
        }

        private bool IsLoopbackProxyEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)
                || !endpoint.EndsWith(":" + _proxyPort.ToString(CultureInfo.InvariantCulture),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return endpoint.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0
                || endpoint.IndexOf("::1", StringComparison.OrdinalIgnoreCase) >= 0;
        }

    }
}
