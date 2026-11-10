using System;
using System.Collections.Generic;
using System.Threading;

namespace BatteryPill
{
    internal sealed class BatteryReading
    {
        public bool Ok;
        public int Percent;
        public int VoltageMv;
        public bool Charging;
        public bool ExternalPower;
        public bool Full;
        public bool PercentIsStale;
        public byte Flags;
        public bool Stale;
        public string Source = "HID++";
        public double MileageHours;
        public string DeviceName = "罗技无线鼠标";
        public bool Present;
        public DateTime Updated = DateTime.Now;
        public string Error = "";
    }

    /// <summary>
    /// 罗技 HID++ 2.0 电量读取。
    /// Bolt / Unifying 接收器在系统里表现为多个 HID 集合，控制通道是 usage page 0xFF00、
    /// 输入/输出各 20 字节的那个；电池走 feature 0x1000（百分比）或 0x1001（电压）。
    /// </summary>
    internal sealed class LogitechBattery : IDisposable
    {
        private const byte ReportLong = 0x11;
        private const int ReportLength = 20;

        private HidHandle _handle;
        private string _path;
        private byte _deviceIndex;
        private byte _batteryFeature;   // 0 = 未找到
        private bool _hasPercent;
        private string _name = "罗技无线鼠标";
        private DateTime _lastOpenAttempt = DateTime.MinValue;
        private readonly object _lock = new object();
        private int _fullMv = 4200;
        private int _emptyMv = 3500;

        public void Configure(int fullMv, int emptyMv)
        {
            if (fullMv > emptyMv && fullMv - emptyMv > 300) { _fullMv = fullMv; _emptyMv = emptyMv; }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_handle != null) { _handle.Dispose(); _handle = null; }
            }
        }

        public BatteryReading Read()
        {
            lock (_lock)
            {
                BatteryReading last = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    bool noInterface;
                    last = ReadOnce(out noInterface);
                    if (last.Ok) return last;
                    Reconnect();
                    // 接口本身都不在（鼠标深度休眠时接收器会撤掉 vendor 接口），重试没意义
                    if (noInterface) break;
                    if (attempt < 2) Thread.Sleep(180);
                }
                return last;
            }
        }

        private BatteryReading ReadOnce(out bool noInterface)
        {
            noInterface = false;
            {
                var reading = new BatteryReading { DeviceName = _name };
                try
                {
                    if (_handle == null || !_handle.IsOpen)
                    {
                        if ((DateTime.Now - _lastOpenAttempt).TotalSeconds < 3)
                            return Fail(reading, "接收器未就绪");
                        _lastOpenAttempt = DateTime.Now;
                        if (!Connect(out noInterface))
                            return Fail(reading, noInterface ? "接收器不在（可能休眠）" : "设备无响应（可能休眠）");
                    }

                    byte[] resp = Request(_batteryFeature, 0x00, new byte[0]);
                    if (resp == null)
                    {
                        // 超时：鼠标可能在休眠 / 已关机，重连一次
                        Reconnect();
                        return Fail(reading, "设备无响应");
                    }

                    if (_hasPercent)
                    {
                        byte level = resp[4];
                        byte chargeState = resp[6];
                        reading.Ok = true;
                        reading.Present = true;
                        reading.Percent = Math.Min(100, (int)level);
                        reading.Charging = chargeState == 0x01 || chargeState == 0x02 || chargeState == 0x03;
                        return reading;
                    }

                    // 0x1001 第 3 字节：bit0..2 = chargeStatus，bit5 = critical，bit7 = extPower
                    byte flags = resp[6];
                    reading.Flags = flags;
                    int chargeStatus = flags & 0x07;
                    reading.ExternalPower = (flags & 0x80) != 0;
                    reading.Full = chargeStatus == 1;
                    reading.Charging = reading.ExternalPower && (chargeStatus == 0 || chargeStatus == 1);

                    int mv = (resp[4] << 8) | resp[5];
                    if (mv < 2000 || mv > 5000)
                    {
                        // 读到一帧不相干的报文时再问一次
                        resp = Request(_batteryFeature, 0x00, new byte[0]);
                        if (resp == null) return Fail(reading, "设备无响应");
                        mv = (resp[4] << 8) | resp[5];
                        if (mv < 2000 || mv > 5000) return Fail(reading, "电压异常 " + mv + "mV");
                    }
                    reading.Ok = true;
                    reading.Present = true;
                    reading.VoltageMv = mv;
                    if (reading.Full) reading.Percent = 100;
                    else if (reading.ExternalPower) reading.Percent = -1; // 充电中电压被抬高，算不出真实电量
                    else reading.Percent = VoltageToPercent(mv, _fullMv, _emptyMv);
                    return reading;
                }
                catch (Exception ex)
                {
                    Reconnect();
                    return Fail(reading, ex.Message);
                }
            }
        }

        private static BatteryReading Fail(BatteryReading r, string why)
        {
            r.Ok = false;
            r.Present = false;
            r.Error = why;
            r.Updated = DateTime.Now;
            return r;
        }

        private void Reconnect()
        {
            if (_handle != null) { _handle.Dispose(); _handle = null; }
            _batteryFeature = 0;
            _lastOpenAttempt = DateTime.MinValue;
        }

        private bool Connect(out bool noInterface)
        {
            noInterface = true;
            List<HidDeviceInfo> devices = Hid.Enumerate(0x046D);
            foreach (var dev in devices)
            {
                if (dev.UsagePage != 0xFF00) continue;
                if (dev.InputLength != ReportLength || dev.OutputLength != ReportLength) continue;

                var handle = new HidHandle();
                if (!handle.Open(dev.Path)) { handle.Dispose(); continue; }

                // 清掉可能残留的旧回复
                Drain(handle);

                noInterface = false;
                // 先试设备索引 1（常见情况），不行再扫其它索引
                foreach (byte idx in new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0xFF })
                {
                    byte[] status = Request(handle, idx, 0x00, 0x00, new byte[] { 0x10, 0x00 });
                    if (status != null && status[4] != 0)
                    {
                        _handle = handle;
                        _path = dev.Path;
                        _deviceIndex = idx;
                        _batteryFeature = status[4];
                        _hasPercent = true;
                        TryReadName(handle, idx);
                        return true;
                    }
                    byte[] voltage = Request(handle, idx, 0x00, 0x00, new byte[] { 0x10, 0x01 });
                    if (voltage != null && voltage[4] != 0)
                    {
                        _handle = handle;
                        _path = dev.Path;
                        _deviceIndex = idx;
                        _batteryFeature = voltage[4];
                        _hasPercent = false;
                        TryReadName(handle, idx);
                        return true;
                    }
                }
                handle.Dispose();
            }
            return false;
        }

        private static void Drain(HidHandle handle)
        {
            for (int i = 0; i < 4; i++)
            {
                var junk = handle.ReadReport(ReportLength, 40);
                if (junk == null) break;
            }
        }

        private void TryReadName(HidHandle handle, byte idx)
        {
            // feature 0x0005 = DEVICE_NAME：部分固件返回空，失败就用默认名
            byte[] feature = Request(handle, idx, 0x00, 0x00, new byte[] { 0x00, 0x05 });
            if (feature == null || feature[4] == 0) return;
            byte nameFeature = feature[4];
            byte[] resp = Request(handle, idx, nameFeature, 0x00, new byte[0]);
            if (resp == null) return;
            int len = resp[4];
            if (len <= 0 || len > 16) return;
            string name = "";
            for (int i = 0; i < len; i++)
            {
                byte c = resp[5 + i];
                if (c >= 32 && c < 127) name += (char)c;
            }
            name = name.Trim();
            if (name.Length >= 3) _name = name;
        }

        private byte[] Request(byte featureIndex, byte function, byte[] parameters)
        {
            return Request(_handle, _deviceIndex, featureIndex, function, parameters);
        }

        private byte[] Request(HidHandle handle, byte deviceIndex, byte featureIndex, byte function, byte[] parameters)
        {
            var buf = new byte[ReportLength];
            buf[0] = ReportLong;
            buf[1] = deviceIndex;
            buf[2] = featureIndex;
            buf[3] = (byte)((function & 0x0F) << 4);
            Array.Copy(parameters, 0, buf, 4, Math.Min(parameters.Length, ReportLength - 4));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var resp = handle.Exchange(buf, ReportLength, 300);
                if (resp == null || resp.Length < ReportLength) continue;
                // 只接受和本次请求匹配的回复（feature/function 对得上），避免读到通知报文
                if (resp[2] == featureIndex && (resp[3] & 0xF0) == ((function & 0x0F) << 4))
                    return resp;
            }
            return null;
        }

        /// <summary>电压 → 百分比（锂聚合物 3.5V~4.2V，按经验曲线插值）。</summary>
        // 归一化到 0..1 的锂电放电曲线（0=空，1=满），端点可由 settings.ini 校准
        private static readonly double[] Curve = { 0, 0.143, 0.214, 0.286, 0.357, 0.429, 0.5, 0.571, 0.643, 0.714, 0.786, 0.857, 0.929, 1.0 };
        private static readonly int[] Pcts = { 0, 7, 14, 23, 33, 43, 53, 63, 72, 80, 87, 93, 97, 100 };

        public static int VoltageToPercent(int mv, int fullMv, int emptyMv)
        {
            if (fullMv <= emptyMv) { fullMv = 4200; emptyMv = 3500; }
            double t = (mv - emptyMv) / (double)(fullMv - emptyMv);
            if (t <= 0) return 0;
            if (t >= 1) return 100;
            for (int i = 1; i < Curve.Length; i++)
            {
                if (t < Curve[i])
                {
                    double k = (t - Curve[i - 1]) / (Curve[i] - Curve[i - 1]);
                    return (int)Math.Round(Pcts[i - 1] + k * (Pcts[i] - Pcts[i - 1]));
                }
            }
            return 100;
        }
    }
}
