using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryPill
{
    /// <summary>
    /// 全局鼠标观察器（Raw Input + RIDEV_INPUTSINK）。
    /// Win11 的任务栏（XAML InputSite）会抓住鼠标捕获，任务栏区域里的普通 WM_LBUTTONUP
    /// 根本到不了我们的子窗口，所以点击/悬停只能靠 Raw Input 旁路监听。
    /// 只"看"不拦截，不装钩子，不影响其它程序。
    /// </summary>
    internal sealed class RawMouseWatcher : NativeWindow, IDisposable
    {
        private const int WM_INPUT = 0x00FF;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

        private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
        private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
        private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
        private const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
        private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
        private const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct RAWMOUSE
        {
            [FieldOffset(0)] public ushort usFlags;
            [FieldOffset(4)] public ushort usButtonFlags;
            [FieldOffset(6)] public ushort usButtonData;
            [FieldOffset(8)] public uint ulRawButtons;
            [FieldOffset(12)] public int lLastX;
            [FieldOffset(16)] public int lLastY;
            [FieldOffset(20)] public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUT
        {
            public RAWINPUTHEADER header;
            public RAWMOUSE mouse;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size,
            uint headerSize);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Native.POINT point);

        public event Action<Point> MouseMoved;
        public event Action<Point> LeftButtonUp;
        public event Action<Point> RightButtonUp;

        private IntPtr _target;
        private bool _registered;

        public bool Start()
        {
            var cp = new CreateParams
            {
                Caption = "BatteryPillInput",
                Style = Native.WS_POPUP,
                ExStyle = Native.WS_EX_TOOLWINDOW,
                X = 0,
                Y = 0,
                Width = 1,
                Height = 1
            };
            CreateHandle(cp);
            if (Handle == IntPtr.Zero) return false;
            _target = Handle;
            var devices = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = HID_USAGE_PAGE_GENERIC,
                    usUsage = HID_USAGE_GENERIC_MOUSE,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = _target
                }
            };
            _registered = RegisterRawInputDevices(devices, 1,
                (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            Diag.Log("rawinput registered=" + _registered + " err=" + Marshal.GetLastWin32Error());
            return _registered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                HandleRawInput(m.LParam);
            }
            base.WndProc(ref m);
        }

        private void HandleRawInput(IntPtr rawInput)
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            GetRawInputData(rawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0 || size > 1024) return;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(rawInput, RID_INPUT, buffer, ref size, headerSize) != size) return;
                var header = (RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(RAWINPUTHEADER));
                if (header.dwType != 0) return; // 只关心鼠标
                IntPtr mousePtr = new IntPtr(buffer.ToInt64() + Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                var mouse = (RAWMOUSE)Marshal.PtrToStructure(mousePtr, typeof(RAWMOUSE));
                ushort flags = mouse.usButtonFlags;
                if (flags == 0)
                {
                    if (MouseMoved != null) MouseMoved(Cursor());
                    return;
                }
                if ((flags & (RI_MOUSE_LEFT_BUTTON_DOWN | RI_MOUSE_LEFT_BUTTON_UP |
                              RI_MOUSE_RIGHT_BUTTON_DOWN | RI_MOUSE_RIGHT_BUTTON_UP |
                              RI_MOUSE_MIDDLE_BUTTON_DOWN | RI_MOUSE_MIDDLE_BUTTON_UP)) == 0)
                {
                    if (MouseMoved != null) MouseMoved(Cursor());
                    return;
                }
                var p = Cursor();
                if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) Diag.Log("raw left down " + p);
                if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) Diag.Log("raw right down " + p);
                if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0 && LeftButtonUp != null) LeftButtonUp(p);
                if ((flags & RI_MOUSE_RIGHT_BUTTON_UP) != 0 && RightButtonUp != null) RightButtonUp(p);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static Point Cursor()
        {
            Native.POINT p;
            GetCursorPos(out p);
            return new Point(p.X, p.Y);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
