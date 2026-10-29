// =====================================================================
//  SettingsPage.cs  --  系统设置页（全部参数运行时可改，无需改代码）
//  ---------------------------------------------------------------------
//  分六块：
//    ① 读卡距离：射频功率档位 + 连续命中确认（对应“刷卡距离限制”）
//    ② 刷卡间隔与防刷：全局间隔/同卡方向间隔/异常刷卡锁定（“刷卡间隔时限”）
//    ③ 借用期限：默认借用天数 + 临期提醒
//    ④ 借用人卡：双卡模式开关
//    ⑤ 数据备份与恢复：一键备份 / 备份列表 / 一键恢复
//    ⑥ 刷卡审计：被拦截的刷卡留痕（防恶意刷数据的证据）
//
//  现场演示时若老师临时提要求，绝大多数情况只要在这里改数字 → 点“保存设置”
//  即可立即生效，不需要重新编译（这也是把规则做成配置项的初衷）。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LabBorrow
{
    public partial class MainForm
    {
        // ---- 设置页控件 ----
        private NumericUpDown numPower, numHits, numTotalHits, numReadWindow, numPoll, numTagCd;
        private CheckBox chkSound;
        private NumericUpDown numVolume, numSoundMax;
        private TextBox txtSfxBorrow, txtSfxReturn, txtSfxBlocked, txtSfxAbuse;

        // 备份列表要枚举磁盘、审计列表要查 200 条记录，每次切到设置页都做一遍就会明显卡顿。
        // 改成"首次进入 / 数据变动后"才真正刷新（见 ReloadSettingsPage）。
        private bool backupsDirty;
        private bool auditDirty;
        private NumericUpDown numGlobalGap, numB2R, numR2B, numAbuseWin, numAbuseMax, numAbuseGlobal, numAbuseLock;
        private NumericUpDown numBorrowDays, numWarnHours, numUserWait, numBackupKeep;
        private CheckBox chkUserCardMode, chkAutoBackup;
        private ComboBox cboDistancePreset;
        private Label lblReaderPower, lblRuleSummary, lblDbInfo, lblBackupTip;
        private Label lblUnsaved;            // 「有未保存的修改」提示
        private string settingsSnapshot = ""; // 上次保存/载入时各控件的取值快照
        private System.Windows.Forms.Timer unsavedTimer;

        // ---- ⑧ 界面字号 / 触屏 ----
        private ComboBox cboFontLevel;
        private CheckBox chkTouchFriendly;
        // ---- ⑨ 归还到期提醒 ----
        private ComboBox cboRemindMode;
        private NumericUpDown numRemindLead, numRemindInterval;
        private CheckBox chkRemindOverdue, chkPrintSlip;
        // ---- ⑩ 自助终端 ----
        private CheckBox chkKiosk, chkAutoStart, chkKeepScreen;
        private NumericUpDown numIdleSec;
        // ---- ⑫ 自动邮件提醒 ----
        private CheckBox chkMail, chkMailOnBorrow, chkMailOnDue, chkMailSsl;
        private TextBox txtMailHost, txtMailUser, txtMailPass, txtMailFrom, txtMailFromName;
        private NumericUpDown numMailPort;

        // ---- ⑪ 校园卡适配 ----
        private CheckBox chkNfc;
        private ComboBox cboNfcReader;
        private TextBox txtNfcPrefix;
        private ListBox lstBackups;
        private ListView lvAudit;
        private bool loadingSettings;   // 载入控件时抑制事件

        // 距离档位：索引 -> dBm（0 = 不修改读卡器功率）
        private static readonly int[] DistancePresets = { -1, 15, 18, 22, 26, 30, 0 };
        private static readonly string[] DistancePresetNames =
        {
            "自定义（不改变下面的数值）",
            "超近距 ≈ 10 cm（15 dBm）",
            "近距   ≈ 30 cm（18 dBm）",
            "中距   ≈ 50 cm（22 dBm）",
            "远距   ≈ 1 m （26 dBm）",
            "最远   ≈ 2 m 以上（30 dBm，出厂默认）",
            "不修改（沿用读卡器当前功率）"
        };

        // ===================================================================
        //  构建设置页
        // ===================================================================
        private Control MakeSettingsPanel()
        {
            var scroll = new Panel { BackColor = Th.BgPage, Dock = DockStyle.Fill, AutoScroll = true };
            // 用单列 TableLayoutPanel 纵向堆叠各分区：宽度自动撑满，高度按内容自适应
            settingsStack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                BackColor = Th.BgPage,
                Padding = new Padding(0)
            };
            settingsStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            // ---------------- 顶部操作条 ----------------
            // 设置页很长，「保存」按钮原本只在最底部，很容易改完找不到它，
            // 于是在最上面也放一份，并用一行文字明确告诉用户「改了还没保存」。
            var topBar = new Panel { BackColor = Th.BgCard, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 8) };
            var topFlow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
            lblUnsaved = new Label
            {
                Text = "所有修改均已保存", AutoSize = true, ForeColor = Th.TxtMuted,
                Font = Th.NormFont, Margin = new Padding(0, 10, 14, 0)
            };
            var btnSaveTop = MakeButton("保存设置（立即生效）", Th.Accent, Point.Empty, new Size(180, 36));
            btnSaveTop.Click += (s, e) => SaveSettingsFromControls(true);
            topFlow.Controls.AddRange(new Control[] { btnSaveTop, lblUnsaved });
            topBar.Controls.Add(topFlow);
            AddSection(topBar);

            // ---------------- ① 读卡距离 ----------------
            var s1 = MakeSection("① 读卡距离限制（避免无意义的误刷）");
            cboDistancePreset = new ComboBox
            {
                Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Th.BgInput,
                ForeColor = Th.TxtPri, FlatStyle = FlatStyle.Flat, Font = Th.NormFont
            };
            cboDistancePreset.Items.AddRange(DistancePresetNames);
            cboDistancePreset.SelectedIndex = 0;
            cboDistancePreset.SelectedIndexChanged += (s, e) =>
            {
                if (loadingSettings) return;
                int idx = cboDistancePreset.SelectedIndex;
                if (idx > 0 && idx < DistancePresets.Length && DistancePresets[idx] >= 0)
                {
                    numPower.Value = Clamp(numPower, DistancePresets[idx]);
                    // 选预设档位时自动调整「总命中次数 + 窗口」：档位越近，限制越严
                    int[] minHitsByPreset = { -1, 4, 3, 3, 2, 2, 1 };
                    int[] windowMsByPreset = { -1, 1500, 2000, 2500, 3000, 3500, 2500 };
                    if (idx < minHitsByPreset.Length && minHitsByPreset[idx] > 0)
                        numTotalHits.Value = Clamp(numTotalHits, minHitsByPreset[idx]);
                    if (idx < windowMsByPreset.Length && windowMsByPreset[idx] > 0)
                        numReadWindow.Value = Clamp(numReadWindow, windowMsByPreset[idx]);
                }
            };
            AddRow(s1, "距离档位：", cboDistancePreset, "选择后自动填入下面的功率值与读取门槛（数字越大约严格）");

            numPower = MakeNum(0, 30, 1);
            AddRow(s1, "射频功率(dBm)：", numPower, "0=不修改；数值越小读卡距离越近，误刷越少");

            numHits = MakeNum(1, 20, 1);
            AddRow(s1, "连续命中确认(轮)：", numHits, "标签需连续 N 轮都被读到才算有效刷卡，1=关闭");

            numTotalHits = MakeNum(1, 20, 1);
            AddRow(s1, "窗口内累计命中(次)：", numTotalHits, "标签在指定时间窗口内累计被读到这么多次才算稳定；专门过滤远距离偶发读到");

            numReadWindow = MakeNum(200, 10000, 100);
            AddRow(s1, "累计命中窗口(ms)：", numReadWindow, "从首次读到开始计，超时则窗口内计数清零；过短会严苛，过长不灵活");

            numPoll = MakeNum(50, 1000, 10);
            AddRow(s1, "盘点间隔(ms)：", numPoll, "越小反应越快，标签多时建议 250~400");

            numTagCd = MakeNum(0, 60000, 100);
            AddRow(s1, "同标签上报冷却(ms)：", numTagCd, "同一标签两次上报的最小间隔，防抖动重复触发");

            var btnApplyPower = MakeButton("应用到读卡器", Th.Accent, Point.Empty, new Size(120, 30));
            btnApplyPower.Click += (s, e) => ApplyDistanceToReader(true);
            var btnReadInfo = MakeButton("读取读卡器信息", Th.Accent2, Point.Empty, new Size(130, 30));
            btnReadInfo.Margin = new Padding(10, 0, 0, 0);
            btnReadInfo.Click += (s, e) => ShowReaderInfo();
            var btnClearLocks = MakeButton("解除全部刷卡锁定", Th.Warn, Point.Empty, new Size(150, 30));
            btnClearLocks.Margin = new Padding(10, 0, 0, 0);
            btnClearLocks.Click += (s, e) =>
            {
                if (guard != null) guard.ClearLocks();
                MsgBox("已解除全部临时锁定。");
            };
            AddRow(s1, "", MakeRowHost(btnApplyPower, btnReadInfo, btnClearLocks), null);

            lblReaderPower = new Label
            {
                Text = "读卡器状态：未知", AutoSize = true, ForeColor = Th.TxtSec,
                Font = Th.MonoFont, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s1, "", lblReaderPower, null);
            SetSectionCollapsed(s1, false);     // 常用，默认展开
            AddSection(s1);

            // ---------------- ② 刷卡间隔与防刷 ----------------
            var s2 = MakeSection("② 刷卡间隔时限（防止恶意刷数据）");
            numGlobalGap = MakeNum(0, 10000, 50);
            AddRow(s2, "全局最小间隔(ms)：", numGlobalGap, "任意两次成功登记的最小时间差，0=不限");
            numB2R = MakeNum(0, 3600, 1);
            AddRow(s2, "借出→归还(秒)：", numB2R, "借出后多久才能归还，防止造出 0 分钟假记录");
            numR2B = MakeNum(0, 3600, 1);
            AddRow(s2, "归还→借出(秒)：", numR2B, "同一设备归还后多久才能再次借出");
            numAbuseWin = MakeNum(1, 3600, 1);
            AddRow(s2, "异常统计窗口(秒)：", numAbuseWin, "在该时间窗口内统计刷卡次数");
            numAbuseMax = MakeNum(1, 1000, 1);
            AddRow(s2, "窗口内同卡上限(次)：", numAbuseMax, "同一标签超过该次数即判为恶意刷数据并锁定");
            numAbuseGlobal = MakeNum(1, 5000, 1);
            AddRow(s2, "窗口内全局上限(次)：", numAbuseGlobal, "所有标签合计超限即全局锁定");
            numAbuseLock = MakeNum(1, 3600, 1);
            AddRow(s2, "锁定时长(秒)：", numAbuseLock, "锁定期间该标签刷卡一律拦截并记录审计");
            var lblTip2 = new Label
            {
                Text = "提示：被拦截的刷卡不会写借还流水，但都会记入下方「刷卡审计」，可作为追责证据。",
                AutoSize = true, ForeColor = Th.TxtMuted, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s2, "", lblTip2, null);

            // 一键演示：不需要真的把一张卡连刷 N 次，点击立即触发一次"异常锁定"
            // （播放异常音效 + 主控台显示锁定提示），课堂上验证音效最方便。
            var btnDemoAbuse = MakeButton("模拟一次异常锁定（演示音效）", Th.Warn, Point.Empty, new Size(200, 30));
            btnDemoAbuse.Click += (s, e) =>
            {
                try
                {
                    var r2 = rules ?? SwipeRules.Load();
                    Sfx.VerboseLog = true;
                    Sfx.Play(Sfx.Kind.Abuse, r2.SfxAbuseFile, r2.SoundVolume, r2.SoundMaxSec);
                    ShowHeroHintTemp("⚠ 模拟演示：该标签已触发异常锁定，播放异常音效", 8000);
                    AddLogRow("演示：手动触发一次异常锁定音效（文件：" + r2.SfxAbuseFile + "）");
                }
                catch (Exception ex) { MsgBox("演示触发失败：" + ex.Message); }
            };
            AddRow(s2, "", btnDemoAbuse, "不用真刷卡，点击立即播放「异常锁定」音效，用于课堂演示/自检");

            SetSectionCollapsed(s2, false);     // 常用（防刷演示），默认展开
            AddSection(s2);

            // ---------------- ③ 借用期限 ----------------
            var s3 = MakeSection("③ 借用期限与逾期提醒");
            numBorrowDays = MakeNum(0, 365, 1);
            AddRow(s3, "默认借用期限(天)：", numBorrowDays, "0=不设期限；借出时自动写入应还时间");
            numWarnHours = MakeNum(0, 720, 1);
            AddRow(s3, "临期提醒提前(小时)：", numWarnHours, "不足该时长时在借看板标「临期」");
            SetSectionCollapsed(s3, true);      // 不常用，默认收起（点标题可展开）
            AddSection(s3);

            // ---------------- ④ 借用人卡 ----------------
            var s4 = MakeSection("④ 借用人卡（学生卡）双卡模式");
            chkUserCardMode = new CheckBox
            {
                Text = "启用：先刷学生卡 → 再刷设备卡 → 自动完成借出（不需要手工输入姓名）",
                AutoSize = true, ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 2, 0, 0)
            };
            AddRow(s4, "", chkUserCardMode, null);
            numUserWait = MakeNum(1, 300, 1);
            AddRow(s4, "借用人卡有效期(秒)：", numUserWait, "刷学生卡后多少秒内刷设备卡可直接借出");
            var lblTip4 = new Label
            {
                Text = "未启用该模式时：刷学生卡也会自动填入借出对话框的「借用人」，只是仍需点确认。\n" +
                       "学生卡在「用户管理」页登记（卡号 + 姓名），未登记的学生卡会被当作陌生标签提示。",
                AutoSize = true, ForeColor = Th.TxtMuted, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s4, "", lblTip4, null);
            SetSectionCollapsed(s4, true);      // 不常用，默认收起
            AddSection(s4);

            // ---------------- ⑤ 备份与恢复 ----------------
            var s5 = MakeSection("⑤ 数据备份与恢复");
            chkAutoBackup = new CheckBox
            {
                Text = "程序启动时自动备份数据库", AutoSize = true,
                ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 2, 0, 0)
            };
            AddRow(s5, "", chkAutoBackup, null);
            numBackupKeep = MakeNum(1, 500, 1);
            AddRow(s5, "自动备份保留(份)：", numBackupKeep, "超出份数时删除最旧的自动备份");

            lstBackups = new ListBox
            {
                Width = 360, Height = 120, BackColor = Th.BgInput, ForeColor = Th.TxtPri,
                BorderStyle = BorderStyle.FixedSingle, Font = Th.MonoFont, IntegralHeight = false
            };
            var btnBackupNow = MakeButton("立即备份", Th.Accent, Point.Empty, new Size(100, 30));
            btnBackupNow.Click += (s, e) => DoBackup(true);
            var btnRestore = MakeButton("恢复选中备份", Th.Warn, Point.Empty, new Size(120, 30));
            btnRestore.Margin = new Padding(10, 0, 0, 0);
            btnRestore.Click += (s, e) => DoRestore();
            var btnRefreshBak = MakeButton("刷新列表", Th.Accent2, Point.Empty, new Size(100, 30));
            btnRefreshBak.Margin = new Padding(10, 0, 0, 0);
            btnRefreshBak.Click += (s, e) => ReloadBackups();
            var btnOpenBak = MakeButton("打开备份目录", Th.Accent2, Point.Empty, new Size(120, 30));
            btnOpenBak.Margin = new Padding(10, 0, 0, 0);
            btnOpenBak.Click += (s, e) => OpenPath(Db.BackupDir);
            var btnOpenDb = MakeButton("打开数据库位置", Th.Accent2, Point.Empty, new Size(130, 30));
            btnOpenDb.Margin = new Padding(10, 0, 0, 0);
            btnOpenDb.Click += (s, e) => OpenPath(Db.DbPath);
            AddRow(s5, "备份文件：", MakeRowHost(lstBackups), null);
            AddRow(s5, "", MakeRowHost(btnBackupNow, btnRestore, btnRefreshBak, btnOpenBak, btnOpenDb), null);

            lblBackupTip = new Label
            {
                Text = "", AutoSize = true, ForeColor = Th.TxtMuted, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s5, "", lblBackupTip, null);
            lblDbInfo = new Label
            {
                Text = "数据库文件：" + Db.DbPath, AutoSize = true, ForeColor = Th.TxtMuted,
                Font = Th.MonoFont, Margin = new Padding(0, 4, 0, 0)
            };
            AddRow(s5, "", lblDbInfo, null);
            SetSectionCollapsed(s5, true);      // 不常用，默认收起
            AddSection(s5);

            // ---------------- ⑥ 刷卡审计 ----------------
            var s6 = MakeSection("⑥ 刷卡审计（被拦截的刷卡记录）");
            lvAudit = new ListView
            {
                Width = 720, Height = 170, View = View.Details, FullRowSelect = true,
                GridLines = false, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Th.BgInput, ForeColor = Th.TxtPri, Font = Th.NormFont
            };
            lvAudit.Columns.Add("时间", 150);
            lvAudit.Columns.Add("类型", 90);
            lvAudit.Columns.Add("标签 EPC", 200);
            lvAudit.Columns.Add("原因", 420);
            EnableDoubleBuffer(lvAudit);
            var btnAuditRefresh = MakeButton("刷新审计", Th.Accent, Point.Empty, new Size(100, 30));
            btnAuditRefresh.Click += (s, e) => ReloadAudit();
            var btnAuditClear = MakeButton("清空审计日志", Th.Danger, Point.Empty, new Size(120, 30));
            btnAuditClear.Margin = new Padding(10, 0, 0, 0);
            btnAuditClear.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "确认清空全部刷卡审计日志？\n（不影响借还流水与设备台账）", "清空确认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Db.ClearAudit();
                ReloadAudit();
            };
            AddRow(s6, "审计日志：", MakeRowHost(lvAudit), null);
            AddRow(s6, "", MakeRowHost(btnAuditRefresh, btnAuditClear), null);
            SetSectionCollapsed(s6, true);      // 不常用，默认收起
            AddSection(s6);

            // ---------------- ⑦ 提示音效 ----------------
            var s7 = MakeSection("⑦ 刷卡提示音效（借出 / 归还 / 拦截 / 异常）");

            chkSound = new CheckBox
            {
                Text = "启用提示音效（借出、归还、被拦截、判定恶意刷数据时各播一种声音）",
                AutoSize = true, ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 2, 0, 0)
            };
            AddRow(s7, "", chkSound, null);
            numVolume = MakeNum(0, 100, 5);
            AddRow(s7, "音量(%)：", numVolume, "0=静音，100=最大");
            numSoundMax = MakeNum(0, 600, 1);
            AddRow(s7, "最长播放(秒)：", numSoundMax, "自定义音频文件最多播几秒就停；0=整首播完");

            AddSfxRow(s7, "借出成功：", txtSfxBorrow = MakePathBox(), Sfx.Kind.Borrow);
            AddSfxRow(s7, "归还成功：", txtSfxReturn = MakePathBox(), Sfx.Kind.Return);
            AddSfxRow(s7, "刷卡被拦截：", txtSfxBlocked = MakePathBox(), Sfx.Kind.Blocked);
            AddSfxRow(s7, "异常刷数据被锁定：", txtSfxAbuse = MakePathBox(), Sfx.Kind.Abuse);

            var lblTip7 = new Label
            {
                Text = "提示：支持 mp3 / wav / wma / m4a / aac / flac / ogg / mid。\n" +
                       "最快的方式是把音频文件直接拖到下面任意一行里导入，会问你要不要复制一份到程序目录的 sounds 文件夹。\n" +
                       "路径留空则该项自动使用内置合成音（借出=上行双音，归还=下行双音，拦截=低沉双音，异常=急促警报）。\n" +
                       "注意：如果程序放在桌面且没运行过 fix_integrity.bat，Windows 会把程序降级运行，\n" +
                       "此时音乐文件放不出来（只能响内置合成音），事件列表会给出提示。",
                AutoSize = true, ForeColor = Th.TxtMuted, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s7, "", lblTip7, null);

            // 一键打开音效文件夹：把 mp3 丢进去就会被自动识别（不用进设置页配，也不受数据库只读影响）
            var btnOpenSfxDir = MakeButton("打开音效文件夹…", Th.Accent, Point.Empty, new Size(150, 30));
            btnOpenSfxDir.Click += (s, e) =>
            {
                try
                {
                    string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sounds");
                    System.IO.Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                    MsgBox("已打开：\n" + dir + "\n\n把音频文件放进去就会被自动识别，文件名建议带关键字：\n"
                           + "异常音效：文件名含『异常 / 锁定 / man / abuse』\n"
                           + "借出音效：含『借出 / borrow』    归还音效：含『归还 / return』\n"
                           + "拦截音效：含『拦截 / blocked』\n"
                           + "（目录里只有一个音频文件时，不管叫什么都会直接用它）");
                }
                catch (Exception ex) { MsgBox("打开失败：" + ex.Message); }
            };
            AddRow(s7, "", btnOpenSfxDir, "点开文件夹，把音频文件丢进去即可自动识别使用");
            SetSectionCollapsed(s7, false);     // 音效，默认展开
            AddSection(s7);

            // ---------------- ⑧ 界面字号（照顾不同年龄 / 不同视力） ----------------
            var s8 = MakeSection("⑧ 界面字号（照顾不同年龄、不同视力人群）");
            cboFontLevel = new ComboBox
            {
                Width = 260, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Th.BgInput,
                ForeColor = Th.TxtPri, FlatStyle = FlatStyle.Flat, Font = Th.NormFont
            };
            cboFontLevel.Items.AddRange(new object[]
            {
                "紧凑（0.9 倍：管理员想看更多内容）",
                "标准（1.0 倍）",
                "大（1.18 倍：建议教室演示用）",
                "超大（1.40 倍：建议自助机 / 远距离查看）"
            });
            cboFontLevel.SelectedIndex = 1;
            AddRow(s8, "字号档位：", cboFontLevel, "切换后整个界面一起放大/缩小，切换时需要重启一次程序（会自动重启）");

            chkTouchFriendly = new CheckBox
            {
                Text = "触屏友好：把按钮、列表行放大到手指点得中", AutoSize = true,
                ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s8, "", chkTouchFriendly, "放在一体机、触摸屏上时建议开启");
            SetSectionCollapsed(s8, false);
            AddSection(s8);

            // ---------------- ⑨ 归还到期提醒 ----------------
            var s9 = MakeSection("⑨ 归还到期提醒（提醒对象是借用设备的同学本人）");
            var lblRemindWho = new Label
            {
                Text = "这一区的目的都是让「借东西的同学本人」知道什么时候该还：\n"
                     + "· 借出当场：语音 + 大字 + 借条，把归还日期交到他手上；\n"
                     + "· 他下次来刷卡：当面提醒他名下还有哪些快到期 / 已逾期；\n"
                     + "· 到期名单：按人分组生成催还消息，可直接复制发到群里或私聊通知本人。\n"
                     + "顶部横幅只是给值班的人看「该去催了」，真正的提醒是发给借用同学的。",
                AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont,
                MaximumSize = new Size(720, 0), Margin = new Padding(0, 0, 0, 8)
            };
            AddRow(s9, "", lblRemindWho, null);
            cboRemindMode = new ComboBox
            {
                Width = 260, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Th.BgInput,
                ForeColor = Th.TxtPri, FlatStyle = FlatStyle.Flat, Font = Th.NormFont
            };
            cboRemindMode.Items.AddRange(new object[]
            {
                "关闭提醒",
                "仅界面提示（顶部到期横幅）",
                "界面提示 + 语音播报（推荐）"
            });
            cboRemindMode.SelectedIndex = 2;
            AddRow(s9, "提醒方式：", cboRemindMode, "语音由系统自带朗读引擎发声，借出当场就会念出归还时间");

            numRemindLead = MakeNum(0, 720, 1);
            AddRow(s9, "提前提醒(小时)：", numRemindLead, "距应还时间不足该值时开始提醒（24=提前一天）");

            chkRemindOverdue = new CheckBox
            {
                Text = "已逾期的设备持续提醒", AutoSize = true,
                ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s9, "", chkRemindOverdue, null);

            numRemindInterval = MakeNum(0, 1440, 10);
            AddRow(s9, "重复提醒间隔(分钟)：", numRemindInterval, "0=只在借出/归还和开机时提醒，不反复打扰");

            chkPrintSlip = new CheckBox
            {
                Text = "借出后自动打印「借条」给借用同学（需接打印机，小票打印机即可）",
                AutoSize = true, ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s9, "", chkPrintSlip,
                "纸条上写着设备、借用人、应归还时间 —— 同学带走后自己就记得什么时候还");

            var btnNowRemind = MakeButton("立即检查一次", Th.Accent2, Point.Empty, new Size(130, 30));
            btnNowRemind.Click += (s, e) =>
            {
                SaveSettingsFromControls(false);
                CheckRemind(true);
                var items = Reminder.Scan(rules.RemindLeadHours, rules.RemindOverdue);
                if (items.Count == 0) { MsgBox("当前没有临期或逾期的设备，不需要提醒。"); return; }
                using (var dlg = new RemindDialog(items)) dlg.ShowDialog(this);
            };
            var btnDueList = MakeButton("查看到期名单…", Th.Accent, Point.Empty, new Size(140, 30));
            btnDueList.Margin = new Padding(10, 0, 0, 0);
            btnDueList.Click += (s, e) =>
            {
                var items = Reminder.Scan(rules == null ? 24 : rules.RemindLeadHours,
                                          rules == null || rules.RemindOverdue);
                if (items.Count == 0) { MsgBox("当前没有临期或逾期的设备。"); return; }
                using (var dlg = new RemindDialog(items)) dlg.ShowDialog(this);
            };
            AddRow(s9, "", MakeRowHost(btnNowRemind, btnDueList), "先保存 → 立刻检查并（有到期的话）打开名单");
            SetSectionCollapsed(s9, false);
            AddSection(s9);

            // ---------------- ⑩ 自助终端（刷卡机 / 一体机） ----------------
            var s10 = MakeSection("⑩ 自助终端模式（刷卡机 / 一体机，无人值守运行）");
            chkKiosk = new CheckBox
            {
                Text = "启用自助模式：全屏无边框、触控大按钮、空闲自动回主屏、不被随手关闭",
                AutoSize = true, ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s10, "", chkKiosk, "开关后点「保存设置」即可生效；退出程序按 Ctrl+Alt+Q");

            numIdleSec = MakeNum(0, 3600, 5);
            AddRow(s10, "空闲多久回主屏(秒)：", numIdleSec, "0=不自动返回；建议 60（上一个人用完，画面自动复原）");

            chkAutoStart = new CheckBox
            {
                Text = "开机自动启动（写入当前用户的启动项，不需要管理员权限）",
                AutoSize = true, ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s10, "", chkAutoStart, "断电来电后，机器开机会自动进入可刷卡状态");

            chkKeepScreen = new CheckBox
            {
                Text = "屏幕常亮：阻止系统休眠和熄屏", AutoSize = true,
                ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 6, 0, 0)
            };
            AddRow(s10, "", chkKeepScreen, "自助机建议开启，避免人来扫码时发现屏幕是黑的");
            SetSectionCollapsed(s10, true);
            AddSection(s10);

            // ---------------- ⑪ 校园卡适配 ----------------
            var s11 = MakeSection("⑪ 校园卡适配（为何现在刷不了，怎么解决）");
            var lblWhy = new Label
            {
                Text = "现在的 UHF 读写器工作在 860~960MHz，读的是贴在设备上的 EPC 标签；"
                     + "而江南大学校园卡工作在 13.56MHz（Mifare / CPU 卡）。\n"
                     + "两者频率、天线、协议都不同 —— 就像收音机收不到 WiFi —— 校园卡放上去物理上激发不了，"
                     + "这不是程序问题。\n解决办法：插一个 USB 免驱的 13.56MHz 读卡器（常见的 PC/SC 读卡器，几十元），"
                     + "系统会自动识别，\n程序读取校园卡的唯一卡号(UID)来识别是哪位同学，原来的设备标签读写不受影响。",
                AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont,
                MaximumSize = new Size(720, 0), Margin = new Padding(0, 0, 0, 8)
            };
            AddRow(s11, "", lblWhy, null);

            chkNfc = new CheckBox
            {
                Text = "启用校园卡读头（PC/SC）", AutoSize = true,
                ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 4, 0, 0)
            };
            AddRow(s11, "", chkNfc, null);

            cboNfcReader = new ComboBox
            {
                Width = 360, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Th.BgInput,
                ForeColor = Th.TxtPri, FlatStyle = FlatStyle.Flat, Font = Th.NormFont
            };
            var btnNfcRefresh = MakeButton("刷新读卡器列表", Th.Accent2, Point.Empty, new Size(140, 30));
            btnNfcRefresh.Click += (s, e) => RefreshNfcReaders();
            AddRow(s11, "读卡器：", MakeRowHost(cboNfcReader, btnNfcRefresh), "留空/不选=自动使用检测到的第一个");

            txtNfcPrefix = new TextBox
            {
                Width = 200, BackColor = Th.BgInput, ForeColor = Th.TxtPri,
                BorderStyle = BorderStyle.FixedSingle, Font = Th.NormFont
            };
            AddRow(s11, "只接受的卡号前缀：", txtNfcPrefix, "留空=接受所有卡；填了前缀（如 A1B2）可避免无关卡片误触发");

            var btnNfcTest = MakeButton("放卡测试读号", Th.Accent, Point.Empty, new Size(140, 30));
            btnNfcTest.Click += (s, e) =>
            {
                try
                {
                    var names = NfcReader.ListReaderNames();
                    if (names.Count == 0) { MsgBox("没有检测到 NFC 读卡器：请插上 USB 免驱的 13.56MHz 读卡器后重试。"); return; }
                    MsgBox("检测到读卡器：\n· " + string.Join("\n· ", names.ToArray())
                           + "\n\n点「确定」后将等待 15 秒，请把校园卡平放在读卡器的感应区上…");
                    string id = null;
                    try
                    {
                        using (var n = new NfcReader())
                        {
                            n.Open(cboNfcReader.SelectedItem as string ?? "");
                            DateTime end = DateTime.Now.AddSeconds(15);
                            while (DateTime.Now < end)
                            {
                                string got = n.TryReadOnce();
                                if (!string.IsNullOrEmpty(got)) { id = got; break; }
                                Application.DoEvents();
                                System.Threading.Thread.Sleep(150);
                            }
                        }
                    }
                    catch (Exception ex2) { MsgBox("读取失败：" + ex2.Message); return; }

                    if (string.IsNullOrEmpty(id)) { MsgBox("这 15 秒内没有读到卡片。请把卡平贴在感应区中央再试一次。"); return; }
                    var exist = Db.FindUserByCard(id);
                    MessageBox.Show(this,
                        "读到校园卡卡号：" + id + "\n\n" +
                        (exist != null ? "该卡已登记给：" + exist.Name + "（可直接刷卡借还）"
                                       : "该卡尚未登记。\n可到「用户管理 → 刷卡建人」把它绑定到某位同学，之后刷这张卡即可借还。"),
                        "读卡测试成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MsgBox("测试失败：" + ex.Message); }
            };
            AddRow(s11, "", btnNfcTest, null);
            RefreshNfcReaders();
            SetSectionCollapsed(s11, false);
            AddSection(s11);

            // ---------------- ⑫ 自动邮件提醒 ----------------
            var s12 = MakeSection("⑫ 自动邮件提醒（把归还日期发进借用同学的邮箱）");
            var lblMailWhy = new Label
            {
                Text = "语音和屏幕大字只能提醒站在机器前的人；同学把设备借走后就看不到了。\n"
                     + "配好邮箱后：借出即发一封「借用确认信」（写着归还日期），到期/逾期自动发「催还信」。\n"
                     + "没填邮箱的同学会自动跳过，不影响借还；没网也只是发不出去。",
                AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont,
                MaximumSize = new Size(720, 0), Margin = new Padding(0, 0, 0, 8)
            };
            AddRow(s12, "", lblMailWhy, null);

            chkMail = new CheckBox
            {
                Text = "启用自动邮件提醒", AutoSize = true,
                ForeColor = Th.TxtPri, Font = Th.NormFont, Margin = new Padding(0, 4, 0, 0)
            };
            AddRow(s12, "", chkMail, null);

            chkMailOnBorrow = new CheckBox { Text = "借出成功即发确认信（含归还日期）", AutoSize = true, ForeColor = Th.TxtPri, Font = Th.NormFont };
            chkMailOnDue = new CheckBox { Text = "到期 / 逾期自动发催还信", AutoSize = true, ForeColor = Th.TxtPri, Font = Th.NormFont };
            AddRow(s12, "发送时机：", MakeRowHost(chkMailOnBorrow, chkMailOnDue), "催还信按「提醒间隔」最多每小时一封，不会骚扰");

            txtMailHost = new TextBox { Width = 220, BackColor = Th.BgInput, ForeColor = Th.TxtPri, BorderStyle = BorderStyle.FixedSingle, Font = Th.NormFont };
            numMailPort = MakeNum(1, 65535, 587);
            numMailPort.Width = 90;
            AddRow(s12, "SMTP 服务器：", MakeRowHost(txtMailHost, new Label { Text = "端口", AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont }, numMailPort),
                "QQ 邮箱 smtp.qq.com（465 或 587）、163 邮箱 smtp.163.com（465/994）");

            chkMailSsl = new CheckBox { Text = "使用 SSL（一般都勾上）", AutoSize = true, ForeColor = Th.TxtPri, Font = Th.NormFont };
            AddRow(s12, "", chkMailSsl, null);

            txtMailUser = new TextBox { Width = 220, BackColor = Th.BgInput, ForeColor = Th.TxtPri, BorderStyle = BorderStyle.FixedSingle, Font = Th.NormFont };
            AddRow(s12, "发件邮箱账号：", txtMailUser, "通常就是完整邮箱地址，如 123456@qq.com");

            txtMailPass = new TextBox
            {
                Width = 220, BackColor = Th.BgInput, ForeColor = Th.TxtPri, BorderStyle = BorderStyle.FixedSingle,
                Font = Th.NormFont, UseSystemPasswordChar = true
            };
            AddRow(s12, "密码 / 授权码：", txtMailPass,
                "不是邮箱登录密码！要在邮箱网页版「设置→账户」里开启 SMTP 后生成授权码；"
                + "这里用 Windows 加密保存，数据库被拷走也看不到");

            txtMailFrom = new TextBox { Width = 220, BackColor = Th.BgInput, ForeColor = Th.TxtPri, BorderStyle = BorderStyle.FixedSingle, Font = Th.NormFont };
            txtMailFromName = new TextBox { Width = 160, BackColor = Th.BgInput, ForeColor = Th.TxtPri, BorderStyle = BorderStyle.FixedSingle, Font = Th.NormFont };
            AddRow(s12, "发件地址：", MakeRowHost(txtMailFrom, new Label { Text = "显示名", AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont }, txtMailFromName),
                "显示名如「物联网实验室」，同学收到信时看到的就是它");

            var btnMailTest = MakeButton("发送测试邮件", Th.Accent, Point.Empty, new Size(150, 30));
            btnMailTest.Click += (s, e) =>
            {
                try
                {
                    var r = CollectMailRulesFromUI();
                    if (!Mailer.Configured(r))
                    {
                        MsgBox("还发不出去，请检查：\n"
                             + "· 是否勾选了「启用自动邮件提醒」\n"
                             + "· SMTP 服务器、端口、发件地址是否填了\n"
                             + "· 密码/授权码是否填了");
                        return;
                    }
                    string to = txtMailFrom.Text.Trim();     // 默认发给自己，先验证能不能发通
                    string err = Mailer.Send(r, to, "【测试】实验室设备管理系统邮件提醒可用",
                        "这封是测试邮件。\n\n收到它就说明自动提醒邮件已经配置成功：\n"
                        + "· 同学借出设备时会收到「借用确认信」（写着归还日期）\n"
                        + "· 设备到期/逾期时会自动收到「催还信」\n\n"
                        + "发送时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    if (err == null) MsgBox("发送成功！请到 " + to + " 收信确认。");
                    else MsgBox("发送失败：" + err + "\n\n常见原因：\n"
                        + "· 用的是邮箱登录密码，而不是 SMTP 授权码\n"
                        + "· 邮箱没开启 SMTP 服务\n"
                        + "· 端口和 SSL 不匹配（465 配 SSL，587 一般也开 SSL）\n"
                        + "· 校园网屏蔽了 SMTP 端口");
                }
                catch (Exception ex) { MsgBox("测试失败：" + ex.Message); }
            };
            AddRow(s12, "", btnMailTest, "建议先点这里测一下，收到信再正式启用");

            SetSectionCollapsed(s12, true);
            AddSection(s12);

            // ---------------- 保存 / 恢复默认 ----------------
            var saveRow = new Panel { BackColor = Th.BgPage, AutoSize = true, Margin = new Padding(0, 4, 0, 24), Padding = new Padding(0) };
            var saveFlow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
            var btnSave = MakeButton("保存设置（立即生效）", Th.Accent, Point.Empty, new Size(180, 36));
            btnSave.Click += (s, e) => SaveSettingsFromControls(true);
            var btnDefault = MakeButton("恢复默认值", Th.BgCard, Point.Empty, new Size(130, 36));
            btnDefault.Margin = new Padding(12, 0, 0, 0);
            btnDefault.ForeColor = Th.TxtPri;
            btnDefault.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "确认把全部参数恢复为默认值？\n（借还流水、设备台账、借用人数据不受影响）",
                        "恢复默认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                rules = new SwipeRules();
                try { rules.Save(); }
                catch (Exception ex) { ShowDbError(this, ex); return; }
                ReloadSettingsPage();
                ApplyRulesToReader();
                MsgBox("已恢复默认设置。");
            };
            saveFlow.Controls.AddRange(new Control[] { btnSave, btnDefault });
            saveRow.Controls.Add(saveFlow);
            AddSection(saveRow);

            lblRuleSummary = new Label
            {
                Text = "", AutoSize = false, Height = 46, Dock = DockStyle.Fill,
                ForeColor = Th.TxtMuted, Font = Th.MonoFont, Margin = new Padding(0, 0, 0, 20)
            };
            AddSection(lblRuleSummary);

            scroll.Controls.Add(settingsStack);
            return scroll;
        }

        private TableLayoutPanel settingsStack;
        private int settingsRow;

        /// <summary>把分区按顺序压入设置页（宽度自动撑满，高度按内容自适应）</summary>
        private void AddSection(Control c)
        {
            if (settingsStack == null) return;
            c.Dock = DockStyle.Fill;
            settingsStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settingsStack.Controls.Add(c, 0, settingsRow++);
        }

        // ===================================================================
        //  构建辅助
        // ===================================================================
        private Panel MakeSection(string title)
        {
            var outer = new Panel
            {
                BackColor = Th.BgPanel,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(16, 12, 16, 14),
                Margin = new Padding(0, 0, 0, 14)
            };
            outer.Paint += PaintCardBorder;
            RoundRegion(outer, 8);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 1
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var lbl = new Label
            {
                Text = title, Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold),
                ForeColor = Th.Accent, AutoSize = true, Margin = new Padding(0, 0, 0, 10),
                Cursor = Cursors.Hand
            };
            lbl.Click += (s, e) => ToggleSection(outer);
            grid.Controls.Add(lbl, 0, 0);
            grid.SetColumnSpan(lbl, 2);

            outer.Controls.Add(grid);
            outer.Tag = grid;
            sectionRows[outer] = 1;
            return outer;
        }

        private readonly Dictionary<Control, int> sectionRows = new Dictionary<Control, int>();
        private readonly Dictionary<Control, bool> sectionCollapsed = new Dictionary<Control, bool>();

        /// <summary>
        /// 折叠/展开某个设置分区。折叠后里面的行不再参与布局，
        /// 设置页控件多，这能显著降低切到该页时的布局耗时。
        /// </summary>
        private void SetSectionCollapsed(Panel section, bool collapsed)
        {
            var grid = section.Tag as TableLayoutPanel;
            if (grid == null) return;
            sectionCollapsed[section] = collapsed;
            for (int r = 1; r < grid.RowCount; r++)
            {
                for (int c = 0; c < grid.ColumnCount; c++)
                {
                    var ctrl = grid.GetControlFromPosition(c, r);
                    if (ctrl != null) ctrl.Visible = !collapsed;
                }
                // 行高不用手动改：TableLayoutPanel 布局时会跳过不可见控件，行高自动归零
            }
            var lbl = grid.GetControlFromPosition(0, 0) as Label;
            if (lbl != null)
            {
                string t = lbl.Text;
                if (t.StartsWith("▶") || t.StartsWith("▼")) t = t.Substring(1).TrimStart();
                lbl.Text = (collapsed ? "▶ " : "▼ ") + t;
            }
        }

        private void ToggleSection(Panel section)
        {
            bool cur = sectionCollapsed.ContainsKey(section) && sectionCollapsed[section];
            SetSectionCollapsed(section, !cur);
            // 收起/展开后 AutoSize 容器会自动重算高度，无需手动干预
        }

        private void AddRow(Panel section, string label, Control ctrl, string hint)
        {
            var grid = section.Tag as TableLayoutPanel;
            if (grid == null) return;
            int row = sectionRows.ContainsKey(section) ? sectionRows[section] : 1;
            sectionRows[section] = row + 1;
            if (grid.RowCount <= row)
            {
                grid.RowCount = row + 1;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            if (!string.IsNullOrEmpty(label))
            {
                grid.Controls.Add(new Label
                {
                    Text = label, AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont,
                    Margin = new Padding(0, 7, 10, 6)
                }, 0, row);
            }

            var host = ctrl as FlowLayoutPanel;
            if (host == null)
            {
                // 提示类长文本自动限宽折行，避免撑出分区
                var lblCtrl = ctrl as Label;
                if (lblCtrl != null && lblCtrl.AutoSize && lblCtrl.MaximumSize.Width == 0)
                    lblCtrl.MaximumSize = new Size(760, 0);

                // 注意：不换行（WrapContents=false），否则窗体未定型时的高度会被误算，
                // 造成分区底部出现大块空白；提示文字改由 Label 自身按 MaximumSize 折行。
                host = new FlowLayoutPanel
                {
                    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                    BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 4), Anchor = AnchorStyles.Left
                };
                host.Controls.Add(ctrl);
                if (!string.IsNullOrEmpty(hint))
                    host.Controls.Add(new Label
                    {
                        Text = hint, AutoSize = true, ForeColor = Th.TxtMuted, Font = Th.NormFont,
                        Margin = new Padding(10, 7, 0, 0), MaximumSize = new Size(330, 0)
                    });
            }
            grid.Controls.Add(host, 1, row);
        }

        // ===================================================================
        //  音效设置行：路径输入框 + 浏览 + 试听
        // ===================================================================
        private TextBox MakePathBox()
        {
            var box = new TextBox
            {
                Width = 330, BackColor = Th.BgInput, ForeColor = Th.TxtPri,
                BorderStyle = BorderStyle.FixedSingle, Font = Th.NormFont
            };
            // 支持把音频文件直接拖进来导入
            EnableAudioDrop(box, p => ImportAudioFile(box, p));
            return box;
        }

        // ---------------- 音频文件拖入导入 ----------------
        private static readonly string[] AudioExts =
            { ".mp3", ".wav", ".wma", ".mid", ".midi", ".m4a", ".aac", ".flac", ".ogg" };

        private static bool IsAudioFile(string p)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(p);
                return !string.IsNullOrEmpty(ext)
                       && Array.IndexOf(AudioExts, ext.ToLowerInvariant()) >= 0;
            }
            catch { return false; }
        }

        /// <summary>让一个控件接受音频文件拖放</summary>
        private void EnableAudioDrop(Control target, Action<string> onFile)
        {
            target.AllowDrop = true;
            target.DragEnter += (s, e) =>
            {
                try
                {
                    if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0 && IsAudioFile(files[0]))
                        e.Effect = DragDropEffects.Copy;
                }
                catch { }
            };
            target.DragDrop += (s, e) =>
            {
                try
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0 && IsAudioFile(files[0])) onFile(files[0]);
                    else MsgBox("只能导入音频文件：mp3 / wav / wma / m4a / aac / flac / ogg / mid。");
                }
                catch (Exception ex) { MsgBox("导入失败：" + ex.Message); }
            };
        }

        /// <summary>
        /// 导入音频：问一句要不要复制一份到程序目录的 sounds 文件夹。
        /// 复制的好处是——以后原文件被移动、改名、删掉，或者程序拿到别的电脑上，音效都还在。
        /// </summary>
        private void ImportAudioFile(TextBox box, string src)
        {
            if (!System.IO.File.Exists(src)) { MsgBox("文件不存在：" + src); return; }

            var dr = MessageBox.Show(this,
                "识别到音频文件：\n" + System.IO.Path.GetFileName(src) + "\n\n" +
                "要不要复制一份到程序目录的 sounds 文件夹并使用它？\n" +
                "（推荐：原文件移动/删除后音效依然有效，拷到别的电脑也不会丢）\n\n" +
                "选「否」= 直接引用原文件路径；选「取消」= 放弃导入。",
                "导入音效", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (dr == DialogResult.Cancel) return;

            if (dr == DialogResult.No) { box.Text = src; return; }

            try
            {
                string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sounds");
                System.IO.Directory.CreateDirectory(dir);
                string dst = UniquePath(System.IO.Path.Combine(dir, System.IO.Path.GetFileName(src)));
                System.IO.File.Copy(src, dst, true);
                box.Text = dst;
                MsgBox("已导入到：\n" + dst + "\n\n别忘了点「保存设置（立即生效）」。");
            }
            catch (Exception ex)
            {
                // 程序目录不可写（例如被系统降级运行）时，退回直接引用原路径
                MsgBox("复制到程序目录失败：" + ex.Message + "\n\n将直接使用原文件路径。");
                box.Text = src;
            }
        }

        /// <summary>同名文件自动改名：xxx.mp3 → xxx (2).mp3</summary>
        private static string UniquePath(string path)
        {
            if (!System.IO.File.Exists(path)) return path;
            string dir = System.IO.Path.GetDirectoryName(path);
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            string ext = System.IO.Path.GetExtension(path);
            for (int i = 2; i < 99; i++)
            {
                string p = System.IO.Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!System.IO.File.Exists(p)) return p;
            }
            return path;
        }

        /// <summary>一行音效配置：留空即使用内置合成音</summary>
        private void AddSfxRow(Panel section, string label, TextBox box, Sfx.Kind kind)
        {
            var btnBrowse = MakeButton("浏览…", Th.BgCard, Point.Empty, new Size(70, 28));
            btnBrowse.Margin = new Padding(8, 0, 0, 0);
            btnBrowse.ForeColor = Th.TxtPri;
            btnBrowse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog
                {
                    Title = "选择音效文件",
                    Filter = "音频文件|*.mp3;*.wav;*.wma;*.mid;*.midi;*.m4a;*.aac;*.flac;*.ogg|所有文件|*.*"
                })
                {
                    if (!string.IsNullOrEmpty(box.Text) && System.IO.File.Exists(box.Text))
                        dlg.FileName = box.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
                }
            };

            var btnTest = MakeButton("试听", Th.Accent2, Point.Empty, new Size(60, 28));
            btnTest.Margin = new Padding(8, 0, 0, 0);
            btnTest.Click += (s, e) => PreviewSfx(kind, box.Text);

            var host = MakeRowHost(box, btnBrowse, btnTest);
            EnableAudioDrop(host, p => ImportAudioFile(box, p));   // 整行都可接拖放
            AddRow(section, label, host, "可直接把音频文件拖进来；留空=内置合成音");
        }

        /// <summary>试听：直接用界面上的当前值播放（不必先保存）</summary>
        private void PreviewSfx(Sfx.Kind kind, string file)
        {
            try
            {
                int vol = numVolume != null ? (int)numVolume.Value : 90;
                int maxSec = numSoundMax != null ? (int)numSoundMax.Value : 6;
                Sfx.VerboseLog = true;      // 试听属于主动排查，务必留下日志
                Sfx.Play(kind, file, vol, maxSec);
                Sfx.VerboseLog = false;
            }
            catch (Exception ex) { MsgBox("试听失败：" + ex.Message); }
        }

        private FlowLayoutPanel MakeRowHost(params Control[] ctrls)
        {
            var host = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 4), Anchor = AnchorStyles.Left
            };
            host.Controls.AddRange(ctrls);
            return host;
        }

        private NumericUpDown MakeNum(int min, int max, int step)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max, Increment = step, Width = 100,
                BackColor = Th.BgInput, ForeColor = Th.TxtPri, BorderStyle = BorderStyle.FixedSingle,
                Font = Th.NormFont, TextAlign = HorizontalAlignment.Right
            };
        }

        private static decimal Clamp(NumericUpDown n, int v)
        {
            if (v < n.Minimum) return n.Minimum;
            if (v > n.Maximum) return n.Maximum;
            return v;
        }

        private void OpenPath(string path)
        {
            try
            {
                if (System.IO.Directory.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
                else
                    System.Diagnostics.Process.Start("explorer.exe", "/select," + path);
            }
            catch { MsgBox("位置：" + path); }
        }

        // ===================================================================
        //  载入 / 保存
        // ===================================================================
        /// <summary>
        /// 用当前规则回填设置页。force=true 时连备份列表和审计列表一起刷新；
        /// 否则只在它们被标记为"脏"时才刷新，避免每次切页都做磁盘/数据库 IO。
        /// </summary>
        // ===================================================================
        //  （已回退）此前的"布局冻结/解冻"机制会在高度重算与滚动条变化之间
        //  来回振荡，导致设置页无限闪烁，已整体移除。
        //  切换卡顿的优化改为依靠：① 分区可折叠（收起的控件不参与布局）；
        //  ② 页面数据按需加载（见 MainForm 的 pageLoaded/pageDirty）。
        // ===================================================================

        private void ReloadSettingsPage(bool force = false)
        {
            if (numPower == null) return;
            loadingSettings = true;
            try
            {
                if (rules == null) rules = SwipeRules.Load();
                numPower.Value = Clamp(numPower, rules.RfPowerDbm);
                numHits.Value = Clamp(numHits, rules.HitConfirmTimes);
                numTotalHits.Value = Clamp(numTotalHits, rules.MinTotalHits);
                numReadWindow.Value = Clamp(numReadWindow, rules.MaxReadWindowMs);
                numPoll.Value = Clamp(numPoll, rules.PollIntervalMs);
                numTagCd.Value = Clamp(numTagCd, rules.TagCooldownMs);

                numGlobalGap.Value = Clamp(numGlobalGap, rules.GlobalGapMs);
                numB2R.Value = Clamp(numB2R, rules.BorrowToReturnSec);
                numR2B.Value = Clamp(numR2B, rules.ReturnToBorrowSec);
                numAbuseWin.Value = Clamp(numAbuseWin, rules.AbuseWindowSec);
                numAbuseMax.Value = Clamp(numAbuseMax, rules.AbuseMaxHits);
                numAbuseGlobal.Value = Clamp(numAbuseGlobal, rules.AbuseGlobalMax);
                numAbuseLock.Value = Clamp(numAbuseLock, rules.AbuseLockSec);

                numBorrowDays.Value = Clamp(numBorrowDays, rules.BorrowDays);
                numWarnHours.Value = Clamp(numWarnHours, rules.DueWarnHours);

                chkUserCardMode.Checked = rules.UserCardMode;
                numUserWait.Value = Clamp(numUserWait, rules.UserCardWaitSec);

                chkAutoBackup.Checked = rules.AutoBackupOnStart;
                numBackupKeep.Value = Clamp(numBackupKeep, rules.AutoBackupKeep);

                chkSound.Checked = rules.SoundEnabled;
                numVolume.Value = Clamp(numVolume, rules.SoundVolume);
                numSoundMax.Value = Clamp(numSoundMax, rules.SoundMaxSec);
                txtSfxBorrow.Text = rules.SfxBorrowFile;
                txtSfxReturn.Text = rules.SfxReturnFile;
                txtSfxBlocked.Text = rules.SfxBlockedFile;
                txtSfxAbuse.Text = rules.SfxAbuseFile;

                // ---- ⑧ 界面字号 / 触屏 ----
                cboFontLevel.SelectedIndex = Math.Max(0, Math.Min(3, rules.UiFontLevel));
                chkTouchFriendly.Checked = rules.TouchFriendly;
                // ---- ⑨ 到期提醒 ----
                cboRemindMode.SelectedIndex = Math.Max(0, Math.Min(2, rules.RemindMode));
                numRemindLead.Value = Clamp(numRemindLead, rules.RemindLeadHours);
                numRemindInterval.Value = Clamp(numRemindInterval, rules.RemindIntervalMin);
                chkRemindOverdue.Checked = rules.RemindOverdue;
                chkPrintSlip.Checked = rules.PrintSlipOnBorrow;
                // ---- ⑩ 自助终端 ----
                chkKiosk.Checked = rules.KioskEnabled;
                numIdleSec.Value = Clamp(numIdleSec, rules.KioskIdleSec);
                chkAutoStart.Checked = Kiosk.IsAutoStart();      // 实际是否随系统启动，从注册表读
                chkKeepScreen.Checked = rules.KeepScreenOn;
                // ---- ⑪ 校园卡 ----
                chkNfc.Checked = rules.NfcEnabled;
                txtNfcPrefix.Text = rules.NfcIdPrefix ?? "";
                if (!string.IsNullOrEmpty(rules.NfcReaderName) && cboNfcReader.Items.Contains(rules.NfcReaderName))
                    cboNfcReader.SelectedItem = rules.NfcReaderName;
                // ---- ⑫ 邮件提醒 ----
                chkMail.Checked = rules.MailEnabled;
                chkMailOnBorrow.Checked = rules.MailOnBorrow;
                chkMailOnDue.Checked = rules.MailOnDue;
                txtMailHost.Text = rules.MailHost ?? "";
                numMailPort.Value = Clamp(numMailPort, rules.MailPort);
                chkMailSsl.Checked = rules.MailSsl;
                txtMailUser.Text = rules.MailUser ?? "";
                txtMailPass.Text = rules.MailPass ?? "";      // Load 时已解密为明文
                txtMailFrom.Text = rules.MailFrom ?? "";
                txtMailFromName.Text = rules.MailFromName ?? "";

                cboDistancePreset.SelectedIndex = PresetIndexOf(rules.RfPowerDbm);
            }
            finally { loadingSettings = false; }

            MarkSettingsSaved();          // 载入/恢复默认后：基线重置，提示回到「已保存」
            StartUnsavedWatcher();
            RefreshReaderPowerLabel();
            if (force || backupsDirty) { ReloadBackups(); backupsDirty = false; }
            if (force || auditDirty) { ReloadAudit(); auditDirty = false; }
            UpdateRuleSummary();
        }

        private static int PresetIndexOf(int dbm)
        {
            for (int i = 1; i < DistancePresets.Length; i++)
                if (DistancePresets[i] == dbm && i != 0) return i;
            return 0;
        }

        private void SaveSettingsFromControls(bool notify)
        {
            if (rules == null) rules = new SwipeRules();
            rules.RfPowerDbm = (int)numPower.Value;
            rules.HitConfirmTimes = (int)numHits.Value;
            rules.MinTotalHits = (int)numTotalHits.Value;
            rules.MaxReadWindowMs = (int)numReadWindow.Value;
            rules.PollIntervalMs = (int)numPoll.Value;
            rules.TagCooldownMs = (int)numTagCd.Value;

            rules.GlobalGapMs = (int)numGlobalGap.Value;
            rules.BorrowToReturnSec = (int)numB2R.Value;
            rules.ReturnToBorrowSec = (int)numR2B.Value;
            rules.AbuseWindowSec = (int)numAbuseWin.Value;
            rules.AbuseMaxHits = (int)numAbuseMax.Value;
            rules.AbuseGlobalMax = (int)numAbuseGlobal.Value;
            rules.AbuseLockSec = (int)numAbuseLock.Value;

            rules.BorrowDays = (int)numBorrowDays.Value;
            rules.DueWarnHours = (int)numWarnHours.Value;

            rules.UserCardMode = chkUserCardMode.Checked;
            rules.UserCardWaitSec = (int)numUserWait.Value;

            rules.AutoBackupOnStart = chkAutoBackup.Checked;
            rules.AutoBackupKeep = (int)numBackupKeep.Value;

            rules.SoundEnabled = chkSound.Checked;
            rules.SoundVolume = (int)numVolume.Value;
            rules.SoundMaxSec = (int)numSoundMax.Value;
            rules.SfxBorrowFile = txtSfxBorrow.Text.Trim();
            rules.SfxReturnFile = txtSfxReturn.Text.Trim();
            rules.SfxBlockedFile = txtSfxBlocked.Text.Trim();
            rules.SfxAbuseFile = txtSfxAbuse.Text.Trim();

            // ---- ⑧ 界面字号 / 触屏 ----
            int levelBefore = rules.UiFontLevel;
            rules.UiFontLevel = Math.Max(0, Math.Min(3, cboFontLevel.SelectedIndex));
            rules.TouchFriendly = chkTouchFriendly.Checked;
            // ---- ⑨ 到期提醒 ----
            rules.RemindMode = Math.Max(0, Math.Min(2, cboRemindMode.SelectedIndex));
            rules.RemindLeadHours = (int)numRemindLead.Value;
            rules.RemindIntervalMin = (int)numRemindInterval.Value;
            rules.RemindOverdue = chkRemindOverdue.Checked;
            rules.PrintSlipOnBorrow = chkPrintSlip.Checked;
            // ---- ⑩ 自助终端 ----
            rules.KioskEnabled = chkKiosk.Checked;
            rules.KioskIdleSec = (int)numIdleSec.Value;
            rules.KeepScreenOn = chkKeepScreen.Checked;
            bool wantAutoStart = chkAutoStart.Checked;
            rules.KioskAutoStart = wantAutoStart;   // 真正的开关在注册表，这里记一份便于备份还原
            // ---- ⑪ 校园卡 ----
            rules.NfcEnabled = chkNfc.Checked;
            rules.NfcReaderName = (cboNfcReader.SelectedItem as string ?? "");
            if (rules.NfcReaderName.StartsWith("（")) rules.NfcReaderName = "";   // 那是占位提示，不是真读卡器
            rules.NfcIdPrefix = txtNfcPrefix.Text.Trim();
            // ---- ⑫ 邮件提醒 ----
            rules.MailEnabled = chkMail.Checked;
            rules.MailOnBorrow = chkMailOnBorrow.Checked;
            rules.MailOnDue = chkMailOnDue.Checked;
            rules.MailHost = txtMailHost.Text.Trim();
            rules.MailPort = (int)numMailPort.Value;
            rules.MailSsl = chkMailSsl.Checked;
            rules.MailUser = txtMailUser.Text.Trim();
            rules.MailPass = txtMailPass.Text;               // Save() 里会加密后入库
            rules.MailFrom = txtMailFrom.Text.Trim();
            rules.MailFromName = txtMailFromName.Text.Trim();
            if (string.IsNullOrEmpty(rules.MailUser)) rules.MailUser = rules.MailFrom;

            rules.Normalize();
            try { rules.Save(); }
            catch (Exception ex) { ShowDbError(this, ex); return; }

            // ---- 保存之外的副作用：能马上生效的就马上生效 ----
            // 开机自启会改动注册表（系统级），只在用户主动点保存时写入，
            // 免得别的地方（比如自动化测试）误改了电脑的启动项。
            if (notify) { try { Kiosk.SetAutoStart(wantAutoStart); } catch { } }
            try { Kiosk.KeepScreenOn(rules.KeepScreenOn); } catch { }
            StartRemindTimer();            // 提醒间隔可能改了
            CheckRemind(false);            // 更新顶部到期横幅
            StartNfcReader();              // 校园卡读头开关状态可能变了
            StartIdleWatch();              // 空闲回主屏时间可能改了

            MarkSettingsSaved();          // 刷新快照 → 提示回到「已保存」
            ApplyRulesToReader();
            UpdateRuleSummary();
            ReloadLent();

            // 字号是建界面的时候就定下来的，改了必须重启才能整版面生效 —— 自动帮用户重启，不用手动操作
            if (levelBefore != rules.UiFontLevel)
            {
                if (notify)
                {
                    var dr = MessageBox.Show(this,
                        "界面字号已改为「" + new[] { "紧凑", "标准", "大", "超大" }[rules.UiFontLevel] + "」。\n\n" +
                        "字号作用在界面创建时，需要重启程序才能整版面生效。\n现在自动重启吗？",
                        "字号已保存", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr == DialogResult.Yes) { RestartApp(); return; }
                }
                else { RestartApp(); return; }
            }

            if (notify) MsgBox("设置已保存并立即生效。\n（参数已写入数据库 settings 表，下次启动继续有效）");
        }

        // ===================================================================
        //  「改了还没保存」检测：把各控件取值拼成快照，定时比对
        // ===================================================================
        private string SnapshotSettings()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var fname in new[] { "numPower", "numHits", "numTotalHits", "numReadWindow", "numPoll", "numTagCd",
                                          "numGlobalGap", "numB2R", "numR2B", "numAbuseWin", "numAbuseMax", "numAbuseGlobal", "numAbuseLock",
                                          "numBorrowDays", "numWarnHours", "numUserWait", "numBackupKeep",
                                          "numVolume", "numSoundMax",
                                          "numRemindLead", "numRemindInterval", "numIdleSec", "numMailPort" })
            {
                var f = typeof(MainForm).GetField(fname,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var n = f != null ? f.GetValue(this) as NumericUpDown : null;
                sb.Append(fname).Append('=').Append(n != null ? n.Value.ToString() : "?").Append(';');
            }
            foreach (var fname in new[] { "chkUserCardMode", "chkAutoBackup", "chkSound",
                                          "chkTouchFriendly", "chkRemindOverdue", "chkPrintSlip",
                                          "chkKiosk", "chkAutoStart", "chkKeepScreen", "chkNfc",
                                          "chkMail", "chkMailOnBorrow", "chkMailOnDue", "chkMailSsl" })
            {
                var f = typeof(MainForm).GetField(fname,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var c = f != null ? f.GetValue(this) as CheckBox : null;
                sb.Append(fname).Append('=').Append(c != null ? c.Checked.ToString() : "?").Append(';');
            }
            foreach (var fname in new[] { "txtSfxBorrow", "txtSfxReturn", "txtSfxBlocked", "txtSfxAbuse",
                                          "txtMailHost", "txtMailUser", "txtMailPass", "txtMailFrom", "txtMailFromName" })
            {
                var f = typeof(MainForm).GetField(fname,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var x = f != null ? f.GetValue(this) as TextBox : null;
                sb.Append(fname).Append('=').Append(x != null ? x.Text : "?").Append(';');
            }
            return sb.ToString();
        }

        /// <summary>界面上是否存在尚未保存的修改（MainForm 关闭时会用它来提醒）</summary>
        internal bool SettingsHasUnsaved()
        {
            try { return settingsSnapshot != "" && SnapshotSettings() != settingsSnapshot; }
            catch { return false; }
        }

        private void MarkSettingsSaved()
        {
            settingsSnapshot = SnapshotSettings();
            UpdateUnsavedLabel();
        }

        private void UpdateUnsavedLabel()
        {
            if (lblUnsaved == null) return;
            bool dirty = SettingsHasUnsaved();
            lblUnsaved.Text = dirty ? "● 有未保存的修改，请点「保存设置（立即生效）」" : "所有修改均已保存";
            lblUnsaved.ForeColor = dirty ? Th.Warn : Th.TxtMuted;
        }

        /// <summary>启动未保存提醒的定时刷新（只在设置页存在时跑，开销可忽略）</summary>
        private void StartUnsavedWatcher()
        {
            if (unsavedTimer != null) return;
            unsavedTimer = new System.Windows.Forms.Timer { Interval = 600 };
            unsavedTimer.Tick += (s, e) =>
            {
                try { if (activePage == 5) UpdateUnsavedLabel(); } catch { }
            };
            unsavedTimer.Start();
        }

        /// <summary>重新启动本程序（切换界面字号后用它让整版面立即按新字号重建）</summary>
        private void RestartApp()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                System.Diagnostics.Process.Start(exe);
                Application.Exit();
            }
            catch (Exception ex)
            {
                MsgBox("自动重启失败，请手动关闭并重新打开本程序。\n" + ex.Message);
            }
        }

        /// <summary>把界面上填的邮件参数临时组装成规则对象（给「发送测试邮件」用，不必先保存）</summary>
        private SwipeRules CollectMailRulesFromUI()
        {
            var r = new SwipeRules();
            r.MailEnabled = chkMail.Checked;
            r.MailOnBorrow = chkMailOnBorrow.Checked;
            r.MailOnDue = chkMailOnDue.Checked;
            r.MailHost = txtMailHost.Text.Trim();
            r.MailPort = (int)numMailPort.Value;
            r.MailSsl = chkMailSsl.Checked;
            r.MailUser = txtMailUser.Text.Trim();
            r.MailPass = txtMailPass.Text;                 // 界面上是明文，发送时才用
            r.MailFrom = txtMailFrom.Text.Trim();
            r.MailFromName = txtMailFromName.Text.Trim();
            if (string.IsNullOrEmpty(r.MailUser)) r.MailUser = r.MailFrom;   // 多数邮箱账号就是地址
            return r;
        }

        /// <summary>刷新 PC/SC 读卡器下拉列表（用于校园卡识别）</summary>
        private void RefreshNfcReaders()
        {
            if (cboNfcReader == null) return;
            try
            {
                string cur = cboNfcReader.SelectedItem as string;
                cboNfcReader.Items.Clear();
                foreach (var n in NfcReader.ListReaderNames()) cboNfcReader.Items.Add(n);
                if (cboNfcReader.Items.Count > 0)
                {
                    int idx = cur != null ? cboNfcReader.Items.IndexOf(cur) : -1;
                    cboNfcReader.SelectedIndex = idx >= 0 ? idx : 0;
                }
                else
                {
                    cboNfcReader.Items.Add("（未检测到读卡器：请插上 USB 免驱的 13.56MHz 读卡器）");
                    cboNfcReader.SelectedIndex = 0;
                }
            }
            catch { }
        }

        /// <summary>把规则同步到读卡器（热更新，不影响正在进行的轮询）</summary>
        private void ApplyRulesToReader()
        {
            if (reader == null) return;
            reader.UpdateOptions(new ReaderOptions
            {
                PollIntervalMs = rules.PollIntervalMs,
                HitConfirmTimes = rules.HitConfirmTimes,
                TagCooldownMs = rules.TagCooldownMs,
                RfPowerDbm = rules.RfPowerDbm,
                MinTotalHits = rules.MinTotalHits,
                MaxReadWindowMs = rules.MaxReadWindowMs
            });
            ApplyDistanceToReader(false);
        }

        /// <summary>把功率写入读卡器硬件</summary>
        private void ApplyDistanceToReader(bool notify)
        {
            // 先把界面当前功率值同步进规则对象：
            // 距离档位下拉只改界面控件，如果用户选完档位直接点「应用到读卡器」，
            // 不同步就会套用上一次保存的旧功率（表现为弹窗功率与所选档位不一致）。
            if (rules != null && numPower != null) rules.RfPowerDbm = (int)numPower.Value;

            if (reader == null || !reader.Connected)
            {
                if (notify) MsgBox("读卡器未连接，功率设置已保存，连接读卡器后会自动应用。");
                return;
            }
            bool ok = false;
            if (rules.RfPowerDbm <= 0)
            {
                reader.RefreshPower();
                if (notify) MsgBox("当前设置为「不修改读卡器功率」，读卡器保持自身功率：" + reader.PowerText());
                ok = true;
            }
            else
            {
                ok = reader.SetPower(rules.RfPowerDbm);
                if (notify)
                    MsgBox(ok
                        ? (reader.CurrentPowerDbm >= 0
                            ? "射频功率已设为 " + rules.RfPowerDbm + " dBm，读卡距离随之缩短，读回值：" + reader.PowerText()
                            : "射频功率已设为 " + rules.RfPowerDbm + " dBm，设置已生效。\n（该型号读卡器不支持功率回读，无法显示读回值）")
                        : "设置功率失败（读卡器未响应或型号不支持），程序仍可正常刷卡。");
            }
            RefreshReaderPowerLabel();
            if (ok) AddLogRow("读卡距离设置已应用：功率 " + reader.PowerText());
        }

        private void ShowReaderInfo()
        {
            if (reader == null || !reader.Connected) { MsgBox("读卡器未连接。"); return; }
            string info = reader.ReadInfo();
            RefreshReaderPowerLabel();
            MsgBox(string.IsNullOrEmpty(info)
                ? "读取读卡器信息失败（读卡器未响应）。\n当前功率：" + reader.PowerText()
                : "读卡器信息：\n" + info);
        }

        private void RefreshReaderPowerLabel()
        {
            if (lblReaderPower == null) return;
            if (reader == null || !reader.Connected)
            {
                lblReaderPower.Text = "读卡器状态：未连接（连接后功率设置会自动应用）";
                lblReaderPower.ForeColor = Th.TxtMuted;
                return;
            }
            string lockInfo = "";
            if (guard != null)
            {
                var locked = guard.LockedEpcs(DateTime.Now);
                if (locked.Count > 0) lockInfo = "　|　当前锁定标签 " + locked.Count + " 个";
                else if (guard.GlobalLocked(DateTime.Now)) lockInfo = "　|　全局锁定中";
            }
            lblReaderPower.Text = string.Format("读卡器状态：已连接 COM{0}　|　当前功率 {1}　|　目标功率 {2}{3}",
                reader.PortIndex, reader.PowerText(),
                rules == null || rules.RfPowerDbm <= 0 ? "不修改" : rules.RfPowerDbm + " dBm", lockInfo);
            lblReaderPower.ForeColor = Th.Accent;
        }

        private void UpdateRuleSummary()
        {
            if (lblRuleSummary == null || rules == null) return;
            lblRuleSummary.Text = string.Format(
                "当前生效规则：功率 {0}　|　连续命中 {1} 轮 / 窗口 {2} 次/{3} ms　|　盘点间隔 {4} ms　|　全局间隔 {5} ms　|　借出→归还 ≥ {6} 秒　|　" +
                "归还→借出 ≥ {7} 秒　|　防刷窗口 {8} 秒（同卡 > {9} 次 / 全局 > {10} 次即锁定 {11} 秒）　|　" +
                "借用期限 {12} 天（临期提前 {13} 小时）　|　借用人卡模式 {14}　|　提示音效 {15}",
                rules.RfPowerDbm <= 0 ? "不修改" : rules.RfPowerDbm + " dBm",
                rules.HitConfirmTimes, rules.MinTotalHits, rules.MaxReadWindowMs,
                rules.PollIntervalMs, rules.GlobalGapMs,
                rules.BorrowToReturnSec, rules.ReturnToBorrowSec,
                rules.AbuseWindowSec, rules.AbuseMaxHits, rules.AbuseGlobalMax, rules.AbuseLockSec,
                rules.BorrowDays == 0 ? "不限" : rules.BorrowDays.ToString(),
                rules.DueWarnHours,
                rules.UserCardMode ? "已启用" : "未启用",
                rules.SoundEnabled ? "已启用（音量 " + rules.SoundVolume + "%）" : "已关闭");
        }

        // ===================================================================
        //  备份 / 恢复
        // ===================================================================
        private void ReloadBackups()
        {
            if (lstBackups == null) return;
            lstBackups.Items.Clear();
            var list = Db.ListBackups();
            for (int i = 0; i < list.Count; i++) lstBackups.Items.Add(list[i]);
            if (lstBackups.Items.Count > 0) lstBackups.SelectedIndex = 0;
            if (lblBackupTip != null)
                lblBackupTip.Text = "备份目录：" + Db.BackupDir + "　（共 " + list.Count + " 份）";
        }

        private void DoBackup(bool notify)
        {
            try
            {
                string f = Db.BackupNow("manual");
                backupsDirty = true;      // 下次进设置页时真正刷新列表
                ReloadBackups();
                if (notify) MsgBox("备份完成：\n" + f);
            }
            catch (Exception ex)
            {
                if (notify) MsgBox("备份失败：" + ex.Message);
            }
        }

        private void DoRestore()
        {
            var sel = lstBackups == null ? null : lstBackups.SelectedItem as Db.BackupFile;
            if (sel == null) { MsgBox("请先在列表中选择一个备份文件。"); return; }
            if (MessageBox.Show(this,
                    "确认用以下备份覆盖当前数据库？\n\n" + sel + "\n\n" +
                    "· 当前数据库会先自动备份为 before_restore，可再恢复回来；\n" +
                    "· 恢复后界面数据会立即刷新。",
                    "恢复确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                string safety = Db.RestoreFrom(sel.Path);
                Db.Reinitialize();                     // 备份可能是老结构：自动补表补列
                rules = SwipeRules.Load();             // 参数也回到备份时的状态
                guard.Reset();
                activeUser = null;
                ApplyRulesToReader();
                ReloadSettingsPage();
                ReloadDevices();
                ReloadRecords();
                backupsDirty = true;
                auditDirty = true;
                ReloadUsers();
                RefreshAll();
                MsgBox("已恢复备份：" + sel.Name + (string.IsNullOrEmpty(safety) ? "" : "\n恢复前的数据已备份到：\n" + safety));
            }
            catch (Exception ex)
            {
                MsgBox("恢复失败：" + ex.Message + "\n（数据库可能正被其它程序占用，请关闭后重试）");
            }
        }

        // ===================================================================
        //  审计
        // ===================================================================
        private void ReloadAudit()
        {
            if (lvAudit == null) return;
            lvAudit.Items.Clear();
            var list = Db.ListAudit(200);
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                var lvi = new ListViewItem(new[] { a.EventTime, a.KindLabel, a.Epc, a.Reason });
                lvi.ForeColor = (a.Kind == "abuse") ? Th.Danger : Th.TxtSec;
                lvAudit.Items.Add(lvi);
            }
        }
    }
}
