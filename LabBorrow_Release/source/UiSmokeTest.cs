// =====================================================================
//  UiSmokeTest.cs  --  界面冒烟测试（不需要读卡器）
//  ---------------------------------------------------------------------
//  作用：在没有硬件的情况下，把主窗体真正构造出来、逐页切换、把每个页面
//        渲染成 PNG 截图，用来验证界面代码（布局/绘制/数据绑定）不会抛异常。
//        数据库指向临时库（LABBORROW_DB），不会碰真实数据。
//
//  编译（由 rebuild.bat / build_app.py 的可选步骤完成）：
//     csc /target:exe /main:LabBorrow.UiSmokeTest /platform:x86 ^
//         /reference:_lib\UHFReader18CSharp.dll /reference:_lib\System.Data.SQLite.dll ^
//         /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
//         source\*.cs（除 Program.cs）
//  运行：UiSmokeTest.exe [输出目录]
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace LabBorrow
{
    internal static class UiSmokeTest
    {
        private static int failed;

        // Console 重定向到文件时是带缓冲的：进程一旦被杀，缓冲区里的输出会全部丢失，
        // 什么都看不到。所以统一走 Log()，写完立刻 flush。
        private static void Log(string msg)
        {
            Console.WriteLine(msg);
            try { Console.Out.Flush(); } catch { }
        }

        [STAThread]
        internal static int Main(string[] args)
        {
            string outDir = args != null && args.Length > 0 ? args[0] : "ui_smoke";
            // 可选第二个参数：指定已有数据库文件（用于「只读库/老库」场景诊断），此时不再造测试数据
            bool useExistingDb = args != null && args.Length >= 2;
            string dbPath = useExistingDb ? args[1] : Path.Combine(outDir, "uismoke.db");
            Directory.CreateDirectory(outDir);
            if (!useExistingDb)
                foreach (var f in new[] { dbPath, dbPath + "-journal" })
                    try { if (File.Exists(f)) File.Delete(f); } catch { }

            Environment.SetEnvironmentVariable("LABBORROW_DB", Path.GetFullPath(dbPath));

            if (!useExistingDb)
            {
                try
                {
                    Seed();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[失败] 造测试数据出错：" + ex.Message);
                    return 1;
                }
            }
            else
            {
                Console.WriteLine("使用已有数据库（不造数据）：" + Path.GetFullPath(dbPath));
                Console.WriteLine("数据库可写: " + Db.Writable + "　结构就绪: " + Db.SchemaReady);
                if (!string.IsNullOrEmpty(Db.SchemaError)) Console.WriteLine("数据库状态说明: " + Db.SchemaError);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm form = null;
            try
            {
                form = new MainForm();
                form.Opacity = 0;          // 透明显示：测试期间不干扰桌面
                form.Show();
                Application.DoEvents();
                Console.WriteLine("[通过] 主窗体构造并 Load 成功");

                string[] names = { "01_主控台", "02_设备管理", "03_借还记录", "04_用户管理", "05_统计报表", "06_系统设置" };
                for (int i = 0; i < names.Length; i++)
                {
                    try
                    {
                        // 测「点导航 → 页面出现」这段同步耗时：切页卡顿就体现在这里
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        form.SwitchPage(i);
                        sw.Stop();
                        Application.DoEvents();
                        Application.DoEvents();
                        Shot(form, Path.Combine(outDir, names[i] + ".png"));
                        Log("[通过] 切换到「" + names[i] + "」并渲染成功，首帧耗时 " + sw.ElapsedMilliseconds + " ms");
                        Log("[步骤] 布局压力测试 " + names[i]);
                        AuditLongText(form, names[i]);
                        // 第二次切换：数据已缓存，应该更快（验证"按需加载"确实生效）
                        var sw2 = System.Diagnostics.Stopwatch.StartNew();
                        form.SwitchPage(0);
                        form.SwitchPage(i);
                        sw2.Stop();
                        Log("[性能] 二次切换到「" + names[i] + "」耗时 " + sw2.ElapsedMilliseconds + " ms");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine("[失败] 页面「" + names[i] + "」：" + ex.Message);
                    }
                }

                // 小窗口体检：复现笔记本 1080x654 的场景，检查主控台 hero 卡
                // 是否出现控件重叠/内容被压缩（用户反馈"布局对吗"的场景）
                AuditSmallWindow(form, outDir);

                // 缩放窗口体检：模拟拖动窗口改变大小，测量重排耗时并检查主控台是否错位
                AuditResize(form, outDir);

                // 设置项体检：逐个改值 → 保存 → 重新载入页面，看值是否真的留住了
                AuditSettingsItems(form);

                // 设置页分区体检：全部展开，检查有没有内容被裁剪（折叠功能是改过的）
                AuditSettingsSections(form, outDir);

                // 各对话框构造（不显示为可见窗口）
                TryDialog("借出登记对话框", () =>
                {
                    var u = new User { Name = "李四", UserNo = "2026001", ClassName = "电气2601", Role = "student", CardEpc = "CARD0001" };
                    using (var d = new BorrowDialog("示波器 DS1102", null, u, SwipeRules.Load()))
                    { d.Opacity = 0; d.Show(); Application.DoEvents(); d.Close(); }
                });
                TryDialog("设备登记对话框", () =>
                {
                    using (var d = new DeviceDialog(null, "EPCTEST0001"))
                    { d.Opacity = 0; d.Show(); Application.DoEvents(); d.Close(); }
                });
                TryDialog("借用人登记对话框", () =>
                {
                    using (var d = new UserDialog(null, null))
                    { d.Opacity = 0; d.Show(); Application.DoEvents(); d.Close(); }
                });
                TryDialog("读取标签对话框", () =>
                {
                    using (var d = new ReadEpcDialog(null))
                    { d.Opacity = 0; d.Show(); Application.DoEvents(); d.Close(); }
                });
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("[失败] 主窗体流程异常：" + ex);
            }
            finally
            {
                try { if (form != null) { form.Close(); form.Dispose(); } } catch { }
            }

            Console.WriteLine(failed == 0 ? "界面冒烟测试：全部通过 ✔" : ("界面冒烟测试：失败 " + failed + " 项 ✘"));
            return failed == 0 ? 0 : 1;
        }

        // ----------------------------------------------------------------
        //  小窗口体检：把窗口缩到 1080x654（普通笔记本大小），
        //  检查主控台顶部卡片里的文字行有没有互相重叠、内容有没有被压缩出卡。
        //  重叠 = 行高不足时 TableLayoutPanel 把 AutoSize 行压扁，看起来就是"文字叠在一起"。
        // ----------------------------------------------------------------
        private static void AuditSmallWindow(MainForm form, string outDir)
        {
            try
            {
                Size old = form.Size;
                form.Size = new Size(1080, 654);
                form.SwitchPage(0);
                Application.DoEvents();
                Application.DoEvents();

                var fld = typeof(MainForm).GetField("heroCard",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var heroCard = fld.GetValue(form) as Panel;

                var rects = new List<Rectangle>();
                var names = new List<string>();
                CollectAbs(heroCard, heroCard, rects, names);
                int overlaps = 0;
                for (int i = 0; i < rects.Count; i++)
                    for (int j = i + 1; j < rects.Count; j++)
                    {
                        Rectangle a = rects[i], b = rects[j];
                        a.Intersect(b);
                        if (!a.IsEmpty && a.Width > 4 && a.Height > 4)
                        {
                            overlaps++;
                            Log("    重叠: [" + names[i] + "] x [" + names[j] + "] 交叠 " + a.Size);
                        }
                    }
                int bottomMost = 0;
                foreach (var r in rects) bottomMost = Math.Max(bottomMost, r.Bottom);
                bool overflow = bottomMost > heroCard.ClientSize.Height + 2;

                Shot(form, Path.Combine(outDir, "00_主控台_小窗口.png"));
                Log("[体检] 小窗口(1080x654) hero 卡：可见控件 " + names.Count
                    + "，内容底部 " + bottomMost + " / 卡高 " + heroCard.ClientSize.Height);
                if (overlaps == 0 && !overflow) Log("[通过] 小窗口下 hero 卡无重叠、无溢出");
                else { failed++; Log("[失败] 小窗口下 hero 卡：重叠 " + overlaps + " 处" + (overflow ? "，内容超出卡片底部" : "")); }

                form.Size = old;
                Application.DoEvents();
            }
            catch (Exception ex) { failed++; Log("[失败] 小窗口体检异常：" + ex.Message); }
        }

        // ----------------------------------------------------------------
        //  缩放窗口体检：模拟真实拖动（开始拖 → 改一串尺寸 → 松手），
        //  拖动中应当几乎零耗时（程序刻意跳过重排），松手后统一算一次。
        //  这样"拖窗口时卡顿/闪烁"能被客观测出来。
        // ----------------------------------------------------------------
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void AuditResize(MainForm form, string outDir)
        {
            try
            {
                Size old = form.Size;
                Size[] sizes = { new Size(1280, 760), new Size(1600, 900), new Size(2560, 1440),
                                 new Size(1080, 654), new Size(1920, 1080) };
                var fld = typeof(MainForm).GetField("heroCard",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var heroCard = fld.GetValue(form) as Panel;

                // 模拟真实拖动：先发 WM_ENTERSIZEMOVE（开始拖），改一串尺寸，再发 WM_EXITSIZEMOVE（松手）。
                // 判定用「重排实际执行次数」这个精确指标：拖动期间必须一次都不重排。
                // （耗时本身包含系统对整个窗体的重绘，只能作参考值打印出来。）
                var callsField = typeof(MainForm).GetField("DashboardLayoutCalls",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                Func<int> getCalls = () =>
                {
                    try
                    {
                        var f = typeof(MainForm).GetField("DashboardLayoutCalls",
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Public);
                        return f != null ? (int)f.GetValue(null) : -1;
                    }
                    catch { return -1; }
                };

                int callsBefore = getCalls();
                SendMessage(form.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
                long worstDrag = 0;
                foreach (var sz in sizes)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    form.Size = sz;
                    Application.DoEvents();
                    Application.DoEvents();
                    sw.Stop();
                    if (sw.ElapsedMilliseconds > worstDrag) worstDrag = sw.ElapsedMilliseconds;
                    Log("    [拖动中] " + sz.Width + "x" + sz.Height + " 耗时 " + sw.ElapsedMilliseconds + " ms");
                }
                int callsDuringDrag = getCalls() - callsBefore;

                SendMessage(form.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
                var swEnd = System.Diagnostics.Stopwatch.StartNew();
                Application.DoEvents();
                Application.DoEvents();
                swEnd.Stop();
                int callsAfterRelease = getCalls() - callsBefore - callsDuringDrag;
                Log("    [松手后] 统一重排一次 " + swEnd.ElapsedMilliseconds + " ms");

                bool scrolled = heroCard.HorizontalScroll.Visible || heroCard.VerticalScroll.Visible;
                bool bad = false;
                if (callsDuringDrag > 0)
                {
                    bad = true;
                    Log("    拖动过程中仍执行了 " + callsDuringDrag + " 次重排（应为 0，否则会边拖边卡）");
                }
                if (callsAfterRelease < 1) { bad = true; Log("    松手后没有按最终尺寸重排"); }
                if (swEnd.ElapsedMilliseconds > 350) { bad = true; Log("    松手后重排过慢（>350ms）"); }
                if (scrolled) { bad = true; Log("    hero 卡出现滚动条（内容被挤出）"); }

                Shot(form, Path.Combine(outDir, "00_主控台_缩放后.png"));
                Log("[体检] 模拟拖动 5 种尺寸：拖动中重排 " + callsDuringDrag + " 次（期望 0），"
                    + "松手后重排 " + callsAfterRelease + " 次耗时 " + swEnd.ElapsedMilliseconds
                    + " ms；单帧耗时参考（含系统重绘）最慢 " + worstDrag + " ms");
                if (!bad) Log("[通过] 拖动窗口不做重排（松手才统一算一次），布局结果正确");
                else { failed++; Log("[失败] 窗口缩放体检未通过"); }

                form.Size = old;
                Application.DoEvents();
            }
            catch (Exception ex) { failed++; Log("[失败] 缩放体检异常：" + ex.Message); }
        }

        // ----------------------------------------------------------------
        //  设置项体检：逐个「改值 → 保存 → 重新载入设置页」，
        //  检查改过的值有没有真的留下来。这样"某个修改项不生效"能被精确定位到字段。
        // ----------------------------------------------------------------
        private static void AuditSettingsItems(MainForm form)
        {
            const System.Reflection.BindingFlags BF =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var t = typeof(MainForm);

            try
            {
                form.SwitchPage(5);          // 系统设置
                Application.DoEvents();
                Application.DoEvents();

                var save = t.GetMethod("SaveSettingsFromControls", BF);
                var reload = t.GetMethod("ReloadSettingsPage", BF);
                if (save == null) { failed++; Log("[失败] 找不到 SaveSettingsFromControls"); return; }

                int bad = 0, ok = 0;
                foreach (var fname in SettingFieldNames)
                {
                    var f = t.GetField(fname, BF);
                    if (f == null) { bad++; Log("    字段缺失: " + fname); continue; }
                    var ctrl = f.GetValue(form) as Control;
                    if (ctrl == null) { bad++; Log("    控件为空: " + fname + "（保存时会读不到，可能导致保存中断）"); continue; }

                    try
                    {
                        string before, expect, after;
                        if (ctrl is NumericUpDown)
                        {
                            var n = (NumericUpDown)ctrl;
                            decimal old = n.Value;
                            decimal want = (old + 1 <= n.Maximum) ? old + 1 : (old - 1 >= n.Minimum ? old - 1 : old);
                            if (want == old) { Log("    " + fname + " 取值范围只有一档，跳过"); continue; }
                            n.Value = want;
                            before = old.ToString(); expect = want.ToString();
                            save.Invoke(form, new object[] { false });
                            if (reload != null) reload.Invoke(form, new object[] { true });
                            Application.DoEvents();
                            after = n.Value.ToString();
                        }
                        else if (ctrl is CheckBox)
                        {
                            var c = (CheckBox)ctrl;
                            bool oldB = c.Checked;
                            c.Checked = !oldB;
                            before = oldB.ToString(); expect = (!oldB).ToString();
                            save.Invoke(form, new object[] { false });
                            if (reload != null) reload.Invoke(form, new object[] { true });
                            Application.DoEvents();
                            after = c.Checked.ToString();
                        }
                        else if (ctrl is TextBox)
                        {
                            var x = (TextBox)ctrl;
                            string oldS = x.Text;
                            x.Text = @"C:\Temp\test_sfx.mp3";
                            before = oldS; expect = x.Text;
                            save.Invoke(form, new object[] { false });
                            if (reload != null) reload.Invoke(form, new object[] { true });
                            Application.DoEvents();
                            after = x.Text;
                        }
                        else continue;

                        if (after == expect) ok++;
                        else { bad++; Log("    修改未生效: " + fname + "（原 " + before + " → 设为 " + expect + "，重载后却是 " + after + "）"); }
                    }
                    catch (Exception ex)
                    {
                        bad++;
                        Log("    保存时异常: " + fname + " → " + ex.InnerException != null
                            ? ex.InnerException.Message : ex.Message);
                    }
                }

                Log("[体检] 系统设置修改项：生效 " + ok + " 项，异常 " + bad + " 项");
                if (bad == 0) Log("[通过] 所有设置项改完保存后都能正确保留");
                else { failed++; Log("[失败] 有 " + bad + " 个设置项改了不生效/保存报错"); }
            }
            catch (Exception ex) { failed++; Log("[失败] 设置项体检异常：" + ex.Message); }
        }

        // ----------------------------------------------------------------
        //  设置页分区体检：把每个分区都展开，检查内容有没有被裁剪/看不见。
        //  折叠功能改过（为修闪烁），必须确认展开后内容完整。
        // ----------------------------------------------------------------
        private static void AuditSettingsSections(MainForm form, string outDir)
        {
            const System.Reflection.BindingFlags BF =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var t = typeof(MainForm);

            try
            {
                var stackF = t.GetField("settingsStack", BF);
                var stack = stackF != null ? stackF.GetValue(form) as Control : null;
                var setCollapsed = t.GetMethod("SetSectionCollapsed", BF);
                if (stack == null || setCollapsed == null) { Log("[跳过] 找不到设置页分区容器"); return; }

                form.SwitchPage(5);
                Application.DoEvents();
                Application.DoEvents();

                // 全部展开
                foreach (Control sec in stack.Controls)
                {
                    var p = sec as Panel;
                    if (p != null) setCollapsed.Invoke(form, new object[] { p, false });
                }
                Application.DoEvents();
                Application.DoEvents();

                int bad = 0, expanded = 0;
                foreach (Control sec in stack.Controls)
                {
                    var p = sec as Panel;
                    if (p == null) continue;
                    expanded++;
                    int maxBottom = 0;
                    foreach (var c in AllVisible(p)) maxBottom = Math.Max(maxBottom, BottomIn(p, c));
                    if (maxBottom > p.Height + 2)
                    {
                        bad++;
                        Log("    分区内容被裁剪：分区高 " + p.Height + "，内容底部 " + maxBottom);
                    }
                }

                Shot(form, Path.Combine(outDir, "07_系统设置_全展开.png"));
                Log("[体检] 设置页分区全展开：共 " + expanded + " 个分区，裁剪/异常 " + bad + " 处");
                if (bad == 0) Log("[通过] 设置页所有分区展开后内容完整可见");
                else { failed++; Log("[失败] 设置页有 " + bad + " 个分区内容显示不全"); }
            }
            catch (Exception ex) { failed++; Log("[失败] 设置页分区体检异常：" + ex.Message); }
        }

        private static IEnumerable<Control> AllVisible(Control root)
        {
            var list = new List<Control>();
            foreach (Control c in root.Controls)
            {
                if (!c.Visible) continue;
                list.Add(c);
                list.AddRange(AllVisible(c));
            }
            return list;
        }

        private static int BottomIn(Control root, Control c)
        {
            var pt = root.PointToClient(c.PointToScreen(new Point(0, 0)));
            return pt.Y + c.Height;
        }

        // 系统设置页里所有可修改项对应的私有字段名
        private static readonly string[] SettingFieldNames =
        {
            "numPower", "numHits", "numTotalHits", "numReadWindow", "numPoll", "numTagCd",
            "numGlobalGap", "numB2R", "numR2B", "numAbuseWin", "numAbuseMax", "numAbuseGlobal", "numAbuseLock",
            "numBorrowDays", "numWarnHours", "chkUserCardMode", "numUserWait",
            "chkAutoBackup", "numBackupKeep",
            "chkSound", "numVolume", "numSoundMax",
            "txtSfxBorrow", "txtSfxReturn", "txtSfxBlocked", "txtSfxAbuse",
            // 本轮新增：到期提醒 / 自助终端 / 校园卡
            "numRemindLead", "numRemindInterval", "numIdleSec",
            "chkTouchFriendly", "chkRemindOverdue", "chkKiosk", "chkKeepScreen", "chkNfc"
        };

        /// <summary>收集后代控件的可见矩形，统一换算到 root 的坐标系（避免父/子坐标系混比的误报）</summary>
        private static void CollectVisible(Control root, List<Control> list)
        {
            foreach (Control c in root.Controls)
            {
                if (c.Visible) list.Add(c);
                CollectVisible(c, list);
            }
        }

        private static void CollectAbs(Control root, Control current, List<Rectangle> rects, List<string> names)
        {
            foreach (Control c in current.Controls)
            {
                if (!c.Visible) continue;
                // 只收集叶子控件（真正的文字/按钮）：父容器必然包含子控件，
                // 拿进来做两两比较只会产生"父子相交"的误报
                if (c.Controls.Count == 0)
                {
                    Rectangle abs = c.RectangleToScreen(new Rectangle(Point.Empty, c.Size));
                    rects.Add(root.RectangleToClient(abs));
                    names.Add(c.GetType().Name + ":" + ShortTxt(c.Text));
                }
                else CollectAbs(root, c, rects, names);
            }
        }

        private static string ShortTxt(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 12 ? s : s.Substring(0, 12) + "…";
        }

        private static void TryDialog(string name, Action act)
        {
            try { act(); Console.WriteLine("[通过] " + name + " 构造成功"); }
            catch (Exception ex) { failed++; Console.WriteLine("[失败] " + name + "：" + ex.Message); }
        }

        // ----------------------------------------------------------------
        //  布局体检：把页面上所有 Label 的文字临时换成超长文本，
        //  再看有没有容器被迫出现滚动条——有滚动条就说明真实数据下会"顶出/错位"。
        //  这能在没有肉眼的情况下，客观判断「页面是不是又乱了」。
        // ----------------------------------------------------------------
        private static void AuditLongText(Form form, string pageName)
        {
            var labels = new List<Label>();
            var scrollables = new List<ScrollableControl>();
            Collect(form, labels, scrollables);

            var backup = new List<string>(labels.Count);
            foreach (var l in labels) backup.Add(l.Text);

            foreach (var l in labels)
            {
                // 统一换成固定长度的超长文本：足够触发溢出，但不会把 AutoSize
                // 容器撑到几万像素宽（那会让布局计算慢到像卡死）。
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 40; i++) sb.Append("超长文本压力测试ABC123-");
                l.Text = sb.ToString();
            }

            Application.DoEvents();
            Application.DoEvents();
            int overflow = 0;
            string detail = "";
            foreach (var s in scrollables)
            {
                // 只统计横向滚动：文字变长导致上下滚动是正常的；
                // 被迫出现横向滚动条才说明内容把容器撑破（界面看起来就是错位/缺一块）。
                if (!s.HorizontalScroll.Visible) continue;
                overflow++;
                string nm = string.IsNullOrEmpty(s.Name) ? s.GetType().Name : s.Name;
                if (string.IsNullOrEmpty(detail))
                    detail = nm + " 横向溢出，首个文本：" + FirstShortLabel(s);
            }

            for (int i = 0; i < labels.Count; i++) labels[i].Text = backup[i];
            Application.DoEvents();

            if (overflow == 0)
                Log("[通过] 「" + pageName + "」布局压力测试：超长文本未撑破容器");
            else
                Log("[警告] 「" + pageName + "」布局压力测试：" + overflow
                    + " 个容器出现滚动条，例如 " + detail);
        }

        private static void Collect(Control root, List<Label> labels, List<ScrollableControl> scrollables)
        {
            var sc = root as ScrollableControl;
            if (sc != null && sc.AutoScroll) scrollables.Add(sc);
            var lab = root as Label;
            if (lab != null) labels.Add(lab);
            foreach (Control c in root.Controls) Collect(c, labels, scrollables);
        }

        /// <summary>找一个便于定位的文本（截到 30 字），用于溢出警告</summary>
        private static string FirstShortLabel(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var l = c as Label;
                if (l != null)
                {
                    string t = l.Text ?? "";
                    return t.Length > 30 ? t.Substring(0, 30) + "…" : t;
                }
                string sub = FirstShortLabel(c);
                if (!string.IsNullOrEmpty(sub)) return sub;
            }
            return "(无文本控件)";
        }

        private static void Shot(Control c, string path)
        {
            using (var bmp = new Bitmap(Math.Max(10, c.Width), Math.Max(10, c.Height)))
            {
                c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>造一批有代表性的测试数据：在库/外借/逾期/维修/报废 + 学生卡 + 流水</summary>
        private static void Seed()
        {
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var devices = new[]
            {
                new object[] { "EPC0000000001", "数字示波器", "DS1102Z-E", "仪器仪表", "A柜-1层", "idle", "", 0, 0 },
                new object[] { "EPC0000000002", "数字万用表", "UT61E", "仪器仪表", "A柜-2层", "borrowed", "李四", -3, 7 },
                new object[] { "EPC0000000003", "直流稳压电源", "DP832", "仪器仪表", "B柜-1层", "borrowed", "王五", -30, 7 },
                new object[] { "EPC0000000004", "热风枪", "858D", "电动工具", "C柜", "maintenance", "", 0, 0 },
                new object[] { "EPC0000000005", "逻辑分析仪", "LA1010", "仪器仪表", "A柜-3层", "retired", "", 0, 0 },
                new object[] { "EPC0000000006", "树莓派 4B", "Pi4-4G", "计算与存储", "D柜", "borrowed", "赵六", -1, 7 },
            };
            foreach (var d in devices)
            {
                var dev = new Device
                {
                    Epc = (string)d[0], Name = (string)d[1], Model = (string)d[2], Category = (string)d[3],
                    Location = (string)d[4], Status = (string)d[5], CurrentBorrower = (string)d[6],
                    Notes = "", CreatedAt = now, UpdatedAt = now
                };
                int backHours = (int)d[7];
                int days = (int)d[8];
                if (backHours != 0)
                {
                    DateTime bt = DateTime.Now.AddHours(backHours);
                    dev.BorrowTime = bt.ToString("yyyy-MM-dd HH:mm:ss");
                    dev.DueTime = bt.AddDays(days).ToString("yyyy-MM-dd HH:mm:ss");
                }
                Db.UpsertDevice(dev);
            }

            Db.InsertUser(new User { CardEpc = "STUCARD0001", Name = "李四", UserNo = "2026001", ClassName = "电气 2601", Role = "student", CreatedAt = now, UpdatedAt = now });
            Db.InsertUser(new User { CardEpc = "STUCARD0002", Name = "王五", UserNo = "2026002", ClassName = "电气 2602", Role = "student", CreatedAt = now, UpdatedAt = now });
            Db.InsertUser(new User { CardEpc = "TEACARD0001", Name = "张老师", UserNo = "T001", ClassName = "实验中心", Role = "teacher", CreatedAt = now, UpdatedAt = now });

            // 近 30 天的借还流水，供统计报表出图
            var rnd = new Random(20261016);
            string[] borrowers = { "李四", "王五", "赵六", "张老师" };
            string[] epcs = { "EPC0000000001", "EPC0000000002", "EPC0000000003", "EPC0000000006" };
            string[] eNames = { "数字示波器", "数字万用表", "直流稳压电源", "树莓派 4B" };
            for (int day = 29; day >= 0; day--)
            {
                int n = rnd.Next(0, 4);
                for (int k = 0; k < n; k++)
                {
                    int idx = rnd.Next(epcs.Length);
                    DateTime t = DateTime.Today.AddDays(-day).AddHours(8 + rnd.Next(9)).AddMinutes(rnd.Next(60));
                    double dur = 20 + rnd.Next(300);
                    bool overdue = rnd.Next(10) == 0;
                    Db.AddRecord(new BorrowRecord
                    {
                        Epc = epcs[idx], DeviceName = eNames[idx], Borrower = borrowers[rnd.Next(borrowers.Length)],
                        Direction = "borrow", EventTime = t.ToString("yyyy-MM-dd HH:mm:ss"), DurationMin = 0,
                        Notes = "", DueTime = t.AddDays(7).ToString("yyyy-MM-dd HH:mm:ss"), UserCard = "STUCARD0001"
                    });
                    Db.AddRecord(new BorrowRecord
                    {
                        Epc = epcs[idx], DeviceName = eNames[idx], Borrower = borrowers[rnd.Next(borrowers.Length)],
                        Direction = "return", EventTime = t.AddMinutes(dur).ToString("yyyy-MM-dd HH:mm:ss"), DurationMin = Math.Round(dur, 1),
                        Notes = "借用时长 " + Math.Round(dur, 1) + " 分钟", DueTime = t.AddDays(7).ToString("yyyy-MM-dd HH:mm:ss"),
                        UserCard = "STUCARD0001", Overdue = overdue
                    });
                }
            }

            // 几条审计记录
            Db.AddAudit("EPC0000000001", "interval", "借出后再次归还的最短间隔为 10 秒，本次仅间隔 2.3 秒", "数字示波器 / 状态 borrowed");
            Db.AddAudit("EPC0000000002", "abuse", "异常刷卡：60 秒内同一标签刷卡 9 次（上限 8 次），已锁定 120 秒", "数字万用表 / 状态 borrowed");
            Db.AddAudit("UNKNOWN0001", "unknown", "未登记标签，已忽略", "既不是设备标签也不是借用人卡");
        }
    }
}
