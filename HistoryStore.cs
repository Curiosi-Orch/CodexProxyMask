using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexProxyMask
{
    internal sealed class HistoryStore
    {
        private const int MaxStoredEntries = 1000;

        public IList<HistoryEntry> Load()
        {
            var entries = new List<HistoryEntry>();

            try
            {
                if (!File.Exists(AppDataPaths.HistoryFile))
                {
                    return entries;
                }

                var lines = File.ReadAllLines(AppDataPaths.HistoryFile, Encoding.UTF8);
                var start = Math.Max(0, lines.Length - MaxStoredEntries);
                for (var i = start; i < lines.Length; i++)
                {
                    var fields = lines[i].Split(new[] { '|' }, 4);
                    if (fields.Length != 4)
                    {
                        continue;
                    }

                    DateTimeOffset timestamp;
                    if (!DateTimeOffset.TryParse(fields[0], CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out timestamp))
                    {
                        continue;
                    }

                    entries.Add(new HistoryEntry
                    {
                        Timestamp = timestamp,
                        Action = fields[1],
                        Reason = fields[2],
                        Detail = fields[3]
                    });
                }
            }
            catch
            {
                // A missing or locked history file should not stop the utility.
            }

            return entries;
        }

        public HistoryEntry Add(string action, string reason, string detail)
        {
            var entry = new HistoryEntry
            {
                Timestamp = DateTimeOffset.Now,
                Action = Clean(action),
                Reason = Clean(reason),
                Detail = Clean(detail)
            };

            try
            {
                Directory.CreateDirectory(AppDataPaths.Root);
                var line = string.Join("|", new[]
                {
                    entry.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                    entry.Action,
                    entry.Reason,
                    entry.Detail
                });
                File.AppendAllText(AppDataPaths.HistoryFile, line + Environment.NewLine, Encoding.UTF8);

                TrimIfNeeded();
            }
            catch
            {
                // UI history still receives the entry even if persistence fails.
            }

            return entry;
        }

        private static string Clean(string value)
        {
            return (value ?? string.Empty).Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        private static void TrimIfNeeded()
        {
            try
            {
                var lines = File.ReadAllLines(AppDataPaths.HistoryFile, Encoding.UTF8);
                if (lines.Length <= MaxStoredEntries)
                {
                    return;
                }

                File.WriteAllLines(AppDataPaths.HistoryFile,
                    lines.Skip(lines.Length - MaxStoredEntries).ToArray(), Encoding.UTF8);
            }
            catch
            {
                // Trimming is best effort.
            }
        }
    }
}
