using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryPill
{
    internal static class TaskbarGeometry
    {
        public static IntPtr Find(string monitor)
        {
            IntPtr taskbar = Native.FindWindow("Shell_SecondaryTrayWnd", null);
            if (monitor == "primary" || taskbar == IntPtr.Zero)
                taskbar = Native.FindWindow("Shell_TrayWnd", null);
            return taskbar;
        }
    }

    /// <summary>
    /// 直接画在任务栏上的胶囊窗口。
    /// Win11 的任务栏永远压在顶层窗口之上，所以要把窗口挂成任务栏的子窗口；
    /// 低权限进程（被沙箱降权时）挂不上去，就退化成贴在任务栏上沿的浮窗。
    /// </summary>
    internal sealed class PillWindow : NativeWindow, IDisposable
    {
        private const int PillWidthDip = 100;
        private const int PillHeightDip = 32;

        private readonly Settings _settings;
        private BatteryReading _reading = new BatteryReading();
        private bool _hover;
        private bool _tracking;
        private bool _attached;
        private IntPtr _parent;
        private int _x, _y, _w, _h;
        private float _scale = 1f;

        public event Action LeftClicked;

        public PillWindow(Settings settings)
        {
            _settings = settings;
        }

        public bool IsAlive
        {
            get { return Handle != IntPtr.Zero && Native.IsWindow(Handle); }
        }

        public bool Attached { get { return _attached; } }

        public bool Create(IntPtr parent)
        {
            if (parent == IntPtr.Zero || !Native.IsWindow(parent)) return false;
            _parent = parent;

            uint dpi = Native.GetDpiForWindow(parent);
            if (dpi == 0) dpi = 96;
            _scale = dpi / 96f;
            _w = (int)Math.Round(PillWidthDip * _scale);
            _h = (int)Math.Round(PillHeightDip * _scale);
            ComputePosition();

            var cp = new CreateParams
            {
                Caption = "BatteryPill",
                Style = Native.WS_POPUP | Native.WS_VISIBLE,
                ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                X = _x,
                Y = _y,
                Width = _w,
                Height = _h
            };
            try
            {
                CreateHandle(cp);
            }
            catch (Exception ex)
            {
                CrashLog.Write("create", ex);
                return false;
            }
            if (Handle == IntPtr.Zero) return false;

            TryAttach(parent);
            ComputePosition();
            Render();
            return true;
        }

        private void TryAttach(IntPtr parent)
        {
            if (!IsAlive || parent == IntPtr.Zero || !Native.IsWindow(parent)) return;
            if (Native.GetParent(Handle) == parent)
            {
                _attached = true;
                return;
            }
            int parentPid;
            Native.GetWindowThreadProcessId(parent, out parentPid);
            int myIl = Native.IntegrityLevel();
            int parentIl = Native.ProcessIntegrity(parentPid);
            Native.RECT tb;
            Native.GetWindowRect(parent, out tb);

            long style = (long)Native.GetWindowLongPtr(Handle, Native.GWL_STYLE);
            style = (style & ~(long)Native.WS_POPUP) | Native.WS_CHILD;
            Native.SetWindowLongPtr(Handle, Native.GWL_STYLE, (IntPtr)style);
            Native.SetParent(Handle, parent);
            int err = Marshal.GetLastWin32Error();
            _attached = Native.GetParent(Handle) == parent;
            Diag.Log("attachA parent=" + parent + " pid=" + parentPid + " rect=" + tb.Left + "," + tb.Top + "," +
                     tb.Right + "," + tb.Bottom + " myIL=" + myIl + " parentIL=" + parentIl +
                     " attached=" + _attached + " err=" + err);

            if (!_attached)
            {
                // 方法 B：销毁后直接以任务栏为父窗口重建
                if (Handle != IntPtr.Zero && Native.IsWindow(Handle)) DestroyHandle();
                int localX = tb.Right - (int)Math.Round(_settings.OffsetRightDip * _scale) - _w - tb.Left;
                int localY = tb.Top + (tb.Bottom - tb.Top - _h) / 2 - tb.Top;
                var cp = new CreateParams
                {
                    Caption = "BatteryPill",
                    Style = Native.WS_CHILD | Native.WS_VISIBLE,
                    ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                    X = localX,
                    Y = localY,
                    Width = _w,
                    Height = _h,
                    Parent = parent
                };
                try
                {
                    CreateHandle(cp);
                    _attached = Handle != IntPtr.Zero && Native.GetParent(Handle) == parent;
                    Diag.Log("attachB hwnd=" + Handle + " attached=" + _attached + " err=" + Marshal.GetLastWin32Error());
                }
                catch (Exception ex)
                {
                    Diag.Log("attachB failed: " + ex.Message + " err=" + Marshal.GetLastWin32Error());
                }
                if (!_attached)
                {
                    // 方法 C：退回顶层浮窗
                    if (Handle != IntPtr.Zero && Native.IsWindow(Handle)) DestroyHandle();
                    var cp2 = new CreateParams
                    {
                        Caption = "BatteryPill",
                        Style = Native.WS_POPUP | Native.WS_VISIBLE,
                        ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                        X = _x,
                        Y = _y,
                        Width = _w,
                        Height = _h
                    };
                    CreateHandle(cp2);
                    Diag.Log("attachC fallback-float hwnd=" + Handle);
                }
            }
            if (_attached)
            {
                ComputePosition();
                Render();
                Raise();
            }
        }

        private void ComputePosition()
        {
            if (_parent == IntPtr.Zero || !Native.IsWindow(_parent)) return;
            Native.RECT tb;
            Native.GetWindowRect(_parent, out tb);
            uint dpi = Native.GetDpiForWindow(_parent);
            _scale = (dpi == 0 ? 96u : dpi) / 96f;
            _w = (int)Math.Round(PillWidthDip * _scale);
            _h = (int)Math.Round(PillHeightDip * _scale);
            _x = tb.Right - (int)Math.Round(_settings.OffsetRightDip * _scale) - _w;
            _y = _attached
                ? tb.Top + (tb.Bottom - tb.Top - _h) / 2
                : tb.Top - _h - (int)Math.Round(6 * _scale);
        }

        public void Raise()
        {
            if (!IsAlive) return;
            if (_attached)
            {
                Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            else
            {
                Native.SetWindowPos(Handle, (IntPtr)Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        /// <summary>任务栏换了（分辨率、缩放、Explorer 重启）就重挂一次。</summary>
        public void Ensure(IntPtr parent)
        {
            if (parent == IntPtr.Zero || !Native.IsWindow(parent)) return;
            if (!IsAlive)
            {
                _attached = false;
                Create(parent);
                return;
            }
            if (_parent != parent || Native.GetParent(Handle) != parent)
            {
                _parent = parent;
                if (!_attached) TryAttach(parent);
                else if (Native.GetParent(Handle) != parent) { _attached = false; TryAttach(parent); }
            }
            int oldX = _x, oldY = _y, oldW = _w, oldH = _h;
            ComputePosition();
            if (oldX != _x || oldY != _y || oldW != _w || oldH != _h) Render();
            Raise();
        }

        public void SetReading(BatteryReading reading)
        {
            _reading = reading;
            Render();
        }

        public Rectangle ScreenRect
        {
            get { return new Rectangle(_x, _y, _w, _h); }
        }

        public bool Contains(Point screenPoint)
        {
            return _x <= screenPoint.X && screenPoint.X < _x + _w &&
                   _y <= screenPoint.Y && screenPoint.Y < _y + _h;
        }

        public void SetHover(bool hover)
        {
            if (_hover == hover) return;
            _hover = hover;
            Diag.Log("hover=" + hover + " rect=" + _x + "," + _y + "," + _w + "," + _h);
            Render();
        }

        private void Render()
        {
            if (!IsAlive) return;
            using (var bmp = DrawBitmap(Math.Max(8, _w), Math.Max(8, _h), _scale)) Present(bmp);
        }

        /// <summary>离屏渲染（生成文档截图用，不依赖窗口）。</summary>
        public Bitmap DrawBitmap(int w, int h, float scale)
        {
            _scale = scale;
            _w = w;
            _h = h;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) DrawInto(g, bmp);
            return bmp;
        }

        private void DrawInto(Graphics g, Bitmap bmp)
        {
            int w = Math.Max(8, _w), h = Math.Max(8, _h);
            float s = _scale;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                var body = new RectangleF(0.75f * s, 0.75f * s, w - 1.5f * s, h - 1.5f * s);
                using (var path = Rounded.Rect(body, body.Height / 2f))
                {
                    int alpha = _hover ? 252 : 226;
                    using (var brush = new SolidBrush(Color.FromArgb(alpha, 24, 24, 24)))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(Color.FromArgb(_hover ? 52 : 32, 255, 255, 255), Math.Max(1f, 1f * s)))
                        g.DrawPath(pen, path);
                }

                int iconX = (int)Math.Round(13 * s);
                int iconW = (int)Math.Round(10 * s);
                int iconH = (int)Math.Round(16 * s);
                DrawMouseIcon(g, iconX, (h - iconH) / 2f, iconW, iconH, Color.FromArgb(232, 255, 255, 255));

                bool charging = _reading != null && _reading.ExternalPower;
                bool stale = _reading != null && _reading.Stale;
                bool hasPercent = _reading != null && _reading.Ok && _reading.Percent >= 0;
                string text;
                Color color;
                bool realPercent = _reading != null && _reading.Source == "G HUB";
                if (charging && !(realPercent && hasPercent))
                {
                    text = "充电中";
                    color = Color.FromArgb(255, 255, 201, 91);
                }
                else if (hasPercent)
                {
                    text = _reading.Percent + "%";
                    color = LevelColor(_reading.Percent);
                }
                else
                {
                    text = "—";
                    color = Color.FromArgb(255, 158, 158, 158);
                }
                if (stale) color = Color.FromArgb(255, 158, 158, 158); // 睡眠 = 明确置灰

                float textLeft = iconX + iconW + 6 * s;
                if (charging)
                {
                    float bx = iconX + iconW + 4 * s;
                    DrawBolt(g, bx, (h - 11 * s) / 2f, 7 * s, 11 * s);
                    textLeft = bx + 10 * s;
                }

                using (var font = new Font("Segoe UI Semibold", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(color))
                using (var format = new StringFormat
                {
                    Alignment = StringAlignment.Far,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap
                })
                {
                    var rect = new RectangleF(textLeft, 0, w - textLeft - 13 * s, h);
                    g.DrawString(text, font, brush, rect, format);
                }

                if (Environment.GetEnvironmentVariable("BATTERYPILL_DEBUG") == "1")
                    bmp.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pill.png"), ImageFormat.Png);
        }

        private void Present(Bitmap bitmap)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(memDc, hBitmap);
            try
            {
                var dst = new Native.POINT(_x, _y);
                if (_attached && _parent != IntPtr.Zero)
                    Native.ScreenToClient(_parent, ref dst);
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

        private static Color LevelColor(int percent)
        {
            if (percent >= 60) return Color.FromArgb(255, 124, 217, 142); // 绿
            if (percent >= 30) return Color.FromArgb(255, 255, 201, 91);  // 黄
            return Color.FromArgb(255, 255, 118, 118);                    // 红
        }

        private static void DrawBolt(Graphics g, float x, float y, float w, float h)
        {
            var points = new[]
            {
                new PointF(x + w * 0.55f, y),
                new PointF(x + w * 0.05f, y + h * 0.55f),
                new PointF(x + w * 0.42f, y + h * 0.55f),
                new PointF(x + w * 0.25f, y + h),
                new PointF(x + w * 0.95f, y + h * 0.38f),
                new PointF(x + w * 0.58f, y + h * 0.38f),
                new PointF(x + w * 0.8f, y)
            };
            using (var brush = new SolidBrush(Color.FromArgb(255, 255, 212, 90)))
                g.FillPolygon(brush, points);
        }

        internal static void DrawMouseIcon(Graphics g, float x, float y, float w, float h, Color color)
        {
            using (var pen = new Pen(color, Math.Max(1f, w / 9f)))
            {
                using (var body = Rounded.Rect(new RectangleF(x, y, w, h), w / 2f))
                    g.DrawPath(pen, body);
                float split = y + h * 0.36f;
                g.DrawLine(pen, x + 0.5f, split, x + w - 0.5f, split);
                if (h > 12)
                {
                    float wheel = y + h * 0.12f;
                    g.DrawLine(pen, x + w / 2f, wheel, x + w / 2f, wheel + h * 0.16f);
                }
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
