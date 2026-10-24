using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenLight
{
    internal static class Theme
    {
        internal static readonly Color Background = Color.FromArgb(250, 250, 252), Card = Color.White,
            Text = Color.FromArgb(29, 29, 31), Muted = Color.FromArgb(105, 109, 118), Accent = Color.FromArgb(0, 122, 255),
            Soft = Color.FromArgb(238, 245, 255), Border = Color.FromArgb(227, 229, 233), Green = Color.FromArgb(36, 119, 90),
            Amber = Color.FromArgb(140, 98, 31), AmberSoft = Color.FromArgb(255, 245, 228), Red = Color.FromArgb(181, 67, 67);
        internal static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2; var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        internal static void Box(Graphics g, RectangleF rect, float radius, Color fill, Color? border)
        {
            using (var path = Round(rect, radius))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (border.HasValue) using (var pen = new Pen(border.Value)) g.DrawPath(pen, path);
            }
        }
        internal static void TextAt(Graphics g, string text, float size, Color color, RectangleF rect, bool bold = false, StringAlignment align = StringAlignment.Near, string family = "Microsoft YaHei UI")
        {
            using (var font = new Font(family, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, font, brush, rect, format);
        }
        internal static void Sun(Graphics g, float x, float y, float radius, Color color)
        {
            using (var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawEllipse(pen, x - radius * .43f, y - radius * .43f, radius * .86f, radius * .86f);
                for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; g.DrawLine(pen, x + (float)Math.Cos(a) * radius * .73f, y + (float)Math.Sin(a) * radius * .73f, x + (float)Math.Cos(a) * radius, y + (float)Math.Sin(a) * radius); }
            }
        }
        internal static void Symbol(Graphics g, string symbol, float x, float y, Color color)
        {
            using (var p = new Pen(color, 1.3f))
            {
                if (symbol == "monitor") { g.DrawRectangle(p, x - 9, y - 7, 18, 12); g.DrawLine(p, x, y + 5, x, y + 9); g.DrawLine(p, x - 5, y + 9, x + 5, y + 9); }
                else if (symbol == "settings")
                {
                    for (int i = -1; i <= 1; i++) { float yy = y + i * 5, xx = x + (i == 0 ? 3 : -3); g.DrawLine(p, x - 8, yy, x + 8, yy); using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, xx - 2, yy - 2, 4, 4); g.DrawEllipse(p, xx - 2, yy - 2, 4, 4); }
                }
                else if (symbol == "moon") { g.DrawArc(p, x - 6, y - 6, 12, 12, 45, 270); g.DrawArc(p, x - 1, y - 7, 9, 12, 100, 175); }
                else if (symbol == "book") { g.DrawRectangle(p, x - 7, y - 5, 14, 11); g.DrawLine(p, x, y - 5, x, y + 6); }
                else if (symbol == "work") { g.DrawRectangle(p, x - 7, y - 4, 14, 10); g.DrawRectangle(p, x - 3, y - 7, 6, 3); g.DrawLine(p, x - 7, y + 1, x + 7, y + 1); }
                else Sun(g, x, y, 8, color);
            }
        }
    }
    internal sealed class ToggleSwitch : CheckBox
    {
        internal float UiScale = 1;
        internal ToggleSwitch()
        {
            AutoSize = false; Cursor = Cursors.Hand; TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(UiScale, UiScale);
            float x = Width / UiScale - 40, y = (Height / UiScale - 22) / 2;
            Theme.Box(g, new RectangleF(x, y, 38, 22), 11, !Enabled ? Color.FromArgb(231, 233, 238) : Checked ? Theme.Accent : Color.FromArgb(216, 219, 226), null);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x + (Checked ? 19 : 3), y + 3, 16, 16);
            if (Text.Length > 0) Theme.TextAt(g, Text, 12, Enabled ? Theme.Muted : Color.FromArgb(170, 174, 182), new RectangleF(0, 0, x - 8, Height / UiScale));
            if (Focused && ShowFocusCues) using (var pen = new Pen(Theme.Accent)) g.DrawRectangle(pen, x - 2, y - 2, 42, 26);
        }
    }

    internal sealed class DesignPanel : Panel
    {
        internal float UiScale = 1;
        internal Action<Graphics, float> PaintContent;
        internal DesignPanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Background; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.ScaleTransform(UiScale, UiScale); if (PaintContent != null) PaintContent(e.Graphics, Width / UiScale); }
    }
    internal class UiButton : Button
    {
        internal float UiScale = 1;
        internal bool Selected;
        internal string Subtitle;
        internal bool Link, Primary, Quiet, MutedLink;
        internal string Symbol;
        private bool hover;
        internal UiButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; TabStop = true;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.ScaleTransform(UiScale, UiScale);
            float w = Width / UiScale, h = Height / UiScale;
            Color fill = Primary ? Theme.Accent : Selected ? Theme.Soft : hover ? Color.FromArgb(235, 240, 247) : Quiet || Subtitle != null ? Color.FromArgb(240, 242, 246) : Color.White;
            if (!Link || hover) Theme.Box(e.Graphics, new RectangleF(.6f, .6f, w - 1.2f, h - 1.2f), 8, Link ? Theme.Soft : fill, Link || Quiet || Subtitle != null || Primary ? (Color?)null : Theme.Border);
            Color ink = !Enabled ? Color.FromArgb(164, 168, 176) : Primary ? Color.White : Selected || (Link && !MutedLink) ? Theme.Accent : MutedLink ? Theme.Muted : Theme.Text;
            if (Subtitle == null)
            {
                if (Symbol != null) Theme.Symbol(e.Graphics, Symbol, w / 2, h / 2, ink);
                else Theme.TextAt(e.Graphics, Text, 12, ink, new RectangleF(5, 0, w - 10, h), false, StringAlignment.Center);
            }
            else
            {
                if (Symbol != null) Theme.Symbol(e.Graphics, Symbol, 21, 19, Theme.Muted);
                Theme.TextAt(e.Graphics, Text, 12, ink, new RectangleF(Symbol == null ? 14 : 35, 8, w - (Symbol == null ? 25 : 43), 22));
                Theme.TextAt(e.Graphics, Subtitle, 18, ink, new RectangleF(14, 31, w - 25, 24), false, StringAlignment.Near, "Segoe UI");
            }
            if (Focused) using (var pen = new Pen(Theme.Accent, 1.5f)) using (var path = Theme.Round(new RectangleF(3, 3, w - 6, h - 6), 9)) e.Graphics.DrawPath(pen, path);
        }
    }
    internal sealed class BrightnessSlider : Control
    {
        private int value = 100;
        internal float UiScale = 1;
        internal int Minimum;
        internal event EventHandler ValueChanged;
        internal int Value
        {
            get { return value; }
            set
            {
                int next = Math.Max(Minimum, BrightnessMath.Clamp(value)); if (this.value == next) return;
                this.value = next; AccessibleName = "亮度 " + next + "%"; Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }
        internal BrightnessSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider; BackColor = Color.White;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.ScaleTransform(UiScale, UiScale);
            float w = Width / UiScale, h = Height / UiScale, pad = 13, y = h / 2, x = pad + (w - pad * 2) * (value - Minimum) / Math.Max(1f, 100 - Minimum);
            using (var pen = new Pen(Color.FromArgb(232, 235, 240), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round }) e.Graphics.DrawLine(pen, pad, y, w - pad, y);
            using (var pen = new Pen(Enabled ? Theme.Accent : Theme.Muted, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round }) e.Graphics.DrawLine(pen, pad, y, x, y);
            using (var brush = new SolidBrush(Color.FromArgb(25, 80, 80, 130))) e.Graphics.FillEllipse(brush, x - 12, y - 10, 24, 24);
            using (var brush = new SolidBrush(Color.White)) e.Graphics.FillEllipse(brush, x - 10, y - 10, 20, 20);
            using (var pen = new Pen(Color.FromArgb(206, 213, 224), 1)) e.Graphics.DrawEllipse(pen, x - 10, y - 10, 20, 20);
            if (Focused && ShowFocusCues) using (var pen = new Pen(Theme.Accent)) e.Graphics.DrawEllipse(pen, x - 13, y - 13, 26, 26);
        }
        private void SetFromX(int x) { float w = Width / UiScale; Value = Minimum + (int)Math.Round((x / UiScale - 13) * (100 - Minimum) / Math.Max(1, w - 26)); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); Capture = true; SetFromX(e.X); } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture && e.Button == MouseButtons.Left) SetFromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) Capture = false; }
        protected override bool IsInputKey(Keys data) { Keys key = data & Keys.KeyCode; return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(data); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value -= e.Shift ? 1 : 5;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value += e.Shift ? 1 : 5;
            else if (e.KeyCode == Keys.Home) Value = Minimum;
            else if (e.KeyCode == Keys.End) Value = 100;
            else return;
            e.Handled = true;
        }
    }
}
