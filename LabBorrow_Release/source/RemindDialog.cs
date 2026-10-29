// =====================================================================
//  到期 / 逾期名单窗口
//
//  老师要的「提醒借用者」最后总要落到「去找谁要」——
//  所以这里不是干巴巴一句提示，而是给出可直接联系、可直接导出的名单：
//   姓名 / 学号 / 班级 / 设备 / 应还时间 / 状态（临期剩多久 or 逾期多久）
//  并支持导出 CSV（可以贴到班级群、Excel 里打印张贴）。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LabBorrow
{
    internal class RemindDialog : Form
    {
        private readonly List<RemindItem> items;
        private DataGridView grid;

        public RemindDialog(List<RemindItem> list)
        {
            items = list ?? new List<RemindItem>();

            Text = "到期提醒名单";
            Size = new Size(860, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = MainForm.Th.BgPanel;
            ForeColor = MainForm.Th.TxtPri;
            Font = MainForm.Th.NormFont;
            MinimizeBox = false;

            int over = 0, soon = 0;
            foreach (var it in items) { if (it.Overdue) over++; else soon++; }

            var head = new Label
            {
                Dock = DockStyle.Top, Height = 58, AutoSize = false,
                Text = "需要催归还的设备：" + items.Count + " 件"
                       + (over > 0 ? "　（已逾期 " + over + " 件）" : "")
                       + (soon > 0 ? "　（即将到期 " + soon + " 件）" : "") + "\n" +
                       "可以按学号/班级联系借用同学，或导出成表格发到群里",
                Font = MainForm.Th.MidFont, ForeColor = over > 0 ? MainForm.Th.Danger : MainForm.Th.Warn,
                Padding = new Padding(14, 10, 10, 0)
            };

            grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = MainForm.Th.BgPanel, BorderStyle = BorderStyle.None,
                RowHeadersVisible = true, MultiSelect = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing
            };
            var gridFont = new Font(MainForm.Th.NormFont.FontFamily, Math.Max(11f, MainForm.Th.NormFont.Size + 2), FontStyle.Regular);
            grid.Font = gridFont;
            grid.ColumnHeadersDefaultCellStyle.Font = MainForm.Th.BoldFont;
            grid.ColumnHeadersDefaultCellStyle.BackColor = MainForm.Th.BgCard;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = MainForm.Th.TxtPri;
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.ColumnHeadersHeight = Math.Max(32, gridFont.Height + 14);
            grid.DefaultCellStyle.BackColor = MainForm.Th.BgPage;
            grid.DefaultCellStyle.ForeColor = MainForm.Th.TxtPri;
            grid.DefaultCellStyle.SelectionBackColor = MainForm.Th.BgHover;
            grid.DefaultCellStyle.SelectionForeColor = MainForm.Th.TxtPri;
            grid.RowTemplate.Height = Math.Max(28, gridFont.Height + 12);
            grid.EnableHeadersVisualStyles = false;

            grid.Columns.Add("colName", "借用人");
            grid.Columns.Add("colNo", "学号/工号");
            grid.Columns.Add("colClass", "班级/部门");
            grid.Columns.Add("colPhone", "手机号");
            grid.Columns.Add("colDev", "设备");
            grid.Columns.Add("colDue", "应还时间");
            grid.Columns.Add("colState", "状态");
            grid.Columns["colPhone"].Width = 110;
            grid.Columns["colState"].Width = 150;

            foreach (var it in items)
            {
                int idx = grid.Rows.Add(it.Borrower, it.UserNo, it.ClassName, it.Phone, it.DeviceName,
                    Reminder.ShortDue(it.DueTime),
                    it.Overdue ? ("已逾期 " + DueUtil.CompactSpan(TimeSpan.FromHours(it.Hours)))
                               : ("还剩 " + DueUtil.CompactSpan(TimeSpan.FromHours(it.Hours))));
                grid.Rows[idx].DefaultCellStyle.ForeColor = it.Overdue ? MainForm.Th.Danger : MainForm.Th.Warn;
            }

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = MainForm.Th.BgCard };
            var btnUrge = new SmoothButton
            {
                Text = "生成催还消息…", Size = new Size(160, 36), Location = new Point(14, 9),
                BaseColor = MainForm.Th.Accent, ForeColor = Color.White, Font = MainForm.Th.NormFont
            };
            btnUrge.Click += (s, e) =>
            {
                string text = Reminder.BuildUrgeText(items, MainForm.LabName);
                if (string.IsNullOrEmpty(text)) { MessageBox.Show(this, "没有可提醒的内容。"); return; }
                using (var dlg = new UrgeMessageDialog(text)) dlg.ShowDialog(this);
            };
            var btnExport = new SmoothButton
            {
                Text = "导出名单(CSV)", Size = new Size(150, 36), Location = new Point(186, 9),
                BaseColor = MainForm.Th.Accent2, ForeColor = Color.White, Font = MainForm.Th.NormFont
            };
            btnExport.Click += (s, e) => Export();
            var btnClose = new SmoothButton
            {
                Text = "知道了", Size = new Size(120, 36), Location = new Point(352, 9),
                BaseColor = MainForm.Th.BgHover, ForeColor = MainForm.Th.TxtPri, Font = MainForm.Th.NormFont
            };
            btnClose.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            bar.Controls.AddRange(new Control[] { btnUrge, btnExport, btnClose });
            CancelButton = btnClose;

            Controls.Add(grid);
            Controls.Add(head);
            Controls.Add(bar);
        }

        private void Export()
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "导出到期名单",
                Filter = "CSV 表格|*.csv",
                FileName = "到期名单_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    // 带 UTF-8 BOM：Excel 直接双击打开不会乱码
                    using (var sw = new StreamWriter(dlg.FileName, false, new System.Text.UTF8Encoding(true)))
                    {
                        sw.WriteLine("借用人,学号/工号,班级/部门,手机号,设备,应还时间,状态");
                        foreach (var it in items)
                            sw.WriteLine(string.Join(",",
                                Q(it.Borrower), Q(it.UserNo), Q(it.ClassName), Q(it.Phone), Q(it.DeviceName),
                                Q(Reminder.ShortDue(it.DueTime)),
                                Q(it.Overdue ? ("已逾期 " + DueUtil.CompactSpan(TimeSpan.FromHours(it.Hours)))
                                            : ("还剩 " + DueUtil.CompactSpan(TimeSpan.FromHours(it.Hours))))));
                    }
                    MessageBox.Show(this, "已导出：\n" + dlg.FileName + "\n\n可以直接用 Excel 打开，或发到班级群里催还。",
                        "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string Q(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }

    // =====================================================================
    //  催还消息预览窗
    //  消息是写给「借东西的同学本人」的（"你借的…请于…前归还"），
    //  复制后可直接粘到班级群、QQ/微信私聊或短信里 —— 这样提醒才真正到人。
    // =====================================================================
    internal class UrgeMessageDialog : Form
    {
        private readonly TextBox txt;

        public UrgeMessageDialog(string text)
        {
            Text = "催还消息（发给借用同学本人）";
            Size = new Size(620, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = MainForm.Th.BgPanel;
            ForeColor = MainForm.Th.TxtPri;
            Font = MainForm.Th.NormFont;
            MinimizeBox = false;

            var head = new Label
            {
                Dock = DockStyle.Top, Height = 46, AutoSize = false,
                Text = "下面这段消息按人分组，措辞是给借用同学本人看的。\n"
                     + "点「复制」后可直接粘贴到班级群 / 私聊 / 短信发出去。",
                Font = MainForm.Th.NormFont, ForeColor = MainForm.Th.TxtSec,
                Padding = new Padding(12, 10, 10, 0)
            };

            txt = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = MainForm.Th.BgPage, ForeColor = MainForm.Th.TxtPri,
                BorderStyle = BorderStyle.FixedSingle, Text = text,
                Font = new Font(MainForm.Th.NormFont.FontFamily,
                                Math.Max(11f, MainForm.Th.NormFont.Size + 1), FontStyle.Regular)
            };
            txt.Select(0, 0);

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = MainForm.Th.BgCard };
            var btnCopy = new SmoothButton
            {
                Text = "复制到剪贴板", Size = new Size(160, 36), Location = new Point(14, 9),
                BaseColor = MainForm.Th.Accent, ForeColor = Color.White, Font = MainForm.Th.NormFont
            };
            btnCopy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(txt.Text);
                    MessageBox.Show(this, "已复制。\n\n可以粘贴到班级群、私聊或短信里发给对应同学了。",
                        "已复制", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(this, "复制失败：" + ex.Message); }
            };
            var btnClose = new SmoothButton
            {
                Text = "关闭", Size = new Size(120, 36), Location = new Point(190, 9),
                BaseColor = MainForm.Th.BgHover, ForeColor = MainForm.Th.TxtPri, Font = MainForm.Th.NormFont
            };
            btnClose.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            bar.Controls.AddRange(new Control[] { btnCopy, btnClose });
            CancelButton = btnClose;

            Controls.Add(txt);
            Controls.Add(head);
            Controls.Add(bar);
        }
    }
}
