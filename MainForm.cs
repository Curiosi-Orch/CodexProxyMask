using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CodexProxyMask
{
    internal sealed class MainForm : Form
    {
        private readonly HistoryStore _history = new HistoryStore();
        private readonly AppSettings _settings;
        private readonly FirewallRuleManager _firewall;
        private ActivityMonitor _activityMonitor;
        private Timer _timer;
        private Timer _enforcementTimer;
        private DateTime _lastActivityUtc;
        private MaskMode _maskMode = MaskMode.None;
        private ActivitySnapshot _lastSnapshot;
        private int _enforcedConnectionCount;
        private DateTime _lastEnforcementErrorUtc = DateTime.MinValue;
        private DateTime _lastFastSessionCheckUtc = DateTime.MinValue;
        private bool _updatingControls;
        private bool _closing;

        private CheckBox _autoMaskCheckBox;
        private CheckBox _autoUnmaskCheckBox;
        private NumericUpDown _idleMinutesUpDown;
        private NumericUpDown _proxyPortUpDown;
        private Button _maskButton;
        private Button _forceMaskButton;
        private Label _maskStateLabel;
        private Label _targetStateLabel;
        private Label _idleStateLabel;
        private Label _signalStateLabel;
        private Label _ruleStateLabel;
        private ListView _historyListView;

        public MainForm()
        {
            _settings = SettingsStore.Load();
            _firewall = new FirewallRuleManager(_settings.ProxyPort);
            _activityMonitor = new ActivityMonitor(_settings.ProxyPort);
            _lastActivityUtc = DateTime.UtcNow;

            InitializeForm();
            InitializeControls();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            foreach (var entry in _history.Load())
            {
                AddHistoryRow(entry);
            }

            var cleanup = _firewall.RemoveMask();
            if (cleanup.RuleCount > 0 || !cleanup.Success)
            {
                AddHistory("STARTUP_CLEANUP", "清理上次残留规则", cleanup.Message);
                if (!cleanup.Success)
                {
                    MessageBox.Show(this,
                        "上次运行遗留的部分防火墙规则无法删除。请先确认程序以管理员身份运行，然后点击‘解除 Mask’重试。\r\n\r\n"
                        + cleanup.Message,
                        "Codex Proxy Mask", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            _timer = new Timer { Interval = 5000 };
            _timer.Tick += OnTimerTick;
            _timer.Start();

            _enforcementTimer = new Timer { Interval = 200 };
            _enforcementTimer.Tick += OnEnforcementTick;
            OnTimerTick(this, EventArgs.Empty);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_closing)
            {
                base.OnFormClosing(e);
                return;
            }

            if (_timer != null)
            {
                _timer.Stop();
            }
            if (_enforcementTimer != null)
            {
                _enforcementTimer.Stop();
            }

            if (_firewall.StoredRuleCount > 0)
            {
                var result = _firewall.RemoveMask();
                if (!result.Success)
                {
                    MessageBox.Show(this,
                        "无法完全解除屏蔽规则，程序将保持打开以便重试。\r\n\r\n" + result.Message,
                        "Codex Proxy Mask", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    e.Cancel = true;
                    if (_timer != null) _timer.Start();
                    if (_enforcementTimer != null && _maskMode != MaskMode.None)
                    {
                        _enforcementTimer.Start();
                    }
                    return;
                }
            }

            SettingsStore.Save(_settings);
            if (_activityMonitor != null)
            {
                _activityMonitor.Dispose();
            }
            _closing = true;
            base.OnFormClosing(e);
        }

        private void InitializeForm()
        {
            Text = "Codex Proxy Mask v16";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(860, 620);
            Size = new Size(980, 700);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.Sizable;
        }

        private void InitializeControls()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            root.Controls.Add(BuildSettingsGroup(), 0, 0);
            root.Controls.Add(BuildStatusGroup(), 0, 1);
            root.Controls.Add(BuildHistoryGroup(), 0, 2);
        }

        private Control BuildSettingsGroup()
        {
            var group = new GroupBox { Text = "运行设置", Dock = DockStyle.Fill };
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 8, 10, 4),
                AutoScroll = true,
                WrapContents = true
            };
            group.Controls.Add(panel);

            _autoMaskCheckBox = new CheckBox
            {
                Text = "启用自动 Mask",
                Checked = _settings.AutoMaskEnabled,
                AutoSize = true,
                Margin = new Padding(3, 7, 14, 3)
            };
            _autoMaskCheckBox.CheckedChanged += OnAutoMaskChanged;
            panel.Controls.Add(_autoMaskCheckBox);

            _autoUnmaskCheckBox = new CheckBox
            {
                Text = "检测到 Codex 用户操作或处理/输出时解除自动/手动 Mask",
                Checked = _settings.AutoUnmaskEnabled,
                AutoSize = true,
                Margin = new Padding(3, 7, 14, 3)
            };
            _autoUnmaskCheckBox.CheckedChanged += OnAutoUnmaskChanged;
            panel.Controls.Add(_autoUnmaskCheckBox);

            panel.Controls.Add(new Label
            {
                Text = "空闲分钟:",
                AutoSize = true,
                Margin = new Padding(3, 9, 3, 3)
            });
            _idleMinutesUpDown = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1440,
                Value = _settings.IdleMinutes,
                Width = 62,
                Margin = new Padding(0, 5, 14, 3)
            };
            _idleMinutesUpDown.ValueChanged += OnIdleMinutesChanged;
            panel.Controls.Add(_idleMinutesUpDown);

            panel.Controls.Add(new Label
            {
                Text = "MonoCloud 端口:",
                AutoSize = true,
                Margin = new Padding(3, 9, 3, 3)
            });
            _proxyPortUpDown = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 65535,
                Value = _settings.ProxyPort,
                Width = 70,
                Margin = new Padding(0, 5, 14, 3)
            };
            _proxyPortUpDown.ValueChanged += OnProxyPortChanged;
            panel.Controls.Add(_proxyPortUpDown);

            _maskButton = new Button
            {
                Text = "手动启用 Mask",
                AutoSize = true,
                Padding = new Padding(10, 3, 10, 3),
                Margin = new Padding(3, 3, 6, 3)
            };
            _maskButton.Click += OnMaskToggleClick;
            panel.Controls.Add(_maskButton);

            _forceMaskButton = new Button
            {
                Text = "强制启用 Mask",
                AutoSize = true,
                Padding = new Padding(10, 3, 10, 3),
                Margin = new Padding(3, 3, 6, 3)
            };
            _forceMaskButton.Click += OnForceMaskToggleClick;
            panel.Controls.Add(_forceMaskButton);

            panel.Controls.Add(new Label
            {
                Text = "只拦截并持续断开目标进程访问 127.0.0.1 的指定端口，不会关闭 MonoCloud，也不影响其他程序。",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Margin = new Padding(3, 9, 3, 3)
            });

            return group;
        }

        private Control BuildStatusGroup()
        {
            var group = new GroupBox { Text = "当前状态", Dock = DockStyle.Fill };
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(8, 5, 8, 5)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 4; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            }
            group.Controls.Add(grid);

            _maskStateLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            _targetStateLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            _idleStateLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            _signalStateLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            _ruleStateLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

            AddStatusRow(grid, 0, "Mask 状态:", _maskStateLabel);
            AddStatusRow(grid, 1, "目标进程:", _targetStateLabel);
            AddStatusRow(grid, 2, "空闲计时:", _idleStateLabel);
            AddStatusRow(grid, 3, "活动信号:", _signalStateLabel);

            // Keep the rule summary visible in the target row's tooltip; it is also shown in the window title area.
            _ruleStateLabel.Visible = false;
            return group;
        }

        private static void AddStatusRow(TableLayoutPanel grid, int row, string caption, Control value)
        {
            grid.Controls.Add(new Label
            {
                Text = caption,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray
            }, 0, row);
            grid.Controls.Add(value, 1, row);
        }

        private Control BuildHistoryGroup()
        {
            var group = new GroupBox { Text = "Mask 操作历史（本地保存最近 1000 条）", Dock = DockStyle.Fill };
            _historyListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                MultiSelect = false
            };
            _historyListView.Columns.Add("时间", 155);
            _historyListView.Columns.Add("操作", 145);
            _historyListView.Columns.Add("原因", 220);
            _historyListView.Columns.Add("详情", 390);
            group.Controls.Add(_historyListView);
            return group;
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_closing)
            {
                return;
            }

            try
            {
                var snapshot = _activityMonitor.Sample();
                _lastSnapshot = snapshot;

                if (snapshot.WorkActivity)
                {
                    _lastActivityUtc = DateTime.UtcNow;
                }

                if ((_maskMode == MaskMode.Automatic || _maskMode == MaskMode.Manual)
                    && _settings.AutoUnmaskEnabled
                    && snapshot.UserActivity)
                {
                    RemoveMask("检测到 Codex 用户操作或处理/输出");
                }

                var idle = DateTime.UtcNow - _lastActivityUtc;
                if (MaskAutomationPolicy.ShouldApplyAutomatic(
                    _maskMode, _settings.AutoMaskEnabled,
                    snapshot.TargetProcessCount, DateTime.UtcNow,
                    _lastActivityUtc, _settings.IdleMinutes))
                {
                    ApplyMask(MaskMode.Automatic,
                        "Codex 未检测到活动且已空闲 " + _settings.IdleMinutes.ToString() + " 分钟");
                }

                UpdateStatus(snapshot, idle);
            }
            catch (Exception ex)
            {
                _signalStateLabel.Text = "监测异常: " + ex.Message;
            }
        }

        private void OnEnforcementTick(object sender, EventArgs e)
        {
            if (_closing || _maskMode == MaskMode.None)
            {
                return;
            }

            try
            {
                if ((_maskMode == MaskMode.Automatic || _maskMode == MaskMode.Manual)
                    && _settings.AutoUnmaskEnabled)
                {
                    var nowUtc = DateTime.UtcNow;
                    if (nowUtc - _lastFastSessionCheckUtc >= TimeSpan.FromSeconds(1))
                    {
                        _lastFastSessionCheckUtc = nowUtc;
                        var activeTaskCount = _activityMonitor.GetActiveSessionTaskCount();
                        if (activeTaskCount > 0)
                        {
                            if (_lastSnapshot != null)
                            {
                                _lastSnapshot.ActiveTaskCount = activeTaskCount;
                            }
                            SetImmediateActivitySignal("Codex任务处理中:"
                                + activeTaskCount.ToString());
                            RemoveMask("检测到 Codex 任务开始，立即自动解除 Mask");
                            return;
                        }
                    }

                    _activityMonitor.RefreshSubmissionTargets();

                    // Read Enter submissions before the 200 ms connection
                    // terminator can kill the newly submitted request.
                    if (_activityMonitor.TryConsumeImmediateSubmission())
                    {
                        SetImmediateActivitySignal("用户提交请求（即时检测）");
                        RemoveMask("检测到用户提交请求（Enter），立即自动解除 Mask");
                        return;
                    }

                    // A target-window click alone is not enough. Only pair it
                    // with a target process opening the masked proxy connection,
                    // which distinguishes a send-button action from ordinary UI clicks.
                    if (_activityMonitor.HasRecentTargetClick(TimeSpan.FromSeconds(2)))
                    {
                        var inspection = _firewall.InspectMaskConnections();
                        if (inspection.Success && inspection.FoundCount > 0)
                        {
                            _activityMonitor.ConsumeRecentTargetClick();
                            _activityMonitor.ConfirmImmediateSubmission();
                            SetImmediateActivitySignal("用户点击发送并发起代理连接（即时检测）");
                            RemoveMask("检测到点击发送请求，立即自动解除 Mask");
                            return;
                        }
                    }
                }

                var result = _firewall.EnforceMaskConnections();
                if (result.TerminatedCount > 0)
                {
                    _enforcedConnectionCount = Math.Min(
                        int.MaxValue - result.TerminatedCount,
                        _enforcedConnectionCount) + result.TerminatedCount;
                }

                if (!result.Success
                    && (DateTime.UtcNow - _lastEnforcementErrorUtc >= TimeSpan.FromSeconds(30)))
                {
                    _lastEnforcementErrorUtc = DateTime.UtcNow;
                    AddHistory("ENFORCE_ERROR", "持续断开 Codex 代理连接失败", result.Message);
                }
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow - _lastEnforcementErrorUtc >= TimeSpan.FromSeconds(30))
                {
                    _lastEnforcementErrorUtc = DateTime.UtcNow;
                    AddHistory("ENFORCE_ERROR", "持续断开 Codex 代理连接异常", ex.Message);
                }
            }
        }

        private void SetImmediateActivitySignal(string signal)
        {
            _lastActivityUtc = DateTime.UtcNow;
            if (_lastSnapshot != null)
            {
                _lastSnapshot.UserActivity = true;
                _lastSnapshot.WorkActivity = true;
                _lastSnapshot.Signals = signal;
            }
        }

        private void OnMaskToggleClick(object sender, EventArgs e)
        {
            if (_maskMode == MaskMode.None && _firewall.StoredRuleCount == 0)
            {
                ApplyMask(MaskMode.Manual, "用户点击手动启用 Mask");
                return;
            }

            RemoveMask("用户点击手动解除 Mask");
        }

        private void OnForceMaskToggleClick(object sender, EventArgs e)
        {
            if (_maskMode == MaskMode.Forced)
            {
                RemoveMask("用户点击解除强制 Mask");
                return;
            }

            if (_maskMode == MaskMode.None || _firewall.StoredRuleCount == 0)
            {
                ApplyMask(MaskMode.Forced, "用户点击强制启用 Mask");
                return;
            }

            _maskMode = MaskMode.Forced;
            if (_enforcementTimer != null)
            {
                _enforcementTimer.Start();
            }
            AddHistory("MASK_FORCE", "用户将现有 Mask 切换为强制模式",
                "沿用当前防火墙规则并持续断开目标代理连接；用户提交请求、处理/输出和测试脚本均不会自动解除。");
            UpdateStatus(_lastSnapshot, DateTime.UtcNow - _lastActivityUtc);
        }

        private void OnAutoMaskChanged(object sender, EventArgs e)
        {
            if (_updatingControls) return;
            _settings.AutoMaskEnabled = _autoMaskCheckBox.Checked;
            SettingsStore.Save(_settings);

            if (!_settings.AutoMaskEnabled && _maskMode == MaskMode.Automatic)
            {
                RemoveMask("用户关闭自动 Mask");
            }
        }

        private void OnAutoUnmaskChanged(object sender, EventArgs e)
        {
            if (_updatingControls) return;
            _settings.AutoUnmaskEnabled = _autoUnmaskCheckBox.Checked;
            SettingsStore.Save(_settings);
        }

        private void OnIdleMinutesChanged(object sender, EventArgs e)
        {
            if (_updatingControls) return;
            _settings.IdleMinutes = (int)_idleMinutesUpDown.Value;
            SettingsStore.Save(_settings);
        }

        private void OnProxyPortChanged(object sender, EventArgs e)
        {
            if (_updatingControls) return;
            if (_maskMode != MaskMode.None)
            {
                _updatingControls = true;
                _proxyPortUpDown.Value = _settings.ProxyPort;
                _updatingControls = false;
                MessageBox.Show(this, "请先解除 Mask，再修改 MonoCloud 端口。", "Codex Proxy Mask",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _settings.ProxyPort = (int)_proxyPortUpDown.Value;
            _firewall.ProxyPort = _settings.ProxyPort;
            var previousMonitor = _activityMonitor;
            _activityMonitor = new ActivityMonitor(_settings.ProxyPort);
            if (previousMonitor != null)
            {
                previousMonitor.Dispose();
            }
            _lastActivityUtc = DateTime.UtcNow;
            SettingsStore.Save(_settings);
        }

        private void ApplyMask(MaskMode mode, string reason)
        {
            var result = _firewall.ApplyMask();
            if (!result.Success)
            {
                AddHistory("MASK_ERROR", reason, result.Message);
                MessageBox.Show(this, result.Message, "Codex Proxy Mask",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _maskMode = mode;
            _enforcedConnectionCount = 0;
            _lastEnforcementErrorUtc = DateTime.MinValue;
            if (_enforcementTimer != null)
            {
                _enforcementTimer.Start();
            }
            AddHistory(mode == MaskMode.Forced ? "FORCE_MASK_ON" : "MASK_ON", reason,
                result.TargetPathCount.ToString() + " 个目标路径，" + result.RuleCount.ToString()
                + " 条规则；仅限本机代理端口 " + _settings.ProxyPort.ToString()
                + "；已启动持续断连保护");
            UpdateStatus(_lastSnapshot, DateTime.UtcNow - _lastActivityUtc);
        }

        private void RemoveMask(string reason)
        {
            if (_maskMode == MaskMode.None && _firewall.StoredRuleCount == 0)
            {
                if (_enforcementTimer != null) _enforcementTimer.Stop();
                return;
            }

            if (_enforcementTimer != null) _enforcementTimer.Stop();
            var enforcedConnectionCount = _enforcedConnectionCount;
            var result = _firewall.RemoveMask();
            if (!result.Success)
            {
                if (_enforcementTimer != null && _maskMode != MaskMode.None)
                {
                    _enforcementTimer.Start();
                }
                AddHistory("UNMASK_ERROR", reason, result.Message);
                MessageBox.Show(this, result.Message, "Codex Proxy Mask",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _maskMode = MaskMode.None;
            _lastActivityUtc = DateTime.UtcNow;
            _enforcedConnectionCount = 0;
            AddHistory("MASK_OFF", reason, result.Message
                + (enforcedConnectionCount > 0
                    ? " Mask 期间持续断开 " + enforcedConnectionCount.ToString() + " 条目标代理连接。"
                    : string.Empty));
            UpdateStatus(_lastSnapshot, TimeSpan.Zero);
        }

        private void UpdateStatus(ActivitySnapshot snapshot, TimeSpan idle)
        {
            if (snapshot == null)
            {
                snapshot = new ActivitySnapshot
                {
                    TargetProcessCount = 0,
                    Signals = "等待第一次采样"
                };
            }

            string stateText;
            if (_maskMode == MaskMode.None)
            {
                stateText = "未屏蔽";
            }
            else if (_maskMode == MaskMode.Manual)
            {
                stateText = "已屏蔽（手动临时）";
            }
            else if (_maskMode == MaskMode.Forced)
            {
                stateText = "已屏蔽（强制锁定，持续断连）";
            }
            else
            {
                stateText = "已屏蔽（自动空闲）";
            }
            _maskStateLabel.Text = stateText;
            _maskStateLabel.ForeColor = _maskMode == MaskMode.None ? Color.DarkGreen : Color.DarkRed;

            _targetStateLabel.Text = snapshot.TargetProcessCount.ToString()
                + " 个目标进程"
                + (snapshot.ActiveTaskCount > 0
                    ? "；活动 Codex 任务 " + snapshot.ActiveTaskCount.ToString()
                    : string.Empty)
                + (snapshot.TestProcessCount > 0
                    ? "；测试脚本进程 " + snapshot.TestProcessCount.ToString()
                    : string.Empty)
                + "；代理 127.0.0.1:" + _settings.ProxyPort.ToString()
                + "；代理连接 " + snapshot.ProxySocketCount.ToString()
                + (_maskMode != MaskMode.None
                    ? "；累计断开 " + _enforcedConnectionCount.ToString()
                    : string.Empty);
            _idleStateLabel.Text = "最近活动 " + _lastActivityUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                + "；已空闲 " + FormatElapsed(idle)
                + "；阈值 " + _settings.IdleMinutes.ToString() + " 分钟";
            _signalStateLabel.Text = snapshot.Signals;
            _signalStateLabel.ForeColor = snapshot.WorkActivity ? Color.DarkOrange : Color.DimGray;
            _ruleStateLabel.Text = "规则数: " + _firewall.StoredRuleCount.ToString();
            _maskButton.Text = _maskMode == MaskMode.None && _firewall.StoredRuleCount == 0
                ? "手动启用 Mask"
                : "解除 Mask";
            _maskButton.Enabled = true;
            _forceMaskButton.Text = _maskMode == MaskMode.Forced
                ? "解除强制 Mask"
                : _maskMode == MaskMode.None
                    ? "强制启用 Mask"
                    : "切换为强制 Mask";
            _forceMaskButton.Enabled = true;
            _proxyPortUpDown.Enabled = _maskMode == MaskMode.None;
        }

        private void AddHistory(string action, string reason, string detail)
        {
            var entry = _history.Add(action, reason, detail);
            AddHistoryRow(entry);
        }

        private void AddHistoryRow(HistoryEntry entry)
        {
            if (_historyListView == null || entry == null)
            {
                return;
            }

            var item = new ListViewItem(entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(entry.Action ?? string.Empty);
            item.SubItems.Add(entry.Reason ?? string.Empty);
            item.SubItems.Add(entry.Detail ?? string.Empty);
            _historyListView.Items.Insert(0, item);

            while (_historyListView.Items.Count > 300)
            {
                _historyListView.Items.RemoveAt(_historyListView.Items.Count - 1);
            }
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            if (elapsed.TotalDays >= 1) return elapsed.ToString("d'天 'hh':'mm':'ss");
            return elapsed.ToString("hh':'mm':'ss");
        }
    }
}
