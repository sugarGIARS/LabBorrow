// =====================================================================
//  ReportsPage.cs  --  统计报表页
//  ---------------------------------------------------------------------
//  内容：区间指标卡（借出/归还/在借/逾期/平均时长/设备总数）
//        + 借出归还趋势（分组柱状图）
//        + 设备借出排行（横条）
//        + 借用人借出排行（横条）
//        + 分类占比（饼图）
//        + 一键导出 CSV
//  全部图表由 Charts.cs 手绘，不依赖任何第三方组件。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace LabBorrow
{
    public partial class MainForm
    {
        // ---- 报表页控件 ----
        private ComboBox cboReportRange;
        private Label rpBorrowV, rpReturnV, rpOutV, rpOverdueV, rpAvgV, rpTotalV;
        private ChartPanel chartTrend, chartDevices, chartBorrowers, chartCategory;
        private Db.ReportData lastReport;

        // 指标卡字体统一放在 Th 里（按字号档位生成），这里不再单独定义，
        // 否则切换字号时报表页不会跟着变。

        private static readonly string[] ReportRanges = { "近 7 天", "近 30 天", "本月", "近 90 天", "全部" };

        private Control MakeToolbarReports()
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
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent
            };

            var lbl = new Label { Text = "统计区间：", AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont, Margin = new Padding(0, 8, 4, 0) };
            cboReportRange = new ComboBox
            {
                Width = 120, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Th.BgInput,
                ForeColor = Th.TxtPri, FlatStyle = FlatStyle.Flat, Font = Th.NormFont, Margin = new Padding(0, 4, 12, 0)
            };
            cboReportRange.Items.AddRange(ReportRanges);
            cboReportRange.SelectedIndex = 1;
            cboReportRange.SelectedIndexChanged += (s, e) => ReloadReports();

            var btnRefresh = MakeButton("刷新统计", Th.Accent, Point.Empty, new Size(110, 30));
            btnRefresh.Margin = new Padding(0, 3, 10, 0);
            btnRefresh.Click += (s, e) => ReloadReports();

            var btnExport = MakeButton("导出报表 CSV", Th.Accent2, Point.Empty, new Size(130, 30));
            btnExport.Margin = new Padding(0, 3, 0, 0);
            btnExport.Click += (s, e) => BtnExportReport_Click(null, EventArgs.Empty);

            flow.Controls.AddRange(new Control[] { lbl, cboReportRange, btnRefresh, btnExport });
            bar.Controls.Add(flow);
            return bar;
        }

        private void BuildReportsPage()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Th.BgPage,
                ColumnCount = 2, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            // 指标卡行高按字体实测（Font.Height 已随 DPI 缩放），
            // 固定像素在 2560x1600 / 150% 缩放的屏上会把 26pt 的大数字裁掉一半
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Th.RepValFont.Height + Th.RepTitleFont.Height + 46));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            // ---- 指标卡 ----
            var statRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Th.BgPage,
                ColumnCount = 6, RowCount = 1, Margin = new Padding(0, 0, 0, 14)
            };
            for (int i = 0; i < 6; i++) statRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 6F));

            var c1 = MakeReportCard("借出次数", "0", "次", Th.Success);
            var c2 = MakeReportCard("归还次数", "0", "次", Th.Accent2);
            var c3 = MakeReportCard("当前在借", "0", "件", Th.Warn);
            var c4 = MakeReportCard("逾期未还", "0", "件", Th.Danger);
            var c5 = MakeReportCard("平均借用", "0", "小时", Th.Accent);
            var c6 = MakeReportCard("设备总数", "0", "件", Color.FromArgb(0, 150, 199));
            c1.Margin = new Padding(0, 0, 12, 0);
            c2.Margin = new Padding(0, 0, 12, 0);
            c3.Margin = new Padding(0, 0, 12, 0);
            c4.Margin = new Padding(0, 0, 12, 0);
            c5.Margin = new Padding(0, 0, 12, 0);
            c6.Margin = Padding.Empty;
            statRow.Controls.Add(c1, 0, 0); statRow.Controls.Add(c2, 1, 0);
            statRow.Controls.Add(c3, 2, 0); statRow.Controls.Add(c4, 3, 0);
            statRow.Controls.Add(c5, 4, 0); statRow.Controls.Add(c6, 5, 0);
            rpBorrowV = (Label)c1.Tag; rpReturnV = (Label)c2.Tag; rpOutV = (Label)c3.Tag;
            rpOverdueV = (Label)c4.Tag; rpAvgV = (Label)c5.Tag; rpTotalV = (Label)c6.Tag;

            root.Controls.Add(statRow, 0, 0);
            root.SetColumnSpan(statRow, 2);

            // ---- 图表 ----
            chartTrend = NewChart("借出 / 归还趋势", ChartKind.Column, Th.Accent, Th.Accent2);
            chartDevices = NewChart("设备借出排行 TOP8", ChartKind.HBar, Color.FromArgb(0, 150, 199), Th.Accent);
            chartBorrowers = NewChart("借用人借出排行 TOP8", ChartKind.HBar, Th.Accent2, Color.FromArgb(214, 48, 149));
            chartCategory = NewChart("分类借出占比", ChartKind.Pie, Th.Accent, Th.Accent2);

            chartCategory.DonutCenterTitle = "借出合计";

            chartTrend.Margin = new Padding(0, 0, 14, 14);
            chartDevices.Margin = new Padding(0, 0, 0, 14);
            chartBorrowers.Margin = new Padding(0, 0, 14, 0);
            chartCategory.Margin = Padding.Empty;

            root.Controls.Add(chartTrend, 0, 1);
            root.Controls.Add(chartDevices, 1, 1);
            root.Controls.Add(chartBorrowers, 0, 2);
            root.Controls.Add(chartCategory, 1, 2);

            reportsRoot = root;
        }

        private Panel reportsRoot;

        /// <summary>
        /// 报表页专用指标卡：顶部细色条 + 微渐变背景 + 大数字 + 单位，
        /// 比原来的纯色卡片更接近现代仪表盘的观感。
        /// </summary>
        private Panel MakeReportCard(string title, string val, string unit, Color accent)
        {
            var p = new Panel { BackColor = Th.BgCard, Dock = DockStyle.Fill, Margin = Padding.Empty };
            RoundRegion(p, 10);

            var inner = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Color.Transparent,
                ColumnCount = 1, RowCount = 2,
                Padding = new Padding(14, 10, 12, 10)
            };
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var t = new Label
            {
                Text = title, Font = Th.RepTitleFont, ForeColor = Th.TxtSec,
                AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0)
            };

            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false, BackColor = Color.Transparent,
                AutoSize = false, Margin = new Padding(0), Padding = new Padding(0)
            };
            var v = new Label
            {
                Text = val, Font = Th.RepValFont,
                ForeColor = Th.TxtPri, AutoSize = true, Margin = new Padding(0),
                TextAlign = ContentAlignment.MiddleLeft
            };
            var u = new Label
            {
                Text = unit, Font = Th.RepTitleFont, ForeColor = Th.TxtMuted,
                AutoSize = true, Margin = new Padding(6, 14, 0, 0)
            };
            row.Controls.Add(v);
            row.Controls.Add(u);

            inner.Controls.Add(t, 0, 0);
            inner.Controls.Add(row, 0, 1);

            p.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
                if (r.Width < 20 || r.Height < 20) return;
                using (var path = RoundedPath(r, 10))
                {
                    using (var b = new LinearGradientBrush(r,
                        ControlPaint.Light(Th.BgCard, 0.05f), Th.BgCard, 90f))
                        g.FillPath(b, path);
                    using (var pen = new Pen(Th.Border, 1f))
                        g.DrawPath(pen, path);
                }
                // 右上角一团很淡的主题色光晕，让卡片不显得死板
                using (var b2 = new SolidBrush(Color.FromArgb(26, accent)))
                    g.FillEllipse(b2, r.Right - 40, r.Y - 6, 46, 46);
                // 顶部细色条
                using (var b3 = new SolidBrush(accent))
                    g.FillRectangle(b3, 0, 0, r.Width, 3);
            };

            p.Controls.Add(inner);
            p.Tag = v;      // 与 MakeStatCard 保持一致：Tag 是数值标签
            return p;
        }

        private ChartPanel NewChart(string title, ChartKind kind, Color c1, Color c2)
        {
            var p = new ChartPanel
            {
                Dock = DockStyle.Fill, Kind = kind, Title = title,
                BarColor = c1, BarColor2 = c2, EmptyText = "该区间暂无数据"
            };
            return p;
        }

        // -----------------------------------------------------------------
        //  取数
        // -----------------------------------------------------------------
        private void ResolveRange(out DateTime from, out DateTime to)
        {
            to = DateTime.Today;
            int idx = cboReportRange == null ? 1 : cboReportRange.SelectedIndex;
            switch (idx)
            {
                case 0: from = to.AddDays(-6); break;
                case 2: from = new DateTime(to.Year, to.Month, 1); break;
                case 3: from = to.AddDays(-89); break;
                case 4: from = new DateTime(2000, 1, 1); break;
                default: from = to.AddDays(-29); break;
            }
        }

        private void ReloadReports()
        {
            if (rpBorrowV == null) return;
            DateTime from, to;
            ResolveRange(out from, out to);
            int warn = rules == null ? 24 : rules.DueWarnHours;
            Db.ReportData rep;
            try { rep = Db.GetReport(from, to, warn, 8); }
            catch { return; }
            lastReport = rep;

            rpBorrowV.Text = rep.Borrows.ToString();
            rpReturnV.Text = rep.Returns.ToString();
            rpOutV.Text = rep.CurrentOut.ToString();
            rpOverdueV.Text = rep.OverdueNow.ToString();
            rpOverdueV.ForeColor = rep.OverdueNow > 0 ? Th.Danger : Th.TxtPri;
            rpAvgV.Text = rep.AvgDurationMin > 0 ? (rep.AvgDurationMin / 60.0).ToString("0.#") : "—";
            rpTotalV.Text = rep.TotalDevices.ToString();

            chartTrend.SetData(ToItems(rep.DailyBorrow), ToItems(rep.DailyReturn));
            chartDevices.SetData(rep.TopDevices);
            chartBorrowers.SetData(rep.TopBorrowers);
            chartCategory.SetData(rep.ByCategory);

            if (chartTrend != null)
            {
                chartTrend.SeriesName1 = "借出";
                chartTrend.SeriesName2 = "归还";
            }
        }

        private static List<ChartItem> ToItems(List<Db.KV> src)
        {
            var list = new List<ChartItem>();
            if (src != null)
                for (int i = 0; i < src.Count; i++) list.Add(new ChartItem(src[i].Label, src[i].Value));
            return list;
        }

        private static string FormatMinutes(double min)
        {
            if (min < 60) return min.ToString("0.#") + " 分";
            return (min / 60.0).ToString("0.#") + " 小时";
        }

        // -----------------------------------------------------------------
        //  导出
        // -----------------------------------------------------------------
        private void BtnExportReport_Click(object sender, EventArgs e)
        {
            if (lastReport == null) ReloadReports();
            if (lastReport == null) { MsgBox("暂无可导出的统计数据。"); return; }

            using (var sfd = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = "LabBorrow统计报表_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var sw = new StreamWriter(sfd.FileName, false, Encoding.UTF8))
                    {
                        var rep = lastReport;
                        sw.WriteLine("LabBorrow 统计报表");
                        sw.WriteLine("统计区间," + rep.From.ToString("yyyy-MM-dd") + " 至 " + rep.To.ToString("yyyy-MM-dd"));
                        sw.WriteLine();
                        sw.WriteLine("汇总指标,数值");
                        sw.WriteLine("借出次数," + rep.Borrows);
                        sw.WriteLine("归还次数," + rep.Returns);
                        sw.WriteLine("当前在借," + rep.CurrentOut);
                        sw.WriteLine("逾期未还," + rep.OverdueNow);
                        sw.WriteLine("区间内归还时已逾期次数," + rep.OverdueReturns);
                        sw.WriteLine("平均借用时长(分钟)," + rep.AvgDurationMin.ToString("0.#"));
                        sw.WriteLine("累计借用时长(小时)," + rep.TotalDurationHours.ToString("0.##"));
                        sw.WriteLine("设备总数," + rep.TotalDevices);
                        sw.WriteLine();
                        sw.WriteLine("日期,借出,归还");
                        for (int i = 0; i < rep.DailyBorrow.Count; i++)
                        {
                            double ret = i < rep.DailyReturn.Count ? rep.DailyReturn[i].Value : 0;
                            sw.WriteLine(Csv(rep.DailyBorrow[i].Label) + "," + rep.DailyBorrow[i].Value + "," + ret);
                        }
                        sw.WriteLine();
                        sw.WriteLine("设备借出排行,次数");
                        for (int i = 0; i < rep.TopDevices.Count; i++)
                            sw.WriteLine(Csv(rep.TopDevices[i].Label) + "," + rep.TopDevices[i].Value);
                        sw.WriteLine();
                        sw.WriteLine("借用人借出排行,次数");
                        for (int i = 0; i < rep.TopBorrowers.Count; i++)
                            sw.WriteLine(Csv(rep.TopBorrowers[i].Label) + "," + rep.TopBorrowers[i].Value);
                        sw.WriteLine();
                        sw.WriteLine("分类,次数");
                        for (int i = 0; i < rep.ByCategory.Count; i++)
                            sw.WriteLine(Csv(rep.ByCategory[i].Label) + "," + rep.ByCategory[i].Value);
                    }
                    MsgBox("已导出到：\n" + sfd.FileName);
                }
                catch (Exception ex) { MsgBox("导出失败：" + ex.Message); }
            }
        }

        private static string Csv(string v)
        {
            return "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        }
    }
}
