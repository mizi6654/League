using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace League.uitls
{
    public class ImageTabControl : TabControl
    {
        private int _tabPadding = 1;
        private Size _imageSize = new Size(40, 40);
        private Color _separatorColor = Color.Gray;

        public ImageTabControl()
        {
            // 控件支持设计器
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            // 设置 TabControl 样式
            Alignment = TabAlignment.Left;
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;

            ItemSize = new Size(_imageSize.Height + 10, _imageSize.Width + 10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(this.BackColor);

            // 绘制每个选项卡上的图片
            for (int i = 0; i < TabPages.Count; i++)
            {
                DrawTabImage(e.Graphics, i);
            }

            // 绘制分隔线
            DrawSeparatorLine(e.Graphics);
        }

        private void DrawTabImage(Graphics g, int index)
        {
            Rectangle tabRect = GetTabRect(index);
            bool isSelected = (SelectedIndex == index);
            tabRect.Inflate(-_tabPadding, -_tabPadding);

            using (var brush = new SolidBrush(isSelected ? Color.White : Color.FromArgb(230, 230, 230)))
            {
                g.FillRectangle(brush, tabRect);
            }

            Image img = GetTabImage(TabPages[index]);
            if (img != null)
            {
                Size imgSize = _imageSize;
                Rectangle imgRect = new Rectangle(
                    tabRect.X + (tabRect.Width - imgSize.Width) / 2,
                    tabRect.Y + (tabRect.Height - imgSize.Height) / 2,
                    imgSize.Width,
                    imgSize.Height);

                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                g.DrawImage(img, imgRect);
            }
        }

        private Image GetTabImage(TabPage page)
        {
            return page.Tag as Image;
        }

        private void DrawSeparatorLine(Graphics g)
        {
            int x = GetTabRect(0).Right + _tabPadding;
            using (var pen = new Pen(_separatorColor, 1))
            {
                g.DrawLine(pen, x, 0, x, Height);
            }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            // 忽略默认绘制
        }
    }
}
