# Codex Proxy Mask

一个 Windows 本地 WinForms 工具，用 Windows Defender Firewall 规则只屏蔽 Codex/ChatGPT 进程访问本机 MonoCloud 代理端口。默认目标是当前 MonoCloud 配置使用的 `127.0.0.1:7078`，不会关闭 MonoCloud，也不会改变 Chrome、迅雷或其他程序的代理规则。

## 功能

- 自动检测目标进程活动；连续 5 分钟无活动时自动启用 Mask。
- 用户提交请求或目标进程正在处理/输出时自动解除自动/手动临时 Mask。
- 普通按钮根据当前状态切换为“手动启用 Mask”或“解除 Mask”。
- 独立“强制启用 Mask”按钮提供锁定屏蔽，用户交互、处理/输出和测试脚本均不会自动解除。
- 显示当前 Mask 状态、目标进程数量、代理连接数、空闲计时和活动信号。
- 本地持久化最近 1000 条 Mask 操作历史。
- 正常退出时删除本工具创建的防火墙规则；下次启动时也会尝试清理上次异常退出留下的规则。

## 构建与运行

当前工作区没有安装 `dotnet`，项目使用 Windows 自带的 .NET Framework C# 编译器：

```powershell
Set-Location 'F:\Codex\Common Project\CodexProxyMask'
PowerShell -ExecutionPolicy Bypass -File .\build.ps1
.\out\CodexProxyMask.exe
```

程序需要管理员权限，因为创建/删除 Windows 防火墙规则及断开目标 TCP 连接会触发 UAC。程序在启用 Mask 前会查询 Windows 防火墙的有效策略 `ActiveStore`；只有 `AllowLocalFirewallRules = False` 时才判定本地规则不会生效。`netsh advfirewall show allprofiles` 中单独出现 `LocalFirewallRules = N/A (GPO-store only)` 不再被当作失败条件。由于部分 Windows 环境不会用普通防火墙规则阻断本机回环通信，Mask 启用期间还会每 200ms 检查一次，并只断开 `ChatGPT.exe`/`codex.exe` 到 `127.0.0.1:7078` 的新连接；Chrome 和其他程序不在目标进程集合中。也可以直接执行：

```powershell
PowerShell -ExecutionPolicy Bypass -File .\build.ps1 -Run
```

## 隔离范围

每条规则同时限定：

1. 精确的 `ChatGPT.exe` 或 `codex.exe` 文件路径；
2. 出站方向；
3. 回环地址 `127.0.0.1`（当前 MonoCloud 配置的 IPv4 监听地址）；
4. MonoCloud 代理端口；
5. TCP 或 UDP 协议。

因此它不会按端口全局阻断，也不会影响其他程序。更新 ChatGPT/Codex 后，程序会重新发现新的安装路径。

## 活动判断说明

程序会只读监测 `CODEX_HOME/sessions` 下 Codex 自身写入的 rollout 日志，并依据明确的 `task_started`、`task_complete` 和 `turn_aborted` 事件识别所有本地项目中的活动任务。任务被用户中断时也会立即结束“处理中”状态。新建项目和重新打开的旧任务都无需登记目录。活动时间依据日志中的事件时间和实际文件增长判断；部分 Windows 文件系统追加日志时不会更新“最后修改时间”。自动或手动临时 Mask 期间约每秒检查任务状态，发现新任务即自动解除。另一个项目中的任务在远端推理、编辑文件、调用工具或等待命令时也会显示“Codex任务处理中”并暂停空闲计时。异常退出且没有任何结束事件的任务在日志连续 30 分钟无更新后失效，或所有目标进程都已退出后立即失效，避免永久阻止自动 Mask。

进程级备用检测仍使用真实提交请求、`codex-command-runner` 下实际存在的命令进程、经过确认的测试脚本 CPU/IO 活动以及提交后目标进程的 CPU/IO。代理连接数量仍会显示，但代理连接建立、断开或数量变化不会单独重置空闲计时。真实提交后的两分钟内会显示“请求等待/处理中”并暂停空闲计时；CPU/IO 处理信号只在这段时间或会话任务确实进行中生效，短暂的输出缓冲不能自行无限延长“处理中”。提交后的 CPU/IO 只有连续两个采样都明显超过空闲噪声基线（实测空闲 ChatGPT 桌面主进程每 5 秒写约 0.6–1.1 MB 配置/缓存 IO、单进程 CPU 不超过约 60 毫秒）才会判定为“处理/输出”，后台渲染、遥测和配置写入等空闲噪声不会再打断空闲计时。新出现的 ChatGPT/Codex 渲染器或辅助进程只建立采样基线，不再被当作工作活动。

用户输入通过目标 Codex 窗口中按下非 `Shift+Enter` 的 Enter 键判定为“提交请求”，Mask 期间会在 200ms 监测周期内优先解除，不再等待 5 秒活动采样。鼠标点击只有在 2 秒内同时观察到目标 Codex 进程尝试连接被屏蔽的代理端口时，才判定为点击发送；普通点击不会单独解除。切换窗口到前台、普通输入和后台 CPU/IO 变化不会单独触发退出 Mask。`codex-command-runner` 实际派生出的 PowerShell、cmd、Python、Node 等命令在进程存活期间直接算“命令/测试处理中”，包括程序为测试本工具而启动的命令；其他普通脚本仍需观察到 CPU/IO 增量，避免长期驻留的后台进程打断空闲计时。

## Mask 状态转换条件

进入自动 Mask 需要同时满足：

1. 当前没有 Mask；
2. “启用自动 Mask”已勾选；
3. 已发现 ChatGPT.exe 或 codex.exe；
4. 连续达到设置的空闲分钟数，期间没有提交请求、经过确认的测试脚本、确认的处理/输出或新目标进程；代理连接变化不影响计时；
5. Windows 防火墙规则创建成功。

手动临时进入 Mask：当前没有 Mask 且没有残留规则时，点击“手动启用 Mask”，并且规则创建成功。勾选自动解除时，它与自动 Mask 一样会在真实交互后解除并将状态更新为“未屏蔽”。

强制进入 Mask：点击“强制启用 Mask”创建规则并启动持续断连保护；如果当前已经处于自动或手动临时 Mask，则直接切换为强制锁定并沿用现有规则。强制 Mask 只能通过“解除强制 Mask”/“解除 Mask”按钮或关闭程序退出，用户交互不会退出。界面中的“累计断开”显示 Mask 期间被阻止重新连接的目标连接数量。

自动/手动临时 Mask 的退出条件：

1. “检测到 Codex 用户操作或处理/输出时自动解除”已勾选，且发生提交请求、确认的处理/输出或测试脚本运行；
2. 点击合并按钮“解除 Mask”；
3. 在自动 Mask 状态下取消“启用自动 Mask”；
4. 关闭程序，程序会删除本工具创建的规则。

前台切换、普通输入、`Shift+Enter` 换行、低强度后台 CPU/IO 和单纯代理连接变化不会重置空闲计时或直接退出 Mask。强制 Mask 不受任何活动信号影响。规则创建或删除失败时，Mask 状态保持不变。

本工具退出后会解除 Mask；如果需要让 Mask 持续生效，必须保持本工具运行。
