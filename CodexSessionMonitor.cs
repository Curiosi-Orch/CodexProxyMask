using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexProxyMask
{
    // Reads Codex's append-only rollout files. A turn starts with
    // task_started and ends with task_complete or turn_aborted; these markers
    // are more reliable than inferring
    // another thread's remote reasoning state from local CPU or child processes.
    internal sealed class CodexSessionMonitor
    {
        private const string TaskStartedMarker = "\"type\":\"task_started\"";
        private const string TaskCompleteMarker = "\"type\":\"task_complete\"";
        private const string TurnAbortedMarker = "\"type\":\"turn_aborted\"";
        private const string RecordTimestampMarker = "{\"timestamp\":\"";
        private const int ReverseScanChunkBytes = 1024 * 1024;
        private const int MarkerBoundaryOverlapBytes = 128;
        private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ActiveSessionStaleTimeout = TimeSpan.FromMinutes(30);

        private readonly string _sessionsRoot;
        private readonly Dictionary<string, SessionFileState> _states =
            new Dictionary<string, SessionFileState>(StringComparer.OrdinalIgnoreCase);
        private DateTime _nextDiscoveryUtc = DateTime.MinValue;

        public CodexSessionMonitor()
            : this(ResolveCodexHome())
        {
        }

        internal CodexSessionMonitor(string codexHome)
        {
            _sessionsRoot = string.IsNullOrWhiteSpace(codexHome)
                ? string.Empty
                : Path.Combine(codexHome, "sessions");
        }

        public CodexSessionSnapshot Sample(DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(_sessionsRoot)
                || !Directory.Exists(_sessionsRoot))
            {
                return new CodexSessionSnapshot();
            }

            if (nowUtc >= _nextDiscoveryUtc)
            {
                DiscoverFiles();
                _nextDiscoveryUtc = nowUtc + DiscoveryInterval;
            }

            var activeIds = new List<string>();
            foreach (var state in _states.Values.ToList())
            {
                RefreshState(state, nowUtc);
                if (state.Active
                    && state.LastEventUtc != DateTime.MinValue
                    && nowUtc >= state.LastEventUtc
                    && nowUtc - state.LastEventUtc <= ActiveSessionStaleTimeout)
                {
                    activeIds.Add(state.ThreadId);
                }
            }

            return new CodexSessionSnapshot
            {
                ActiveTaskCount = activeIds.Count,
                ActiveThreadIds = activeIds
            };
        }

        private void DiscoverFiles()
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(
                    _sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
                {
                    if (_states.ContainsKey(path))
                    {
                        continue;
                    }

                    // Rollout files can keep their creation timestamp even
                    // after new turns have been appended to them.
                    _states[path] = ReadInitialState(path);
                }

                var missing = _states.Values
                    .Where(state => !File.Exists(state.Path))
                    .Select(state => state.Path)
                    .ToList();
                foreach (var path in missing)
                {
                    _states.Remove(path);
                }
            }
            catch
            {
                // Session monitoring is an additional strong signal. Failure
                // falls back to input, process and command activity detection.
            }
        }

        private static SessionFileState ReadInitialState(string path)
        {
            var state = new SessionFileState
            {
                Path = path,
                ThreadId = ExtractThreadId(path)
            };

            try
            {
                using (var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    state.Position = stream.Length;
                    state.LastEventUtc = FindLatestRecordTimestamp(stream);
                    state.Active = FindLatestTaskMarker(stream);
                }
            }
            catch
            {
                state.Position = 0;
                state.Active = false;
            }

            return state;
        }

        private static void RefreshState(SessionFileState state, DateTime nowUtc)
        {
            try
            {
                var info = new FileInfo(state.Path);
                if (!info.Exists)
                {
                    state.Active = false;
                    return;
                }

                if (info.Length < state.Position)
                {
                    state.Position = 0;
                    state.Active = false;
                    state.LastEventUtc = DateTime.MinValue;
                }
                if (info.Length == state.Position)
                {
                    return;
                }

                using (var stream = new FileStream(
                    state.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(state.Position, SeekOrigin.Begin);
                    var remaining = stream.Length - state.Position;
                    if (remaining > ReverseScanChunkBytes)
                    {
                        // A rollover or unexpectedly huge append is handled by
                        // re-reading only the latest definitive marker.
                        state.Active = FindLatestTaskMarker(stream);
                        state.LastEventUtc = FindLatestRecordTimestamp(stream);
                    }
                    else
                    {
                        var bytes = new byte[(int)remaining];
                        var read = ReadFully(stream, bytes, 0, bytes.Length);
                        var text = Encoding.UTF8.GetString(bytes, 0, read);
                        ApplyLatestMarker(text, state);
                        DateTime eventUtc;
                        state.LastEventUtc = TryFindLatestTimestamp(text, out eventUtc)
                            ? eventUtc
                            : nowUtc;
                    }
                    state.Position = stream.Length;
                }
            }
            catch
            {
                // Keep the previous state for this sample. The stale timeout
                // prevents a permanently active result after repeated failures.
            }
        }

        private static DateTime FindLatestRecordTimestamp(FileStream stream)
        {
            var cursor = stream.Length;
            while (cursor > 0)
            {
                var start = Math.Max(0, cursor - ReverseScanChunkBytes);
                var end = Math.Min(stream.Length, cursor + MarkerBoundaryOverlapBytes);
                var bytes = new byte[checked((int)(end - start))];
                stream.Seek(start, SeekOrigin.Begin);
                var read = ReadFully(stream, bytes, 0, bytes.Length);
                DateTime eventUtc;
                if (TryFindLatestTimestamp(
                    Encoding.UTF8.GetString(bytes, 0, read), out eventUtc))
                {
                    return eventUtc;
                }
                cursor = start;
            }

            return DateTime.MinValue;
        }

        private static bool TryFindLatestTimestamp(string text, out DateTime eventUtc)
        {
            var searchBefore = text.Length;
            while (searchBefore > 0)
            {
                var markerAt = text.LastIndexOf(RecordTimestampMarker,
                    searchBefore - 1, StringComparison.Ordinal);
                if (markerAt < 0) break;
                var valueAt = markerAt + RecordTimestampMarker.Length;
                var end = text.IndexOf('"', valueAt);
                DateTimeOffset parsed;
                if (end > valueAt
                    && DateTimeOffset.TryParse(
                        text.Substring(valueAt, end - valueAt), out parsed))
                {
                    eventUtc = parsed.UtcDateTime;
                    return true;
                }
                searchBefore = markerAt;
            }

            eventUtc = DateTime.MinValue;
            return false;
        }

        private static bool FindLatestTaskMarker(FileStream stream)
        {
            var cursor = stream.Length;
            while (cursor > 0)
            {
                var start = Math.Max(0, cursor - ReverseScanChunkBytes);
                var end = Math.Min(stream.Length, cursor + MarkerBoundaryOverlapBytes);
                var length = checked((int)(end - start));
                var bytes = new byte[length];
                stream.Seek(start, SeekOrigin.Begin);
                var read = ReadFully(stream, bytes, 0, bytes.Length);
                var text = Encoding.UTF8.GetString(bytes, 0, read);
                var startedAt = text.LastIndexOf(
                    TaskStartedMarker, StringComparison.Ordinal);
                var endedAt = GetLastTerminalMarkerIndex(text);
                if (startedAt >= 0 || endedAt >= 0)
                {
                    return startedAt > endedAt;
                }
                cursor = start;
            }

            return false;
        }

        private static void ApplyLatestMarker(string text, SessionFileState state)
        {
            var startedAt = text.LastIndexOf(
                TaskStartedMarker, StringComparison.Ordinal);
            var endedAt = GetLastTerminalMarkerIndex(text);
            if (startedAt < 0 && endedAt < 0)
            {
                return;
            }
            state.Active = startedAt > endedAt;
        }

        private static int GetLastTerminalMarkerIndex(string text)
        {
            return Math.Max(
                text.LastIndexOf(TaskCompleteMarker, StringComparison.Ordinal),
                text.LastIndexOf(TurnAbortedMarker, StringComparison.Ordinal));
        }

        private static int ReadFully(
            Stream stream,
            byte[] buffer,
            int offset,
            int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static string ExtractThreadId(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var separator = name.LastIndexOf('-');
            if (separator < 0 || separator >= name.Length - 1)
            {
                return name;
            }

            // A thread id is a UUID-like five-part suffix. Keep the final 36
            // characters when present; otherwise use the file name.
            return name.Length >= 36
                ? name.Substring(name.Length - 36)
                : name;
        }

        private static string ResolveCodexHome()
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(configured)
                && Directory.Exists(Path.Combine(configured, "sessions")))
            {
                return configured;
            }

            const string commonConfiguredHome = @"F:\CodexHome";
            var profileHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
            var candidates = new[] { commonConfiguredHome, profileHome }
                .Where(home => Directory.Exists(Path.Combine(home, "sessions")))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(GetCodexHomeFreshnessUtc)
                .ToList();
            return candidates.Count == 0 ? string.Empty : candidates[0];
        }

        private static DateTime GetCodexHomeFreshnessUtc(string codexHome)
        {
            try
            {
                var stateDatabase = Path.Combine(codexHome, "state_5.sqlite");
                return File.Exists(stateDatabase)
                    ? File.GetLastWriteTimeUtc(stateDatabase)
                    : Directory.GetLastWriteTimeUtc(Path.Combine(codexHome, "sessions"));
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private sealed class SessionFileState
        {
            public string Path;
            public string ThreadId;
            public long Position;
            public bool Active;
            public DateTime LastEventUtc;
        }
    }

    internal sealed class CodexSessionSnapshot
    {
        public int ActiveTaskCount;
        public IList<string> ActiveThreadIds = new List<string>();
    }
}
