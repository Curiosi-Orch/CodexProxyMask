using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexProxyMask
{
    internal sealed class FirewallRuleManager
    {
        private const string RulePrefix = "CodexProxyMask-";
        // MonoCloud is currently configured with an IPv4 mixed port
        // (127.0.0.1:7078). The netsh build on this machine rejects a bare
        // IPv6 loopback literal (::1) as an invalid remoteip value. Keep the
        // rule IPv4-only to match the observed listener and avoid a command
        // that Windows cannot parse.
        private readonly string[] _loopbackAddresses = { "127.0.0.1" };
        private readonly string[] _protocols = { "TCP", "UDP" };

        public FirewallRuleManager(int proxyPort)
        {
            ProxyPort = proxyPort;
        }

        public int ProxyPort { get; set; }

        public int StoredRuleCount
        {
            get { return LoadRuleNames().Count; }
        }

        public TcpTerminationResult EnforceMaskConnections()
        {
            return TcpConnectionTerminator.Terminate(
                TargetProcessDiscovery.GetRunningTargetProcessIds(),
                ProxyPort);
        }

        public TcpTerminationResult InspectMaskConnections()
        {
            return TcpConnectionTerminator.Inspect(
                TargetProcessDiscovery.GetRunningTargetProcessIds(),
                ProxyPort);
        }

        public FirewallOperationResult ApplyMask()
        {
            var oldRules = LoadRuleNames();
            if (oldRules.Count > 0)
            {
                var cleanup = RemoveMask();
                if (!cleanup.Success)
                {
                    return new FirewallOperationResult
                    {
                        Success = false,
                        Message = "无法清理上一次的屏蔽规则: " + cleanup.Message
                    };
                }
            }

            var records = TargetProcessDiscovery.GetProcessRecords();
            var paths = TargetProcessDiscovery.DiscoverProgramPaths(records);
            if (paths.Count == 0)
            {
                return new FirewallOperationResult
                {
                    Success = false,
                    Message = "没有找到 ChatGPT.exe 或 codex.exe 的实际路径。请先启动 Codex，再重试。"
                };
            }

            var policy = QueryActiveStoreLocalRuleMerge();
            if (!policy.Success)
            {
                return new FirewallOperationResult
                {
                    Success = false,
                    Message = "无法读取 Windows 防火墙有效策略（ActiveStore）: " + policy.Message,
                    TargetPathCount = paths.Count
                };
            }

            if (!policy.Allowed)
            {
                return new FirewallOperationResult
                {
                    Success = false,
                    Message = "Windows 防火墙有效策略禁止本地规则合并（AllowLocalFirewallRules = False"
                        + (string.IsNullOrEmpty(policy.ProfileNames) ? string.Empty : "：" + policy.ProfileNames)
                        + "），本程序创建的规则不会生效。请让管理员启用本地防火墙规则后重试。",
                    TargetPathCount = paths.Count
                };
            }

            var descriptors = BuildDescriptors(paths);
            var created = new List<string>();
            var errors = new List<string>();

            foreach (var descriptor in descriptors)
            {
                var result = RunNetsh(BuildAddArguments(descriptor));
                if (result.ExitCode == 0)
                {
                    created.Add(descriptor.Name);
                    SaveRuleNames(created);
                }
                else
                {
                    errors.Add(descriptor.Name + ": " + result.Message);
                    break;
                }
            }

            if (errors.Count > 0)
            {
                foreach (var ruleName in created)
                {
                    RunNetsh(BuildDeleteArguments(ruleName));
                }
                SaveRuleNames(new List<string>());

                return new FirewallOperationResult
                {
                    Success = false,
                    Message = "创建防火墙规则失败: " + string.Join("; ", errors),
                    RuleCount = created.Count,
                    TargetPathCount = paths.Count
                };
            }

            var targetProcessIds = TargetProcessDiscovery.GetTargetProcessIds(records);
            var termination = TcpConnectionTerminator.Terminate(targetProcessIds, ProxyPort);
            if (!termination.Success)
            {
                return new FirewallOperationResult
                {
                    Success = false,
                    Message = "防火墙规则已创建，但无法断开已有 Codex 代理连接，因此屏蔽可能仍未立即生效: "
                        + termination.Message,
                    RuleCount = created.Count,
                    TargetPathCount = paths.Count
                };
            }

            return new FirewallOperationResult
            {
                Success = true,
                Message = "已屏蔽 Codex 访问本机 MonoCloud 代理；" + termination.Message,
                RuleCount = created.Count,
                TargetPathCount = paths.Count
            };
        }

        public FirewallOperationResult RemoveMask()
        {
            var names = LoadRuleNames();
            if (names.Count == 0)
            {
                return new FirewallOperationResult
                {
                    Success = true,
                    Message = "当前没有已记录的屏蔽规则。"
                };
            }

            var remaining = new List<string>();
            var removed = 0;
            var errors = new List<string>();

            foreach (var name in names)
            {
                var result = RunNetsh(BuildDeleteArguments(name));
                if (result.ExitCode == 0)
                {
                    removed++;
                }
                else
                {
                    remaining.Add(name);
                    errors.Add(name + ": " + result.Message);
                }
            }

            SaveRuleNames(remaining);
            return new FirewallOperationResult
            {
                Success = remaining.Count == 0,
                Message = remaining.Count == 0
                    ? "已解除 Codex 的代理屏蔽。"
                    : "已删除 " + removed.ToString(CultureInfo.InvariantCulture)
                        + " 条规则，但仍有 " + remaining.Count.ToString(CultureInfo.InvariantCulture)
                        + " 条未删除: " + string.Join("; ", errors),
                RuleCount = removed
            };
        }

        private List<RuleDescriptor> BuildDescriptors(IList<string> paths)
        {
            var descriptors = new List<RuleDescriptor>();
            foreach (var path in paths)
            {
                foreach (var address in _loopbackAddresses)
                {
                    foreach (var protocol in _protocols)
                    {
                        descriptors.Add(new RuleDescriptor
                        {
                            Name = BuildRuleName(path, address, protocol),
                            Path = path,
                            RemoteAddress = address,
                            Protocol = protocol
                        });
                    }
                }
            }
            return descriptors;
        }

        private string BuildRuleName(string path, string address, string protocol)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
                var hash = BitConverter.ToString(bytes).Replace("-", string.Empty).Substring(0, 12);
                return RulePrefix + hash + "-" + protocol.ToLowerInvariant() + "-"
                    + "v4-" + ProxyPort.ToString(CultureInfo.InvariantCulture);
            }
        }

        private string BuildAddArguments(RuleDescriptor descriptor)
        {
            return "advfirewall firewall add rule"
                + " name=\"" + Escape(descriptor.Name) + "\""
                + " dir=out action=block enable=yes profile=any"
                + " program=\"" + Escape(descriptor.Path) + "\""
                + " protocol=" + descriptor.Protocol
                + " remoteip=" + descriptor.RemoteAddress
                + " remoteport=" + ProxyPort.ToString(CultureInfo.InvariantCulture);
        }

        private static string BuildDeleteArguments(string name)
        {
            return "advfirewall firewall delete rule name=\"" + Escape(name) + "\"";
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\"", "\\\"");
        }

        private static NetshResult RunNetsh(string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    if (!process.Start())
                    {
                        return new NetshResult { ExitCode = -1, Message = "无法启动 netsh.exe" };
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch { }
                        return new NetshResult { ExitCode = -2, Message = "netsh.exe 超时" };
                    }

                    var outputText = (output ?? string.Empty).Trim();
                    var errorText = (error ?? string.Empty).Trim();
                    var message = errorText;
                    if (message.Length == 0) message = outputText;
                    if (message.Length == 0) message = "退出码 " + process.ExitCode.ToString(CultureInfo.InvariantCulture);

                    return new NetshResult
                    {
                        ExitCode = process.ExitCode,
                        Message = Truncate(message, 240),
                        Output = outputText,
                        Error = errorText
                    };
                }
            }
            catch (Exception ex)
            {
                return new NetshResult { ExitCode = -3, Message = Truncate(ex.Message, 240) };
            }
        }

        private static LocalRuleMergeResult QueryActiveStoreLocalRuleMerge()
        {
            const string command = "$ErrorActionPreference='Stop'; "
                + "@(Get-NetFirewallProfile -PolicyStore ActiveStore) | ForEach-Object { "
                + "if ($_.AllowLocalFirewallRules -eq $true) { Write-Output ($_.Name + '=1') } "
                + "elseif ($_.AllowLocalFirewallRules -eq $false) { Write-Output ($_.Name + '=0') } "
                + "else { Write-Output ($_.Name + '=?') } }";

            var query = RunPowerShell(command);
            if (query.ExitCode != 0)
            {
                return new LocalRuleMergeResult
                {
                    Success = false,
                    Message = query.Message
                };
            }

            var allowedCount = 0;
            var disabledProfiles = new List<string>();
            var unknownProfiles = new List<string>();
            var lines = (query.Output ?? string.Empty).Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                var separator = line.LastIndexOf('=');
                if (separator <= 0 || separator >= line.Length - 1)
                {
                    continue;
                }

                var profileName = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                if (value == "1")
                {
                    allowedCount++;
                }
                else if (value == "0")
                {
                    disabledProfiles.Add(profileName);
                }
                else
                {
                    unknownProfiles.Add(profileName);
                }
            }

            if (disabledProfiles.Count > 0)
            {
                return new LocalRuleMergeResult
                {
                    Success = true,
                    Allowed = false,
                    ProfileNames = string.Join("、", disabledProfiles)
                };
            }

            if (allowedCount == 0 || unknownProfiles.Count > 0)
            {
                return new LocalRuleMergeResult
                {
                    Success = false,
                    Message = unknownProfiles.Count > 0
                        ? "无法确定配置文件状态: " + string.Join("、", unknownProfiles)
                        : "查询未返回任何防火墙配置文件。"
                };
            }

            return new LocalRuleMergeResult
            {
                Success = true,
                Allowed = true
            };
        }

        private static NetshResult RunPowerShell(string command)
        {
            try
            {
                var powerShellPath = Path.Combine(
                    Environment.SystemDirectory,
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe");
                if (!File.Exists(powerShellPath))
                {
                    powerShellPath = "powershell.exe";
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = powerShellPath,
                    Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"" + command + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    if (!process.Start())
                    {
                        return new NetshResult { ExitCode = -1, Message = "无法启动 powershell.exe" };
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch { }
                        return new NetshResult { ExitCode = -2, Message = "查询 ActiveStore 超时" };
                    }

                    var outputText = (output ?? string.Empty).Trim();
                    var errorText = (error ?? string.Empty).Trim();
                    var message = errorText;
                    if (message.Length == 0) message = outputText;
                    if (message.Length == 0) message = "退出码 " + process.ExitCode.ToString(CultureInfo.InvariantCulture);

                    return new NetshResult
                    {
                        ExitCode = process.ExitCode,
                        Message = Truncate(message, 240),
                        Output = outputText,
                        Error = errorText
                    };
                }
            }
            catch (Exception ex)
            {
                return new NetshResult { ExitCode = -3, Message = Truncate(ex.Message, 240) };
            }
        }

        private static List<string> LoadRuleNames()
        {
            try
            {
                if (!File.Exists(AppDataPaths.RuleStateFile))
                {
                    return new List<string>();
                }

                return File.ReadAllLines(AppDataPaths.RuleStateFile, Encoding.UTF8)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static void SaveRuleNames(IList<string> names)
        {
            try
            {
                Directory.CreateDirectory(AppDataPaths.Root);
                File.WriteAllLines(AppDataPaths.RuleStateFile, names.ToArray(), Encoding.UTF8);
            }
            catch
            {
                // If persistence fails, the rules are still applied for this session.
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength) + "...";
        }

        private sealed class RuleDescriptor
        {
            public string Name;
            public string Path;
            public string RemoteAddress;
            public string Protocol;
        }

        private sealed class LocalRuleMergeResult
        {
            public bool Success;
            public bool Allowed;
            public string ProfileNames;
            public string Message;
        }

        private sealed class NetshResult
        {
            public int ExitCode;
            public string Message;
            public string Output;
            public string Error;
        }
    }
}
