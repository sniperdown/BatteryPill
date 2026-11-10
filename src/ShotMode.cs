using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace BatteryPill
{
    /// <summary>
    /// 生成文档截图：把胶囊和面板离屏画出来存成 PNG（不碰任务栏、不截图桌面）。
    /// 用法：BatteryPill.exe --shot &lt;目录&gt; [缩放，默认 2]
    /// </summary>
    internal static class ShotMode
    {
        public static void Run(string dir, float scale)
        {
            Directory.CreateDirectory(dir);
            var settings = Settings.Load();
            var pill = new PillWindow(settings);
            var panel = new PanelForm(settings);

            var normal = new BatteryReading
            {
                Ok = true,
                DeviceName = "G502 LIGHTSPEED Wireless Gaming Mouse",
                Percent = 88,
                ExternalPower = false,
                Updated = DateTime.Now,
                Source = "G HUB",
                MileageHours = 24.6
            };

            var charging = new BatteryReading
            {
                Ok = true,
                DeviceName = normal.DeviceName,
                Percent = 88,
                ExternalPower = true,
                Charging = true,
                Updated = DateTime.Now,
                Source = "G HUB",
                MileageHours = 24.6
            };

            var asleep = new BatteryReading
            {
                Ok = true,
                Stale = true,
                DeviceName = normal.DeviceName,
                Percent = 88,
                PercentIsStale = true,
                Updated = DateTime.Now.AddMinutes(-12),
                Source = "HID++"
            };

            var low = new BatteryReading
            {
                Ok = true,
                DeviceName = normal.DeviceName,
                Percent = 14,
                Updated = DateTime.Now,
                Source = "HID++",
                VoltageMv = 3580
            };

            Shot(pill, panel, dir, "pill", normal, scale, panelShot: false);
            Shot(pill, panel, dir, "pill_charging", charging, scale, panelShot: false);
            Shot(pill, panel, dir, "pill_asleep", asleep, scale, panelShot: false);
            Shot(pill, panel, dir, "pill_low", low, scale, panelShot: false);
            Shot(pill, panel, dir, "panel", normal, scale, panelShot: true);
            Shot(pill, panel, dir, "panel_charging", charging, scale, panelShot: true);
        }

        private static void Shot(PillWindow pill, PanelForm panel, string dir, string name,
                                 BatteryReading reading, float scale, bool panelShot)
        {
            pill.SetReading(reading);
            panel.SetReading(reading);
            using (Bitmap bmp = panelShot ? panel.DrawBitmap(scale) : pill.DrawBitmap(
                       (int)Math.Round(100 * scale), (int)Math.Round(32 * scale), scale))
            {
                bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
            }
        }
    }
}
