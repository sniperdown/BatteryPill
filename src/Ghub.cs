using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace BatteryPill
{
    internal sealed class GhubReading
    {
        public bool Ok;
        public int Percent = -1;
        public bool Charging;
        public double MileageHours;
        public string DeviceName = "罗技无线鼠标";
        public string Error = "";
    }

    /// <summary>
    /// 从 G HUB 的本地 WebSocket 读真实电量（lghub_agent.exe 监听 ws://localhost:9010）。
    /// 这套协议是从 LGSTrayBattery_GHUB（andyvorld）逆向的代码里拿的：
    ///   握手头 Origin: file:// · Sec-WebSocket-Protocol: json
    ///   {"msgId":"","verb":"GET","path":"/devices/list"}
    ///   {"msgId":"","verb":"GET","path":"/battery/&lt;deviceId&gt;/state"}
    ///   回复 payload: {percentage, mileage, charging, ...}
    /// 好处：插线充电时也有真实百分比（HID++ 只给电压，充电时电压跟电量无关）。
    /// G HUB 没开时端口拒绝连接，调用方回落到 HID++。
    /// </summary>
    internal sealed class GhubBattery
    {
        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer { MaxJsonLength = 8 << 20 };

        private const string Url = "ws://127.0.0.1:9010";

        private readonly object _lock = new object();
        private ClientWebSocket _ws;
        private string _deviceId;
        private string _deviceName;
        private DateTime _retryAfter = DateTime.MinValue;
        private string _lastError = "";

        public string LastError { get { return _lastError; } }

        public GhubReading Read()
        {
            lock (_lock)
            {
                var r = new GhubReading();
                try
                {
                    if (!EnsureOpen()) { r.Error = _lastError; return r; }
                    if (_deviceId == null && !Discover()) { r.Error = _lastError; Close(); return r; }

                    Dictionary<string, object> payload = Request("/battery/" + _deviceId + "/state", 2500);
                    if (payload == null)
                    {
                        r.Error = _lastError;
                        Close();
                        return r;
                    }

                    r.Percent = ToInt(Get(payload, "percentage"), -1);
                    r.Charging = ToBool(Get(payload, "charging"));
                    r.MileageHours = ToDouble(Get(payload, "mileage"), 0);
                    r.DeviceName = _deviceName ?? "罗技无线鼠标";
                    r.Ok = r.Percent >= 0 && r.Percent <= 100;
                    if (!r.Ok) r.Error = "G HUB 未返回百分比";
                }
                catch (Exception ex)
                {
                    r.Error = ex.Message;
                    Close();
                }
                return r;
            }
        }

        // ---------- 连接 ----------

        private bool EnsureOpen()
        {
            if (_ws != null && _ws.State == WebSocketState.Open) return true;
            if (DateTime.Now < _retryAfter) { _lastError = "G HUB 未连接"; return false; }
            Close();
            try
            {
                var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin", "file://");
                ws.Options.SetRequestHeader("Pragma", "no-cache");
                ws.Options.SetRequestHeader("Cache-Control", "no-cache");
                ws.Options.AddSubProtocol("json");

                using (var cts = new CancellationTokenSource(1500))
                    ws.ConnectAsync(new Uri(Url), cts.Token).GetAwaiter().GetResult();

                _ws = ws;
                _lastError = "";
                return true;
            }
            catch (Exception ex)
            {
                _lastError = "连不上 G HUB（" + Short(ex) + "）";
                _retryAfter = DateTime.Now.AddSeconds(15);
                Close();
                return false;
            }
        }

        private bool Discover()
        {
            Dictionary<string, object> payload = Request("/devices/list", 2500);
            if (payload == null) return false;

            object infosObj = Get(payload, "deviceInfos");
            var infos = infosObj as object[];
            if (infos == null) { _lastError = "G HUB 没返回设备列表"; return false; }

            foreach (object item in infos)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                string type = Convert.ToString(Get(d, "deviceType"));
                if (type != null && type.IndexOf("MOUSE", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _deviceId = Convert.ToString(Get(d, "id"));
                    string name = Convert.ToString(Get(d, "extendedDisplayName"));
                    _deviceName = string.IsNullOrEmpty(name) ? "罗技无线鼠标" : name;
                    return _deviceId != null;
                }
            }
            _lastError = "G HUB 里没有鼠标";
            return false;
        }

        /// <summary>发一条 GET，等到 path 对得上的回复，返回 payload。</summary>
        private Dictionary<string, object> Request(string path, int timeoutMs)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return null;
            try
            {
                string body = Json.Serialize(new Dictionary<string, object>
                {
                    { "msgId", "" },
                    { "verb", "GET" },
                    { "path", path }
                });
                Send(body);

                DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
                while (DateTime.Now < deadline)
                {
                    string text = Receive((int)Math.Max(200, (deadline - DateTime.Now).TotalMilliseconds));
                    if (text == null) continue;

                    Dictionary<string, object> msg;
                    try { msg = Json.DeserializeObject(text) as Dictionary<string, object>; }
                    catch { continue; }
                    if (msg == null) continue;

                    string msgPath = Convert.ToString(Get(msg, "path"));
                    if (msgPath == null || !msgPath.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;

                    object result = Get(msg, "result");
                    var res = result as Dictionary<string, object>;
                    if (res != null)
                    {
                        string code = Convert.ToString(Get(res, "code"));
                        if (code != null && !code.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
                        {
                            _lastError = "G HUB 回 " + code;
                            return null;
                        }
                    }
                    return Get(msg, "payload") as Dictionary<string, object>;
                }
                _lastError = "G HUB 无响应";
                return null;
            }
            catch (Exception ex)
            {
                _lastError = Short(ex);
                Close();
                return null;
            }
        }

        private void Send(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            using (var cts = new CancellationTokenSource(1500))
                _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token)
                   .GetAwaiter().GetResult();
        }

        private string Receive(int timeoutMs)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return null;
            var buffer = new byte[16 * 1024];
            var sb = new StringBuilder();
            try
            {
                using (var cts = new CancellationTokenSource(Math.Max(200, timeoutMs)))
                {
                    while (true)
                    {
                        var res = _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token)
                                     .GetAwaiter().GetResult();
                        if (res.MessageType == WebSocketMessageType.Close)
                        {
                            Close();
                            return null;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                        if (res.EndOfMessage) return sb.ToString();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception)
            {
                Close();
                return null;
            }
        }

        private void Close()
        {
            try
            {
                if (_ws != null)
                {
                    try { _ws.Dispose(); } catch { }
                }
            }
            finally
            {
                _ws = null;
                _deviceId = null;
                _deviceName = null;
            }
        }

        // ---------- 小工具 ----------

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        private static int ToInt(object v, int fallback)
        {
            if (v == null) return fallback;
            try { return Convert.ToInt32(v); } catch { return fallback; }
        }

        private static double ToDouble(object v, double fallback)
        {
            if (v == null) return fallback;
            try { return Convert.ToDouble(v); } catch { return fallback; }
        }

        private static bool ToBool(object v)
        {
            if (v == null) return false;
            try { return Convert.ToBoolean(v); } catch { return false; }
        }

        private static string Short(Exception ex)
        {
            string m = ex.Message ?? "";
            return m.Length > 60 ? m.Substring(0, 60) : m;
        }
    }
}
