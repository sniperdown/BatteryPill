using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace BatteryPill
{
    /// <summary>用 UpdateLayeredWindow 逐像素绘制的无边框窗口（面板用）。</summary>
    internal class LayeredForm : Form
    {
        protected bool NoActivate;

        public LayeredForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
                if (NoActivate) cp.ExStyle |= Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected float DpiScale
        {
            get
            {
                uint dpi = Native.GetDpiForWindow(Handle);
                if (dpi == 0) dpi = 96;
                return dpi / 96f;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected void Present(Bitmap bitmap, int x, int y)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(memDc, hBitmap);
            try
            {
                var dst = new Native.POINT(x, y);
                var size = new Native.SIZE(bitmap.Width, bitmap.Height);
                var src = new Native.POINT(0, 0);
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = Native.AC_SRC_ALPHA
                };
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend,
                    Native.ULW_ALPHA);
            }
            finally
            {
                Native.SelectObject(memDc, old);
                Native.DeleteObject(hBitmap);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
