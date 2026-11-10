using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BatteryPill
{
    internal static class Rounded
    {
        public static GraphicsPath Rect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal static class Diag
    {
        private static DateTime _last;

        private static long _written;

        public static void Log(string message)
        {
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag.log");
                _written += message.Length + 16;
                if (_written > 262144)
                {
                    _written = 0;
                    try
                    {
                        string old = path + ".old";
                        if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                        if (System.IO.File.Exists(path)) System.IO.File.Move(path, old);
                    }
                    catch { }
                }
                _last = DateTime.Now;
                System.IO.File.AppendAllText(
                    path,
                    DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    internal static class CrashLog
    {
        public static void Write(string tag, Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                    DateTime.Now.ToString("HH:mm:ss") + " [" + tag + "] " + ex + Environment.NewLine);
            }
            catch { }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "BatteryPill_SingleInstance", out created))
            {
                if (Environment.GetEnvironmentVariable("BATTERYPILL_DEBUG") == "1")
                    CrashLog.Write("boot", new Exception("integrity=" + Native.IntegrityLevel() + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id));
                if (!created) return;

                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch
                {
                    try { Native.SetProcessDpiAwareness(2); }
                    catch { Native.SetProcessDPIAware(); }
                }

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => CrashLog.Write("UI", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    CrashLog.Write("AppDomain", e.ExceptionObject as Exception);

                Application.ApplicationExit += (s2, e2) => Diag.Log("app exit (clean)");
                AppDomain.CurrentDomain.ProcessExit += (s2, e2) => Diag.Log("process exit");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new PillApp())
                    Application.Run(app);
            }
        }
    }

    internal sealed class PillApp : ApplicationContext
    {
        private readonly Settings _settings;
        private readonly PillWindow _pill;
        private readonly PanelForm _panel;
        private readonly LogitechBattery _battery = new LogitechBattery();
        private readonly RawMouseWatcher _mouse = new RawMouseWatcher();
        private readonly System.Windows.Forms.Timer _timer;
        private BatteryReading _last = new BatteryReading();
        private BatteryReading _lastOnBattery;
        private BatteryReading _lastGood;
        private readonly GhubBattery _ghub = new GhubBattery();
        private DateTime _lastRead = DateTime.MinValue;
        private DateTime _lastSample = DateTime.MinValue;
        private volatile bool _busy;
        private int _ticks;

        public PillApp()
        {
            _settings = Settings.Load();
            _battery.Configure(_settings.VoltageFullMv, _settings.VoltageEmptyMv);
            _pill = new PillWindow(_settings);
            _panel = new PanelForm(_settings);

            _pill.LeftClicked += TogglePanel;
            _mouse.LeftButtonUp += point =>
            {
                if (_pill.IsAlive && _pill.Contains(point)) { TogglePanel(); return; }
                if (!_panel.Visible) return;
                int hit = _panel.HitTest(point);
                if (hit >= 0) _panel.PerformButton(hit);
                else if (hit == -1) _panel.HidePanel();
            };
            _mouse.MouseMoved += point =>
            {
                if (_pill.IsAlive) _pill.SetHover(_pill.Contains(point));
                if (_panel.Visible) _panel.UpdateHoverAt(point);
            };
            _panel.RefreshRequested += ReadNow;
            _panel.ExitRequested += ExitApp;

            // 启动时先用上次的读数垫底：鼠标还没醒也能看到数字（标灰），不至于空成“—”
            if (_settings.LastPercent >= 0)
            {
                DateTime when;
                if (!DateTime.TryParse(_settings.LastPercentTime, out when)) when = DateTime.Now;
                _lastGood = new BatteryReading
                {
                    Ok = true,
                    Stale = true,
                    Percent = _settings.LastPercent,
                    VoltageMv = _settings.LastVoltageMv,
                    PercentIsStale = true,
                    Updated = when
                };
                _pill.SetReading(_lastGood);
                _panel.SetReading(_lastGood);
            }
            Diag.Log("boot integrity=" + Native.IntegrityLevel() + " sysdpi=" + Native.GetDpiForSystem());
            _pill.Create(TaskbarGeometry.Find(_settings.Monitor));
            _mouse.Start();

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += OnTick;
            _timer.Start();

            ReadNow();
        }

        private void OnTick(object sender, EventArgs e)
        {
            _ticks++;
            if (_ticks % 2 == 0)
            {
                _pill.Ensure(TaskbarGeometry.Find(_settings.Monitor));
                _pill.Raise();
            }
            if (!_busy)
            {
                double wait = _last != null && _last.Ok && !_last.Stale ? _settings.RefreshSeconds : 10;
                if ((DateTime.Now - _lastRead).TotalSeconds >= wait) ReadNow();
            }
        }

        private void ReadNow()
        {
            Diag.Log("readnow busy=" + _busy);
            if (_busy) return;
            _busy = true;
            _lastRead = DateTime.Now;
            Task.Run(() =>
            {
                BatteryReading reading;
                GhubReading gh = null;
                try { gh = _ghub.Read(); } catch { gh = null; }
                if (gh != null && gh.Ok)
                {
                    // G HUB 的本地接口给的是真实电量（充电时也有），优先用。
                    // 但它在无线模式下偶发读不到电量计，会报 0%（同时 mileage≈0），
                    // 这时拿 HID++ 的电压交叉验证一下，别把 0% 当真。
                    BatteryReading hid = null;
                    bool suspicious = !gh.Charging && gh.Percent <= 1;
                    if (suspicious)
                    {
                        try { hid = _battery.Read(); } catch { hid = null; }
                    }

                    if (suspicious && hid != null && hid.Ok && hid.Percent >= 10)
                    {
                        reading = hid;
                        reading.Source = "HID++";
                        Diag.Log("ghub suspicious " + gh.Percent + "% -> hid " + hid.Percent + "%");
                    }
                    else if (suspicious && (hid == null || !hid.Ok))
                    {
                        reading = new BatteryReading
                        {
                            Ok = false,
                            Error = "G HUB 读数可疑（0%）且 HID++ 无响应",
                            Updated = DateTime.Now
                        };
                    }
                    else
                    {
                        reading = new BatteryReading
                        {
                            Ok = true,
                            DeviceName = gh.DeviceName,
                            Percent = gh.Percent,
                            ExternalPower = gh.Charging,
                            Charging = gh.Charging,
                            PercentIsStale = false,
                            Updated = DateTime.Now,
                            Source = "G HUB",
                            MileageHours = gh.MileageHours
                        };
                    }
                }
                else
                {
                    try { reading = _battery.Read(); }
                    catch (Exception ex)
                    {
                        reading = new BatteryReading { Ok = false, Error = ex.Message, Updated = DateTime.Now };
                    }
                    if (gh != null && !string.IsNullOrEmpty(gh.Error) && !reading.Ok)
                        reading.Error = reading.Error + " / " + gh.Error;
                }
                _busy = false;
                if (reading.Ok) _lastGood = reading;
                if (reading.Ok && !reading.ExternalPower && reading.Percent >= 3)
                {
                    _lastOnBattery = reading;
                    if (reading.Percent != _settings.LastPercent)
                    {
                        _settings.LastPercent = reading.Percent;
                        _settings.LastVoltageMv = reading.VoltageMv;
                        _settings.LastPercentTime = reading.Updated.ToString("yyyy-MM-dd HH:mm");
                        _settings.Save();
                    }
                }
                var display = reading;
                if (!reading.Ok && _lastGood != null)
                {
                    // 鼠标休眠 / 接收器暂时掉线：保留上次读数，标灰显示，别让胶囊变成“—”
                    display = new BatteryReading
                    {
                        Ok = true,
                        Stale = true,
                        DeviceName = _lastGood.DeviceName,
                        VoltageMv = _lastGood.VoltageMv,
                        ExternalPower = _lastGood.ExternalPower,
                        Charging = _lastGood.Charging,
                        Full = _lastGood.Full,
                        Percent = _lastGood.Percent,
                        PercentIsStale = true,
                        Updated = _lastGood.Updated,
                        Error = reading.Error
                    };
                }
                else if (reading.Ok && reading.Percent < 0)
                {
                    display = new BatteryReading
                    {
                        Ok = true,
                        DeviceName = reading.DeviceName,
                        VoltageMv = reading.VoltageMv,
                        ExternalPower = reading.ExternalPower,
                        Charging = reading.Charging,
                        Full = reading.Full,
                        Updated = reading.Updated,
                        Percent = _lastOnBattery != null ? _lastOnBattery.Percent : _settings.LastPercent,
                        PercentIsStale = _lastOnBattery != null || _settings.LastPercent >= 0
                    };
                }
                _last = display;
                MaybeSample(reading);
                Diag.Log("read ok=" + reading.Ok + " pct=" + reading.Percent + " mv=" + reading.VoltageMv +
                         " ext=" + reading.ExternalPower + " full=" + reading.Full + " src=" + reading.Source + " err=" + reading.Error);
                _pill.SetReading(display);
                _panel.SetReading(display);
            });
        }

        /// <summary>按 sample_minutes 记录电压样本，用来以后校准放电曲线。</summary>
        private void MaybeSample(BatteryReading reading)
        {
            if (_settings.SampleMinutes <= 0 || !reading.Ok) return;
            if ((DateTime.Now - _lastSample).TotalMinutes < _settings.SampleMinutes) return;
            _lastSample = DateTime.Now;
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "battery-log.csv");
                bool needHeader = !System.IO.File.Exists(path);
                var sb = new System.Text.StringBuilder();
                if (needHeader) sb.AppendLine("time,voltage_mv,percent,ext_power,flags");
                sb.AppendLine(string.Format("{0},{1},{2},{3},{4}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), reading.VoltageMv,
                    reading.Percent, reading.ExternalPower ? 1 : 0, reading.Flags));
                System.IO.File.AppendAllText(path, sb.ToString());
            }
            catch { }
        }

        private void TogglePanel()
        {
            Diag.Log("click visible=" + _panel.Visible);
            if (_panel.Visible)
            {
                _panel.HidePanel();
                return;
            }
            _panel.SetReading(_last);
            float scale = 1f;
            uint dpi = _pill.IsAlive ? Native.GetDpiForWindow(_pill.Handle) : 0;
            if (dpi != 0) scale = dpi / 96f;
            _panel.ShowAbove(_pill.ScreenRect, scale);
            Diag.Log("panel shown visible=" + _panel.Visible);
        }

        private void ExitApp()
        {
            _timer.Stop();
            _battery.Dispose();
            _panel.HidePanel();
            _mouse.Dispose();
            _pill.Dispose();
            ExitThread();
        }
    }
}
