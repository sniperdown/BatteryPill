using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryPill
{
    internal sealed class Settings
    {
        public int RefreshSeconds = 60;
        public int OffsetRightDip = 118;
        public int PillWidthDip = 100;
        public int PillHeightDip = 32;
        public string Monitor = "secondary";
        public bool AutoStart;
        public int SampleMinutes = 60;
        public int VoltageFullMv = 4200;
        public int VoltageEmptyMv = 3500;
        public int LastPercent = -1;
        public int LastVoltageMv;
        public string LastPercentTime = "";

        private static string ConfigPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    foreach (var raw in File.ReadAllLines(ConfigPath))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string val = line.Substring(eq + 1).Trim();
                        switch (key)
                        {
                            case "refresh": s.RefreshSeconds = Math.Max(15, ParseInt(val, s.RefreshSeconds)); break;
                            case "offset_right": s.OffsetRightDip = Math.Max(0, ParseInt(val, s.OffsetRightDip)); break;
                            case "pill_width": s.PillWidthDip = Math.Max(60, ParseInt(val, s.PillWidthDip)); break;
                            case "pill_height": s.PillHeightDip = Math.Max(20, ParseInt(val, s.PillHeightDip)); break;
                            case "monitor": s.Monitor = val; break;
                            case "sample_minutes": s.SampleMinutes = Math.Max(0, ParseInt(val, 60)); break;
                            case "voltage_full": s.VoltageFullMv = ParseInt(val, 4200); break;
                            case "voltage_empty": s.VoltageEmptyMv = ParseInt(val, 3500); break;
                            case "last_percent": s.LastPercent = ParseInt(val, -1); break;
                            case "last_voltage": s.LastVoltageMv = ParseInt(val, 0); break;
                            case "last_percent_time": s.LastPercentTime = val; break;
                        }
                    }
                }
            }
            catch { /* 配置坏了就用默认值 */ }
            s.AutoStart = s.IsAutoStartEnabled();
            if (!File.Exists(ConfigPath)) s.Save();
            return s;
        }

        public void Save()
        {
            try
            {
                var lines = new List<string>
                {
                    "# 电量胶囊 配置文件（改完重启程序生效）",
                    "refresh = " + RefreshSeconds,
                    "offset_right = " + OffsetRightDip,
                    "pill_width = " + PillWidthDip,
                    "pill_height = " + PillHeightDip,
                    "monitor = " + Monitor,
                    "sample_minutes = " + SampleMinutes,
                    "voltage_full = " + VoltageFullMv,
                    "voltage_empty = " + VoltageEmptyMv,
                    "last_percent = " + LastPercent,
                    "last_voltage = " + LastVoltageMv,
                    "last_percent_time = " + LastPercentTime,
                };
                File.WriteAllLines(ConfigPath, lines);
            }
            catch { }
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "BatteryPill";

        public bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    var value = key.GetValue(RunValue) as string;
                    return !string.IsNullOrEmpty(value) &&
                           value.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        public void SetAutoStart(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (on) key.SetValue(RunValue, "\"" + ExePath + "\" --autostart");
                    else key.DeleteValue(RunValue, false);
                }
                AutoStart = on;
            }
            catch { }
        }

        public static string ExePath
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }
    }
}
