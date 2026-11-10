using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BatteryPill
{
    internal sealed class HidDeviceInfo
    {
        public string Path;
        public ushort Vid;
        public ushort Pid;
        public ushort UsagePage;
        public ushort Usage;
        public ushort InputLength;
        public ushort OutputLength;
        public ushort FeatureLength;
        public string Product = "";
    }

    internal static class Hid
    {
        public static List<HidDeviceInfo> Enumerate(ushort? vendorId = null)
        {
            var list = new List<HidDeviceInfo>();
            Guid guid = Native.GUID_DEVINTERFACE_HID;
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
            try
            {
                var did = new Native.SP_DEVICE_INTERFACE_DATA();
                did.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                uint index = 0;
                while (Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref did))
                {
                    index++;
                    int required;
                    int detailSize = IntPtr.Size == 8 ? 8 + 2048 : 6 + 2048;
                    IntPtr detail = Marshal.AllocHGlobal(detailSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref did, detail, detailSize,
                                out required, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        var info = new HidDeviceInfo { Path = path };
                        IntPtr h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h != new IntPtr(-1))
                        {
                            try
                            {
                                var attrs = new Native.HIDD_ATTRIBUTES();
                                attrs.Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                                if (Native.HidD_GetAttributes(h, ref attrs))
                                {
                                    info.Vid = attrs.VendorID;
                                    info.Pid = attrs.ProductID;
                                }
                                IntPtr pre;
                                if (Native.HidD_GetPreparsedData(h, out pre))
                                {
                                    try
                                    {
                                        var caps = new Native.HIDP_CAPS();
                                        if (Native.HidP_GetCaps(pre, ref caps) == 0x00110000)
                                        {
                                            info.UsagePage = caps.UsagePage;
                                            info.Usage = caps.Usage;
                                            info.InputLength = caps.InputReportByteLength;
                                            info.OutputLength = caps.OutputReportByteLength;
                                            info.FeatureLength = caps.FeatureReportByteLength;
                                        }
                                    }
                                    finally { Native.HidD_FreePreparsedData(pre); }
                                }
                            }
                            finally { Native.CloseHandle(h); }
                        }
                        if (vendorId.HasValue && info.Vid != vendorId.Value) continue;
                        list.Add(info);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return list;
        }
    }

    /// <summary>一个带重叠 I/O 的 HID 句柄：写请求 + 限时读回复。</summary>
    internal sealed class HidHandle : IDisposable
    {
        private IntPtr _handle = new IntPtr(-1);
        private IntPtr _readEvent;
        private bool _disposed;

        public string Path { get; private set; }

        public bool Open(string path)
        {
            Path = path;
            _handle = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (_handle == new IntPtr(-1)) return false;
            _readEvent = Native.CreateEvent(IntPtr.Zero, true, false, null);
            return _readEvent != IntPtr.Zero;
        }

        public bool IsOpen { get { return _handle != new IntPtr(-1); } }

        /// <summary>写一帧，然后在 timeoutMs 内读一帧回复；超时返回 null。</summary>
        public byte[] Exchange(byte[] report, int responseLength, int timeoutMs)
        {
            if (!IsOpen) return null;
            if (!Write(report, timeoutMs)) return null;
            return Read(responseLength, timeoutMs);
        }

        private bool Write(byte[] data, int timeoutMs)
        {
            IntPtr buf = Marshal.AllocHGlobal(data.Length);
            var ov = new Native.OVERLAPPED();
            try
            {
                Marshal.Copy(data, 0, buf, data.Length);
                ov.hEvent = Native.CreateEvent(IntPtr.Zero, true, false, null);
                bool ok = Native.WriteFile(_handle, buf, (uint)data.Length, IntPtr.Zero, ref ov);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING) return false;
                    if (Native.WaitForSingleObject(ov.hEvent, (uint)timeoutMs) != 0)
                    {
                        Native.CancelIoEx(_handle, ref ov);
                        return false;
                    }
                    uint transferred;
                    if (!Native.GetOverlappedResult(_handle, ref ov, out transferred, false)) return false;
                }
                return true;
            }
            finally
            {
                if (ov.hEvent != IntPtr.Zero) Native.CloseHandle(ov.hEvent);
                Marshal.FreeHGlobal(buf);
            }
        }

        private byte[] Read(int length, int timeoutMs)
        {
            IntPtr buf = Marshal.AllocHGlobal(length);
            IntPtr ev = Native.CreateEvent(IntPtr.Zero, true, false, null);
            var ov = new Native.OVERLAPPED();
            try
            {
                ov.hEvent = ev;
                uint transferred = 0;
                bool ok = Native.ReadFile(_handle, buf, (uint)length, IntPtr.Zero, ref ov);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING) return null;
                    if (Native.WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                    {
                        // 取消后必须等 I/O 真正结束，否则缓冲区可能在归还前又被写
                        Native.CancelIoEx(_handle, ref ov);
                        Native.WaitForSingleObject(ev, 2000);
                        return null;
                    }
                    if (!Native.GetOverlappedResult(_handle, ref ov, out transferred, false)) return null;
                }
                else
                {
                    transferred = (uint)length;
                }
                if (transferred == 0) return null;
                var result = new byte[transferred];
                Marshal.Copy(buf, result, 0, (int)transferred);
                return result;
            }
            finally
            {
                if (ev != IntPtr.Zero) Native.CloseHandle(ev);
                Marshal.FreeHGlobal(buf);
            }
        }

        public byte[] ReadReport(int length, int timeoutMs)
        {
            return Read(length, timeoutMs);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_readEvent != IntPtr.Zero) Native.CloseHandle(_readEvent);
            if (_handle != new IntPtr(-1)) Native.CloseHandle(_handle);
            _handle = new IntPtr(-1);
            _readEvent = IntPtr.Zero;
        }
    }
}
