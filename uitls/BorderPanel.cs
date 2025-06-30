namespace League.uitls
{
    public class BorderPanel : Panel
    {
        public Color BorderColor { get; set; } = Color.Black;
        public int BorderWidth { get; set; } = 2;

        public BorderPanel()
        {
            this.DoubleBuffered = true;
            this.ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(BorderColor, BorderWidth))
            {
                pen.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset; // 关键！
                Rectangle rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
                e.Graphics.DrawRectangle(pen, rect);
            }
        }
    }
}
