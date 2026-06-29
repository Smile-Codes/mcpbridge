using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MCPBridge
{
    [InitializeOnLoad]
    public static class MCPServer
    {
        // ใช้ TcpListener (raw socket) แทน HttpListener
        // เหตุผล: HttpListener ผูกกับ HTTP.sys (kernel) ถ้า Unity crash/force-quit
        // → kernel ถือ port ค้างเป็น zombie จน reboot. TcpListener คืน port ทันทีที่ process ตาย.
        // ── Multi-instance: Main=23457, Clone N=23458+N (ParrelSync) — bridge เลือก target จาก registry ──
        const int BASE_PORT = 23457;
        const int PORT_RANGE = 10;   // 23457..23466 — กัน auto-increment วิ่งไกล
        static int _port;            // port ที่ instance นี้ bind จริง

        static TcpListener _listener;
        static Thread _thread;
        static volatile bool _running;

        // ── identity ของ instance นี้ (ตรวจจาก ParrelSync) ──────────────────────
        static string _label;        // "Main" / "Clone 0" / "Clone 1"
        public static string Label => _label ?? (_label = DetectLabel());
        public static int Port => _port;   // port ที่ bind จริง (0 = ยังไม่ start)

        // ตรวจว่าเป็น ParrelSync clone ไหม + index — ใช้ path ของ project (ไม่ผูก asmdef กับ ParrelSync)
        static string DetectLabel()
        {
            try
            {
                // Application.dataPath = ".../<ProjectName>[_clone_N]/Assets"
                string proj = Directory.GetParent(Application.dataPath)?.Name ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(proj, @"_clone_(\d+)$");
                if (m.Success) return $"Clone {m.Groups[1].Value}";
                // ยืนยันด้วย ParrelSync API ถ้ามี (reflection) — IsClone() == true แต่ path ไม่เข้า pattern
                var t = FindType("ParrelSync.ClonesManager");
                var mi = t?.GetMethod("IsClone", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (mi != null && mi.Invoke(null, null) is bool b && b) return "Clone";
                return "Main";
            }
            catch { return "Main"; }
        }

        static int CloneIndex()
        {
            var m = System.Text.RegularExpressions.Regex.Match(Label, @"(\d+)");
            return m.Success ? int.Parse(m.Value) + 1 : 0;   // Main=0 → 23457, Clone 0=1 → 23458
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullName); if (t != null) return t; } catch { }
            }
            return null;
        }

        // SessionState: persist ผ่าน domain reload (recompile) แต่ reset ตอนเปิด Unity ใหม่
        // → ใช้แทน EditorPrefs เพื่อแยก "recompile" ออกจาก "เปิด Unity ใหม่"
        static bool WasRunning
        {
            get => SessionState.GetBool("DeltaMCP_WasRunning", false);
            set => SessionState.SetBool("DeltaMCP_WasRunning", value);
        }

        static MCPServer()
        {
            MCPHandlers.LoadLog();

            // .mcp.json เต็มเสมอ → Node bridge โหลดทุก session → unity_ping เรียกได้ตลอด
            // (gate จริงอยู่ที่ /unity-mcp-open + TcpListener ON/OFF ไม่ใช่ที่ .mcp.json)
            EnsureMcpJson();

            if (WasRunning)
                Start();          // recompile กลางคัน → restore TcpListener อัตโนมัติ
            else
            {
                WritePresence(false);   // เปิด editor แต่ server OFF → ลงทะเบียนให้ AI เห็น (เลือกมาสั่ง start ได้)
                StartWatching();        // เฝ้า trigger file รอ AI สั่งเปิด
            }

            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                MCPHandlers.SaveLog();
                StopInternal();   // stop listener (presence → serverOn:false, domain ใหม่ rewrite)
            };
            EditorApplication.quitting += () =>
            {
                MCPHandlers.SaveLog();
                StopInternal();
                RemovePresence();   // ปิด editor จริง → ถอน presence ออกจาก registry
            };
        }

        public static bool IsRunning => _running;

        const string MENU_START = "MCP Bridge/Server/Start";
        const string MENU_STOP  = "MCP Bridge/Server/Stop";

        [MenuItem(MENU_START, true)]
        static bool ValidateStart()
        {
            Menu.SetChecked(MENU_START, _running);
            return !_running;
        }

        [MenuItem(MENU_STOP, true)]
        static bool ValidateStop()
        {
            Menu.SetChecked(MENU_STOP, !_running);
            return _running;
        }

        // ── Toggle: อนุญาตคำสั่งที่แก้ scene/asset (default = ปิด = read-only) ──
        const string MENU_WRITE = "MCP Bridge/Allow Write Commands";

        [MenuItem(MENU_WRITE, true)]
        static bool ValidateWrite()
        {
            Menu.SetChecked(MENU_WRITE, MCPHandlers.AllowWrites);
            return true;
        }

        [MenuItem(MENU_WRITE)]
        static void ToggleWrite()
        {
            MCPHandlers.AllowWrites = !MCPHandlers.AllowWrites;
            Debug.Log($"[MCP] Write commands {(MCPHandlers.AllowWrites ? "ENABLED" : "disabled (read-only)")}");
        }

        // ── Toggle: log raw CLI stream → ดู format จริงตอน debug ──
        const string MENU_CLIDEBUG = "MCP Bridge/Debug CLI Stream";

        [MenuItem(MENU_CLIDEBUG, true)]
        static bool ValidateCliDebug()
        {
            Menu.SetChecked(MENU_CLIDEBUG, EditorPrefs.GetBool("DeltaMCP_CliDebug", false));
            return true;
        }

        [MenuItem(MENU_CLIDEBUG)]
        static void ToggleCliDebug()
        {
            bool v = !EditorPrefs.GetBool("DeltaMCP_CliDebug", false);
            EditorPrefs.SetBool("DeltaMCP_CliDebug", v);
            Debug.Log($"[MCP] CLI stream debug {(v ? "ON — ดู [MCP stream] ใน Console" : "OFF")}");
        }

        [MenuItem(MENU_START)]
        public static void Start()
        {
            if (_running) return;

            CleanTrigger();        // เคลียร์ trigger file ค้าง กัน re-trigger ไม่ตั้งใจ
            StopInternal();        // กันซ้อน: ถ้ามี listener/thread เก่าค้าง ปิดให้สนิทก่อน (idempotent)

            if (!TryBind())
            {
                Debug.LogWarning($"[MCP] เปิด server ไม่ได้: ไม่มี port ว่างในช่วง {BASE_PORT}..{BASE_PORT + PORT_RANGE - 1} (instance อื่นถือหมด/zombie)");
                StartWatching();   // ยังอยู่ OFF → เฝ้า trigger ต่อ
                return;
            }

            _running = true;
            WasRunning = true;
            StopWatching();      // server ON → ไม่ต้องเฝ้า trigger แล้ว (overhead = 0)
            WritePresence(true); // presence → serverOn:true + actual port
            _thread = new Thread(Listen) { IsBackground = true, Name = "MCP-Server" };
            _thread.Start();
            Debug.Log($"[MCP] Server started — {Label} @ port {_port}");
        }

        // bind port: เลือกตาม clone index ก่อน (Main=23457, Clone N=23458+N) แล้ว scan ช่วงที่เหลือ
        // ไม่ใช้ ReuseAddress → bind ล้มเหลวถ้า port ถูก instance อื่นถืออยู่ (ได้ port ต่างกันจริง)
        static bool TryBind()
        {
            int prefer = BASE_PORT + CloneIndex();
            for (int i = 0; i < PORT_RANGE; i++)
            {
                int p = BASE_PORT + (((prefer - BASE_PORT) + i) % PORT_RANGE);
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    _listener = l;
                    _port = p;
                    return true;
                }
                catch { try { _listener?.Server?.Dispose(); } catch { } _listener = null; }
            }
            return false;
        }

        [MenuItem(MENU_STOP)]
        public static void Stop()
        {
            WasRunning = false;
            StopInternal();   // ปิด TcpListener เท่านั้น — ไม่แตะ .mcp.json (bridge ยังโหลดได้)
        }

        // หยุด TcpListener โดยไม่แตะ WasRunning (ใช้ตอน recompile/quit)
        static void StopInternal()
        {
            if (!_running && _listener == null) return;
            _running = false;
            WritePresence(false);   // server ปิดแต่ editor ยังเปิด → presence serverOn:false (ยังเห็นใน list, AI สั่ง start ได้)
            try { _listener?.Stop(); } catch { }
            try { _listener?.Server?.Dispose(); } catch { }   // ปิด socket ให้สนิท คืน port (กัน zombie)
            _listener = null;
            try { _thread?.Join(300); } catch { }
            _thread = null;
            _lastWatchCheck = 0;   // reset throttle → watcher เช็ครอบแรกเร็วหลัง stop
            StartWatching();       // server OFF → กลับมาเฝ้า trigger รอ AI สั่งเปิด
            Debug.Log("[MCP] Server stopped");
        }

        // ── Presence registry: ทุก editor (server ON/OFF) เขียนไฟล์ลง shared dir ──
        // key = PID → <originalRoot>/Library/DeltaMCP/instances/<pid>.json
        // {pid, label, project, projectPath, port, serverOn} — AI อ่านเพื่อ list/select/start
        // env-independent: ใช้ Library ของ "original project" (delta-unity)
        //   clone เป็น sibling ชื่อ <orig>_clone_N → ตัด suffix → ได้ root เดียวกับที่ bridge
        //   คำนวณจาก path ของ index.js (ไม่ผูก %TEMP%/HOME ที่ Claude Code spawn อาจเห็นคนละตัว)
        static string InstancesDir()
        {
            string projRoot = Directory.GetParent(Application.dataPath).FullName;   // .../delta-unity[_clone_N]
            string parent   = Directory.GetParent(projRoot).FullName;              // parent ร่วม
            string name     = Path.GetFileName(projRoot);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*)_clone_\d+$");
            string origRoot = m.Success ? Path.Combine(parent, m.Groups[1].Value) : projRoot;
            string dir = Path.Combine(origRoot, "Library", "DeltaMCP", "instances");
            Directory.CreateDirectory(dir);
            return dir;
        }

        static int Pid => System.Diagnostics.Process.GetCurrentProcess().Id;
        static string PresencePath() => Path.Combine(InstancesDir(), $"{Pid}.json");

        // เขียน presence ของ editor นี้ — serverOn:true → port จริง, false → preferred port
        static void WritePresence(bool serverOn)
        {
            try
            {
                SweepRegistry();   // ล้าง entry ของ editor ที่ตายไปแล้วก่อน
                string projPath = Directory.GetParent(Application.dataPath)?.FullName ?? "";
                string proj = Path.GetFileName(projPath);
                int port = serverOn ? _port : (BASE_PORT + CloneIndex());   // off → preferred
                string json = $"{{\"pid\":{Pid},\"label\":\"{Escape(Label)}\",\"project\":\"{Escape(proj)}\","
                            + $"\"projectPath\":\"{Escape(projPath.Replace("\\", "/"))}\",\"port\":{port},"
                            + $"\"serverOn\":{(serverOn ? "true" : "false")}}}";
                // UTF8 ไม่ใส่ BOM — ไม่งั้น Node JSON.parse พัง (Encoding.UTF8 ใส่ BOM นำหน้า)
                File.WriteAllText(PresencePath(), json, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { Debug.LogWarning($"[MCP] WritePresence failed: {e.Message}"); }
        }

        static void RemovePresence()
        {
            try { File.Delete(PresencePath()); } catch { }
        }

        // ลบ entry ที่ process ตายแล้ว (editor crash ไม่ได้ถอน presence)
        static void SweepRegistry()
        {
            try
            {
                foreach (var f in Directory.GetFiles(InstancesDir(), "*.json"))
                {
                    try
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(f), "\"pid\"\\s*:\\s*(\\d+)");
                        if (!m.Success) continue;
                        try { System.Diagnostics.Process.GetProcessById(int.Parse(m.Groups[1].Value)); }  // ตาย → throw
                        catch { File.Delete(f); }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ── Watcher: เฝ้า trigger file รอ AI สั่งเปิด (เฉพาะตอน server OFF) ──────
        // throttle ~1.5 วิ/ครั้ง · subscribe เฉพาะตอน OFF · unsubscribe ตอน ON (overhead 0)
        // editor-only — ถูกตัดออกจาก build เกมจริง 100%
        static bool   _watching;
        static double _lastWatchCheck;
        const  double WATCH_INTERVAL = 1.5;

        static string RequestStartPath()
        {
            string dir = Path.Combine(Application.dataPath, "..", "Library", "DeltaMCP");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "request-start");
        }

        // ลบ trigger file ค้าง (ตอน start) — กัน stale trigger ทำให้ re-trigger ไม่ตั้งใจ
        static void CleanTrigger()
        {
            try { var p = RequestStartPath(); if (File.Exists(p)) File.Delete(p); }
            catch { }
        }

        static void StartWatching()
        {
            if (_watching) return;
            EditorApplication.update += WatchUpdate;
            _watching = true;
        }

        static void StopWatching()
        {
            if (!_watching) return;
            EditorApplication.update -= WatchUpdate;
            _watching = false;
        }

        static void WatchUpdate()
        {
            if (_running) { StopWatching(); return; }   // safety
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastWatchCheck < WATCH_INTERVAL) return;
            _lastWatchCheck = now;
            try
            {
                string p = RequestStartPath();
                if (File.Exists(p))
                {
                    File.Delete(p);   // กิน trigger ก่อน กันวน
                    Debug.Log("[MCP] request-start detected → AI สั่งเปิด server");
                    Start();          // Start() จะ StopWatching() ให้เอง
                }
            }
            catch (Exception e) { Debug.LogWarning($"[MCP] watch error: {e.Message}"); }
        }

        // ── .mcp.json — เขียน config เต็มถ้ายังไม่มี/ไม่ตรง ───────────────────
        // เต็มเสมอ → Node bridge โหลดทุก session → unity_ping เรียกได้ตลอด
        // (server ON/OFF จริงอยู่ที่ TcpListener ไม่ใช่ที่ไฟล์นี้)
        static void EnsureMcpJson()
        {
            try
            {
                string projectRoot = Path.Combine(Application.dataPath, "..");
                string mcpPath     = Path.Combine(projectRoot, ".mcp.json");
                const string full  = "{\n  \"mcpServers\": {\n    \"unity\": {\n      \"command\": \"node\",\n      \"args\": [\"./Tools/unity-mcp-server/index.js\"]\n    }\n  }\n}\n";
                // เขียนเฉพาะถ้าไฟล์ไม่มี หรือ content ไม่ตรง (กัน write ซ้ำทุก domain reload)
                if (!File.Exists(mcpPath) || File.ReadAllText(mcpPath).Trim() != full.Trim())
                    File.WriteAllText(mcpPath, full, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { Debug.LogWarning($"[MCP] EnsureMcpJson failed: {e.Message}"); }
        }

        static void Listen()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch (SocketException) { break; }       // listener ถูก Stop()
                catch (ObjectDisposedException) { break; }
                catch (Exception e) { Debug.LogError($"[MCP] Listen error: {e.Message}"); }
            }
        }

        static void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    client.ReceiveTimeout = 5000;

                    // ── อ่าน HTTP request: header จน "\r\n\r\n" แล้วอ่าน body ตาม Content-Length ──
                    var ms = new MemoryStream();
                    var buf = new byte[8192];
                    int headerEnd = -1, contentLength = 0;

                    while (true)
                    {
                        int n = stream.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        ms.Write(buf, 0, n);
                        var data = ms.GetBuffer();
                        int len = (int)ms.Length;

                        if (headerEnd < 0)
                        {
                            headerEnd = IndexOfCrlfCrlf(data, len);
                            if (headerEnd >= 0)
                            {
                                string header = Encoding.ASCII.GetString(data, 0, headerEnd);
                                contentLength = ParseContentLength(header);
                            }
                        }
                        if (headerEnd >= 0 && len - (headerEnd + 4) >= contentLength) break;
                    }

                    var all = ms.ToArray();
                    string headerText = headerEnd >= 0 ? Encoding.ASCII.GetString(all, 0, headerEnd) : "";
                    string path = ParsePath(headerText);
                    string body = "";
                    if (headerEnd >= 0 && contentLength > 0 && headerEnd + 4 + contentLength <= all.Length)
                        body = Encoding.UTF8.GetString(all, headerEnd + 4, contentLength);

                    // ── แยก query string ออกจาก path → merge เข้า body ──
                    int qIdx = path.IndexOf('?');
                    string query = qIdx >= 0 ? path.Substring(qIdx + 1) : "";
                    if (qIdx >= 0) path = path.Substring(0, qIdx);
                    if (!string.IsNullOrEmpty(query) && (string.IsNullOrEmpty(body) || body == "{}"))
                        body = QueryToJson(query);

                    // ── เรียก handler (จัดการ main-thread ภายใน Dispatch) ──
                    string result;
                    int status = 200;
                    try { result = MCPHandlers.Dispatch(path, body); }
                    catch (Exception e) { result = $"{{\"error\":\"{Escape(e.Message)}\"}}"; status = 500; }

                    WriteResponse(stream, status, result);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[MCP] Request error: {e.Message}");
            }
        }

        static void WriteResponse(NetworkStream stream, int status, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json ?? "");
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Internal Server Error")}\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append($"Content-Length: {payload.Length}\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        // หา "\r\n\r\n" ใน buffer
        static int IndexOfCrlfCrlf(byte[] data, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            return -1;
        }

        static int ParseContentLength(string header)
        {
            foreach (var line in header.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = t.Substring("Content-Length:".Length).Trim();
                    if (int.TryParse(v, out int len)) return len;
                }
            }
            return 0;
        }

        // request line: "POST /path HTTP/1.1" → "/path"
        static string ParsePath(string header)
        {
            int nl = header.IndexOf('\n');
            string first = nl >= 0 ? header.Substring(0, nl) : header;
            var parts = first.Trim().Split(' ');
            return parts.Length >= 2 ? parts[1] : "/";
        }

        // แปลง "type=Transform&active=true" → {"type":"Transform","active":true}
        static string QueryToJson(string query)
        {
            if (string.IsNullOrEmpty(query)) return "{}";
            var sb = new StringBuilder("{");
            foreach (var pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                string k = Uri.UnescapeDataString(pair.Substring(0, eq));
                string v = Uri.UnescapeDataString(pair.Substring(eq + 1));
                // ตรวจ type: number / bool / string
                bool isBool = v == "true" || v == "false";
                bool isNum  = double.TryParse(v, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out _);
                string jsonVal = (isBool || isNum) ? v : $"\"{v.Replace("\"", "\\\"")}\"";
                if (sb.Length > 1) sb.Append(',');
                sb.Append($"\"{k}\":{jsonVal}");
            }
            sb.Append('}');
            return sb.ToString();
        }

        static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
    }
}
