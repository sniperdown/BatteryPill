using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace BatteryPill
{
    internal sealed class PanelForm : LayeredForm
    {
        private const int PanelWidthDip = 272;
        private const int PanelHeightDip = 192;

        private readonly Settings _settings;
        private BatteryReading _reading = new BatteryReading();
        private int _hoverIndex = -1;
        private DateTime _exitArmedAt = DateTime.MinValue;
        private int _x;
        private int _y;

        public event Action RefreshRequested;
        public event Action ExitRequested;

        public PanelForm(Settings settings)
        {
            _settings = settings;
            Text = "电量胶囊";
            ShowInTaskbar = false;
            NoActivate = true;
            MouseLeave += (s, e) => { _hoverIndex = -1; Render(); };
        }

        public void SetReading(BatteryReading reading)
        {
            _reading = reading;
            if (Visible) Render();
        }

        public void ShowAbove(Rectangle pillRect, float scale)
        {
            float s = scale > 0 ? scale : 1f;
            int w = (int)Math.Round(PanelWidthDip * s);
            int h = (int)Math.Round(PanelHeightDip * s);
            _x = pillRect.Right - w;
            _y = pillRect.Top - h - (int)Math.Round(8 * s);
            Show();
            Render();
        }

        public void HidePanel()
        {
            Hide();
        }

        public bool ContainsScreen(System.Drawing.Point p)
        {
            return _x <= p.X && p.X < _x + (int)Math.Round(PanelWidthDip * DpiScale)
                && _y <= p.Y && p.Y < _y + (int)Math.Round(PanelHeightDip * DpiScale);
        }

        /// <summary>屏幕坐标命中：0/1/2=按钮，-2=面板内空白，-1=面板外。</summary>
        public int HitTest(System.Drawing.Point screen)
        {
            if (!ContainsScreen(screen)) return -1;
            float s = DpiScale;
            float x = (screen.X - _x) / s;
            float y = (screen.Y - _y) / s;
            for (int i = 0; i < 3; i++)
            {
                var r = ButtonRect(i);
                if (x >= r.X && x <= r.Right && y >= r.Y && y <= r.Bottom) return i;
            }
            return -2;
        }

        public void PerformButton(int index)
        {
            if (index == 0 && RefreshRequested != null) RefreshRequested();
            if (index == 1)
            {
                _settings.SetAutoStart(!_settings.AutoStart);
                Render();
            }
            if (index == 2)
            {
                if ((DateTime.Now - _exitArmedAt).TotalSeconds < 5 && ExitRequested != null)
                {
                    Diag.Log("quit via panel");
                    ExitRequested();
                }
                else
                {
                    _exitArmedAt = DateTime.Now;
                    Render();
                }
            }
        }

        public void UpdateHoverAt(System.Drawing.Point screen)
        {
            int index = HitTest(screen);
            if (index < 0) index = -1;
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Render();
            }
        }

        private RectangleF ButtonRect(int index)
        {
            switch (index)
            {
                case 0: return new RectangleF(18, 156, 62, 28);
                case 1: return new RectangleF(90, 156, 104, 28);
                default: return new RectangleF(198, 156, 56, 28);
            }
        }

        private void UpdateHover(Point point)
        {
            float s = DpiScale;
            int index = -1;
            for (int i = 0; i < 3; i++)
            {
                var r = ButtonRect(i);
                if (point.X >= r.X * s && point.X <= r.Right * s && point.Y >= r.Y * s && point.Y <= r.Bottom * s)
                {
                    index = i;
                    break;
                }
            }
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Render();
            }
        }

        private void HandleClick(Point point)
        {
            Diag.Log("panel click at " + point);
            float s = DpiScale;
            for (int i = 0; i < 3; i++)
            {
                var r = ButtonRect(i);
                if (point.X >= r.X * s && point.X <= r.Right * s && point.Y >= r.Y * s && point.Y <= r.Bottom * s)
                {
                    if (i == 0 && RefreshRequested != null) RefreshRequested();
                    if (i == 1)
                    {
                        _settings.SetAutoStart(!_settings.AutoStart);
                        Render();
                    }
                    if (i == 2 && ExitRequested != null) ExitRequested();
                    return;
                }
            }
        }

        private void Render()
        {
            if (!IsHandleCreated) return;
            using (var bmp = DrawBitmap(DpiScale)) Present(bmp, _x, _y);
        }

        /// <summary>离屏渲染（生成文档截图用）。</summary>
        public Bitmap DrawBitmap(float scale)
        {
            ScaleOverride = scale;
            int w = (int)Math.Round(PanelWidthDip * scale);
            int h = (int)Math.Round(PanelHeightDip * scale);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                DrawInto(g, w, h, scale);
            }
            return bmp;
        }

        private void DrawInto(Graphics g, int w, int h, float s)
        {

                var body = new RectangleF(1 * s, 1 * s, w - 2 * s, h - 2 * s);
                using (var path = Rounded.Rect(body, 13 * s))
                {
                    using (var brush = new SolidBrush(Color.FromArgb(250, 32, 32, 32)))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(Color.FromArgb(32, 255, 255, 255), Math.Max(1f, 1f * s)))
                        g.DrawPath(pen, path);
                }

                bool ok = _reading != null && _reading.Ok;
                bool hasPercent = ok && _reading.Percent >= 0;
                bool charging = _reading != null && _reading.ExternalPower;
                Color accent = hasPercent ? LevelColor(_reading.Percent) : Color.FromArgb(255, 158, 158, 158);
                if (_reading != null && _reading.Stale) accent = Color.FromArgb(255, 158, 158, 158);

                using (var titleFont = new Font("Microsoft YaHei UI", 14f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var subFont = new Font("Microsoft YaHei UI", 10.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var bigFont = new Font("Segoe UI semibold", 38f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var footFont = new Font("Microsoft YaHei UI", 10.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var white = new SolidBrush(Color.FromArgb(244, 245, 245, 245)))
                using (var grey = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                using (var accentBrush = new SolidBrush(accent))
                {
                    using (var titleFormat = new StringFormat())
                    {
                        titleFormat.Trimming = StringTrimming.EllipsisCharacter;
                        titleFormat.FormatFlags = StringFormatFlags.NoWrap;
                        string title = ok && !string.IsNullOrEmpty(_reading.DeviceName)
                            ? _reading.DeviceName : "罗技无线鼠标";
                        g.DrawString(title, titleFont, white,
                            new RectangleF(18 * s, 14 * s, (PanelWidthDip - 36) * s, 22 * s), titleFormat);
                    }
                    string sub = _reading != null && _reading.Stale ? "设备无响应 · 显示上次读数"
                               : charging ? (_reading.Full ? "已充满 · 外部供电" : "充电中 · 外部供电")
                                          : "2.4G · LIGHTSPEED 接收器";
                    g.DrawString(sub, subFont, grey, 18 * s, 36 * s);

                    bool realPercent = _reading != null && _reading.Source == "G HUB";
                    if (charging && !(realPercent && hasPercent))
                    {
                        using (var midFont = new Font("Microsoft YaHei UI", 24f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                        using (var chargeBrush = new SolidBrush(Color.FromArgb(255, 255, 201, 91)))
                            g.DrawString("充电中", midFont, chargeBrush, 18 * s, 62 * s);
                    }
                    else if (hasPercent)
                    {
                        g.DrawString(_reading.Percent + "%", bigFont, accentBrush, 16 * s, 54 * s);
                    }
                    else
                    {
                        g.DrawString("—", bigFont, accentBrush, 16 * s, 54 * s);
                    }

                    if (ok)
                    {
                        string volt = _reading.VoltageMv > 0
                            ? "电压 " + (_reading.VoltageMv / 1000.0).ToString("0.000") + " V"
                            : "";
                        string foot;
                        if (_reading.Source == "G HUB")
                        {
                            string left = _reading.MileageHours > 0
                                ? "剩余 " + _reading.MileageHours.ToString("0.#") + " 小时"
                                : (charging ? "充电中" : "电量计");
                            foot = "G HUB · " + left + " · " + _reading.Updated.ToString("HH:mm");
                        }
                        else if (charging)
                        {
                            foot = volt
                                 + (hasPercent ? " · 上次读数 " + _reading.Percent + "%" : "")
                                 + " · " + _reading.Updated.ToString("HH:mm");
                        }
                        else
                        {
                            string note = _reading.Stale ? " · 设备休眠" : "";
                            foot = volt + " · 更新 " + _reading.Updated.ToString("HH:mm:ss") + note;
                        }
                        g.DrawString(foot, footFont, grey, 18 * s, 122 * s);
                    }
                    else
                    {
                        string why = string.IsNullOrEmpty(_reading != null ? _reading.Error : null)
                            ? "未检测到设备" : _reading.Error;
                        g.DrawString(why, footFont, grey, 18 * s, 122 * s);
                    }
                }

                // 电量条
                var track = new RectangleF(18 * s, 108 * s, (PanelWidthDip - 36) * s, 6 * s);
                using (var path = Rounded.Rect(track, 3 * s))
                using (var brush = new SolidBrush(Color.FromArgb(52, 255, 255, 255)))
                    g.FillPath(brush, path);
                if (hasPercent && _reading.Percent > 0)
                {
                    float fillWidth = Math.Max(6 * s, track.Width * (_reading.Percent / 100f));
                    var fill = new RectangleF(track.X, track.Y, fillWidth, track.Height);
                    using (var path = Rounded.Rect(fill, 3 * s))
                    using (var brush = new SolidBrush(accent))
                        g.FillPath(brush, path);
                }

                // 分隔线
                using (var pen = new Pen(Color.FromArgb(26, 255, 255, 255), Math.Max(1f, 1f * s)))
                    g.DrawLine(pen, 18 * s, 146 * s, (PanelWidthDip - 18) * s, 146 * s);

                DrawButton(g, s, 0, "刷新", false);
                DrawAutoStart(g, s);
                bool armed = (DateTime.Now - _exitArmedAt).TotalSeconds < 5;
                DrawButton(g, s, 2, armed ? "确认退出" : "退出", armed);

        }

        private void DrawButton(Graphics g, float s, int index, string label, bool active)
        {
            var r = ButtonRect(index);
            var rect = new RectangleF(r.X * s, r.Y * s, r.Width * s, r.Height * s);
            if (_hoverIndex == index)
            {
                using (var path = Rounded.Rect(rect, 7 * s))
                using (var brush = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
                    g.FillPath(brush, path);
            }
            using (var font = new Font("Microsoft YaHei UI", 10.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.FromArgb(active ? 255 : 215, 240, 240, 240)))
            using (var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            })
                g.DrawString(label, font, brush, rect, format);
        }

        private void DrawAutoStart(Graphics g, float s)
        {
            var r = ButtonRect(1);
            var rect = new RectangleF(r.X * s, r.Y * s, r.Width * s, r.Height * s);
            if (_hoverIndex == 1)
            {
                using (var path = Rounded.Rect(rect, 7 * s))
                using (var brush = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
                    g.FillPath(brush, path);
            }
            var box = new RectangleF((r.X + 10) * s, (r.Y + (r.Height - 15) / 2f) * s, 15 * s, 15 * s);
            using (var path = Rounded.Rect(box, 4 * s))
            using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255), Math.Max(1f, 1f * s)))
                g.DrawPath(pen, path);
            if (_settings.AutoStart)
            {
                using (var path = Rounded.Rect(box, 4 * s))
                using (var brush = new SolidBrush(Color.FromArgb(255, 124, 217, 142)))
                    g.FillPath(brush, path);
                using (var pen = new Pen(Color.FromArgb(255, 24, 24, 24), Math.Max(1.5f, 1.6f * s)))
                {
                    g.DrawLine(pen, box.X + 3.5f * s, box.Y + 7.5f * s, box.X + 6.2f * s, box.Y + 10.4f * s);
                    g.DrawLine(pen, box.X + 6.2f * s, box.Y + 10.4f * s, box.X + 11.5f * s, box.Y + 4.2f * s);
                }
            }
            using (var font = new Font("Microsoft YaHei UI", 10.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.FromArgb(215, 240, 240, 240)))
                g.DrawString("开机自启", font, brush, (r.X + 32) * s, (r.Y + (r.Height - 15) / 2f) * s - 1 * s);
        }

        private static Color LevelColor(int percent)
        {
            if (percent >= 60) return Color.FromArgb(255, 124, 217, 142); // 绿
            if (percent >= 30) return Color.FromArgb(255, 255, 201, 91);  // 黄
            return Color.FromArgb(255, 255, 118, 118);                    // 红
        }
    }
}
