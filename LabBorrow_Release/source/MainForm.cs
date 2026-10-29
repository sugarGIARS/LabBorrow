// =====================================================================
//  MainForm.cs  --  实验室设备借还管理  (WinForms, 现代深色 UI)
//  核心流程：刷设备标签 -> 在库则登记借出(选借用人) -> 外借中则自动归还
//  容错：陌生标签友好提示 / 短时重复刷卡防抖 / USB 断连不闪退
//  本版新增：刷卡距离限制、刷卡间隔时限、在借明细与逾期提醒、
//            借用人卡(学生卡)双卡模式；设置页/用户页/报表页见
//            SettingsPage.cs / UsersPage.cs / ReportsPage.cs
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace LabBorrow
{
    public partial class MainForm : Form
    {
        // ============== Theme ==============
        public static class Th
        {
            public static readonly Color BgPage    = Color.FromArgb(15, 17, 21);
            public static readonly Color BgPanel   = Color.FromArgb(22, 26, 34);
            public static readonly Color BgCard    = Color.FromArgb(30, 34, 46);
            public static readonly Color BgHover   = Color.FromArgb(45, 54, 70);
            public static readonly Color BgInput   = Color.FromArgb(37, 41, 54);
            public static readonly Color Border    = Color.FromArgb(58, 64, 82);
            public static readonly Color Accent    = Color.FromArgb(0, 184, 148);   // teal
            public static readonly Color Accent2   = Color.FromArgb(108, 92, 231);  // purple
            public static readonly Color Success   = Color.FromArgb(0, 200, 83);
            public static readonly Color Warn      = Color.FromArgb(255, 179, 0);
            public static readonly Color Danger    = Color.FromArgb(255, 82, 82);
            public static readonly Color TxtPri    = Color.FromArgb(232, 234, 237);
            public static readonly Color TxtSec    = Color.FromArgb(154, 160, 166);
            public static readonly Color TxtMuted  = Color.FromArgb(95, 101, 115);
            // ---- 字体：全部按「字号档位」生成，不写死像素/磅值 ----
            // 不同年龄、不同视力的人对字的大小要求差很多：
            // 老师演示要看得清（大），自助机要远看清楚（超大），管理员管理时又要显示更多（紧凑）。
            // 所有控件都只引用这里的字体实例，切档位时整体重建一次即可全界面生效。
            private static float fscale = 1.0f;
            public static float FontScale { get { return fscale; } }

            /// <summary>0=紧凑 1=标准 2=大 3=超大（对应 SwipeRules.UiFontLevel）</summary>
            public static void SetFontLevel(int level)
            {
                float[] map = { 0.9f, 1.0f, 1.18f, 1.40f };
                float s = map[Math.Max(0, Math.Min(map.Length - 1, level))];
                if (Math.Abs(s - fscale) < 0.001f) return;
                fscale = s;
                BuildFonts();
            }

            private static Font FF(string name, float pt, FontStyle st)
            {
                return new Font(name, Math.Max(6f, (float)Math.Round(pt * fscale, 1)), st);
            }

            public static Font TitleFont, BigFont, MidFont, NormFont, BoldFont, MonoFont;
            public static Font HeroFont, ActFont, HintFont, InfoFont, InfoBold, StatFont, CountFont;
            public static Font RepValFont, RepTitleFont, TouchFont;

            static Th()
            {
                BuildFonts();     // 静态构造：任何地方第一次用到字体就已经准备好了
            }

            private static void BuildFonts()
            {
                TitleFont = FF("Microsoft YaHei UI", 18, FontStyle.Bold);
                BigFont   = FF("Microsoft YaHei UI", 28, FontStyle.Bold);
                MidFont   = FF("Microsoft YaHei UI", 14, FontStyle.Bold);
                NormFont  = FF("Microsoft YaHei UI", 10, FontStyle.Regular);
                BoldFont  = FF("Microsoft YaHei UI", 10, FontStyle.Bold);
                MonoFont  = FF("Consolas", 10, FontStyle.Regular);
                // ---- 主控台「关键信息」专用大字号（老师要求看得清）----
                HeroFont  = FF("Microsoft YaHei UI", 32, FontStyle.Bold);   // 设备名称
                ActFont   = FF("Microsoft YaHei UI", 14, FontStyle.Bold);   // 借出/归还结果
                HintFont  = FF("Microsoft YaHei UI", 16, FontStyle.Bold);   // 「借出成功」等反馈
                InfoFont  = FF("Microsoft YaHei UI", 13, FontStyle.Regular);// 型号/分类
                InfoBold  = FF("Microsoft YaHei UI", 13, FontStyle.Bold);   // 借用人/应还
                StatFont  = FF("Microsoft YaHei UI", 24, FontStyle.Bold);   // 统计数字
                CountFont = FF("Consolas", 28, FontStyle.Bold);             // 在借件数
                // ---- 统计报表指标卡 ----
                RepValFont   = FF("Microsoft YaHei UI", 26, FontStyle.Bold);
                RepTitleFont = FF("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
                // ---- 自助机/触屏专用超大按钮 ----
                TouchFont = FF("Microsoft YaHei UI", 20, FontStyle.Bold);
            }
        }

        // ============== UI Root ==============
        private Panel sideBar, topBar, contentArea;
        private Label lblConnDot, lblDevInfo;
        private Button btnOpenPort;
        private ComboBox cboComPort;
        private Panel dbWarnBar;          // 数据库不可写时的红色提示条
        private Label dbWarnLabel;
        private bool dbWarnShown;         // 是否已经弹过一次性说明
        private readonly string[] navItems = { "◉  主控台", "▦  设备管理", "⇄  借还记录", "▣  用户管理", "▤  统计报表", "⚙  系统设置" };
        private readonly Button[] navButtons = new Button[6];
        private Panel[] pages = new Panel[6];
        private int activePage = 0;

        // Dashboard widgets
        private Panel heroCard, lentPanel;
        private Label heroName, heroModel, heroCategory, heroBorrower, heroEpc, heroTime, heroHint;
        private Label lentCountLbl;
        private ListView lentList;              // 在借明细（含逾期高亮）
        private System.Windows.Forms.Timer refreshTimer;
        private Panel statBorrow, statReturn, statOut, statTotal;
        private Label statBorrowV, statReturnV, statOutV, statTotalV;
        private ListView eventList;
        private TableLayoutPanel dashboardLayout;

        // Devices page
        private TextBox txtSearch;
        private ComboBox cboStatus;
        private DataGridView dgvDevices;
        private Button btnAddDev, btnEditDev, btnDelDev, btnReadEpc;

        // Records page
        private DateTimePicker dtFrom, dtTo;
        private DataGridView dgvRecords;

        // ============== Backend ==============
        private Reader reader;
        /// <summary>业务规则（距离/间隔/期限/双卡），设置页可随时修改并保存</summary>
        internal SwipeRules rules;
        /// <summary>刷卡间隔与防刷判定</summary>
        internal SwipeGuard guard;
        /// <summary>借用人卡模式下已识别的借用人（刷学生卡后有效期内有效）</summary>
        private User activeUser;
        private System.Windows.Forms.Timer userCardTimer;
        private System.Windows.Forms.Timer hintTimer;      // 一段时间没动作后重新显示 heroHint
        // 借出对话框打开期间，忽略新到的刷卡事件（防对话框被连续刷卡打断）
        private volatile bool suppressTag;
        // 页面切换动画计时器（防重入）
        private System.Windows.Forms.Timer pageAnimTimer;
        // 连接状态呼吸灯
        private System.Windows.Forms.Timer connPulseTimer;
        private bool connPulseOn;

        // ===================================================================
        //  刷卡提示音效（借出 / 归还 / 拦截 / 异常锁定）
        // ===================================================================
        /// <summary>按当前设置播放音效；设置里关了或发生异常都直接返回，不影响业务</summary>
        private void PlaySfx(Sfx.Kind kind)
        {
            try
            {
                if (rules == null || !rules.SoundEnabled) return;
                string file = "";
                switch (kind)
                {
                    case Sfx.Kind.Borrow: file = rules.SfxBorrowFile; break;
                    case Sfx.Kind.Return: file = rules.SfxReturnFile; break;
                    case Sfx.Kind.Blocked: file = rules.SfxBlockedFile; break;
                    case Sfx.Kind.Abuse: file = rules.SfxAbuseFile; break;
                }
                Sfx.Play(kind, file, rules.SoundVolume, rules.SoundMaxSec);
            }
            catch { /* 音效属于锦上添花，绝不影响借还登记 */ }
        }

        // ============== Entry ==============
        public MainForm()
        {
            Text = "LabBorrow  实验室设备借还管理系统";
            Size = new Size(1280, 760);
            MinimumSize = new Size(1000, 660);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = CreateAppIcon();
            FormBorderStyle = FormBorderStyle.Sizable;
            BackColor = Th.BgPage;
            ForeColor = Th.TxtPri;
            Font = Th.NormFont;
            DoubleBuffered = true;
            Opacity = 0;   // 启动淡入
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            // ---- 先把规则读进来：界面字号/自助模式/提醒开关都写在设置里，
            //     必须在建界面之前应用，否则建好的控件字是旧的。
            //     这里读失败也不能让程序起不来，兜底用默认规则。
            try { rules = SwipeRules.Load(); }
            catch { rules = new SwipeRules(); }
            Th.SetFontLevel(rules.UiFontLevel);

            BuildUi();
            Load += (s, e) => { FadeIn(); OnLoadUi(); };
            // 音效播放链路诊断同步到主控台右侧「实时事件」列表（后台线程 → UI 线程）
            try
            {
                Sfx.Diagnostics = msg =>
                {
                    try
                    {
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke((Action)(() => AddLogRow(msg)));
                    }
                    catch { }
                };
            }
            catch { }
            FormClosing += (s, e) =>
            {
                // 设置页改了没保存就关程序：先问一句，避免"改了不生效"的误会
                try
                {
                    if (activePage == 5 && SettingsHasUnsaved())
                    {
                        var dr = MessageBox.Show(this,
                            "系统设置里有还没保存的修改。\n\n要保存后再退出吗？",
                            "未保存的设置", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (dr == DialogResult.Cancel) { e.Cancel = true; return; }
                        if (dr == DialogResult.Yes) SaveSettingsFromControls(false);
                    }
                }
                catch { }

                if (pageAnimTimer != null) pageAnimTimer.Stop();
                if (connPulseTimer != null) connPulseTimer.Stop();
                if (refreshTimer != null) refreshTimer.Stop();
                if (userCardTimer != null) userCardTimer.Stop();
                if (unsavedTimer != null) unsavedTimer.Stop();
                if (reader != null) reader.Dispose();
            };
        }

        // 窗口淡入（约 300ms）
        private void FadeIn()
        {
            var t = new System.Windows.Forms.Timer { Interval = 15 };
            t.Tick += (s2, e2) =>
            {
                Opacity = Math.Min(1.0, Opacity + 0.09);
                if (Opacity >= 1.0) { t.Stop(); t.Dispose(); }
            };
            t.Start();
        }

        // ===================================================================
        //  Build UI
        // ===================================================================
        private void BuildUi()
        {
            // ====== Top Bar ======
            topBar = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Th.BgPanel };
            topBar.Paint += (s, e) =>
            {
                using (var p = new Pen(Th.Border, 1))
                    e.Graphics.DrawLine(p, 0, topBar.Height - 1, Width, topBar.Height - 1);
            };
            var topLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 5,
                RowCount = 1,
                Padding = new Padding(20, 0, 20, 0)
            };
            topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var lblTitle = new Label
            {
                Text = "🧰   LabBorrow  设备借还管理",
                Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold),
                ForeColor = Th.TxtPri,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 16, 0)
            };
            topLayout.Controls.Add(lblTitle, 0, 0);

            lblDevInfo = new Label
            {
                Text = "", ForeColor = Th.TxtMuted, Font = Th.MonoFont,
                AutoSize = false, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(0, 0, 16, 0)
            };
            topLayout.Controls.Add(lblDevInfo, 1, 0);

            lblConnDot = new Label
            {
                AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont,
                Text = "●  未连接", Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 8, 0)
            };
            topLayout.Controls.Add(lblConnDot, 2, 0);

            cboComPort = new ComboBox
            {
                Width = 90, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Th.BgInput, ForeColor = Th.TxtPri,
                FlatStyle = FlatStyle.Flat, Font = Th.NormFont,
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0)
            };
            cboComPort.Items.Add("自动");
            foreach (var pn in System.IO.Ports.SerialPort.GetPortNames().OrderBy(s => s)) cboComPort.Items.Add(pn);
            cboComPort.SelectedIndex = 0;
            // 展开时刷新端口列表：USB 重插后无需重启程序即可看到新端口
            cboComPort.DropDown += (s, e) =>
            {
                string cur = cboComPort.SelectedItem == null ? "自动" : cboComPort.SelectedItem.ToString();
                cboComPort.Items.Clear();
                cboComPort.Items.Add("自动");
                foreach (var pn in System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p)) cboComPort.Items.Add(pn);
                int i = cboComPort.Items.IndexOf(cur);
                cboComPort.SelectedIndex = i >= 0 ? i : 0;
            };
            topLayout.Controls.Add(cboComPort, 3, 0);

            btnOpenPort = MakeButton("连接读卡器", Th.Accent, Point.Empty, new Size(120, 34));
            btnOpenPort.Anchor = AnchorStyles.Right;
            btnOpenPort.Margin = new Padding(0);
            btnOpenPort.Click += BtnOpenPort_Click;
            topLayout.Controls.Add(btnOpenPort, 4, 0);

            topBar.Controls.Add(topLayout);

            // ====== Sidebar ======
            sideBar = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Th.BgPanel };
            sideBar.Paint += (s, e) =>
            {
                using (var p = new Pen(Th.Border, 1))
                    e.Graphics.DrawLine(p, sideBar.Width - 1, 0, sideBar.Width - 1, sideBar.Height);
            };
            for (int i = 0; i < navItems.Length; i++)
            {
                var btn = new SmoothButton
                {
                    Text = navItems[i],
                    BaseColor = Th.BgPanel,
                    Radius = 8,
                    ForeColor = Th.TxtSec,
                    Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true,
                    Size = new Size(184, 44),
                    Location = new Point(8, 80 + i * 52),
                    Cursor = Cursors.Hand,
                    Tag = i
                };
                btn.Click += (s, e) => SwitchPage((int)((Button)s).Tag);
                sideBar.Controls.Add(btn);
                navButtons[i] = btn;
            }
            var sidebarLogo = new Label
            {
                Text = "L B",
                Font = new Font("Microsoft YaHei UI", 22, FontStyle.Bold),
                ForeColor = Th.Accent,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(28, 22)
            };
            sideBar.Controls.Add(sidebarLogo);

            // ====== Content Area ======
            contentArea = new Panel { Dock = DockStyle.Fill, BackColor = Th.BgPage, Padding = new Padding(20) };
            contentArea.Resize += ContentArea_Resize;
            Controls.Add(topBar);
            Controls.Add(sideBar);
            Controls.Add(contentArea);

            // 数据库不可写时的红色提示条（平时隐藏，不占高度）
            dbWarnBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(74, 26, 32), Visible = false };
            dbWarnLabel = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Color.FromArgb(255, 196, 196),
                Font = Th.NormFont, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 8, 0), AutoEllipsis = true,
                Text = "数据库不可写（只读模式）"
            };
            var btnDbRetry = MakeButton("修复后点此重试", Color.FromArgb(178, 46, 60), Point.Empty, new Size(130, 28));
            btnDbRetry.Dock = DockStyle.Right;
            btnDbRetry.Margin = new Padding(0);
            btnDbRetry.Click += (s, e) => RetryDbUpgrade();
            dbWarnBar.Controls.Add(dbWarnLabel);
            dbWarnBar.Controls.Add(btnDbRetry);
            Controls.Add(dbWarnBar);

            Controls.SetChildIndex(contentArea, 0);
            Controls.SetChildIndex(sideBar, 1);
            Controls.SetChildIndex(dbWarnBar, 2);   // 在顶栏之下、内容区之上
            Controls.SetChildIndex(topBar, 3);

            BuildDashboard();
            BuildDevicesPage();
            BuildRecordsPage();
            BuildUsersPage();
            BuildReportsPage();

            // Page 0: Dashboard
            pages[0] = new Panel { Dock = DockStyle.Fill, BackColor = Th.BgPage };
            pages[0].Controls.Add(dashboardLayout);
            BuildRemindBar(pages[0]);     // 主控台顶部的「到期提醒」横幅（有到期才显示）

            // Page 1: Devices (add the fill control first so the toolbar docks correctly)
            pages[1] = new Panel { Dock = DockStyle.Fill, BackColor = Th.BgPage };
            pages[1].Controls.Add(dgvDevices);
            pages[1].Controls.Add(MakeToolbarDevices());

            // Page 2: Records
            pages[2] = new Panel { Dock = DockStyle.Fill, BackColor = Th.BgPage };
            pages[2].Controls.Add(dgvRecords);
            pages[2].Controls.Add(MakeToolbarRecords());

            // Page 3: Users（借用人 / 学生卡）
            pages[3] = new Panel { Dock = DockStyle.Fill, BackColor = Th.BgPage };
            pages[3].Controls.Add(dgvUsers);
            pages[3].Controls.Add(MakeToolbarUsers());

            // Page 4: Reports（统计报表）
            pages[4] = new Panel { Dock = DockStyle.Fill, BackColor = Th.BgPage };
            pages[4].Controls.Add(reportsRoot);
            pages[4].Controls.Add(MakeToolbarReports());

            // Page 5: Settings
            pages[5] = new Panel { Dock = DockStyle.Fill, BackColor = Th.BgPage };
            settingsPanel = MakeSettingsPanel();
            pages[5].Controls.Add(settingsPanel);

            foreach (var p in pages)
            {
                // 页面容器开双缓冲：切换页面/拖动窗口时不闪成一片
                EnableDoubleBuffer(p);
                p.Visible = false;
                contentArea.Controls.Add(p);
            }
            EnableDoubleBuffer(dashboardLayout);
            contentArea.PerformLayout();
            ContentArea_Resize(null, null);
            SwitchPage(0);
        }

        private Control settingsPanel;

        private void ContentArea_Resize(object sender, EventArgs e)
        {
            if (contentArea == null || pages == null || pages[0] == null) return;
            ScheduleDashboardLayout(false);
        }

        // --------------------------------------------------------------
        //  布局节流：拖动窗口时 SizeChanged/Resize 会每一像素触发一次，
        //  直接同步重算主控台行高就会「边拖边卡」；这里合并到 100ms 后做一次。
        //  切页则是立即算（immediate=true），避免看到 100ms 的错误行高。
        // --------------------------------------------------------------
        private bool dashboardBusy;
        private System.Windows.Forms.Timer dashLayoutTimer;
        /// <summary>重排实际执行次数（冒烟测试用来验证拖动窗口时确实一次都没重排）</summary>
        internal static int DashboardLayoutCalls;
        private bool resizing;          // 正在用鼠标拖动窗口边框/最大化动画中

        // 拖动窗口的起止消息：拖动期间不重排，松手后统一重排一次
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;

        // 注意：WndProc 统一写在下面「自助终端」那一段里
        // （同时处理「拖动中不重排」和「自助模式下接管关闭按钮」），此处不再重复定义。

        private void ScheduleDashboardLayout(bool immediate)
        {
            if (immediate)
            {
                if (dashLayoutTimer != null) dashLayoutTimer.Stop();
                LayoutDashboardRows();
                return;
            }
            // 正在拖动窗口边框：一次都不重排。
            // 拖动时每次重排都要 150ms 量级（整页重绘），100ms 节流也扛不住，
            // 表现为"边拖边卡"；改成松手（WM_EXITSIZEMOVE）时统一算一次，拖动过程就顺了。
            if (resizing) return;
            if (dashLayoutTimer == null)
            {
                dashLayoutTimer = new System.Windows.Forms.Timer { Interval = 100 };
                dashLayoutTimer.Tick += (s, e) =>
                {
                    dashLayoutTimer.Stop();
                    // 排队期间如果用户开始拖窗口，这次就作废：
                    // 松手时（WM_EXITSIZEMOVE）会立刻按最终尺寸算一次，不会漏
                    if (!resizing) LayoutDashboardRows();
                };
            }
            dashLayoutTimer.Stop();
            dashLayoutTimer.Start();
        }

        // 根据可用高度动态调整“设备信息/在借看板”行与“事件列表”行的比例，
        // 保证普通有边框窗口、最大化(全屏)以及各种长宽比下内容都能完整显示、互不遮挡。
        private int heroInnerTotal;   // hero 卡内部 6 行总高（构建时按 Font.Height 算出，已含 DPI 缩放）

        private void LayoutDashboardRows()
        {
            if (dashboardBusy) return;                 // 防重入：改行高会触发布局->SizeChanged->又进来
            if (dashboardLayout == null || dashboardLayout.RowStyles.Count < 3) return;
            int avail = dashboardLayout.ClientSize.Height;
            if (avail <= 0) return;

            dashboardBusy = true;
            DashboardLayoutCalls++;                    // 供冒烟测试验证「拖动窗口时确实没有重排」
            try
            {
                dashboardLayout.SuspendLayout();       // 合并重绘，避免中间状态被画出来（闪烁）

                // 行高跟着字号走（Font.Height 已按系统 DPI 缩放）：
                // 2560x1600 等 150% 缩放的屏上 pt 字体会放大，固定像素行高会把文字裁掉一半。
                int statsRow = Math.Max(125, Th.StatFont.Height + Th.NormFont.Height + 72); // 统计卡片行（含下边距）
                int minHero = Math.Max(285, heroInnerTotal + (heroCard != null ? heroCard.Padding.Vertical : 0) + 6); // 顶部行最小高度：装下全部文字
                int maxHero = Math.Max(400, minHero + 150);                                  // 顶部行最大高度，避免全屏时空旷

                int content = avail - statsRow;
                int hero = (int)(content * 0.45);
                if (hero < minHero) hero = minHero;
                if (hero > maxHero) hero = maxHero;
                if (hero > content) hero = Math.Max(200, content);

                var style = dashboardLayout.RowStyles[0];
                if (style.SizeType != SizeType.Absolute || (int)style.Height != hero)
                    dashboardLayout.RowStyles[0] = new RowStyle(SizeType.Absolute, hero);

                // 第二行（统计卡片）必须同步：以前它一直是初始值 125，
                // 而第 0 行按新的 statsRow 参与分配，两者对不上就会互相挤、来回抖。
                var st1 = dashboardLayout.RowStyles[1];
                if (st1.SizeType != SizeType.Absolute || (int)st1.Height != statsRow)
                    dashboardLayout.RowStyles[1] = new RowStyle(SizeType.Absolute, statsRow);
            }
            finally
            {
                dashboardLayout.ResumeLayout(true);
                dashboardBusy = false;
            }
        }

        // ===================================================================
        //  Dashboard
        // ===================================================================
        private void BuildDashboard()
        {
            // Responsive dashboard root: 2 columns (hero/lent panel), stats row, event list
            dashboardLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Th.BgPage,
                ColumnCount = 2,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            dashboardLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
            dashboardLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            dashboardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 300F));
            dashboardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 125F));
            dashboardLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            // SizeChanged 走节流：拖动窗口时每一像素都同步重算会明显卡顿
            dashboardLayout.SizeChanged += (s, e) => ScheduleDashboardLayout(false);

            // Hero card (device) -- 内部全部交给 TableLayoutPanel，按内容自动排布，永不重叠
            heroCard = new Panel
            {
                BackColor = Th.BgCard,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 16, 16),
                Padding = new Padding(18, 14, 18, 12),
                AutoScroll = true
            };
            heroCard.Paint += PaintCardBorder;
            RoundRegion(heroCard, 8);

            var heroInner = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 6
            };
            heroInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            // 右侧「借出/归还」结果列固定宽度：宽度不受卡片内容长短影响，
            // 避免设备名很长时把时间挤没、或时间变长时把设备名挤出卡片。
            heroInner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210F));
            // 行高「按字体实测高度」确定（Font.Height 已随系统 DPI 缩放）。
            // 以前用 AutoSize 行：窗口一小，TableLayoutPanel 重算行高时会把行压扁，
            // 文字一行行叠在一起；后来改固定像素又会在 2560x1600 这类 150% 缩放屏上
            // 把 pt 字体放大后裁掉半截——只有按 Font.Height 算才能两头都稳。
            var epcFont = new Font("Consolas", 12F);
            int hName = Th.HeroFont.Height + 10;     // 设备名（大字）
            int hHint = Th.HintFont.Height + 14;     // 提示行
            int hInfo = Th.InfoFont.Height + 14;     // 型号 / 分类（含下边距）
            int hBorrower = Th.InfoBold.Height + 14; // 借用人
            int hEpc = epcFont.Height + 8;           // EPC
            int[] heroRowHeights = { hName, hHint, hInfo, hInfo, hBorrower, hEpc };
            for (int i = 0; i < 6; i++) heroInner.RowStyles.Add(new RowStyle(SizeType.Absolute, heroRowHeights[i]));
            heroInner.Height = heroRowHeights.Sum();   // 6 行高度之和（LayoutDashboardRows 的 minHero 依赖它）
            heroInnerTotal = heroInner.Height;

            // 说明：这几行曾经用 AutoSize=true，文字一长就会把内容顶出卡片
            // （设备名长、借用人后带应还时间时出现滚动条/错位）。
            // 现在统一改成「按可用宽度铺满 + 过长显示省略号」，卡片宽度再窄也不会乱。
            heroName = new Label
            {
                Text = "等待刷卡…", Font = Th.HeroFont, ForeColor = Th.TxtMuted,
                AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
                MinimumSize = new Size(0, Th.HeroFont.Height + 4),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 12, 6),
                Cursor = Cursors.Default
            };
            heroTime = new Label
            {
                Text = "", Font = Th.ActFont, ForeColor = Th.Accent,
                AutoSize = false, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomRight,
                MinimumSize = new Size(0, Th.ActFont.Height + 4),
                Margin = new Padding(0, 0, 0, 6),
                Cursor = Cursors.Default
            };
            heroHint = new Label
            {
                Text = "请将设备标签靠近读写器", Font = Th.HintFont, ForeColor = Th.TxtMuted,
                AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
                MinimumSize = new Size(0, Th.HintFont.Height + 4),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 12),
                Cursor = Cursors.Default
            };
            heroModel = new Label
            {
                Text = "型号：", Font = Th.InfoFont, ForeColor = Th.TxtSec,
                AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
                MinimumSize = new Size(0, Th.InfoFont.Height + 2),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 10),
                Cursor = Cursors.Default
            };
            heroCategory = new Label
            {
                Text = "分类：", Font = Th.InfoFont, ForeColor = Th.TxtSec,
                AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
                MinimumSize = new Size(0, Th.InfoFont.Height + 2),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 10),
                Cursor = Cursors.Default
            };
            heroBorrower = new Label
            {
                Text = "借用人：", Font = Th.InfoBold, ForeColor = Th.TxtPri,
                AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
                MinimumSize = new Size(0, Th.InfoBold.Height + 2),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 10),
                Cursor = Cursors.Default
            };
            heroEpc = new Label
            {
                Text = "EPC：", Font = epcFont, ForeColor = Th.TxtSec,
                AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
                MinimumSize = new Size(0, epcFont.Height + 2),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0),
                Cursor = Cursors.Default
            };

            heroInner.Controls.Add(heroName, 0, 0);
            heroInner.Controls.Add(heroTime, 1, 0);
            heroInner.Controls.Add(heroHint, 0, 1); heroInner.SetColumnSpan(heroHint, 2);
            heroInner.Controls.Add(heroModel, 0, 2); heroInner.SetColumnSpan(heroModel, 2);
            heroInner.Controls.Add(heroCategory, 0, 3); heroInner.SetColumnSpan(heroCategory, 2);
            heroInner.Controls.Add(heroBorrower, 0, 4); heroInner.SetColumnSpan(heroBorrower, 2);
            heroInner.Controls.Add(heroEpc, 0, 5); heroInner.SetColumnSpan(heroEpc, 2);

            heroCard.Controls.Add(heroInner);

            // Lent panel -- 在借看板（数量 + 在借明细，逾期高亮）
            lentPanel = new Panel
            {
                BackColor = Th.BgCard,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 16),
                Padding = new Padding(18, 14, 18, 12)
            };
            lentPanel.Paint += PaintCardBorder;
            RoundRegion(lentPanel, 8);

            var lentInner = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 3
            };
            lentInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            lentInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lentInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lentInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var lentTitle = new Label
            {
                Text = "在借看板", Font = Th.MidFont, ForeColor = Th.TxtPri,
                AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 4)
            };
            lentCountLbl = new Label
            {
                Text = "0 件", Font = Th.CountFont, ForeColor = Th.Accent,
                AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 6)
            };

            // 在借明细：设备 / 借用人 / 已借 / 期限状态（逾期红色、临期黄色）
            lentList = new ListView
            {
                BackColor = Th.BgCard, ForeColor = Th.TxtPri, BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false,
                OwnerDraw = true, Font = Th.NormFont, Margin = new Padding(0)
            };
            lentList.Columns.Add("设备", 150);
            lentList.Columns.Add("借用人", 90);
            lentList.Columns.Add("已借", 90);
            lentList.Columns.Add("期限状态", 110);
            lentList.DrawColumnHeader += ListHeaderDraw;
            lentList.DrawItem += ListItemDraw;
            lentList.DrawSubItem += ListSubItemDraw;
            EnableDoubleBuffer(lentList);
            lentList.Resize += (s, e) =>
            {
                if (lentList.Columns.Count < 4) return;
                int[] weights = { 36, 22, 14, 28 };
                int total = Math.Max(200, lentList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
                int sum = 0; for (int i = 0; i < weights.Length; i++) sum += weights[i];
                int used = 0;
                for (int i = 0; i < lentList.Columns.Count; i++)
                {
                    int w = (i == lentList.Columns.Count - 1) ? Math.Max(60, total - used)
                                                              : Math.Max(40, total * weights[i] / sum);
                    used += w;
                    lentList.Columns[i].Width = w;
                }
            };

            lentInner.Controls.Add(lentTitle, 0, 0);
            lentInner.Controls.Add(lentCountLbl, 0, 1);
            lentInner.Controls.Add(lentList, 0, 2);
            lentPanel.Controls.Add(lentInner);

            // Stat cards row (4 equal columns)
            var statRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Th.BgPage,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 16)
            };
            for (int i = 0; i < 4; i++)
                statRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

            statBorrow = MakeStatCard("今日借出", "0",  Th.Accent);
            statReturn = MakeStatCard("今日归还", "0",  Th.Accent2);
            statOut    = MakeStatCard("当前外借", "0",  Th.Warn);
            statTotal  = MakeStatCard("设备总数", "0",  Th.Success);

            statBorrow.Margin = new Padding(0, 0, 16, 0);
            statReturn.Margin = new Padding(0, 0, 16, 0);
            statOut.Margin    = new Padding(0, 0, 16, 0);
            statTotal.Margin  = Padding.Empty;

            statRow.Controls.Add(statBorrow, 0, 0);
            statRow.Controls.Add(statReturn, 1, 0);
            statRow.Controls.Add(statOut, 2, 0);
            statRow.Controls.Add(statTotal, 3, 0);

            statBorrowV = (Label)statBorrow.Tag;
            statReturnV = (Label)statReturn.Tag;
            statOutV    = (Label)statOut.Tag;
            statTotalV  = (Label)statTotal.Tag;

            // event list
            eventList = new ListView
            {
                BackColor = Th.BgCard,
                ForeColor = Th.TxtPri,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                OwnerDraw = true,
                Font = Th.NormFont
            };
            eventList.Columns.Add("时间", 160);
            eventList.Columns.Add("操作", 90);
            eventList.Columns.Add("设备", 140);
            eventList.Columns.Add("借用人", 120);
            eventList.Columns.Add("分类", 100);
            eventList.Columns.Add("时长", 80);
            eventList.Columns.Add("EPC", 280);
            eventList.Columns.Add("备注", 320);
            eventList.DrawColumnHeader += ListHeaderDraw;
            eventList.DrawItem += ListItemDraw;
            eventList.DrawSubItem += ListSubItemDraw;
            EnableDoubleBuffer(eventList);
            // 列宽按比例分配，任何窗口宽度都不会出现文字被挤在一起
            eventList.Resize += (s, e) =>
            {
                if (eventList.Columns.Count == 0) return;
                int[] weights = { 140, 80, 130, 110, 90, 70, 240, 240 };
                int total = Math.Max(400, eventList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
                int sum = 0;
                for (int i = 0; i < eventList.Columns.Count && i < weights.Length; i++) sum += weights[i];
                if (sum <= 0) return;
                int used = 0;
                for (int i = 0; i < eventList.Columns.Count; i++)
                {
                    int w = (i == eventList.Columns.Count - 1)
                        ? Math.Max(90, total - used)
                        : Math.Max(50, total * weights[Math.Min(i, weights.Length - 1)] / sum);
                    used += w;
                    eventList.Columns[i].Width = w;
                }
            };

            dashboardLayout.Controls.Add(heroCard, 0, 0);
            dashboardLayout.Controls.Add(lentPanel, 1, 0);
            dashboardLayout.SetColumnSpan(statRow, 2);
            dashboardLayout.Controls.Add(statRow, 0, 1);
            dashboardLayout.SetColumnSpan(eventList, 2);
            dashboardLayout.Controls.Add(eventList, 0, 2);
        }

        private Panel MakeStatCard(string title, string val, Color accent)
        {
            var p = new Panel { BackColor = Th.BgCard, Dock = DockStyle.Fill };
            p.Paint += PaintCardBorder;
            RoundRegion(p, 8);

            var accentBar = new Panel { BackColor = accent, Dock = DockStyle.Left, Width = 4 };
            var inner = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(14, 12, 12, 10)
            };
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var t = new Label
            {
                Text = title, Font = Th.NormFont, ForeColor = Th.TxtSec,
                AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 6)
            };
            var v = new Label
            {
                Text = val, Font = Th.StatFont, ForeColor = Th.TxtPri,
                AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true, Margin = new Padding(0)
            };

            inner.Controls.Add(t, 0, 0);
            inner.Controls.Add(v, 0, 1);
            // add the stripe last so Dock=Left is applied before Dock=Fill
            p.Controls.Add(inner);
            p.Controls.Add(accentBar);
            p.Tag = v;
            return p;
        }

        // 圆角路径按「控件 + 尺寸」缓存：窗口缩放时每张卡片都要重绘，
        // 以前每次重绘都重新构造一遍路径，白白吃掉不少时间。
        private static readonly Dictionary<Control, GraphicsPath> CardPathCache = new Dictionary<Control, GraphicsPath>();
        private static readonly Dictionary<Control, Size> CardPathSize = new Dictionary<Control, Size>();

        private void PaintCardBorder(object sender, PaintEventArgs e)
        {
            var p = sender as Panel;
            if (p == null) return;
            var rect = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
            if (rect.Width < 16 || rect.Height < 16) return;

            GraphicsPath path;
            if (!CardPathCache.TryGetValue(p, out path) || CardPathSize[p] != rect.Size)
            {
                if (path != null) path.Dispose();
                path = RoundedPath(rect, 8);
                CardPathCache[p] = path;
                CardPathSize[p] = rect.Size;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Th.Border, 1f))
                e.Graphics.DrawPath(pen, path);
        }

        // 圆角矩形路径
        internal static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // 给控件应用圆角裁剪区域（随窗口缩放自动更新）
        private static void RoundRegion(Control c, int radius)
        {
            EventHandler apply = (s, e) =>
            {
                var r = new Rectangle(0, 0, Math.Max(0, c.Width - 1), Math.Max(0, c.Height - 1));
                if (r.Width < radius * 2 || r.Height < radius * 2) return;
                var old = c.Region;
                using (var path = RoundedPath(r, radius)) c.Region = new Region(path);
                if (old != null) old.Dispose();
                c.Invalidate();
            };
            c.Resize += apply;
            apply(null, EventArgs.Empty);
        }

        // 反射开启双缓冲，消除表格/列表滚动闪烁
        private static void EnableDoubleBuffer(Control c)
        {
            typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(c, true, null);
        }

        private void ListHeaderDraw(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var bg = new SolidBrush(Th.BgPanel))
                e.Graphics.FillRectangle(bg, e.Bounds);
            using (var pen = new Pen(Th.Border, 1))
                e.Graphics.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Th.NormFont, e.Bounds, Th.TxtSec, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.LeftAndRightPadding);
        }
        private void ListItemDraw(object sender, DrawListViewItemEventArgs e)
        {
            e.DrawDefault = false;
            var bg = e.Item.Selected ? Th.BgHover : ((e.ItemIndex % 2 == 0) ? Th.BgCard : Th.BgPanel);
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
        }
        private void ListSubItemDraw(object sender, DrawListViewSubItemEventArgs e)
        {
            e.DrawDefault = false;
            var rect = e.SubItem.Bounds;
            // 先自行填背景再画文字：悬停/选中时公共控件会用系统默认配色（黑字）擦除重绘，
            // 若不覆盖会露出黑字；选中行用高亮底色保持可读。
            var bg = e.Item.Selected ? Th.BgHover : ((e.ItemIndex % 2 == 0) ? Th.BgCard : Th.BgPanel);
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, rect);
            // 使用条目自身的颜色（借出绿 / 归还紫 / 拦截红 / 日志灰），否则彩色状态行全部失效
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Th.NormFont, rect, e.Item.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.LeftAndRightPadding);
        }

        // ===================================================================
        //  Devices Page
        // ===================================================================
        private Control MakeToolbarDevices()
        {
            var bar = new Panel
            {
                BackColor = Th.BgCard,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 10, 12, 10),
                MinimumSize = new Size(0, 56)
            };
            bar.Paint += PaintCardBorder;
            RoundRegion(bar, 8);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent
            };

            var lbl = new Label { Text = "搜索：", AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont, Margin = new Padding(0, 8, 4, 0) };
            txtSearch = new TextBox { Width = 200, BackColor = Th.BgInput, ForeColor = Th.TxtPri, BorderStyle = BorderStyle.FixedSingle, Font = Th.NormFont, Margin = new Padding(0, 4, 12, 0) };
            txtSearch.TextChanged += (s, e) => ReloadDevices();
            var lbl2 = new Label { Text = "状态：", AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont, Margin = new Padding(0, 8, 4, 0) };
            cboStatus = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Th.BgInput, ForeColor = Th.TxtPri, FlatStyle = FlatStyle.Flat, Font = Th.NormFont, Margin = new Padding(0, 4, 12, 0) };
            cboStatus.Items.AddRange(new object[] { "全部", "在库", "外借中", "维修中", "已报废" });
            cboStatus.SelectedIndex = 0;
            cboStatus.SelectedIndexChanged += (s, e) => ReloadDevices();

            btnReadEpc = MakeButton("读取标签 EPC", Th.Accent2, Point.Empty, new Size(130, 30));
            btnReadEpc.Margin = new Padding(0, 3, 10, 0);
            btnReadEpc.Click += BtnReadEpc_Click;
            btnAddDev = MakeButton("+ 新增设备", Th.Accent, Point.Empty, new Size(110, 30));
            btnAddDev.Margin = new Padding(0, 3, 10, 0);
            btnAddDev.Click += BtnAddDev_Click;
            btnEditDev = MakeButton("编辑", Th.Accent2, Point.Empty, new Size(80, 30));
            btnEditDev.Margin = new Padding(0, 3, 10, 0);
            btnEditDev.Click += BtnEditDev_Click;
            btnDelDev = MakeButton("删除", Th.Danger, Point.Empty, new Size(80, 30));
            btnDelDev.Margin = new Padding(0, 3, 0, 0);
            btnDelDev.Click += BtnDelDev_Click;

            flow.Controls.AddRange(new Control[] { lbl, txtSearch, lbl2, cboStatus, btnReadEpc, btnAddDev, btnEditDev, btnDelDev });
            bar.Controls.Add(flow);
            return bar;
        }

        private void BuildDevicesPage()
        {
            dgvDevices = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Th.BgPanel,
                ForeColor = Th.TxtPri,
                BorderStyle = BorderStyle.None,
                GridColor = Th.Border,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                Font = Th.NormFont,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 36,
                RowTemplate = { Height = 32 },
                AutoGenerateColumns = false
            };
            dgvDevices.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Th.BgPanel, ForeColor = Th.TxtSec, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0)
            };
            dgvDevices.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Th.BgCard, ForeColor = Th.TxtPri,
                SelectionBackColor = Th.Accent, SelectionForeColor = Color.White,
                Padding = new Padding(8, 0, 0, 0)
            };
            dgvDevices.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.BgPanel };
            EnableDoubleBuffer(dgvDevices);
            dgvDevices.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "EPC", Name = "Epc", Width = 190 });
            dgvDevices.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "设备名称", Name = "Name", Width = 140 });
            dgvDevices.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "型号", Name = "Model", Width = 110 });
            dgvDevices.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "分类", Name = "Category", Width = 90 });
            dgvDevices.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "存放位置", Name = "Location", Width = 110 });
            dgvDevices.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", Name = "Status", Width = 80 });
            dgvDevices.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "当前借用人", Name = "Borrower", Width = 100 });
            dgvDevices.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "借出时间", Name = "BorrowTime", Width = 155 });
            dgvDevices.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "备注", Name = "Notes", Width = 150 });

            // ---- 表格美化：列宽自适应 + EPC 等宽字体 + 状态着色 ----
            foreach (DataGridViewColumn col in dgvDevices.Columns)
            {
                col.FillWeight = Math.Max(30, col.Width);   // 以原宽度作为 Fill 权重
                col.MinimumWidth = 60;
            }
            dgvDevices.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvDevices.Columns["Epc"].DefaultCellStyle.Font = Th.MonoFont;
            dgvDevices.Columns["Epc"].DefaultCellStyle.ForeColor = Th.TxtSec;
            dgvDevices.CellFormatting += (s, e) =>
            {
                if (e.Value == null) return;
                if (dgvDevices.Columns[e.ColumnIndex].Name != "Status") return;
                switch (e.Value.ToString())
                {
                    case "在库": e.CellStyle.ForeColor = Th.Success; break;
                    case "外借中": e.CellStyle.ForeColor = Th.Warn; break;
                    case "维修中": e.CellStyle.ForeColor = Color.FromArgb(255, 146, 60); break;
                    case "已报废": e.CellStyle.ForeColor = Th.TxtMuted; break;
                }
                e.CellStyle.Font = Th.BoldFont;
            };
            // 双击行直接打开编辑对话框
            dgvDevices.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0) BtnEditDev_Click(null, EventArgs.Empty);
            };
        }

        private void ReloadDevices()
        {
            var list = Db.ListDevices(
                string.IsNullOrWhiteSpace(txtSearch.Text) ? null : txtSearch.Text.Trim(),
                cboStatus.SelectedIndex <= 0 ? null : StatusCode(cboStatus.SelectedItem.ToString()));
            dgvDevices.Rows.Clear();
            foreach (var d in list)
                dgvDevices.Rows.Add(d.Epc, d.Name, d.Model, d.Category, d.Location, StatusLabel(d.Status), d.CurrentBorrower, d.BorrowTime, d.Notes);
        }

        private string StatusLabel(string s)
        {
            if (s == "idle") return "在库";
            if (s == "borrowed") return "外借中";
            if (s == "maintenance") return "维修中";
            if (s == "retired") return "已报废";
            return s;
        }

        // 中文状态 -> 数据库状态码（未知值原样返回，保证脏数据不丢失）
        internal static string StatusCode(string label)
        {
            switch (label)
            {
                case "在库": return "idle";
                case "外借中": return "borrowed";
                case "维修中": return "maintenance";
                case "已报废": return "retired";
                default: return label;
            }
        }

        /// <summary>读一张标签的 EPC（供设备登记 / 借用人卡录入调用）；期间抑制主界面刷卡逻辑</summary>
        // ===================================================================
        //  自助终端（Kiosk）模式
        //  放在小型刷卡机 / 一体机上无人值守地运行：全屏、触控友好、
        //  空闲自动回主屏、不会被学生随手关掉、屏幕常亮。
        // ===================================================================
        private System.Windows.Forms.Timer idleTimer;
        private DateTime lastActivity = DateTime.Now;

        private void ApplyKioskMode()
        {
            try
            {
                if (rules == null) rules = SwipeRules.Load();
                if (!rules.KioskEnabled)
                {
                    Kiosk.KeepScreenOn(rules.KeepScreenOn);   // 非自助模式也能单独开"屏幕常亮"
                    StartIdleWatch();
                    return;
                }

                FormBorderStyle = FormBorderStyle.None;       // 无边框：像闸机一样占满整屏
                WindowState = FormWindowState.Maximized;
                TopMost = true;                               // 盖住任务栏和其它窗口
                MaximizeBox = false; MinimizeBox = false;
                StartIdleWatch();
                Kiosk.KeepScreenOn(true);

                AddLogRow("自助模式已启用：全屏运行、触控大按钮、空闲自动回主屏。"
                        + "退出请按 Ctrl+Alt+Q（管理人员），或在右下角按住 3 秒「退出」按钮。");
            }
            catch (Exception ex) { AddLogRow("自助模式启用失败：" + ex.Message); }
        }

        /// <summary>空闲若干秒后自动回到主控台：下一个人过来就是干净的开始画面</summary>
        private void StartIdleWatch()
        {
            try
            {
                if (idleTimer != null) { idleTimer.Stop(); idleTimer.Dispose(); idleTimer = null; }
                if (rules == null || rules.KioskIdleSec <= 0) return;
                idleTimer = new System.Windows.Forms.Timer { Interval = 5000 };
                idleTimer.Tick += (s, e) =>
                {
                    try
                    {
                        if (suppressTag) { lastActivity = DateTime.Now; return; }   // 正在填写借用信息，不打扰
                        var forms = Application.OpenForms;
                        for (int i = 0; i < forms.Count; i++)
                        {
                            var f = forms[i];
                            if (f != null && f != this && f.Visible && f.Modal) { lastActivity = DateTime.Now; return; }
                        }
                        if ((DateTime.Now - lastActivity).TotalSeconds >= rules.KioskIdleSec)
                        {
                            lastActivity = DateTime.Now;
                            if (activePage != 0) SwitchPage(0);
                        }
                    }
                    catch { }
                };
                idleTimer.Start();
            }
            catch { }
        }

        /// <summary>任何交互都算「有人在用」，推迟自动回主屏</summary>
        private void MarkActivity() { lastActivity = DateTime.Now; }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // 自助模式下用的 combo key：Ctrl+Alt+Q 退出（避免学生误触：必须三个键一起按）
            if (keyData == (Keys.Control | Keys.Alt | Keys.Q))
            {
                AskExit();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>退出确认：自助模式下多问一句，防止机器被人顺手关掉</summary>
        private void AskExit()
        {
            try
            {
                bool kiosk = rules != null && rules.KioskEnabled;
                string tip = kiosk
                    ? "自助模式运行中。\n\n确定要退出程序吗？\n（退出后这台机器就不再响应刷卡了，需要重新打开 LabBorrow 才能恢复）"
                    : "确定要退出程序吗？";
                var dr = MessageBox.Show(this, tip, "退出确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr != DialogResult.Yes) return;
                Close();
            }
            catch { Close(); }
        }

        /// <summary>
        /// 窗口关闭请求统一走这里处理：自助模式下按「确定」才允许退出。
        /// （FormClosing 里 Abort 可能来不及，所以在源头就把关窗动作改成询问。）
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_CLOSE = 0xF060;
            try
            {
                if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xFFF0) == SC_CLOSE)
                {
                    AskExit();
                    return;                    // 已处理，不再交给基类
                }
                if (m.Msg == WM_ENTERSIZEMOVE) resizing = true;
                else if (m.Msg == WM_EXITSIZEMOVE)
                {
                    if (resizing)
                    {
                        resizing = false;
                        ScheduleDashboardLayout(true);      // 松手：立刻按最终尺寸算一次
                    }
                }
            }
            catch { }
            base.WndProc(ref m);
        }

        internal void ApplyTouchFriendly(Control root)
        {
            if (root == null) return;
            try
            {
                // 触屏不是为了好看，是"手指点得中"：把行高、按钮都放大一档
                foreach (var c in AllControls(root))
                {
                    var btn = c as SmoothButton;
                    if (btn != null && btn.Height < 44) btn.Height = 44;
                    var lv = c as ListView;
                    if (lv != null) { /* 行高由 ItemHeight 控制，见下方 */ }
                }
            }
            catch { }
        }

        private static System.Collections.Generic.IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var x in AllControls(c)) yield return x;
            }
        }

        // ---- 校园卡 NFC 读头（13.56MHz）：与 UHF 设备读卡器各管一段，互不冲突 ----
        private NfcReader nfc;

        /// <summary>启动 PC/SC 读卡器轮询（用于刷校园卡 / 公交卡等 13.56MHz 卡片）</summary>
        private void StartNfcReader()
        {
            try
            {
                if (nfc != null) { nfc.Dispose(); nfc = null; }
                if (rules == null || !rules.NfcEnabled) return;

                var names = NfcReader.ListReaderNames();
                if (names.Count == 0)
                {
                    AddLogRow("未检测到校园卡读头（13.56MHz）：设备标签走 UHF 读卡器不受影响；"
                            + "要刷校园卡，插一个 USB 免驱的 NFC 读卡器即可自动启用。");
                    return;
                }

                nfc = new NfcReader();
                nfc.OnLog += msg => BeginInvoke((Action)(() => AddLogRow(msg)));
                nfc.OnCard += Nfc_OnCard;
                if (nfc.Open(rules.NfcReaderName))
                {
                    nfc.StartPoll();
                    AddLogRow("校园卡读头已启用：" + names[0] + "（共检测到 " + names.Count + " 个）");
                }
                else
                {
                    AddLogRow("校园卡读头启用失败：" + nfc.LastError);
                }
            }
            catch (Exception ex) { AddLogRow("校园卡读头异常：" + ex.Message); }
        }

        /// <summary>
        /// 刷到 NFC 卡（校园卡）：交给统一的刷卡处理逻辑。
        /// HandleTag 会先按设备标签找，找不到再按借用人卡找 —— 校园卡自然落到「借用人」分支。
        /// </summary>
        private void Nfc_OnCard(object sender, NfcEventArgs e)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        // 前缀过滤：只接受指定号段的卡，防止无关卡片误触发
                        if (rules != null && !string.IsNullOrEmpty(rules.NfcIdPrefix))
                        {
                            if (!e.CardId.StartsWith(rules.NfcIdPrefix.Trim().ToUpperInvariant(), StringComparison.Ordinal)) return;
                        }
                        AddLogRow("读到卡片：" + e.CardId);
                        HandleTag(e.CardId, e.Time);
                    }
                    catch (Exception ex) { AddLogRow("处理卡片出错：" + ex.Message); }
                }));
            }
            catch { }
        }

        /// <summary>等待几秒，用 NFC 读头取一张卡（返回卡号，超时返回 null）</summary>
        private string ReadNfcOnce(int seconds)
        {
            if (nfc == null || !nfc.Polling) return null;
            suppressTag = true;
            try
            {
                DateTime end = DateTime.Now.AddSeconds(Math.Max(3, seconds));
                string last = "";
                while (DateTime.Now < end)
                {
                    string id = nfc.TryReadOnce();
                    if (!string.IsNullOrEmpty(id) && id != last) return id;
                    last = id;
                    Application.DoEvents();
                    Thread.Sleep(150);
                }
            }
            catch { }
            finally { suppressTag = false; }
            MsgBox("这段时间没有读到卡片。请把校园卡平放在读卡器感应区上再试。");
            return null;
        }

        /// <summary>
        /// 用户管理页「读取卡号」用：UHF 设备标签和校园卡都支持。
        /// 先弹 UHF 的读取窗口；若同时插着校园卡读头，也会在校内卡放上去时一并取到。
        /// </summary>
        private string ReadOneCard()
        {
            bool hasUhf = reader != null && reader.Connected;
            bool hasNfc = nfc != null && nfc.Polling;
            if (!hasUhf && !hasNfc)
            {
                MsgBox("没有可用的读卡器：\n\n" +
                       "· 设备标签(UHF)：请先点右上角「连接读卡器」连接 UHF 读写器；\n" +
                       "· 校园卡/13.56MHz：插上 USB 免驱的 NFC 读卡器后会自动启用。\n\n" +
                       "也可以直接手动输入卡号。");
                return null;
            }
            string id = null;
            suppressTag = true;
            try
            {
                if (hasUhf)
                {
                    using (var dlg = new ReadEpcDialog(reader)) if (dlg.ShowDialog(this) == DialogResult.OK) id = dlg.Epc;
                }
                else
                {
                    using (var dlg = new ReadCardDialog(nfc)) if (dlg.ShowDialog(this) == DialogResult.OK) id = dlg.Epc;
                }
            }
            finally { suppressTag = false; }
            return id;
        }

        internal string ReadOneEpc()
        {
            if (reader == null || !reader.Connected)
            {
                // 没有 UHF 读写器时，退一步：能不能用校园卡读头？
                if (nfc != null && nfc.Polling)
                {
                    MsgBox("UHF 读写器未连接。\n\n已检测到校园卡读头，请在校园卡读卡器上刷卡…");
                    return ReadNfcOnce(20);
                }
                MsgBox("读卡器未连接，请先点击右上角「连接读卡器」连接读卡器。");
                return null;
            }
            string epc = null;
            suppressTag = true;   // 对话框期间抑制主界面刷卡处理，避免同时触发借还逻辑
            try
            {
                using (var dlg = new ReadEpcDialog(reader)) if (dlg.ShowDialog(this) == DialogResult.OK) epc = dlg.Epc;
            }
            finally { suppressTag = false; }
            return epc;
        }

        private void BtnReadEpc_Click(object sender, EventArgs e)
        {
            string epc = ReadOneEpc();
            if (string.IsNullOrEmpty(epc)) return;
            using (var dlg2 = new DeviceDialog(null, epc)) if (dlg2.ShowDialog(this) == DialogResult.OK) { MarkPagesDirty(); ReloadDevices(); }
        }

        private void BtnAddDev_Click(object sender, EventArgs e)
        {
            using (var dlg = new DeviceDialog(null, null)) if (dlg.ShowDialog(this) == DialogResult.OK) { MarkPagesDirty(); ReloadDevices(); }
        }
        private void BtnEditDev_Click(object sender, EventArgs e)
        {
            if (dgvDevices.SelectedRows.Count == 0) { MsgBox("请先选中一行"); return; }
            var cellEpc = dgvDevices.SelectedRows[0].Cells["Epc"].Value;
            var epc = cellEpc == null ? "" : cellEpc.ToString();
            var d = Db.FindDevice(epc);
            if (d == null) { MsgBox("未找到该设备"); return; }
            using (var dlg = new DeviceDialog(d, null)) if (dlg.ShowDialog(this) == DialogResult.OK) { MarkPagesDirty(); ReloadDevices(); }
        }
        private void BtnDelDev_Click(object sender, EventArgs e)
        {
            if (dgvDevices.SelectedRows.Count == 0) return;
            var cellE = dgvDevices.SelectedRows[0].Cells["Epc"].Value;
            var epc = cellE == null ? "" : cellE.ToString();
            var cellN = dgvDevices.SelectedRows[0].Cells["Name"].Value;
            var name = cellN == null ? "" : cellN.ToString();
            if (MessageBox.Show(this, "确认删除设备「" + name + "」的登记记录？", "删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                try { Db.DeleteDevice(epc); }
                catch (Exception ex) { ShowDbError(this, ex); return; }
                MarkPagesDirty();
                ReloadDevices();
            }
        }

        // ===================================================================
        //  Records Page
        // ===================================================================
        private Control MakeToolbarRecords()
        {
            var bar = new Panel
            {
                BackColor = Th.BgCard,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 10, 12, 10),
                MinimumSize = new Size(0, 56)
            };
            bar.Paint += PaintCardBorder;
            RoundRegion(bar, 8);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent
            };

            var lbl1 = new Label { Text = "从：", AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont, Margin = new Padding(0, 8, 4, 0) };
            dtFrom = new DateTimePicker { Width = 140, Format = DateTimePickerFormat.Short, Font = Th.NormFont, Margin = new Padding(0, 4, 12, 0) };
            var lbl2 = new Label { Text = "至：", AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont, Margin = new Padding(0, 8, 4, 0) };
            dtTo = new DateTimePicker { Width = 140, Format = DateTimePickerFormat.Short, Font = Th.NormFont, Margin = new Padding(0, 4, 12, 0) };
            dtFrom.Value = DateTime.Today.AddDays(-7); dtTo.Value = DateTime.Today;
            var btnQ = MakeButton("查询", Th.Accent, Point.Empty, new Size(80, 30));
            btnQ.Margin = new Padding(0, 3, 10, 0);
            btnQ.Click += (s, e) => ReloadRecords();
            var btnE = MakeButton("导出 CSV", Th.Accent2, Point.Empty, new Size(110, 30));
            btnE.Margin = new Padding(0, 3, 10, 0);
            btnE.Click += BtnExportCsv_Click;
            var btnDel = MakeButton("删除记录", Th.Danger, Point.Empty, new Size(110, 30));
            btnDel.Margin = new Padding(0, 3, 0, 0);
            btnDel.Click += BtnDeleteRecords_Click;

            flow.Controls.AddRange(new Control[] { lbl1, dtFrom, lbl2, dtTo, btnQ, btnE, btnDel });
            bar.Controls.Add(flow);
            return bar;
        }

        private void BuildRecordsPage()
        {
            dgvRecords = new DataGridView
            {
                Dock = DockStyle.Fill, BackgroundColor = Th.BgPanel, ForeColor = Th.TxtPri,
                BorderStyle = BorderStyle.None, GridColor = Th.Border, RowHeadersVisible = false,
                AllowUserToAddRows = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                Font = Th.NormFont, EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 36, RowTemplate = { Height = 32 }, AutoGenerateColumns = false
            };
            dgvRecords.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.BgPanel, ForeColor = Th.TxtSec, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
            dgvRecords.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.BgCard, ForeColor = Th.TxtPri, SelectionBackColor = Th.Accent, SelectionForeColor = Color.White, Padding = new Padding(8, 0, 0, 0) };
            dgvRecords.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.BgPanel };
            EnableDoubleBuffer(dgvRecords);
            dgvRecords.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", Name = "Id", Width = 50 });
            dgvRecords.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时间", Name = "Time", Width = 175 });
            dgvRecords.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "操作", Name = "Dir", Width = 70 });
            dgvRecords.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", Name = "State", Width = 90 });
            dgvRecords.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "设备", Name = "Device", Width = 120 });
            dgvRecords.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "借用人", Name = "Borrower", Width = 100 });
            dgvRecords.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时长/分", Name = "Duration", Width = 78 });
            dgvRecords.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "应还时间", Name = "Due", Width = 165 });
            dgvRecords.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "EPC", Name = "Epc", Width = 190 });
            dgvRecords.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "备注", Name = "Notes", Width = 170 });

            // ---- 表格美化：列宽自适应 + EPC 等宽字体 + 操作着色 ----
            foreach (DataGridViewColumn col in dgvRecords.Columns)
            {
                col.FillWeight = Math.Max(30, col.Width);
                col.MinimumWidth = 50;
            }
            dgvRecords.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvRecords.Columns["Epc"].DefaultCellStyle.Font = Th.MonoFont;
            dgvRecords.Columns["Epc"].DefaultCellStyle.ForeColor = Th.TxtSec;
            dgvRecords.CellFormatting += (s, e) =>
            {
                if (e.Value == null) return;
                var colName = dgvRecords.Columns[e.ColumnIndex].Name;
                if (colName == "Dir")
                {
                    e.CellStyle.ForeColor = e.Value.ToString() == "借出" ? Th.Success : Th.Accent2;
                    e.CellStyle.Font = Th.BoldFont;
                }
                else if (colName == "State")
                {
                    string v = e.Value.ToString();
                    if (v == "逾期未还") e.CellStyle.ForeColor = Th.Danger;
                    else if (v == "未归还") e.CellStyle.ForeColor = Th.Warn;
                    else if (v == "已归还") e.CellStyle.ForeColor = Th.Success;
                    else e.CellStyle.ForeColor = Th.TxtMuted;
                    e.CellStyle.Font = Th.BoldFont;
                }
                else if (colName == "Due")
                {
                    string v = e.Value.ToString();
                    if (v != "—" && v != "-")
                    {
                        DateTime due;
                        if (DateTime.TryParse(v, out due) && due < DateTime.Now)
                            e.CellStyle.ForeColor = Th.Danger;
                        else e.CellStyle.ForeColor = Th.TxtSec;
                    }
                }
                else if (colName == "Duration" && e.Value != null && e.Value.ToString() != "-")
                {
                    e.CellStyle.ForeColor = Th.TxtSec;
                }
            };
        }

        private void ReloadRecords()
        {
            var recs = Db.ListRecords(dtFrom.Value.Date, dtTo.Value.Date, 5000);
            dgvRecords.Rows.Clear();
            foreach (var r in recs)
            {
                string dir = r.Direction == "borrow" ? "借出" : "归还";
                // 借出记录：未再次刷卡 → 「未归还」（超期则「逾期未还」）；已有对应归还 → 「已归还」。归还行本身显示 —
                string state;
                if (r.Direction != "borrow") state = "—";
                else if (r.Returned) state = "已归还";
                else state = DueUtil.State(r.DueTime, DateTime.Now, 0) == DueUtil.StateOverdue ? "逾期未还" : "未归还";
                dgvRecords.Rows.Add(r.Id, r.EventTime, dir, state, r.DeviceName, r.Borrower,
                    r.DurationMin > 0 ? r.DurationMin.ToString("0.#") : "-",
                    string.IsNullOrEmpty(r.DueTime) ? "—" : r.DueTime, r.Epc, r.Notes);
            }
        }

        private void BtnExportCsv_Click(object sender, EventArgs e)
        {
            using (var sfd = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = "借还记录_" + DateTime.Now.ToString("yyyyMMdd") + ".csv" })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                using (var sw = new System.IO.StreamWriter(sfd.FileName, false, System.Text.Encoding.UTF8))
                {
                    sw.WriteLine("时间,操作,状态,设备,借用人,时长(分钟),应还时间,EPC,备注");
                    foreach (var r in Db.ListRecords(dtFrom.Value.Date, dtTo.Value.Date, 999999))
                    {
                        string state;
                        if (r.Direction != "borrow") state = "—";
                        else if (r.Returned) state = "已归还";
                        else state = DueUtil.State(r.DueTime, DateTime.Now, 0) == DueUtil.StateOverdue ? "逾期未还" : "未归还";
                        sw.WriteLine(string.Join(",", new[] {
                            r.EventTime, r.Direction == "borrow" ? "借出" : "归还", state,
                            r.DeviceName, r.Borrower,
                            r.DurationMin.ToString("0.#"), r.DueTime, r.Epc, r.Notes
                        }.Select(v => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"")));
                    }
                }
                MsgBox("已导出到：\n" + sfd.FileName);
            }
        }

        // 删除选中的借还记录（支持 Ctrl/Shift 多选）
        private void BtnDeleteRecords_Click(object sender, EventArgs e)
        {
            if (dgvRecords.SelectedRows.Count == 0)
            {
                MsgBox("请先选中要删除的记录（按住 Ctrl 可多选）。");
                return;
            }

            var ids = new List<int>();
            foreach (DataGridViewRow row in dgvRecords.SelectedRows)
            {
                var v = row.Cells["Id"].Value;
                int id;
                if (v != null && int.TryParse(v.ToString(), out id)) ids.Add(id);
            }
            if (ids.Count == 0) { MsgBox("选中行没有有效的记录编号。"); return; }

            string msg = ids.Count == 1
                ? "确认删除这 1 条借还记录？\n（删除后不可恢复）"
                : "确认删除选中的 " + ids.Count + " 条借还记录？\n（删除后不可恢复）";
            if (MessageBox.Show(this, msg, "删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                int n = Db.DeleteRecords(ids);
                ReloadRecords();
                UpdateStats();
                MsgBox("已删除 " + n + " 条记录。");
            }
            catch (Exception ex)
            {
                ShowDbError(this, ex);
            }
        }

        // ===================================================================
        //  Settings Page
        //  （控件全部在 SettingsPage.cs 的 MakeSettingsPanel() 中构建）
        // ===================================================================

        // ===================================================================
        //  Page switching
        // ===================================================================
        // -----------------------------------------------------------------
        //  页面数据加载调度
        // -----------------------------------------------------------------
        //  以前每次切页都会同步跑一遍"查数据库 + 重画图表/列表"，这些重活和
        //  切换动画抢主线程，所以切到「统计报表」「系统设置」会明显卡一下。
        //  现在的策略：
        //    · 某个页面第一次打开：先把数据装好（避免看到空白页）；
        //    · 之后再打开：只要数据没变就直接显示，动画结束时再按需补一次；
        //    · 借还/拦截/增删改数据后调用 MarkPagesDirty()，下次进页面才真正刷新。
        // -----------------------------------------------------------------
        private readonly bool[] pageLoaded = new bool[6];
        private readonly bool[] pageDirty = new bool[6];

        /// <summary>数据有变动：标记所有页面需要重新取数（真正的刷新延到切页时）</summary>
        private void MarkPagesDirty()
        {
            for (int i = 0; i < pageDirty.Length; i++) pageDirty[i] = true;
            auditDirty = true;      // 设置页的「刷卡审计」列表
        }

        private void LoadPageData(int idx, bool firstTime)
        {
            switch (idx)
            {
                case 0: RefreshAll(); break;
                case 1: ReloadDevices(); break;
                case 2: ReloadRecords(); break;
                case 3: ReloadUsers(); break;
                case 4: ReloadReports(); break;
                case 5: ReloadSettingsPage(firstTime); break;
            }
        }

        /// <summary>实验室名称（借条/催还消息的抬头）</summary>
        internal static string LabName = "实验室";

        // ===================================================================
        //  归还到期提醒
        //  提醒的对象是【借东西的同学本人】，不是管理员：
        //    ① 借出当场：语音 + 大字 + 纸质借条，把归还日期交到他手上；
        //    ② 他下次来刷卡：当面提醒他名下还有哪些快到期 / 已逾期；
        //    ③ 名单/催还消息：按人分组，可直接复制发到群里或私聊通知本人。
        //  管理端的顶部横幅只作辅助，用来提醒值班的人「该去催了」。
        //    ① 借出瞬间就用语音 + 大字说出归还截止时间；
        //    ② 平时定时扫描，主控台顶部出现醒目的到期横幅；
        //    ③ 点「到期名单」可以看到具体是谁借的，还能导出表格去催。
        // ===================================================================
        private Panel remindBar;
        private Label lblRemind;
        private System.Windows.Forms.Timer remindTimer;
        private DateTime lastRemindSpeak = DateTime.MinValue;
        private List<RemindItem> remindCache = new List<RemindItem>();

        private void BuildRemindBar(Control host)
        {
            var btnList = MakeButton("到期名单…", Th.Accent2, Point.Empty, new Size(120, 34));
            btnList.Dock = DockStyle.Right;
            btnList.Click += (s, e) =>
            {
                var items = remindCache.Count > 0 ? remindCache : Reminder.Scan(rules.RemindLeadHours, rules.RemindOverdue);
                if (items.Count == 0) { MsgBox("当前没有临期或逾期的设备。"); return; }
                try { using (var dlg = new RemindDialog(items)) dlg.ShowDialog(this); }
                catch (Exception ex) { MsgBox("打开名单失败：" + ex.Message); }
            };

            lblRemind = new Label
            {
                Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Th.Warn,
                Font = Th.HintFont, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 8, 0), Text = ""
            };

            remindBar = new Panel
            {
                Dock = DockStyle.Top, Height = Math.Max(48, Th.HintFont.Height + 22),
                BackColor = Th.BgCard, Visible = false, Padding = new Padding(0)
            };
            remindBar.Controls.Add(lblRemind);
            remindBar.Controls.Add(btnList);
            host.Controls.Add(remindBar);      // 后加 → 排在 dashboardLayout(Fill) 上方
            remindBar.BringToFront();
        }

        /// <summary>扫描到期情况 → 更新横幅；必要时语音播报一次</summary>
        private void CheckRemind(bool forceSpeak)
        {
            try
            {
                if (rules == null) rules = SwipeRules.Load();
                if (rules.RemindMode == 0) { if (remindBar != null) remindBar.Visible = false; return; }

                remindCache = Reminder.Scan(rules.RemindLeadHours, rules.RemindOverdue);
                if (remindCache.Count == 0)
                {
                    if (remindBar != null) remindBar.Visible = false;
                    return;
                }

                int over = 0;
                foreach (var it in remindCache) if (it.Overdue) over++;
                lblRemind.Text = "⏰ " + Reminder.Summary(remindCache);
                lblRemind.ForeColor = over > 0 ? Th.Danger : Th.Warn;
                remindBar.Visible = true;

                // 语音提醒：按设定间隔播一次，避免不停地念
                int intervalMin = rules.RemindIntervalMin > 0 ? rules.RemindIntervalMin : 60;
                if (rules.RemindMode == 2 && (forceSpeak || (DateTime.Now - lastRemindSpeak).TotalMinutes >= intervalMin))
                {
                    lastRemindSpeak = DateTime.Now;
                    int dueCnt = remindCache.Count - over;
                    string say = "请注意，";
                    if (over > 0) say += "有 " + over + " 件设备已经逾期，";
                    if (dueCnt > 0) say += "有 " + dueCnt + " 件设备即将到期，";
                    say += "请借用同学尽快归还。";
                    Reminder.Speak(say);
                }

                // 自动发邮件：把「该还了」直接送进借用同学的邮箱（同一台设备按间隔只发一次）
                SendDueMails(remindCache, forceSpeak);
            }
            catch { }
        }

        /// <summary>
        /// 借用人在场时，对【他本人】提醒：他名下还有哪些设备快到期 / 已逾期。
        /// 提醒的对象是借东西的同学，不是管理员 —— 这是最有效的一次提醒时机：
        /// 人正站在机器前，说的又正是他借走的东西。
        /// </summary>
        private void RemindBorrowerOnSpot(string borrower)
        {
            try
            {
                if (string.IsNullOrEmpty(borrower)) return;
                if (rules == null || rules.RemindMode == 0) return;

                // 用最新的在借数据扫一遍（刚借出/刚归还之后，状态已变，必须重扫）
                var all = Reminder.Scan(rules.RemindLeadHours, rules.RemindOverdue);
                var mine = Reminder.ItemsOf(all, borrower);
                if (mine.Count == 0) return;

                string say = Reminder.OnSpotPhrase(borrower, mine);
                if (string.IsNullOrEmpty(say)) return;

                int over = 0;
                foreach (var it in mine) if (it.Overdue) over++;

                // 屏幕大字：他本人看得见
                ShowHeroHintTemp("⏰ " + borrower + "同学，你还有 " + mine.Count + " 件设备要还（"
                    + (over > 0 ? "逾期 " + over + " 件" : "即将到期") + "），请留意归还时间", 15000);

                // 语音：念给本人听
                if (rules.RemindMode == 2) Reminder.Speak(say);

                AddLogRow("已对借用人本人提醒：" + say);
            }
            catch (Exception ex) { AddLogRow("借用人提醒出错：" + ex.Message); }
        }

        // ===================================================================
        //  自动邮件提醒：发到借用同学自己的邮箱
        //  借出即发确认信（写着归还日期）、到期/逾期自动发催还信。
        //  没填邮箱的人自动跳过；没配 SMTP 或没网也只是发不出去，不影响借还。
        // ===================================================================

        /// <summary>借出成功后给本人发一封「借用确认信」（含归还日期）</summary>
        private void SendBorrowMail(string borrower, Device dev, string due)
        {
            try
            {
                if (rules == null || !rules.MailEnabled || !rules.MailOnBorrow) return;
                if (!Mailer.Configured(rules) || string.IsNullOrEmpty(borrower)) return;

                string email = FindUserEmail(borrower);
                if (string.IsNullOrEmpty(email)) { AddLogRow("未发借出确认邮件：" + borrower + " 没填邮箱"); return; }

                string subject = "【设备借用确认】" + (dev != null ? dev.Name : "设备") + "　请在 "
                                 + Reminder.ShortDue(due) + " 前归还";
                string body = Mailer.BorrowMailBody(rules, borrower, dev != null ? dev.Name : "",
                                 dev != null ? dev.Model : "", Reminder.ShortDue(due), LabName);

                string err = Mailer.Send(rules, email, subject, body);
                if (err == null) AddLogRow("已发送借用确认邮件 → " + email);
                else AddLogRow("借用确认邮件发送失败：" + err);
            }
            catch (Exception ex) { AddLogRow("借用确认邮件出错：" + ex.Message); }
        }

        /// <summary>按姓名找邮箱（借还记录里只存了姓名）</summary>
        private static string FindUserEmail(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return "";
                foreach (var u in Db.ListUsers(null))
                    if (string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase))
                        return (u.Email ?? "").Trim();
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 到期 / 逾期自动发催还信。
        /// 一个人名下可能有多件，合并成一封发（不刷屏）；
        /// 同一台设备按提醒间隔只发一次，不会每小时骚扰。
        /// </summary>
        private void SendDueMails(List<RemindItem> items, bool force)
        {
            try
            {
                if (rules == null || !rules.MailEnabled || !rules.MailOnDue) return;
                if (!Mailer.Configured(rules) || items == null || items.Count == 0) return;

                int intervalMin = rules.RemindIntervalMin > 0 ? rules.RemindIntervalMin : 60;
                var now = DateTime.Now;

                // 先过滤掉「刚发过、还没到再发时间」的设备
                var todo = new List<RemindItem>();
                foreach (var it in items)
                {
                    DateTime last;
                    if (!force && DateTime.TryParse(it.MailNotifiedAt, out last)
                        && (now - last).TotalMinutes < intervalMin) continue;
                    todo.Add(it);
                }
                if (todo.Count == 0) return;

                // 按借用人合并：一人一封
                var byPerson = new Dictionary<string, List<RemindItem>>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in todo)
                {
                    List<RemindItem> bucket;
                    if (!byPerson.TryGetValue(it.Borrower, out bucket))
                    {
                        bucket = new List<RemindItem>();
                        byPerson[it.Borrower] = bucket;
                    }
                    bucket.Add(it);
                }

                int ok = 0, fail = 0;
                string stamp = now.ToString("yyyy-MM-dd HH:mm:ss");
                foreach (var kv in byPerson)
                {
                    string email = string.IsNullOrEmpty(kv.Key) ? "" : kv.Value[0].Email;
                    if (string.IsNullOrEmpty(email)) email = FindUserEmail(kv.Key);
                    if (!Mailer.IsValidEmail(email)) continue;      // 没邮箱的人跳过

                    int over = 0;
                    foreach (var it in kv.Value) if (it.Overdue) over++;
                    string subject = over > 0
                        ? "【请尽快归还】你有 " + over + " 件实验室设备已逾期"
                        : "【归还提醒】你借的实验室设备即将到期（请在 "
                          + Reminder.ShortDue(kv.Value[0].DueTime) + " 前归还）";

                    // 转成邮件模块自己的结构（邮件模块不依赖提醒模块，方便单独自测）
                    var mailItems = new List<Mailer.MailItem>();
                    foreach (var it in kv.Value)
                        mailItems.Add(new Mailer.MailItem
                        {
                            DeviceName = it.DeviceName,
                            DueTime = it.DueTime,
                            Overdue = it.Overdue
                        });

                    string err = Mailer.Send(rules, email, subject,
                        Mailer.UrgeMailBody(kv.Key, mailItems, LabName));
                    if (err == null)
                    {
                        ok++;
                        foreach (var it in kv.Value) Db.MarkMailNotified(it.Epc, stamp);
                        AddLogRow("已发送催还邮件 → " + kv.Key + "（" + email + "）");
                    }
                    else { fail++; AddLogRow("催还邮件发送失败（" + kv.Key + "）：" + err); }
                }

                if (ok > 0 || fail > 0)
                    AddLogRow("到期邮件提醒：成功 " + ok + " 封，失败 " + fail + " 封");
            }
            catch (Exception ex) { AddLogRow("到期邮件提醒出错：" + ex.Message); }
        }

        // ===================================================================
        //  借条（借出凭证）：把「什么时候还」写在纸上交给借用的人
        //  机器只能提醒站在机器前的人；一张纸条会跟着设备/同学走，
        //  是他回去之后仍然看得到归还日期的方式。
        // ===================================================================
        internal static SlipInfo LastSlip;

        internal class SlipInfo
        {
            public string Device, Borrower, Due, BorrowTime, LabName, Phone, UserNo;
        }

        /// <summary>借出成功后生成借条内容</summary>
        private SlipInfo BuildSlip(Device dev, string borrower, string due, DateTime now)
        {
            var s = new SlipInfo
            {
                Device = dev != null ? dev.Name : "",
                Borrower = borrower ?? "",
                Due = Reminder.ShortDue(due),
                BorrowTime = now.ToString("MM-dd HH:mm"),
                LabName = LabName
            };
            try
            {
                foreach (var u in Db.ListUsers(null))
                    if (string.Equals(u.Name, borrower, StringComparison.OrdinalIgnoreCase))
                    { s.Phone = u.Phone ?? ""; s.UserNo = u.UserNo ?? ""; break; }
            }
            catch { }
            return s;
        }

        /// <summary>打印借条（走默认打印机；自助机接小票打印机即可自动出纸条）</summary>
        private static void PrintSlip(SlipInfo s)
        {
            try
            {
                var lines = new System.Collections.Generic.List<string>();
                lines.Add(s.LabName + "　设备借用凭证");
                lines.Add("──────────────────");
                lines.Add("设备：" + s.Device);
                lines.Add("借用人：" + s.Borrower + (string.IsNullOrEmpty(s.UserNo) ? "" : "（" + s.UserNo + "）"));
                lines.Add("借出时间：" + s.BorrowTime);
                lines.Add("应归还时间：" + s.Due);
                lines.Add("──────────────────");
                lines.Add("请按时归还，逾期将影响后续借用。");
                if (!string.IsNullOrEmpty(s.Phone)) lines.Add("联系电话：" + s.Phone);
                lines.Add("打印时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

                var doc = new System.Drawing.Printing.PrintDocument();
                doc.PrintPage += (sender, e) =>
                {
                    float y = 10f, left = 10f;
                    using (var fTitle = new Font("Microsoft YaHei UI", 14, FontStyle.Bold))
                    using (var fBig = new Font("Microsoft YaHei UI", 12, FontStyle.Bold))
                    using (var fNorm = new Font("Microsoft YaHei UI", 10))
                    {
                        for (int i = 0; i < lines.Count; i++)
                        {
                            // 应归还时间这一行用大字，最醒目
                            var f = i == 0 ? fTitle : (lines[i].StartsWith("应归还") ? fBig : fNorm);
                            e.Graphics.DrawString(lines[i], f, Brushes.Black, left, y);
                            y += f.GetHeight(e.Graphics) + 3;
                            if (lines[i].StartsWith("应归还")) y += 3;
                        }
                    }
                    e.HasMorePages = false;
                };
                doc.Print();
            }
            catch (Exception ex)
            {
                MessageBox.Show("打印借条失败：" + ex.Message + "\n\n（没接打印机也没关系，归还日期已显示在屏幕上）",
                    "打印失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>启动定时提醒（间隔可在系统设置里改；0=只在借还时提醒，不反复打扰）</summary>
        private void StartRemindTimer()
        {
            try
            {
                if (remindTimer != null) { remindTimer.Stop(); remindTimer.Dispose(); remindTimer = null; }
                if (rules == null || rules.RemindMode == 0 || rules.RemindIntervalMin <= 0) return;
                int ms = Math.Max(60000, rules.RemindIntervalMin * 60000);
                remindTimer = new System.Windows.Forms.Timer { Interval = ms };
                remindTimer.Tick += (s, e) => CheckRemind(false);
                remindTimer.Start();
            }
            catch { }
        }

        // （试过 WS_EX_COMPOSITED 窗口级双缓冲：闪烁确实没了，但每次重排/重绘要
        //  离屏合成整棵控件树，缩放窗口耗时从 316ms 涨到 590ms，得不偿失 → 已放弃。
        //  消闪烁改用：布局节流 + SuspendLayout 合并重绘 + 关键容器 DoubleBuffered。）

        /// <summary>窗口尺寸变化时立即收掉还在跑的切页动画：动画与重排同时发生会互相打架，表现为错位/闪烁</summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (pageAnimTimer == null || pages == null) return;
            try
            {
                pageAnimTimer.Stop();
                pageAnimTimer.Dispose();
                pageAnimTimer = null;
                foreach (var p in pages)
                    if (p != null && p.Dock != DockStyle.Fill) p.Dock = DockStyle.Fill;
            }
            catch { }
        }

        internal void SwitchPage(int idx)
        {
            if (idx < 0 || idx >= pages.Length) return;
            activePage = idx;
            // 先停掉可能还在跑的上一场切换动画：连续快速点导航时，
            // 两个动画会互相覆盖 Dock/位置，把页面搅乱（闪屏的来源之一）
            if (pageAnimTimer != null)
            {
                pageAnimTimer.Stop();
                pageAnimTimer.Dispose();
                pageAnimTimer = null;
            }
            foreach (var p in pages)
            {
                p.Visible = false;
                // 上一次动画若被打断，Dock 会停在 None；这里统一恢复
                if (p.Dock != DockStyle.Fill) p.Dock = DockStyle.Fill;
            }
            var pg = pages[idx];
            pg.Visible = true;
            // 切到主控台：立即算一次行高（不等节流），避免先按旧行高画一帧
            if (idx == 0) ScheduleDashboardLayout(true);
            for (int i = 0; i < navButtons.Length; i++)
            {
                var nb = (SmoothButton)navButtons[i];
                nb.BaseColor = (i == idx) ? Th.BgCard : Th.BgPanel;
                nb.ForeColor = (i == idx) ? Th.Accent : Th.TxtSec;
                nb.Invalidate();
            }

            bool first = !pageLoaded[idx];
            if (first)
            {
                pageLoaded[idx] = true;
                LoadPageData(idx, true);   // 首次先把数据装好，避免看到空白页
                pageDirty[idx] = false;
            }

            AnimatePageIn(pg, () =>
            {
                if (!pageDirty[idx]) return;
                LoadPageData(idx, false);
                pageDirty[idx] = false;   // 取数放前面：RefreshAll 内部可能再次置脏
            });
        }

        // 页面切换：从右侧轻微滑入（ease-out）。
        // 帧数刻意压得很短（约 18 帧 / 260ms）：以前 40 帧，每移动一帧整页都要重绘一次，
        // 重页面（统计报表/系统设置）每帧绘制代价大，累积起来就是"卡一下"。
        private void AnimatePageIn(Panel pg, Action onFinished)
        {
            if (pageAnimTimer != null) { pageAnimTimer.Stop(); pageAnimTimer.Dispose(); }

            var area = contentArea.DisplayRectangle;
            pg.Dock = DockStyle.None;
            pg.Size = new Size(area.Width, area.Height);
            const int offsetX = 20;
            int targetX = area.X;
            pg.Location = new Point(targetX + offsetX, area.Y);

            int step = 0;
            pageAnimTimer = new System.Windows.Forms.Timer { Interval = 16 };
            pageAnimTimer.Tick += (s, e) =>
            {
                step++;
                // ease-out: 偏移量按比例衰减
                double k = 1.0 - Math.Pow(0.62, step);
                int x = targetX + (int)(offsetX * (1.0 - k));
                if (x <= targetX || step > 18)
                {
                    pageAnimTimer.Stop(); pageAnimTimer.Dispose(); pageAnimTimer = null;
                    pg.Dock = DockStyle.Fill;
                    // 数据刷新放在动画结束之后，避免和动画抢主线程（切换卡顿的主因）
                    try { if (onFinished != null) onFinished(); } catch { }
                }
                else pg.Location = new Point(x, area.Y);
            };
            pageAnimTimer.Start();
        }

        // ===================================================================
        //  Load / serial port
        // ===================================================================
        private void OnLoadUi()
        {
            // ---- 载入规则（距离/间隔/期限/双卡），并做一次启动自动备份 ----
            // 构造阶段已读过一次（为了应用字号），这里只读失败时重试，正常情况下不再重复读
            if (rules == null)
            {
                try { rules = SwipeRules.Load(); }
                catch (Exception ex)
                {
                    rules = new SwipeRules();
                    AddLogRow("读取设置失败，已改用默认设置：" + ex.Message);
                }
            }
            guard = new SwipeGuard();
            if (rules.AutoBackupOnStart)
            {
                try
                {
                    Db.BackupNow("auto");
                    Db.PruneAutoBackups(rules.AutoBackupKeep);
                }
                catch { /* 备份失败不影响程序启动 */ }
            }

            reader = new Reader(BuildReaderOptions());
            reader.OnLog += msg => BeginInvoke((Action)(() => AddLogRow(msg)));
            reader.OnTag += Reader_OnTag;
            reader.OnTagRejected += Reader_OnTagRejected;
            reader.OnDisconnected += Reader_OnDisconnected;

            // Try auto-open
            if (reader.Open())
            {
                SetPortUiConnected(true);
                reader.StartPoll();
            }
            else
            {
                SetPortUiConnected(false, reader.LastError);
            }

            // 连接状态呼吸灯：已连接时绿点缓慢明暗交替
            connPulseTimer = new System.Windows.Forms.Timer { Interval = 800 };
            connPulseTimer.Tick += (s, e) =>
            {
                if (reader != null && reader.Connected)
                {
                    connPulseOn = !connPulseOn;
                    lblConnDot.ForeColor = connPulseOn ? Th.Success : Color.FromArgb(110, Th.Success);
                }
            };
            connPulseTimer.Start();

            // 定时刷新：在借时长/逾期状态随时间变化，30 秒刷新一次看板
            refreshTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            refreshTimer.Tick += (s, e) => RefreshAll();
            refreshTimer.Start();

            // ---- 校园卡（13.56MHz）读头 ----
            StartNfcReader();

            // ---- 自助终端模式（刷卡机 / 一体机上无人看管地跑） ----
            ApplyKioskMode();

            // ---- 归还到期提醒 ----
            StartRemindTimer();          // 之后按设定间隔反复提醒（0=不改打扰）
            CheckRemind(true);           // 开机先来一波：有临期/逾期立刻显示在顶部

            AddLogRow(string.Format("规则已载入：功率 {0}，连续命中确认 {1} 轮，借出→归还 ≥ {2} 秒，归还→借出 ≥ {3} 秒",
                rules.RfPowerDbm <= 0 ? "不修改" : rules.RfPowerDbm + " dBm",
                rules.HitConfirmTimes, rules.BorrowToReturnSec, rules.ReturnToBorrowSec));

            // 数据库不可写（例如程序被放在桌面等低完整性目录）：给出明确提示而不是崩溃
            if (!Db.Writable) ShowDbWarn(true);

            RefreshAll();
        }

        // ===================================================================
        //  数据库只读 / 结构未升级的提示与自愈
        // ===================================================================
        /// <summary>显示顶部红色提示条（不弹模态框，避免阻塞启动；详细原因放在提示条上）</summary>
        private void ShowDbWarn(bool firstTime)
        {
            if (dbWarnBar == null) return;
            dbWarnBar.Visible = true;
            dbWarnLabel.Text = "⚠ 数据库不可写（只读模式）：新增/修改数据会失败 → 请双击程序目录里的 fix_integrity.bat，"
                             + "或把程序移到桌面以外，然后点右侧「修复后点此重试」";
            var tip = new ToolTip();
            tip.SetToolTip(dbWarnLabel, string.IsNullOrEmpty(Db.SchemaError) ? dbWarnLabel.Text : Db.SchemaError);
            tip.SetToolTip(dbWarnBar, string.IsNullOrEmpty(Db.SchemaError) ? dbWarnLabel.Text : Db.SchemaError);

            if (firstTime && !dbWarnShown)
            {
                dbWarnShown = true;
                AddLogRow("数据库只读：" + Db.SchemaError);
                AddLogRow("提示：程序仍可查看原有数据；修好目录权限后点顶部提示条上的「修复后点此重试」即可，无需重开程序。");
                PromptFixIntegrity();
            }
        }

        /// <summary>
        /// 数据库只读时弹醒目引导（顶部提示条太容易被人忽略）。
        /// 「立即修复」通过 explorer.exe 启动 fix_integrity.bat：
        /// 资源管理器是中完整性，由它拉起的批处理才能修改本目录的完整性标签
        /// （直接从本进程启动会继承低完整性，改不动）。
        /// </summary>
        private void PromptFixIntegrity()
        {
            try
            {
                string bat = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fix_integrity.bat");
                if (!File.Exists(bat)) return;
                var dr = MessageBox.Show(this,
                    "数据库当前是【只读】的：设置、音效选择、新增数据都保存不住。\n\n" +
                    "原因：程序解压在桌面，Windows 给程序打上了「低完整性」标签，\n" +
                    "导致它无权写入自己的数据库。\n\n" +
                    "点「确定」将打开一次性修复窗口（fix_integrity.bat），\n" +
                    "等黑窗口跑完后，关闭并重新打开本程序即可恢复正常。",
                    "数据库只读，需要一次性修复",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (dr == DialogResult.OK)
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + bat + "\"");
                    MessageBox.Show(this,
                        "已打开修复窗口。\n\n请等黑窗口执行完毕（出现「完成」字样），\n然后关闭本程序并重新打开。",
                        "修复已启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch { /* 引导失败不影响程序使用 */ }
        }

        private void HideDbWarn()
        {
            if (dbWarnBar != null) dbWarnBar.Visible = false;
        }

        /// <summary>写操作前调用：数据库不可写时提示并返回 false（不抛异常）</summary>
        internal bool DbReadyOrWarn()
        {
            if (Db.Writable) return true;
            if (Db.EnsureSchema())      // 用户可能刚修好权限，自动重试升级
            {
                HideDbWarn();
                AddLogRow("数据库已恢复可写，结构升级完成。");
                return true;
            }
            ShowDbWarn(false);
            return false;
        }

        /// <summary>顶部提示条上的「修复后点此重试」</summary>
        private void RetryDbUpgrade()
        {
            if (Db.EnsureSchema())
            {
                HideDbWarn();
                rules = SwipeRules.Load();
                ApplyRulesToReader();
                ReloadDevices();
                ReloadRecords();
                ReloadUsers();
                ReloadSettingsPage(true);   // 修复后连备份/审计列表一起刷新
                RefreshAll();
                MsgBox("数据库已可写，结构升级完成，各项数据已刷新。");
            }
            else
            {
                MessageBox.Show(this,
                    "数据库仍然不可写：\n" + Db.SchemaError, "重试失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>数据库写入失败时的统一友好提示（供各对话框调用）</summary>
        internal static void ShowDbError(IWin32Window owner, Exception ex)
        {
            string msg = ex == null ? "" : ex.Message;
            if (msg.IndexOf("readonly", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MessageBox.Show(owner,
                    "保存失败：数据库当前不可写（只读）。\n\n" +
                    "最常见原因：程序被放在「桌面」等带低完整性(Low)标签的目录，\n" +
                    "低完整性进程无法写入 %LOCALAPPDATA%\\LabBorrow\\ 下的数据库。\n\n" +
                    "解决办法：双击程序目录里的 fix_integrity.bat，或把整个目录移到\n" +
                    "桌面以外（如 C:\\LabBorrow），然后重试。",
                    "LabBorrow 数据库不可写", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show(owner, "保存失败：" + msg, "LabBorrow 错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>规则 → 读卡器参数</summary>
        private ReaderOptions BuildReaderOptions()
        {
            var r = rules == null ? new SwipeRules() : rules;
            return new ReaderOptions
            {
                PollIntervalMs = r.PollIntervalMs,
                HitConfirmTimes = r.HitConfirmTimes,
                TagCooldownMs = r.TagCooldownMs,
                RfPowerDbm = r.RfPowerDbm,
                MinTotalHits = r.MinTotalHits,
                MaxReadWindowMs = r.MaxReadWindowMs
            };
        }

        private void SetPortUiConnected(bool on, string error = "")
        {
            if (on)
            {
                lblConnDot.Text = "●  已连接 COM" + reader.PortIndex;
                lblConnDot.ForeColor = Th.Success;
                btnOpenPort.Text = "断开读卡器";
                lblDevInfo.Text = "地址 0x" + reader.ComAdr.ToString("X2")
                    + "　功率 " + reader.PowerText()
                    + "　" + (rules != null && rules.HitConfirmTimes > 1 ? "命中 " + rules.HitConfirmTimes + " 轮/" + rules.MinTotalHits + "次" : "命中确认 关闭");
                lblDevInfo.ForeColor = Th.TxtMuted;
            }
            else
            {
                lblConnDot.Text = "●  未连接";
                lblConnDot.ForeColor = Th.Danger;
                btnOpenPort.Text = "连接读卡器";
                lblDevInfo.Text = string.IsNullOrEmpty(error) ? "" : error;
                lblDevInfo.ForeColor = Th.Warn;
            }
            RefreshReaderPowerLabel();
        }

        private void BtnOpenPort_Click(object sender, EventArgs e)
        {
            if (reader == null) return;
            if (!reader.Connected)
            {
                bool ok;
                if (cboComPort.SelectedIndex <= 0 || cboComPort.SelectedItem.ToString() == "自动")
                {
                    ok = reader.Open();
                }
                else
                {
                    int portNum;
                    if (!int.TryParse(cboComPort.SelectedItem.ToString().Replace("COM", ""), out portNum))
                    {
                        MsgBox("无法识别选中的端口：" + cboComPort.SelectedItem + "\n请重新选择或使用「自动」。");
                        return;
                    }
                    ok = reader.Open(portNum, 5);   // baud=5 对应 57600，与该型号读卡器默认一致
                }
                if (ok)
                {
                    SetPortUiConnected(true);
                    reader.StartPoll();
                    AddLogRow("串口已连接，恢复轮询；功率 " + reader.PowerText());
                }
                else
                {
                    SetPortUiConnected(false, reader.LastError);
                    MsgBox("连接失败：" + reader.LastError + "\n\n建议：\n1. 确认读卡器电源已打开；\n2. 重新插拔 USB 线；\n3. 在右上角下拉框手动选择 COM 端口后再试。");
                }
            }
            else
            {
                reader.Close();
                SetPortUiConnected(false);
            }
        }

        // 读卡器 USB 断开 / 通信异常：更新状态栏，不崩溃、不弹未处理异常
        private void Reader_OnDisconnected()
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() =>
            {
                SetPortUiConnected(false);
                AddLogRow("读卡器连接已断开（USB 断开或通信异常），程序继续运行。请重新连接后点击「连接读卡器」。");
                var lvi = MakeEventRow(DateTime.Now, "! 断开", "读卡器", "—", "—", "-", "",
                    "USB 连接中断，系统容错未退出；请重新插拔后点「连接读卡器」恢复", Th.Danger);
                AddEvent(lvi);
            }));
        }

        // 距离未确认 / 读取不稳定：说明标签离得太远或没对准天线，只提示、不写库
        private void Reader_OnTagRejected(object sender, TagRejectEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() =>
            {
                if (suppressTag) return;
                var lvi = MakeEventRow(e.Time, "! 距离", "未确认", "—", "—", "-", e.Epc,
                    string.Format("命中 {0}/{1} 轮，判定为距离过远或读取不稳定，未产生借还记录", e.Hits, e.NeedHits),
                    Th.Warn);
                AddEvent(lvi);
            }));
        }

        // ===================================================================
        //  Tag event handler  --  核心业务：借出 / 归还
        // ===================================================================
        private void Reader_OnTag(object sender, TagEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() => HandleTag(e.Epc, e.Time)));
        }

        private void HandleTag(string epc, DateTime now)
        {
            if (suppressTag) return;   // 借出对话框打开中，忽略后续刷卡
            if (rules == null) rules = SwipeRules.Load();
            if (guard == null) guard = new SwipeGuard();

            // ================= 场景 0：借用人卡（学生卡） =================
            // 设备标签优先：已登记为设备的 EPC 一定按设备处理，避免卡号与设备号撞车。
            Device dev = Db.FindDevice(epc);
            if (dev == null)
            {
                User u = Db.FindUserByCard(epc);
                if (u != null) { OnUserCardRead(u, epc, now); return; }
            }

            // ---- 异常场景 1：陌生 / 未登记标签：友好提示，不写库，不崩溃 ----
            if (dev == null)
            {
                ShowHero(null, epc, "unknown", now, false);
                var lvi = MakeEventRow(now, "! 提示", "未登记标签", "—", "—", "-", epc,
                    "陌生标签已忽略（可在「设备管理」中登记）", Th.Danger);
                AddEvent(lvi);
                Db.AddAudit(epc, "unknown", "未登记标签，已忽略", "既不是设备标签也不是借用人卡");
                return;
            }

            string cat = dev.Category;

            // ---- 状态拦截：维修中 / 已报废 ----
            if (dev.Status == "maintenance")
            {
                ShowHero(dev, epc, "blocked", now, false);
                PlaySfx(Sfx.Kind.Blocked);
                var lvi1 = MakeEventRow(now, "! 拦截", dev.Name, "—", cat, "-", epc, "设备维修中，禁止借出", Th.Danger);
                AddEvent(lvi1);
                Db.AddAudit(epc, "status", "设备维修中，禁止借出", dev.Name);
                return;
            }
            if (dev.Status == "retired")
            {
                ShowHero(dev, epc, "blocked", now, false);
                PlaySfx(Sfx.Kind.Blocked);
                var lvi2 = MakeEventRow(now, "! 拦截", dev.Name, "—", cat, "-", epc, "设备已报废，禁止借出", Th.Danger);
                AddEvent(lvi2);
                Db.AddAudit(epc, "status", "设备已报废，禁止借出", dev.Name);
                return;
            }

            // ================= 刷卡间隔 / 防刷判定 =================
            string nextDirection = dev.Status == "borrowed" ? "return" : "borrow";
            string reason;
            SwipeVerdict verdict = guard.Check(epc, nextDirection, rules, now, out reason);
            if (verdict != SwipeVerdict.Allow)
            {
                OnSwipeBlocked(dev, epc, now, verdict, reason);
                return;
            }

            if (nextDirection == "return")
            {
                // ================= 归还流程：同一张卡第二次刷卡 → 一键归还 =================
                if (!DbReadyOrWarn()) return;   // 数据库不可写时先给提示，不写库
                // 不弹窗；借出时间缺失（脏数据）也允许归还，避免死锁
                DateTime tBorrow;
                DateTime? bt = DateTime.TryParse(dev.BorrowTime, out tBorrow) ? tBorrow : (DateTime?)null;
                try
                {
                    FinishReturn(dev, epc, now, bt, dev.CurrentBorrower, null);
                    guard.Commit(epc, "return", now);
                }
                catch (Exception ex) { ShowDbError(this, ex); return; }
            }
            else
            {
                // ================= 借出流程 =================
                if (!DbReadyOrWarn()) return;   // 数据库不可写时先给提示，不弹借出对话框
                // 借用人卡模式：已刷学生卡且在有效期内 → 直接借出，不弹窗
                string presetBorrower = null;
                string presetCard = null;
                if (activeUser != null && rules.UserCardMode)
                {
                    presetBorrower = activeUser.Name;
                    presetCard = activeUser.CardEpc;
                }

                if (presetBorrower != null)
                {
                    string t0 = now.ToString("yyyy-MM-dd HH:mm:ss");
                    try
                    {
                        DoBorrow(dev, epc, presetBorrower, presetCard, "", now, t0);
                        guard.Commit(epc, "borrow", now);
                    }
                    catch (Exception ex) { ShowDbError(this, ex); return; }
                    ClearActiveUser("已完成借出");
                }
                else
                {
                    suppressTag = true;
                    try
                    {
                        using (var dlg = new BorrowDialog(dev.Name, reader, activeUser, rules))
                        {
                            if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.BorrowerName))
                            {
                                string t = now.ToString("yyyy-MM-dd HH:mm:ss");
                                try
                                {
                                    DoBorrow(dev, epc, dlg.BorrowerName, dlg.UserCard, dlg.Note, now, t);
                                    guard.Commit(epc, "borrow", now);
                                }
                                catch (Exception ex) { ShowDbError(this, ex); }
                            }
                            else
                            {
                                // 用户取消：不写任何记录，避免脏数据
                                guard.Abort(epc);
                                var lvi = MakeEventRow(now, "已取消", dev.Name, "—", cat, "-", epc, "借出对话框被取消，未产生记录", Th.TxtMuted);
                                AddEvent(lvi);
                            }
                        }
                    }
                    finally
                    {
                        suppressTag = false;
                    }
                }
            }

            MarkPagesDirty();   // 借还完成后：其它页面下次打开时按需重新取数
            RefreshAll();
        }

        /// <summary>被间隔/防刷规则拦截：不写借还流水，但留审计痕迹</summary>
        private void OnSwipeBlocked(Device dev, string epc, DateTime now, SwipeVerdict verdict, string reason)
        {
            string kind = (verdict == SwipeVerdict.AbuseLocked) ? "abuse" : "interval";
            ShowHero(dev, epc, "blocked", now, false);
            // 异常刷卡（判定恶意刷数据被锁定）与普通间隔拦截用不同音效
            PlaySfx(verdict == SwipeVerdict.AbuseLocked ? Sfx.Kind.Abuse : Sfx.Kind.Blocked);
            var lvi = MakeEventRow(now, "!! 拦截", dev.Name, dev.CurrentBorrower, dev.Category, "-", epc,
                reason, Th.Danger);
            AddEvent(lvi);
            Db.AddAudit(epc, kind, reason, dev.Name + " / 状态 " + dev.Status);
            auditDirty = true;      // 设置页的审计列表下次打开时刷新
            RefreshReaderPowerLabel();
        }

        /// <summary>写入一条借出记录（含应还时间与借用人卡）</summary>
        private void DoBorrow(Device dev, string epc, string borrower, string userCard, string note, DateTime now, string t)
        {
            string due = DueUtil.ComputeDue(t, rules.BorrowDays);
            dev.Status = "borrowed";
            dev.CurrentBorrower = borrower;
            dev.BorrowTime = t;
            dev.DueTime = due;
            dev.UpdatedAt = t;
            Db.UpsertDevice(dev);
            Db.AddRecord(new BorrowRecord
            {
                Epc = epc,
                DeviceName = dev.Name,
                Borrower = borrower,
                Direction = "borrow",
                EventTime = t,
                DurationMin = 0,
                Notes = note,
                DueTime = due,
                UserCard = userCard
            });
            ShowHero(dev, epc, "borrow", now, true);
            PlaySfx(Sfx.Kind.Borrow);

            // 屏幕上也用大字写明归还截止时间（语音可能没听清/当场人多）
            if (!string.IsNullOrEmpty(due))
                ShowHeroHintTemp("✓ 借出成功　请在 " + Reminder.ShortDue(due) + " 前归还", 12000);

            // 提醒效果最好的时机就是「人正站在机器前」的这一刻：
            // 直接把归还截止时间念出来，并在屏幕上用大字显示，避免了他根本不知道几时要还。
            if (rules != null && rules.RemindMode == 2 && !string.IsNullOrEmpty(due))
            {
                string when = Reminder.SpeakDue(due);
                string who = string.IsNullOrEmpty(borrower) ? "" : borrower + "同学，";
                Reminder.Speak(who + "借用" + dev.Name + "成功，请在" + when + "前归还。");
            }

            // 借条：把归还日期印在纸上交给他本人带走（自助机接小票打印机时会自动出纸条）
            LastSlip = BuildSlip(dev, borrower, due, now);
            if (rules != null && rules.PrintSlipOnBorrow) PrintSlip(LastSlip);

            // 借出确认邮件：直接发到他邮箱，等于一张不会丢的电子借条
            SendBorrowMail(borrower, dev, due);

            CheckRemind(false);      // 借出后刷新顶部到期横幅
            RemindBorrowerOnSpot(borrower);   // 顺便提醒他本人：名下还有哪些快到期

            string tip = note;
            if (!string.IsNullOrEmpty(due)) tip = (string.IsNullOrEmpty(tip) ? "" : tip + "　") + "应还 " + due;
            if (string.IsNullOrEmpty(tip)) tip = "-";
            var lvi = MakeEventRow(now, "▶ 借出", dev.Name, borrower, dev.Category, "-", epc, tip, Th.Success);
            AddEvent(lvi);
        }

        private void FinishReturn(Device dev, string epc, DateTime now, DateTime? tBorrow, string borrower, string userCard)
        {
            double duration = 0;
            if (tBorrow.HasValue) duration = Math.Round((now - tBorrow.Value).TotalMinutes, 1);

            // 逾期判定：应还时间早于当前即为逾期
            bool overdue = DueUtil.State(dev.DueTime, now, 0) == DueUtil.StateOverdue;
            double overdueHours = 0;
            DateTime due;
            if (overdue && DateTime.TryParse(dev.DueTime, out due))
                overdueHours = Math.Round((now - due).TotalHours, 1);

            string t = now.ToString("yyyy-MM-dd HH:mm:ss");
            string dueTime = dev.DueTime;

            dev.Status = "idle";
            dev.CurrentBorrower = "";
            dev.BorrowTime = "";
            dev.DueTime = "";
            dev.UpdatedAt = t;
            Db.UpsertDevice(dev);

            string notes = "借用时长 " + duration + " 分钟";
            if (overdue) notes += "　|　逾期归还，超期 " + DueUtil.HumanSpan(TimeSpan.FromHours(overdueHours));

            Db.AddRecord(new BorrowRecord
            {
                Epc = epc,
                DeviceName = dev.Name,
                Borrower = borrower,
                Direction = "return",
                EventTime = t,
                DurationMin = duration,
                Notes = notes,
                DueTime = dueTime,
                UserCard = userCard,
                Overdue = overdue
            });

            ShowHero(dev, epc, "return", now, true);
            PlaySfx(Sfx.Kind.Return);

            // 人就在机器前：顺便提醒他名下还有哪些快到期 / 已逾期的（对本人说，不是对管理员说）
            RemindBorrowerOnSpot(borrower);

            var lvi = MakeEventRow(now, "◀ 归还", dev.Name, borrower, dev.Category,
                duration > 0 ? duration.ToString("0.#") : "-", epc, notes, overdue ? Th.Warn : Th.Accent2);
            AddEvent(lvi);
            if (overdue)
                Db.AddAudit(epc, "other", "逾期归还：" + notes, dev.Name + " / 借用人 " + borrower);
            UpdateStats();
        }

        private void ShowHero(Device dev, string epc, string action, DateTime now, bool ok)
        {
            ShowHeroHintTemp(HintTextForAction(action, ok), 12000);
            heroName.ForeColor = ok ? Th.TxtPri : Th.Danger;
            if (dev == null)
            {
                heroName.Text = "未登记标签";
                heroModel.Text = "型号：—";
                heroCategory.Text = "分类：—";
                heroBorrower.Text = "借用人：—";
            }
            else
            {
                heroName.Text = dev.Name;
                heroModel.Text = "型号：" + (string.IsNullOrEmpty(dev.Model) ? "—" : dev.Model);
                heroCategory.Text = "分类：" + (string.IsNullOrEmpty(dev.Category) ? "—" : dev.Category) + "    状态：" + StatusLabel(dev.Status);
                heroBorrower.Text = "借用人：" + (string.IsNullOrEmpty(dev.CurrentBorrower) ? "—" : dev.CurrentBorrower);
                if (!string.IsNullOrEmpty(dev.DueTime))
                    heroBorrower.Text += "    应还：" + dev.DueTime;
            }
            heroEpc.Text = "EPC：" + epc;
            switch (action)
            {
                case "borrow":
                    heroTime.Text = "▶ 借出 " + now.ToString("HH:mm:ss");
                    heroTime.ForeColor = Th.Success;
                    break;
                case "return":
                    heroTime.Text = "◀ 归还 " + now.ToString("HH:mm:ss");
                    heroTime.ForeColor = Th.Accent2;
                    break;
                case "blocked":
                    heroTime.Text = "✖ 操作被拦截 " + now.ToString("HH:mm:ss");
                    heroTime.ForeColor = Th.Danger;
                    break;
                case "unknown":
                    heroTime.Text = "? 陌生标签 " + now.ToString("HH:mm:ss");
                    heroTime.ForeColor = Th.Warn;
                    break;
            }
        }

        /// <summary>主卡片第二行的操作反馈文案（不隐藏该行，只换文字）</summary>
        private static string HintTextForAction(string action, bool ok)
        {
            switch (action)
            {
                case "borrow": return "✓ 借出成功";
                case "return": return "✓ 归还成功";
                case "blocked": return "✖ 本次刷卡已被拦截，设备状态未改动";
                case "unknown": return "? 该标签未登记，请先登记设备";
                default: return ok ? "已处理" : "未能处理";
            }
        }

        private ListViewItem MakeEventRow(DateTime now, string dir, string name, string borrower, string category,
            string duration, string epc, string notes, Color color)
        {
            var lvi = new ListViewItem(new[] {
                now.ToString("HH:mm:ss"), dir, name, borrower, category, duration, epc, notes
            });
            lvi.ForeColor = color;
            lvi.UseItemStyleForSubItems = true;
            return lvi;
        }

        // 统一入口：插入事件行并裁剪列表（所有路径都保证不超上限）
        private void AddEvent(ListViewItem lvi)
        {
            eventList.Items.Insert(0, lvi);
            while (eventList.Items.Count > 200) eventList.Items.RemoveAt(eventList.Items.Count - 1);
        }

        private void AddLogRow(string msg)
        {
            var lvi = MakeEventRow(DateTime.Now, "[读卡器]", "LOG", "—", "—", "-", "", msg, Th.TxtMuted);
            AddEvent(lvi);
        }

        private void UpdateStats()
        {
            try
            {
                var s = Db.GetTodayStats();
                statBorrowV.Text = s.Borrows.ToString();
                statReturnV.Text = s.Returns.ToString();
                statOutV.Text = s.OutNow.ToString();
                statTotalV.Text = s.TotalDevices.ToString();
                lentCountLbl.Text = s.OutNow + " 件" + (s.OverdueNow > 0 ? "　逾期 " + s.OverdueNow : "");
                lentCountLbl.ForeColor = s.OverdueNow > 0 ? Th.Danger : (s.OutNow > 0 ? Th.Warn : Th.Success);
            }
            catch { }
        }

        /// <summary>看板整体刷新：统计数字 + 在借明细</summary>
        internal void RefreshAll()
        {
            UpdateStats();
            ReloadLent();
        }

        /// <summary>在借明细：设备 / 借用人 / 已借时长 / 期限状态（逾期红、临期黄）</summary>
        private void ReloadLent()
        {
            if (lentList == null) return;
            List<Db.LentItem> items;
            try { items = Db.ListLent(rules == null ? 24 : rules.DueWarnHours); }
            catch { return; }

            lentList.BeginUpdate();
            try
            {
                lentList.Items.Clear();
                var now = DateTime.Now;
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    DateTime dueAt;
                    bool hasDue = DateTime.TryParse(it.DueTime, out dueAt);
                    string state;
                    if (it.State == DueUtil.StateOverdue)
                        state = "逾期 " + DueUtil.CompactSpan(TimeSpan.FromHours(it.OverdueHours));
                    else if (!hasDue)
                        state = "未设期限";
                    else if (it.State == DueUtil.StateDueSoon)
                        state = "临期 " + DueUtil.CompactSpan(dueAt - now);
                    else
                        state = "剩 " + DueUtil.CompactSpan(dueAt - now);

                    var lvi = new ListViewItem(new[]
                    {
                        string.IsNullOrEmpty(it.Name) ? it.Epc : it.Name,
                        string.IsNullOrEmpty(it.Borrower) ? "—" : it.Borrower,
                        DueUtil.CompactSpan(TimeSpan.FromHours(it.BorrowedHours)),
                        state
                    });
                    lvi.ForeColor = it.State == DueUtil.StateOverdue ? Th.Danger
                                  : (it.State == DueUtil.StateDueSoon ? Th.Warn : Th.TxtPri);
                    lvi.UseItemStyleForSubItems = true;
                    lvi.ToolTipText = "EPC：" + it.Epc
                        + (string.IsNullOrEmpty(it.Location) ? "" : "　位置：" + it.Location)
                        + (string.IsNullOrEmpty(it.BorrowTime) ? "" : "　借出：" + it.BorrowTime)
                        + (string.IsNullOrEmpty(it.DueTime) ? "" : "　应还：" + it.DueTime);
                    lentList.Items.Add(lvi);
                }
            }
            finally { lentList.EndUpdate(); }
        }

        // ===================================================================
        //  借用人卡（学生卡）
        // ===================================================================
        private void OnUserCardRead(User u, string epc, DateTime now)
        {
            activeUser = u;
            if (rules != null && rules.UserCardWaitSec > 0)
            {
                if (userCardTimer == null)
                {
                    userCardTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                    userCardTimer.Tick += (s, e) => ClearActiveUser(null);
                }
                userCardTimer.Stop();
                userCardTimer.Interval = Math.Max(1, rules.UserCardWaitSec) * 1000;
                userCardTimer.Start();
            }

            ShowHeroHintTemp("已识别借用人：" + u.Name + "，请刷要借的设备标签", 12000);
            heroName.Text = u.Name + "（" + u.RoleLabel + "）";
            heroName.ForeColor = Th.Accent;
            heroModel.Text = "学号/工号：" + (string.IsNullOrEmpty(u.UserNo) ? "—" : u.UserNo);
            heroCategory.Text = "班级/部门：" + (string.IsNullOrEmpty(u.ClassName) ? "—" : u.ClassName);
            heroBorrower.Text = rules != null && rules.UserCardMode
                ? "请在 " + rules.UserCardWaitSec + " 秒内刷要借的设备标签"
                : "已识别借用人，刷设备标签时会自动填入";
            heroEpc.Text = "卡号：" + epc;
            heroTime.Text = "[卡] 借用人卡 " + now.ToString("HH:mm:ss");
            heroTime.ForeColor = Th.Accent;

            var lvi = MakeEventRow(now, "[卡] 借用人", u.Name, u.UserNo, u.ClassName, "-", epc,
                rules != null && rules.UserCardMode
                    ? "双卡模式：等待刷设备标签（" + rules.UserCardWaitSec + " 秒内有效）"
                    : "已识别借用人，借出时会自动填入", Th.Accent);
            AddEvent(lvi);
        }

        // -----------------------------------------------------------------
        //  heroHint 提示语：刷卡后临时换成操作反馈，超时自动恢复默认文案
        // -----------------------------------------------------------------
        //  为什么不再隐藏整行：标签 Visible 一变，卡片里下面几行会整体上移/下移，
        //  连续刷卡时整个主控台看起来像在「跳」，这正是页面显得乱的原因之一。
        //  改成只换文字，行高恒定，不再有位移。
        // -----------------------------------------------------------------
        private const string DefaultHeroHint = "请将设备标签靠近读写器";

        private void ShowHeroHintTemp(string text, int ms)
        {
            if (heroHint == null) return;
            heroHint.Text = string.IsNullOrEmpty(text) ? DefaultHeroHint : text;
            if (hintTimer == null)
            {
                hintTimer = new System.Windows.Forms.Timer();
                hintTimer.Tick += (s, e) =>
                {
                    hintTimer.Stop();
                    if (heroHint != null) heroHint.Text = DefaultHeroHint;
                };
            }
            hintTimer.Stop();
            hintTimer.Interval = Math.Max(2000, ms);
            hintTimer.Start();
        }

        private void ClearActiveUser(string why)
        {
            if (userCardTimer != null) userCardTimer.Stop();
            if (activeUser == null) return;
            if (!string.IsNullOrEmpty(why))
            {
                var lvi = MakeEventRow(DateTime.Now, "[卡] 借用人", activeUser.Name, "—", "—", "-", "",
                    "借用人卡状态已清除（" + why + "）", Th.TxtMuted);
                AddEvent(lvi);
            }
            activeUser = null;
        }

        // ===================================================================
        //  Helpers
        // ===================================================================
        private Button MakeButton(string text, Color bg, Point loc, Size sz)
        {
            return new SmoothButton
            {
                Text = text, Size = sz, Location = loc, BaseColor = bg,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
        }

        private void MsgBox(string m)
        {
            MessageBox.Show(this, m, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 程序图标：程序化绘制（渐变圆角底 + "LB" 字样），无需外部资源文件
        private static Icon CreateAppIcon()
        {
            try
            {
                using (var bmp = new Bitmap(32, 32))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        var rect = new Rectangle(1, 1, 30, 30);
                        using (var path = RoundedPath(rect, 7))
                        using (var brush = new LinearGradientBrush(rect, Th.Accent, Th.Accent2, 45f))
                            g.FillPath(brush, path);
                        using (var f = new Font("Microsoft YaHei UI", 13, FontStyle.Bold))
                            TextRenderer.DrawText(g, "LB", f, rect, Color.White,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch { return null; }
        }
    }

    // =======================================================================
    //  SmoothButton  --  圆角按钮：悬浮颜色平滑过渡 + 按压下沉效果
    // =======================================================================
    internal class SmoothButton : Button
    {
        public Color BaseColor { get; set; }
        public int Radius { get; set; }

        private float hoverT;                       // 0..1 悬浮进度，用于平滑动画
        private bool hovering, pressing;
        private readonly System.Windows.Forms.Timer anim;

        public SmoothButton()
        {
            Radius = 6;
            BaseColor = Color.FromArgb(0, 184, 148);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            anim = new System.Windows.Forms.Timer { Interval = 15 };
            anim.Tick += (s, e) =>
            {
                float target = hovering ? 1f : 0f;
                const float speed = 0.2f;
                if (Math.Abs(target - hoverT) <= speed) hoverT = target;
                else hoverT += (target > hoverT) ? speed : -speed;
                Invalidate();
                if (hoverT == target) anim.Stop();
            };
            MouseEnter += (s, e) => { hovering = true; anim.Start(); };
            MouseLeave += (s, e) => { hovering = false; anim.Start(); };
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            pressing = true; Invalidate();
            base.OnMouseDown(mevent);
        }
        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            pressing = false; Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width < 2 || rect.Height < 2) return;
            using (var path = MainForm.RoundedPath(rect, Radius))
            {
                Color c = Lerp(BaseColor, ControlPaint.Light(BaseColor, 0.3f), hoverT);
                if (pressing) c = ControlPaint.Dark(BaseColor, 0.1f);
                using (var b = new SolidBrush(c)) g.FillPath(b, path);
                using (var p = new Pen(ControlPaint.Dark(c, 0.05f), 1f)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, rect, ForeColor, TextFlags());
        }

        private static Color Lerp(Color a, Color b, float t)
        {
            return Color.FromArgb(
                a.R + (int)((b.R - a.R) * t),
                a.G + (int)((b.G - a.G) * t),
                a.B + (int)((b.B - a.B) * t));
        }

        // 按 TextAlign 生成文本绘制参数（支持左/中/右对齐，用于导航栏）
        private TextFormatFlags TextFlags()
        {
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            var ta = TextAlign;
            if ((ta & ContentAlignment.MiddleRight) != 0) flags |= TextFormatFlags.Right;
            else if ((ta & ContentAlignment.MiddleLeft) != 0) flags |= TextFormatFlags.Left;
            else flags |= TextFormatFlags.HorizontalCenter;
            return flags;
        }
    }

    // =======================================================================
    //  Device dialog  --  设备登记 / 编辑
    // =======================================================================
    internal class DeviceDialog : Form
    {
        public Device Result;
        TextBox txtEpc, txtName, txtModel, txtLocation, txtNotes;
        ComboBox cboCategory, cboStatus;
        Label lblBorrowInfo;
        readonly Device editing;

        public DeviceDialog(Device d, string presetEpc)
        {
            editing = d;
            Text = d == null ? "新增设备" : "编辑设备";
            Size = new Size(480, 500);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = MainForm.Th.BgPanel;
            ForeColor = MainForm.Th.TxtPri;

            int y = 20;
            AddLabel("EPC 标签：", y); txtEpc = AddInput(y); txtEpc.Text = d != null ? d.Epc : (presetEpc ?? "");
            if (d != null) txtEpc.ReadOnly = true;
            y += 36; AddLabel("设备名称：", y); txtName = AddInput(y); if (d != null) txtName.Text = d.Name;
            y += 36; AddLabel("型号：", y); txtModel = AddInput(y); if (d != null) txtModel.Text = d.Model;
            y += 36; AddLabel("分类：", y);
            cboCategory = new ComboBox { Location = new Point(120, y), Width = 290, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = MainForm.Th.BgInput, ForeColor = MainForm.Th.TxtPri };
            cboCategory.Items.AddRange(new object[] { "仪器仪表", "电动工具", "计算与存储", "实验耗材", "其他" });
            cboCategory.SelectedItem = (d != null && !string.IsNullOrEmpty(d.Category) && cboCategory.Items.Contains(d.Category)) ? (object)d.Category : (object)"仪器仪表";
            Controls.Add(cboCategory);
            y += 36; AddLabel("存放位置：", y); txtLocation = AddInput(y); if (d != null) txtLocation.Text = d.Location;
            y += 36; AddLabel("状态：", y);
            cboStatus = new ComboBox { Location = new Point(120, y), Width = 290, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = MainForm.Th.BgInput, ForeColor = MainForm.Th.TxtPri };
            cboStatus.Items.AddRange(new object[] { "在库", "外借中", "维修中", "已报废" });
            cboStatus.SelectedItem = d == null ? "在库" : StatusLabelOf(d.Status);
            Controls.Add(cboStatus);
            y += 36;
            lblBorrowInfo = new Label
            {
                Location = new Point(120, y), AutoSize = true, ForeColor = MainForm.Th.TxtMuted,
                Text = (d != null && d.Status == "borrowed")
                    ? "当前借用人：" + (d.CurrentBorrower ?? "") + "   借出时间：" + (d.BorrowTime ?? "")
                      + (string.IsNullOrEmpty(d.DueTime) ? "" : "   应还：" + d.DueTime)
                    : "（外借状态由刷卡流程自动维护，一般无需手工修改）"
            };
            Controls.Add(lblBorrowInfo);
            y += 34; AddLabel("备注：", y); txtNotes = AddInput(y, 290, 2); if (d != null) txtNotes.Text = d.Notes;

            var btnOk = new SmoothButton { Text = "保存", Location = new Point(120, 400), Size = new Size(120, 36), BaseColor = MainForm.Th.Accent, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) };
            btnOk.Click += BtnOk_Click;
            var btnCancel = new SmoothButton { Text = "取消", Location = new Point(250, 400), Size = new Size(120, 36), BaseColor = MainForm.Th.BgCard, ForeColor = MainForm.Th.TxtPri, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { btnOk, btnCancel });
            AcceptButton = btnOk;      // Enter 保存
            CancelButton = btnCancel;  // Esc 取消
        }

        // 静态版本：供对话框使用（数据库状态码 -> 中文标签）
        private static string StatusLabelOf(string s)
        {
            if (s == "idle") return "在库";
            if (s == "borrowed") return "外借中";
            if (s == "maintenance") return "维修中";
            if (s == "retired") return "已报废";
            return string.IsNullOrEmpty(s) ? "在库" : s;
        }

        private void AddLabel(string t, int y)
        {
            var lbl = new Label { Text = t, Location = new Point(20, y + 3), AutoSize = true, ForeColor = MainForm.Th.TxtSec };
            Controls.Add(lbl);
        }
        private TextBox AddInput(int y, int w = 290, int h = 1)
        {
            var tb = new TextBox { Location = new Point(120, y), Width = w, BackColor = MainForm.Th.BgInput, ForeColor = MainForm.Th.TxtPri, BorderStyle = BorderStyle.FixedSingle };
            if (h > 1) { tb.Multiline = true; tb.Height = 22 * h; }
            Controls.Add(tb);
            return tb;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtEpc.Text)) { MessageBox.Show(this, "EPC 不能为空（可点击「读取标签 EPC」自动获取）"); return; }
            if (string.IsNullOrWhiteSpace(txtName.Text)) { MessageBox.Show(this, "设备名称不能为空"); return; }
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            bool keepBorrowed = editing != null && cboStatus.SelectedItem != null && cboStatus.SelectedItem.ToString() == "外借中";
            Result = new Device
            {
                Epc = txtEpc.Text.Trim().ToUpperInvariant(),
                Name = txtName.Text.Trim(),
                Model = txtModel.Text.Trim(),
                Category = cboCategory.SelectedItem == null ? "其他" : cboCategory.SelectedItem.ToString(),
                Location = txtLocation.Text.Trim(),
                Status = MainForm.StatusCode(cboStatus.SelectedItem == null ? "在库" : cboStatus.SelectedItem.ToString()),
                CurrentBorrower = keepBorrowed ? editing.CurrentBorrower : "",
                BorrowTime = keepBorrowed ? editing.BorrowTime : "",
                DueTime = keepBorrowed ? editing.DueTime : "",
                Notes = txtNotes.Text.Trim(),
                CreatedAt = editing != null ? editing.CreatedAt : now,
                UpdatedAt = now,
            };
            try { Db.UpsertDevice(Result); }
            catch (Exception ex) { MainForm.ShowDbError(this, ex); return; }
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // =======================================================================
    //  Borrow dialog  --  借出登记：选择/输入借用人（可直接刷借用人卡）
    // =======================================================================
    internal class BorrowDialog : Form
    {
        public string BorrowerName;
        public string Note;
        public string UserCard;                 // 刷到的借用人卡 EPC
        ComboBox cboBorrower;
        TextBox txtNote;
        Label lblCard;
        System.Windows.Forms.Timer timeout;
        readonly Reader reader;
        readonly SwipeRules rules;
        int remainSec;
        System.Windows.Forms.Timer waitTimer;

        public BorrowDialog(string deviceName, Reader reader, User activeUser, SwipeRules rules)
        {
            this.reader = reader;
            this.rules = rules == null ? new SwipeRules() : rules;

            Text = "借出登记";
            Size = new Size(460, 300);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = MainForm.Th.BgPanel;
            ForeColor = MainForm.Th.TxtPri;

            var lblT = new Label { Text = "借出设备：" + deviceName, Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold), ForeColor = MainForm.Th.Accent, Location = new Point(20, 18), AutoSize = true };
            Controls.Add(lblT);

            var lbl1 = new Label { Text = "借用人：", ForeColor = MainForm.Th.TxtSec, Location = new Point(20, 70), AutoSize = true };
            Controls.Add(lbl1);
            cboBorrower = new ComboBox
            {
                Location = new Point(100, 66), Width = 310,
                BackColor = MainForm.Th.BgInput, ForeColor = MainForm.Th.TxtPri,
                FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDown
            };
            foreach (var b in Db.ListBorrowers()) cboBorrower.Items.Add(b);
            cboBorrower.AutoCompleteSource = AutoCompleteSource.ListItems;
            cboBorrower.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            Controls.Add(cboBorrower);

            // 借用人卡提示行：可边开着对话框边刷学生卡
            lblCard = new Label
            {
                Text = "借用人卡：可直接把学生卡放到读卡器上自动填入",
                ForeColor = MainForm.Th.TxtMuted, Location = new Point(20, 96), AutoSize = true,
                MaximumSize = new Size(400, 0)
            };
            Controls.Add(lblCard);

            // 应归还时间：借的人当场就要知道自己什么时候该还，所以单独放一行大字
            string dueTxt = Reminder.ShortDue(DueUtil.ComputeDue(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), this.rules.BorrowDays));
            var lblDue = new Label
            {
                Text = this.rules.BorrowDays > 0
                    ? "本次应归还：" + dueTxt + "（借用期限 " + this.rules.BorrowDays + " 天，请按时归还）"
                    : "本次应归还：" + dueTxt + "（请按时归还）",
                ForeColor = MainForm.Th.Warn, Font = MainForm.Th.BoldFont,
                Location = new Point(20, 118), AutoSize = true, MaximumSize = new Size(410, 0)
            };
            Controls.Add(lblDue);

            var lbl2 = new Label { Text = "备注：", ForeColor = MainForm.Th.TxtSec, Location = new Point(20, 146), AutoSize = true };
            Controls.Add(lbl2);
            txtNote = new TextBox { Location = new Point(100, 142), Width = 310, BackColor = MainForm.Th.BgInput, ForeColor = MainForm.Th.TxtPri, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(txtNote);

            var lblTip = new Label
            {
                Text = "提示：可输入姓名或学号；历史借用人会自动补全。取消则不产生任何记录。\n"
                     + "归还日期会同时显示在屏幕上，并可打印成借条交给借用同学。",
                ForeColor = MainForm.Th.TxtMuted, Location = new Point(20, 174), AutoSize = true, MaximumSize = new Size(410, 0)
            };
            Controls.Add(lblTip);

            var btnOk = new SmoothButton { Text = "确认借出", Location = new Point(100, 206), Size = new Size(120, 36), BaseColor = MainForm.Th.Accent, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) };
            btnOk.Click += BtnOk_Click;
            var btnCancel = new SmoothButton { Text = "取消", Location = new Point(240, 206), Size = new Size(120, 36), BaseColor = MainForm.Th.BgCard, ForeColor = MainForm.Th.TxtPri, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { btnOk, btnCancel });
            AcceptButton = btnOk;      // Enter 确认借出
            CancelButton = btnCancel;  // Esc 取消
            ActiveControl = cboBorrower;   // 打开后直接聚焦借用人输入框

            // 已识别的借用人（先刷了学生卡）→ 直接预填
            if (activeUser != null)
            {
                cboBorrower.Text = activeUser.Name;
                UserCard = activeUser.CardEpc;
                ShowCard(activeUser);
                cboBorrower.SelectAll();
            }

            // 对话框打开期间监听刷卡：刷到借用人卡就自动填入
            if (reader != null) reader.OnTag += Reader_OnTag;
            FormClosed += (s, e) =>
            {
                if (reader != null) reader.OnTag -= Reader_OnTag;
                if (waitTimer != null) { waitTimer.Stop(); waitTimer.Dispose(); }
            };

            // 60 秒无操作自动取消，避免对话框长期挂起阻塞后续刷卡
            timeout = new System.Windows.Forms.Timer { Interval = 60000 };
            timeout.Tick += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            timeout.Start();

            // 双卡模式：给出等待读卡的倒计时，让学生知道要刷自己的卡
            if (rules.UserCardMode)
            {
                remainSec = Math.Max(1, rules.UserCardWaitSec);
                waitTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                waitTimer.Tick += (s, e) =>
                {
                    remainSec--;
                    if (remainSec <= 0)
                    {
                        waitTimer.Stop();
                        lblCard.Text = "借用人卡：等待超时，请手工输入姓名或重新刷卡。";
                    }
                    else if (string.IsNullOrEmpty(UserCard))
                        lblCard.Text = "借用人卡：请刷学生卡（" + remainSec + " 秒）";
                };
                lblCard.Text = "借用人卡：请刷学生卡（" + remainSec + " 秒）";
                waitTimer.Start();
            }
        }

        private void ShowCard(User u)
        {
            lblCard.Text = string.Format("借用人卡：已识别 {0}{1}{2}　（卡号 {3}）",
                u.Name,
                string.IsNullOrEmpty(u.UserNo) ? "" : " / " + u.UserNo,
                string.IsNullOrEmpty(u.ClassName) ? "" : " / " + u.ClassName,
                string.IsNullOrEmpty(u.CardEpc) ? "—" : u.CardEpc);
            lblCard.ForeColor = MainForm.Th.Accent;
        }

        private void Reader_OnTag(object sender, TagEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            User u;
            try { u = Db.FindUserByCard(e.Epc); }
            catch { return; }
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                if (u != null)
                {
                    cboBorrower.Text = u.Name;
                    UserCard = u.CardEpc;
                    ShowCard(u);
                }
                else
                {
                    lblCard.Text = "借用人卡：标签 " + e.Epc + " 未登记为学生卡，可在「用户管理」中登记";
                    lblCard.ForeColor = MainForm.Th.Warn;
                }
            }));
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(cboBorrower.Text)) { MessageBox.Show(this, "请输入借用人姓名"); return; }
            BorrowerName = cboBorrower.Text.Trim();

            // 手工输入的姓名若与已登记的借用人同名，自动补上卡号，便于统计
            if (string.IsNullOrEmpty(UserCard))
            {
                try
                {
                    foreach (var u in Db.ListUsers(null))
                        if (string.Equals(u.Name, BorrowerName, StringComparison.OrdinalIgnoreCase))
                        { UserCard = u.CardEpc; break; }
                }
                catch { }
            }

            Note = txtNote.Text.Trim();
            timeout.Stop();
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // =======================================================================
    //  ReadEpcDialog  --  等待读取一张标签的 EPC（用于设备登记）
    // =======================================================================
    internal class ReadEpcDialog : Form
    {
        public string Epc;
        readonly Reader reader;
        Label lblStatus;
        System.Windows.Forms.Timer timeout;

        public ReadEpcDialog(Reader r)
        {
            reader = r;
            Text = "读取标签 EPC";
            Size = new Size(420, 190);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = MainForm.Th.BgPanel;
            ForeColor = MainForm.Th.TxtPri;

            bool linked = reader != null;
            lblStatus = new Label
            {
                Text = linked
                    ? "请把标签贴近读卡器并停留约 1 秒…\n（需连续读到若干轮才算有效，15 秒超时）"
                    : "读卡器未连接，无法读取标签。\n请先点右上角「连接读卡器」后重试。",
                Font = new Font("Microsoft YaHei UI", 11),
                ForeColor = linked ? MainForm.Th.TxtSec : MainForm.Th.Warn,
                Location = new Point(24, 24), AutoSize = true, MaximumSize = new Size(370, 0)
            };
            Controls.Add(lblStatus);

            var btnCancel = new SmoothButton { Text = "关闭", Location = new Point(150, 110), Size = new Size(110, 34), BaseColor = MainForm.Th.BgCard, ForeColor = MainForm.Th.TxtPri, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnCancel);
            CancelButton = btnCancel;  // Esc 取消

            if (linked)
            {
                reader.OnTag += Reader_OnTag;
                FormClosed += (s, e) => reader.OnTag -= Reader_OnTag;
            }

            timeout = new System.Windows.Forms.Timer { Interval = 15000 };
            timeout.Tick += (s, e) => { timeout.Stop(); DialogResult = DialogResult.Cancel; Close(); };
            if (linked) timeout.Start();
        }

        private void Reader_OnTag(object sender, TagEventArgs e)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)(() =>
            {
                Epc = e.Epc;
                timeout.Stop();
                DialogResult = DialogResult.OK;
                Close();
            }));
        }
    }

    // ===================================================================
    //  读取校园卡（13.56MHz NFC）：把卡放到 USB 读卡器上，取到卡号即返回
    //  用在「用户管理 → 读取卡号」，把校园卡绑定到某个同学名下。
    // ===================================================================
    internal class ReadCardDialog : Form
    {
        public string Epc;
        private readonly NfcReader nfc;
        private readonly System.Windows.Forms.Timer poll;
        private readonly System.Windows.Forms.Timer timeout;
        private readonly Label lblStatus;
        private string lastId = "";

        public ReadCardDialog(NfcReader reader)
        {
            nfc = reader;
            Text = "读取校园卡卡号";
            Size = new Size(460, 210);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = MainForm.Th.BgPanel;
            ForeColor = MainForm.Th.TxtPri;

            bool linked = nfc != null && nfc.Polling;
            lblStatus = new Label
            {
                Text = linked
                    ? "请把【校园卡】放到 USB 卡读器上…\n\n读到的卡号会填进「卡号(EPC)」输入框，\n"
                      + "之后这台机器上刷这张卡就能识别到本人。（20 秒超时）"
                    : "未检测到校园卡读卡器。\n\n请先插上 USB 免驱的 NFC 读卡器（13.56MHz），\n"
                      + "或在「系统设置 → 校园卡适配」里查看状态。",
                Font = MainForm.Th.NormFont,
                ForeColor = linked ? MainForm.Th.TxtSec : MainForm.Th.Warn,
                Location = new Point(24, 20), AutoSize = true, MaximumSize = new Size(400, 0)
            };
            Controls.Add(lblStatus);

            var btnCancel = new SmoothButton
            {
                Text = "关闭", Location = new Point(170, 128), Size = new Size(110, 34),
                BaseColor = MainForm.Th.BgCard, ForeColor = MainForm.Th.TxtPri, Font = MainForm.Th.NormFont
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnCancel);
            CancelButton = btnCancel;

            poll = new System.Windows.Forms.Timer { Interval = 250 };
            poll.Tick += (s, e) =>
            {
                try
                {
                    string id = nfc != null ? nfc.TryReadOnce() : "";
                    if (string.IsNullOrEmpty(id) || id == lastId) return;
                    lastId = id;
                    Epc = id;
                    lblStatus.Text = "已读到卡号：" + id + "\n正在返回…";
                    lblStatus.ForeColor = MainForm.Th.Success;
                    poll.Stop();
                    timeout.Stop();
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch { }
            };
            if (linked) poll.Start();

            timeout = new System.Windows.Forms.Timer { Interval = 20000 };
            timeout.Tick += (s, e) => { poll.Stop(); timeout.Stop(); DialogResult = DialogResult.Cancel; Close(); };
            FormClosed += (s, e) => { poll.Stop(); timeout.Stop(); };
            if (linked) timeout.Start();
        }
    }
}
