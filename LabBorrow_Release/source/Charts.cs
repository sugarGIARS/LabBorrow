// =====================================================================
//  Charts.cs  --  统计报表用的轻量图表控件（纯 GDI+ 手绘，不依赖第三方库）
//  支持：竖柱状图 / 横条形图 / 环形占比图（donut）
//  ---------------------------------------------------------------------
//  绘制要点（为了看起来"现代"）：
//   · 卡片：圆角 + 顶部到底部的微渐变背景 + 细边框，鼠标进入时边框提亮；
//   · 柱/条：顶部圆角 + 渐变填充，不再是硬邦邦的直角方块；
//   · 网格：极淡的横向参考线，只做背景不抢视觉；
//   · 占比：改成环形图（donut），中心显示合计数，比实心饼图更清爽；
//   · 悬浮：鼠标移到柱/条/扇区上会高亮并显示数值气泡（命中区在绘制时记录）。
//  性能：全部双缓冲，命中区用简单矩形/角度表，切换页面时不反复重算。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LabBorrow
{
    public class ChartItem
    {
        public string Label;
        public double Value;
        public ChartItem(string label, double value) { Label = label; Value = value; }
    }

    public enum ChartKind { Column, HBar, Pie }

    public class ChartPanel : Panel
    {
        public ChartKind Kind { get; set; }
        public string Title { get; set; }
        public string EmptyText { get; set; }
        public Color BarColor { get; set; }
        public Color BarColor2 { get; set; }
        public bool ShowValues { get; set; }
        public string Unit { get; set; }
        /// <summary>第二组数据（可选）：与第一组并排显示，用于"借出/归还"对比</summary>
        public string SeriesName1 { get; set; }
        public string SeriesName2 { get; set; }
        public Color BarColorB { get; set; }
        /// <summary>环形图中心文字的小标题（默认"合计"）</summary>
        public string DonutCenterTitle { get; set; }

        private List<ChartItem> data = new List<ChartItem>();
        private List<ChartItem> data2;
        private readonly Font smallFont = new Font("Microsoft YaHei UI", 8.5f);
        private readonly Font titleFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        private readonly Font valueFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

        // ---- 悬浮交互 ----
        private readonly ToolTip tip = new ToolTip();
        private bool hovering;
        private int hoverIndex = -1;
        // 命中区（绘制时填充）：矩形图用矩形，环形图用角度
        private readonly List<Rectangle> hitRects = new List<Rectangle>();
        private readonly List<int> hitIdx = new List<int>();
        private Rectangle pieRect;
        private readonly List<float> pieStart = new List<float>();
        private readonly List<float> pieSweep = new List<float>();

        public ChartPanel()
        {
            Kind = ChartKind.Column;
            Title = "";
            EmptyText = "暂无数据";
            BarColor = MainForm.Th.Accent;
            BarColor2 = MainForm.Th.Accent2;
            BarColorB = MainForm.Th.Accent2;
            ShowValues = true;
            Unit = "";
            SeriesName1 = "";
            SeriesName2 = "";
            DonutCenterTitle = "合计";
            BackColor = MainForm.Th.BgCard;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            tip.AutoPopDelay = 4000;
            tip.InitialDelay = 120;
            tip.ReshowDelay = 60;
        }

        public void SetData(List<ChartItem> items)
        {
            data = items ?? new List<ChartItem>();
            data2 = null;
            hoverIndex = -1;
            Invalidate();
        }

        public void SetData(List<ChartItem> items, List<ChartItem> second)
        {
            data = items ?? new List<ChartItem>();
            data2 = second;
            hoverIndex = -1;
            Invalidate();
        }

        public void SetData(List<Db.KV> items)
        {
            var list = new List<ChartItem>();
            if (items != null)
                for (int i = 0; i < items.Count; i++) list.Add(new ChartItem(items[i].Label, items[i].Value));
            SetData(list);
        }

        // ------------------------------------------------------------
        //  绘制入口
        // ------------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            hitRects.Clear();
            hitIdx.Clear();
            pieStart.Clear();
            pieSweep.Clear();

            var full = new Rectangle(0, 0, Width - 1, Height - 1);
            if (full.Width < 24 || full.Height < 24) return;

            // 卡片：微渐变背景 + 圆角边框（鼠标进入时边框提亮）
            using (var path = MainForm.RoundedPath(full, 10))
            {
                using (var b = new LinearGradientBrush(full,
                    ControlPaint.Light(MainForm.Th.BgCard, 0.035f),
                    MainForm.Th.BgCard, 90f))
                    g.FillPath(b, path);
                using (var pen = new Pen(hovering ? ControlPaint.Light(MainForm.Th.Border, 0.35f)
                                                  : MainForm.Th.Border, 1f))
                    g.DrawPath(pen, path);
            }

            int top = 12;
            if (!string.IsNullOrEmpty(Title))
            {
                TextRenderer.DrawText(g, Title, titleFont, new Rectangle(14, top, Width - 28, 22),
                    MainForm.Th.TxtPri, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                top += 26;
                // 标题下的分隔线
                using (var pen = new Pen(Color.FromArgb(40, MainForm.Th.Border), 1f))
                    g.DrawLine(pen, 14, top - 4, Width - 14, top - 4);
                top += 2;
            }

            var plot = new Rectangle(16, top, Math.Max(10, Width - 32), Math.Max(10, Height - top - 14));
            if (data.Count == 0)
            {
                DrawEmpty(g, plot);
                return;
            }

            if (Kind == ChartKind.Pie) DrawDonut(g, plot);
            else if (Kind == ChartKind.HBar) DrawHBar(g, plot);
            else DrawColumn(g, plot);
        }

        private void DrawEmpty(Graphics g, Rectangle plot)
        {
            int cy = plot.Y + plot.Height / 2;
            using (var pen = new Pen(Color.FromArgb(60, MainForm.Th.Border), 2f))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawRectangle(pen, new Rectangle(plot.X + plot.Width / 2 - 40, cy - 22, 80, 34));
            }
            TextRenderer.DrawText(g, EmptyText, smallFont,
                new Rectangle(plot.X, cy + 18, plot.Width, 20), MainForm.Th.TxtMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ------------------------------------------------------------
        //  竖柱状图
        // ------------------------------------------------------------
        private void DrawColumn(Graphics g, Rectangle plot)
        {
            bool grouped = data2 != null && data2.Count == data.Count;
            double max = 0;
            for (int i = 0; i < data.Count; i++) if (data[i].Value > max) max = data[i].Value;
            if (grouped)
                for (int i = 0; i < data2.Count; i++) if (data2[i].Value > max) max = data2[i].Value;
            if (max <= 0) max = 1;

            DrawSeriesLegend(g);

            int axisH = 18;
            int valueH = ShowValues ? 14 : 0;
            var bars = new Rectangle(plot.X, plot.Y + valueH, plot.Width,
                                     Math.Max(10, plot.Height - axisH - valueH));

            // 网格参考线
            using (var gp = new Pen(Color.FromArgb(34, MainForm.Th.Border), 1f))
            {
                gp.DashStyle = DashStyle.Dot;
                for (int i = 0; i <= 4; i++)
                {
                    int y = bars.Y + bars.Height - (bars.Height * i / 4);
                    g.DrawLine(gp, bars.X, y, bars.Right, y);
                }
            }

            int n = data.Count;
            int slot = Math.Max(3, bars.Width / Math.Max(1, n));
            int barW = Math.Max(2, Math.Min(grouped ? 16 : 30, (int)(slot * (grouped ? 0.34 : 0.6))));
            bool labelEvery = slot >= 30;
            int stepLabel = slot >= 14 ? 1 : (slot >= 7 ? 2 : 3);

            for (int i = 0; i < n; i++)
            {
                int x0 = bars.X + i * slot + (slot - (grouped ? barW * 2 + 4 : barW)) / 2;
                bool hot = i == hoverIndex;

                int h = (int)(bars.Height * (data[i].Value / max));
                if (data[i].Value > 0 && h < 2) h = 2;
                var r = new Rectangle(x0, bars.Bottom - h, barW, h);
                if (h > 0) FillBar(g, r, BarColor, BarColor2, true, hot);
                if (h > 0) AddHit(r, i);

                if (ShowValues && data[i].Value > 0 && slot >= 16)
                    TextRenderer.DrawText(g, Fmt(data[i].Value), smallFont,
                        new Rectangle(r.X - 12, r.Y - valueH, barW + 24, valueH),
                        hot ? MainForm.Th.TxtPri : MainForm.Th.TxtSec,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                if (grouped)
                {
                    int h2 = (int)(bars.Height * (data2[i].Value / max));
                    if (data2[i].Value > 0 && h2 < 2) h2 = 2;
                    var r2 = new Rectangle(x0 + barW + 4, bars.Bottom - h2, barW, h2);
                    if (h2 > 0) FillBar(g, r2, ControlPaint.Light(BarColorB, 0.08f),
                                        ControlPaint.Dark(BarColorB, 0.06f), true, hot);
                    if (h2 > 0) AddHit(r2, i);
                }

                if (labelEvery || i == 0 || i == n - 1 || (i % stepLabel == 0))
                {
                    int lw = labelEvery ? slot : Math.Max(46, slot * 2);
                    int lx = i == 0 ? bars.X
                           : (i == n - 1 ? Math.Max(bars.X, bars.X + i * slot + slot - lw)
                                         : bars.X + i * slot + (slot - lw) / 2);
                    TextRenderer.DrawText(g, data[i].Label, smallFont,
                        new Rectangle(lx, bars.Bottom + 2, lw, axisH),
                        hot ? MainForm.Th.TxtSec : MainForm.Th.TxtMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        // ------------------------------------------------------------
        //  横条形图
        // ------------------------------------------------------------
        private void DrawHBar(Graphics g, Rectangle plot)
        {
            double max = 0;
            for (int i = 0; i < data.Count; i++) if (data[i].Value > max) max = data[i].Value;
            if (max <= 0) max = 1;

            int n = data.Count;
            int rowH = Math.Max(16, Math.Min(32, plot.Height / Math.Max(1, n)));
            int labelW = Math.Min(160, Math.Max(64, plot.Width / 3));
            int valueW = 52;
            int barAreaW = Math.Max(20, plot.Width - labelW - valueW - 10);

            for (int i = 0; i < n; i++)
            {
                int y = plot.Y + i * rowH;
                if (y + rowH > plot.Bottom + 2) break;
                bool hot = i == hoverIndex;

                if (hot)
                {
                    using (var b = new SolidBrush(Color.FromArgb(28, MainForm.Th.TxtPri)))
                        g.FillRectangle(b, plot.X - 4, y, plot.Width + 8, rowH - 2);
                }

                TextRenderer.DrawText(g, data[i].Label, smallFont,
                    new Rectangle(plot.X, y, labelW - 8, rowH),
                    hot ? MainForm.Th.TxtPri : MainForm.Th.TxtSec,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                int w = (int)(barAreaW * (data[i].Value / max));
                if (data[i].Value > 0 && w < 3) w = 3;
                int barH = Math.Max(7, rowH - 10);
                var r = new Rectangle(plot.X + labelW, y + (rowH - barH) / 2, w, barH);

                // 底槽（圆角，淡）
                var track = new Rectangle(plot.X + labelW, y + (rowH - barH) / 2, barAreaW, barH);
                using (var tb = new SolidBrush(Color.FromArgb(38, MainForm.Th.Border)))
                    FillRound(g, track, tb, barH / 2);

                if (w > 0)
                {
                    FillBar(g, r, BarColor, BarColor2, false, hot);
                    AddHit(r, i);
                }

                TextRenderer.DrawText(g, Fmt(data[i].Value) + Unit, valueFont,
                    new Rectangle(plot.X + labelW + barAreaW + 6, y, valueW, rowH),
                    hot ? MainForm.Th.TxtPri : MainForm.Th.TxtSec,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        // ------------------------------------------------------------
        //  环形图（donut）
        // ------------------------------------------------------------
        private void DrawDonut(Graphics g, Rectangle plot)
        {
            double total = 0;
            for (int i = 0; i < data.Count; i++) total += Math.Max(0, data[i].Value);
            if (total <= 0) { DrawEmpty(g, plot); return; }

            Color[] palette = Palette();

            int legendW = Math.Min(190, Math.Max(96, plot.Width / 3));
            int size = Math.Max(46, Math.Min(plot.Height - 10, plot.Width - legendW - 20));
            pieRect = new Rectangle(plot.X + 4, plot.Y + (plot.Height - size) / 2, size, size);

            float start = -90f;
            for (int i = 0; i < data.Count; i++)
            {
                float sweep = (float)(360.0 * Math.Max(0, data[i].Value) / total);
                pieStart.Add(start);
                pieSweep.Add(sweep);
                if (sweep <= 0) { start += sweep; continue; }

                bool hot = i == hoverIndex;
                var r = hot ? new Rectangle(pieRect.X - 3, pieRect.Y - 3, pieRect.Width + 6, pieRect.Height + 6)
                            : pieRect;
                using (var b = new SolidBrush(hot ? ControlPaint.Light(palette[i % palette.Length], 0.18f)
                                                  : palette[i % palette.Length]))
                    g.FillPie(b, r, start, sweep);
                start += sweep;
            }

            // 挖空中心，形成 donut
            int inner = (int)(pieRect.Width * 0.60);
            var innerRect = new Rectangle(pieRect.X + (pieRect.Width - inner) / 2,
                                          pieRect.Y + (pieRect.Height - inner) / 2, inner, inner);
            using (var b = new SolidBrush(MainForm.Th.BgCard))
                g.FillEllipse(b, innerRect);
            using (var p = new Pen(Color.FromArgb(50, MainForm.Th.Border), 1f))
                g.DrawEllipse(p, innerRect);

            string centerTitle = hoverIndex >= 0 && hoverIndex < data.Count
                ? Short(data[hoverIndex].Label, 6) : DonutCenterTitle;
            string centerVal = hoverIndex >= 0 && hoverIndex < data.Count
                ? Fmt(data[hoverIndex].Value) : Fmt(total);
            TextRenderer.DrawText(g, centerVal, new Font("Microsoft YaHei UI", 15, FontStyle.Bold),
                innerRect, MainForm.Th.TxtPri,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var tr = new Rectangle(innerRect.X, innerRect.Y + innerRect.Height / 2 - 4, innerRect.Width, 20);
            TextRenderer.DrawText(g, centerTitle, smallFont, tr, MainForm.Th.TxtMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);

            // 图例
            int lx = pieRect.Right + 16;
            int ly = pieRect.Y + Math.Max(0, (pieRect.Height - Math.Min(data.Count, 8) * 20) / 2);
            int legendItemH = 20;
            for (int i = 0; i < data.Count; i++)
            {
                if (ly + legendItemH > plot.Bottom + 4) break;
                bool hot = i == hoverIndex;
                var swatch = new Rectangle(lx, ly + 4, 11, 11);
                using (var b = new SolidBrush(palette[i % palette.Length]))
                    FillRound(g, swatch, b, 3);

                double pct = Math.Max(0, data[i].Value) * 100.0 / total;
                string txt = string.Format("{0}  {1} · {2:0.#}%",
                    Short(data[i].Label, 10), Fmt(data[i].Value), pct);
                TextRenderer.DrawText(g, txt, smallFont,
                    new Rectangle(lx + 17, ly, Math.Max(20, plot.Right - lx - 17), legendItemH),
                    hot ? MainForm.Th.TxtPri : MainForm.Th.TxtSec,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                ly += legendItemH;
            }
        }

        // ------------------------------------------------------------
        //  通用绘制辅助
        // ------------------------------------------------------------
        private void DrawSeriesLegend(Graphics g)
        {
            if (data2 == null || (string.IsNullOrEmpty(SeriesName1) && string.IsNullOrEmpty(SeriesName2))) return;
            int y = 13;
            int right = Width - 14;
            int w2 = string.IsNullOrEmpty(SeriesName2) ? 0 : TextRenderer.MeasureText(g, SeriesName2, smallFont).Width + 20;
            int w1 = string.IsNullOrEmpty(SeriesName1) ? 0 : TextRenderer.MeasureText(g, SeriesName1, smallFont).Width + 20;
            int x = right - w1 - w2 - 8;
            if (x < 10) return;
            if (w1 > 0)
            {
                using (var b = new SolidBrush(BarColor)) FillRound(g, new Rectangle(x, y + 3, 10, 10), b, 3);
                TextRenderer.DrawText(g, SeriesName1, smallFont, new Rectangle(x + 14, y, w1, 16),
                    MainForm.Th.TxtSec, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                x += w1;
            }
            if (w2 > 0)
            {
                using (var b = new SolidBrush(BarColorB)) FillRound(g, new Rectangle(x, y + 3, 10, 10), b, 3);
                TextRenderer.DrawText(g, SeriesName2, smallFont, new Rectangle(x + 14, y, w2, 16),
                    MainForm.Th.TxtSec, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        /// <summary>竖向渐变柱（顶部两角圆角）</summary>
        private void FillBar(Graphics g, Rectangle r, Color c1, Color c2, bool vertical, bool hot)
        {
            var rect = new Rectangle(r.X, r.Y, Math.Max(2, r.Width), Math.Max(2, r.Height));
            if (hot)
            {
                c1 = ControlPaint.Light(c1, 0.22f);
                c2 = ControlPaint.Light(c2, 0.22f);
            }
            int radius = vertical ? Math.Min(5, Math.Min(rect.Width, rect.Height) / 2)
                                  : Math.Min(rect.Height / 2, 7);
            using (var path = RoundTop(rect, radius, vertical))
            using (var b = new LinearGradientBrush(rect, c1, c2, vertical ? 90f : 0f))
                g.FillPath(b, path);
        }

        private static void FillRound(Graphics g, Rectangle r, Brush b, int radius)
        {
            if (r.Width < 2 || r.Height < 2) return;
            using (var path = MainForm.RoundedPath(r, Math.Max(1, radius)))
                g.FillPath(b, path);
        }

        /// <summary>只把顶部（竖向）或右侧（横向）做成圆角的路径</summary>
        private static GraphicsPath RoundTop(Rectangle r, int d, bool vertical)
        {
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            if (d < 2) { var p0 = new GraphicsPath(); p0.AddRectangle(r); return p0; }
            var path = new GraphicsPath();
            if (vertical)
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            }
            else
            {
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddLine(r.X, r.Bottom, r.X, r.Y);
            }
            path.CloseFigure();
            return path;
        }

        private static Color[] Palette()
        {
            return new[] {
                MainForm.Th.Accent, MainForm.Th.Accent2, MainForm.Th.Warn,
                Color.FromArgb(255, 146, 60), MainForm.Th.Success, Color.FromArgb(0, 150, 199),
                Color.FromArgb(214, 48, 149), Color.FromArgb(140, 122, 230)
            };
        }

        private static string Fmt(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 0.05) return Math.Round(v).ToString();
            return v.ToString("0.#");
        }

        private static string Short(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private void AddHit(Rectangle r, int idx) { hitRects.Add(r); hitIdx.Add(idx); }

        // ------------------------------------------------------------
        //  悬浮：命中判定 + 数值气泡
        // ------------------------------------------------------------
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = HitTest(e.Location);
            string text = idx >= 0 ? TipText(idx) : "";
            if (idx != hoverIndex)
            {
                hoverIndex = idx;
                Invalidate();
            }
            if (idx >= 0) tip.SetToolTip(this, text);
            else tip.Hide(this);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIndex != -1 || hovering)
            {
                hoverIndex = -1;
                hovering = false;
                tip.Hide(this);
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!hovering) { hovering = true; Invalidate(); }
        }

        private int HitTest(Point p)
        {
            if (Kind == ChartKind.Pie)
            {
                if (pieRect.Width < 8) return -1;
                double cx = pieRect.X + pieRect.Width / 2.0;
                double cy = pieRect.Y + pieRect.Height / 2.0;
                double dx = p.X - cx, dy = p.Y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double outer = pieRect.Width / 2.0 + 3;
                double inner = outer * 0.60;
                if (dist > outer || dist < inner) return -1;
                double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI + 90.0;   // 以 12 点为 0
                if (ang < 0) ang += 360.0;
                float acc = 0;
                for (int i = 0; i < pieSweep.Count; i++)
                {
                    if (pieSweep[i] <= 0) continue;
                    if (ang >= acc && ang < acc + pieSweep[i]) return i;
                    acc += pieSweep[i];
                }
                return -1;
            }

            for (int i = 0; i < hitRects.Count; i++)
            {
                var r = hitRects[i];
                if (r.Width < 6) { r.Inflate(3, 0); }
                if (r.Contains(p)) return hitIdx[i];
            }
            return -1;
        }

        private string TipText(int idx)
        {
            if (idx < 0 || idx >= data.Count) return "";
            string name = string.IsNullOrEmpty(data[idx].Label) ? "—" : data[idx].Label;
            string v = Fmt(data[idx].Value) + Unit;
            if (Kind == ChartKind.Pie && data.Count > 0)
            {
                double total = 0;
                for (int i = 0; i < data.Count; i++) total += Math.Max(0, data[i].Value);
                if (total > 0)
                    v += string.Format("（{0:0.#}%）", Math.Max(0, data[idx].Value) * 100.0 / total);
            }
            string extra = "";
            if (data2 != null && idx < data2.Count && !string.IsNullOrEmpty(SeriesName2))
                extra = "\n" + SeriesName2 + "：" + Fmt(data2[idx].Value) + Unit;
            string head = string.IsNullOrEmpty(SeriesName1) ? "" : SeriesName1 + "：";
            return name + "\n" + head + v + extra;
        }
    }
}
