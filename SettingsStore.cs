using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodexProxyMask
{
    internal static class AppDataPaths
    {
        public static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexProxyMask");

        public static readonly string SettingsFile = Path.Combine(Root, "settings.ini");
        public static readonly string HistoryFile = Path.Combine(Root, "history.log");
        public static readonly string RuleStateFile = Path.Combine(Root, "active-rules.txt");
    }

    internal static class SettingsStore
    {
        public static AppSettings Load()
        {
            var settings = new AppSettings();

            try
            {
                if (!File.Exists(AppDataPaths.SettingsFile))
                {
                    return settings;
                }

                foreach (var rawLine in File.ReadAllLines(AppDataPaths.SettingsFile, Encoding.UTF8))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, separator).Trim();
                    var value = line.Substring(separator + 1).Trim();

                    if (key.Equals("AutoMaskEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        bool parsed;
                        if (bool.TryParse(value, out parsed)) settings.AutoMaskEnabled = parsed;
                    }
                    else if (key.Equals("AutoUnmaskEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        bool parsed;
                        if (bool.TryParse(value, out parsed)) settings.AutoUnmaskEnabled = parsed;
                    }
                    else if (key.Equals("IdleMinutes", StringComparison.OrdinalIgnoreCase))
                    {
                        int parsed;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        {
                            settings.IdleMinutes = Math.Max(1, Math.Min(1440, parsed));
                        }
                    }
                    else if (key.Equals("ProxyPort", StringComparison.OrdinalIgnoreCase))
                    {
                        int parsed;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        {
                            settings.ProxyPort = Math.Max(1, Math.Min(65535, parsed));
                        }
                    }
                }
            }
            catch
            {
                // Defaults are safer than preventing the mask utility from starting.
            }

            return settings;
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(AppDataPaths.Root);
                var lines = new[]
                {
                    "AutoMaskEnabled=" + settings.AutoMaskEnabled.ToString(),
                    "AutoUnmaskEnabled=" + settings.AutoUnmaskEnabled.ToString(),
                    "IdleMinutes=" + settings.IdleMinutes.ToString(CultureInfo.InvariantCulture),
                    "ProxyPort=" + settings.ProxyPort.ToString(CultureInfo.InvariantCulture)
                };
                File.WriteAllLines(AppDataPaths.SettingsFile, lines, Encoding.UTF8);
            }
            catch
            {
                // Settings persistence is best effort.
            }
        }
    }
}
