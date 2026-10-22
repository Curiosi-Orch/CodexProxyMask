using System;
using System.IO;
using System.Linq;
using System.Text;
using CodexProxyMask;

internal static class CodexSessionMonitorTests
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--live")
        {
            var live = new CodexSessionMonitor(@"F:\CodexHome").Sample(DateTime.UtcNow);
            const string abortedThread = "01a0ce83-24f8-70c0-875f-a557f79eda6e";
            if (live.ActiveThreadIds.Contains(abortedThread))
            {
                throw new Exception("live aborted thread is still counted active");
            }
            Console.WriteLine("PASS: live aborted thread excluded; active count="
                + live.ActiveTaskCount + "; ids="
                + string.Join(",", live.ActiveThreadIds));
            return 0;
        }

        var root = Path.Combine(Path.GetTempPath(),
            "CodexSessionMonitorTests-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions", "2026", "09", "01");
        Directory.CreateDirectory(sessions);
        try
        {
            var now = DateTime.UtcNow;
            var stalePath = Path.Combine(sessions,
                "rollout-2026-09-01T00-00-00-11111111-1111-1111-1111-111111111111.jsonl");
            var activePath = Path.Combine(sessions,
                "rollout-2026-09-01T00-00-00-22222222-2222-2222-2222-222222222222.jsonl");
            var newPath = Path.Combine(sessions,
                "rollout-2026-09-01T00-00-00-33333333-3333-3333-3333-333333333333.jsonl");
            var abortedPath = Path.Combine(sessions,
                "rollout-2026-09-01T00-00-00-44444444-4444-4444-4444-444444444444.jsonl");

            WriteEvent(stalePath, now.AddHours(-2), "task_started");
            WriteEvent(activePath, now.AddMinutes(-2), "task_started");
            File.SetLastWriteTimeUtc(stalePath, now.AddDays(-3));
            File.SetLastWriteTimeUtc(activePath, now.AddDays(-3));

            var monitor = new CodexSessionMonitor(root);
            Assert(1, monitor.Sample(now).ActiveTaskCount,
                "old file timestamp with recent task_started");

            WriteEvent(activePath, now.AddSeconds(1), "task_complete");
            File.SetLastWriteTimeUtc(activePath, now.AddDays(-3));
            Assert(0, monitor.Sample(now.AddSeconds(2)).ActiveTaskCount,
                "appended task_complete");

            WriteEvent(activePath, now.AddSeconds(3), "task_started");
            File.SetLastWriteTimeUtc(activePath, now.AddDays(-3));
            Assert(1, monitor.Sample(now.AddSeconds(4)).ActiveTaskCount,
                "reopened old task file");

            WriteEvent(newPath, now.AddSeconds(5), "task_started");
            File.SetLastWriteTimeUtc(newPath, now.AddDays(-3));
            Assert(2, monitor.Sample(now.AddSeconds(6)).ActiveTaskCount,
                "new project file found on next discovery");

            WriteEvent(abortedPath, now.AddSeconds(7), "task_started");
            Assert(3, monitor.Sample(now.AddSeconds(8)).ActiveTaskCount,
                "aborted task starts as active");
            WriteEvent(abortedPath, now.AddSeconds(9), "turn_aborted");
            WriteEvent(abortedPath, now.AddSeconds(10), "thread_settings_applied");
            Assert(2, monitor.Sample(now.AddSeconds(11)).ActiveTaskCount,
                "turn_aborted ends task despite later non-task event");
            Assert(true, MaskAutomationPolicy.ShouldApplyAutomatic(
                MaskMode.None, true, 1, now.AddMinutes(5), now, 5),
                "auto mask at five-minute idle boundary");
            Assert(false, MaskAutomationPolicy.ShouldApplyAutomatic(
                MaskMode.None, true, 1, now.AddMinutes(5).AddSeconds(-1), now, 5),
                "do not mask before five minutes");
            Assert(false, MaskAutomationPolicy.ShouldApplyAutomatic(
                MaskMode.Forced, true, 1, now.AddMinutes(5), now, 5),
                "forced mask mode does not auto-enter");
            Assert(false, MaskAutomationPolicy.ShouldApplyAutomatic(
                MaskMode.None, true, 0, now.AddMinutes(5), now, 5),
                "no target process does not auto-mask");

            var freshMonitor = new CodexSessionMonitor(root);
            Assert(2, freshMonitor.Sample(now.AddSeconds(12)).ActiveTaskCount,
                "initial scan recognizes turn_aborted as terminal");

            WriteEvent(abortedPath, now.AddSeconds(13), "task_started");
            Assert(3, monitor.Sample(now.AddSeconds(14)).ActiveTaskCount,
                "new task after abort starts again");
            WriteEvent(abortedPath, now.AddSeconds(15), "task_complete");
            Assert(2, monitor.Sample(now.AddSeconds(16)).ActiveTaskCount,
                "completion after restart ends task");

            Console.WriteLine("PASS: stale file time, completion, abort, restart, new project");
            return 0;
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void WriteEvent(string path, DateTime utc, string type)
    {
        var line = "{\"timestamp\":\"" + utc.ToString("O")
            + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\""
            + type + "\"}}\n";
        File.AppendAllText(path, line, Encoding.UTF8);
    }

    private static void Assert(int expected, int actual, string scenario)
    {
        if (expected != actual)
        {
            throw new Exception(scenario + ": expected " + expected + ", got " + actual);
        }
    }

    private static void Assert(bool expected, bool actual, string scenario)
    {
        if (expected != actual)
        {
            throw new Exception(scenario + ": expected " + expected + ", got " + actual);
        }
    }
}
