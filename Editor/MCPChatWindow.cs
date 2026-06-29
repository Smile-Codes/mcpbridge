// MCPChatWindow — MCP Bridge chat UI
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace MCPBridge
{
    public class MCPChatWindow : EditorWindow
    {
        // ── Per-tab sessions (0 = API, 1 = CLI) — แยก state คนละ tab ──────────
        [SerializeField] ChatSession _api = new ChatSession();
        [SerializeField] ChatSession _cli = new ChatSession();

        ChatSession S => CurrentBackend() == 0 ? _api : _cli;

        // ── Shared UI-only state (ไม่ผูกกับ tab) ─────────────────────────────
        string _apiKey = "";
        bool _showSettings;
        int _lastRole = -1;   // ตรวจ role change → invalidate message cache
        Vector2 _inputScroll;
        bool _autoScroll = true;

        bool _showScriptList;
        string _scriptQuery = "";
        Vector2 _scriptScroll;

        // '#' prefab picker (autocomplete ชื่อ prefab — แยกจาก '@' ที่เป็น script)
        bool _showPrefabList;
        string _prefabQuery = "";
        Vector2 _prefabScroll;

        // '/' skill picker (เฉพาะ Subscription/CLI mode)
        bool _showSkillList;
        string _skillQuery = "";
        Vector2 _skillScroll;

        int _caretToEndFrames;   // นับถอยหลังย้าย caret ไปท้ายช่องพิมพ์ หลัง refocus (กัน select-all)
        bool _showLive;     // แผง Profiler สด (real-time)
        double _lastLiveRepaint;
        double _lastThinkRepaint;   // throttle repaint ตอน "กำลังคิด" (กันแย่ง frame เกม)
        bool _showKeywords; // แผง Dev/Art keyword shortcuts

        // Tab ที่ 3 — MCP Log
        [SerializeField] int _activeTab;   // 0=API, 1=CLI, 2=MCPLog (แยกจาก backend)
        Vector2 _logScroll;

        // cache GUIStyles (สร้างครั้งเดียว ไม่ใช่ทุกข้อความทุกเฟรม)
        GUIStyle _msgTextStyle, _roleUser, _roleClaude;
        Font _logFont;
        Font LogFont => _logFont != null ? _logFont
            : (_logFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Consolas", "Menlo", "Courier New", "monospace" }, FONT_SIZE - 1));
        // ฟอนต์เนื้อความ — bundle IBM Plex Sans Thai Looped (OFL) มากับโปรเจกต์ ทุกเครื่องเห็นเหมือนกัน
        // fallback: Leelawadee UI (ไทยระบบ Windows) → Thonburi (mac) → Tahoma — Segoe UI ไม่มี glyph ไทย
        const string UI_FONT_PATH = "Assets/Editor/UnityMCP/Fonts/IBMPlexSansThaiLooped-Regular.ttf";
        Font _uiFont;
        Font UiFont
        {
            get
            {
                if (_uiFont != null) return _uiFont;
                _uiFont = AssetDatabase.LoadAssetAtPath<Font>(UI_FONT_PATH);
                if (_uiFont == null)
                    _uiFont = Font.CreateDynamicFontFromOSFont(
                        new[] { "Leelawadee UI", "Thonburi", "Tahoma", "Segoe UI", "Arial" }, MSG_FONT);
                return _uiFont;
            }
        }

        // format เวลา: <60 วิ = "20s", >=60 = "1m 01s" (ไม่มีเศษ)
        static string FmtTime(double sec)
        {
            int s = (int)sec;
            return s < 60 ? $"{s}s" : $"{s / 60}m {s % 60:00}s";
        }


        // smooth scroll
        float _scrollTarget = -1f;
        bool _scrollAnim;
        bool _stickBottom = true;   // ตามข้อความใหม่ เฉพาะตอนอยู่ล่างสุด
        const string THINKING = "\x02THINKING";   // marker ของ bubble "กำลังคิด" (render เวลาสด)
        const string QUEUED   = "\x03QUEUED";     // marker ของ bubble "รอคิว" (ยกเลิกได้)

        const int MAX_IMAGES = 8;
        const int FONT_SIZE = 12;   // ฟอนต์ chrome (header/tab/ปุ่ม)
        const int MSG_FONT  = 13;   // ฟอนต์เนื้อความ (อ่านง่ายขึ้น)
        const int SCRIPT_LIST_HEIGHT = 162;   // picker panel (@/#//) — 5 แถว + หัว
        const float INPUT_MIN = 40f;
        const float INPUT_MAX = 160f;
        float _inputHeight = INPUT_MIN;

        // ── Theme (Anthropic warm / clay) ───────────────────────────────────
        static readonly Color BG_DARK     = new Color(0.090f, 0.078f, 0.066f); // #171411
        static readonly Color BG_SURFACE  = new Color(0.110f, 0.098f, 0.082f); // #1C1915 bubble/input
        static readonly Color BG_RAISED   = new Color(0.122f, 0.110f, 0.090f); // header / chips
        static readonly Color BORDER      = new Color(0.204f, 0.188f, 0.165f); // #34302A
        static readonly Color BORDER_SOFT = new Color(0.165f, 0.150f, 0.128f);
        static readonly Color ACCENT      = new Color(0.851f, 0.467f, 0.341f); // #D97757 clay
        static readonly Color TEXT_WHITE  = new Color(0.925f, 0.902f, 0.863f); // #ECE6DC
        static readonly Color TEXT_MUTE   = new Color(0.612f, 0.580f, 0.541f); // #9C948A
        static readonly Color TEXT_HINT   = new Color(0.420f, 0.392f, 0.357f); // #6B645B
        static readonly Color ONLINE      = new Color(0.361f, 0.729f, 0.490f); // soft green dot

        // ── rounded-rect helpers (Unity 2022.3 GUI.DrawTexture borderRadius) ──
        static void RRect(Rect r, Color c, float radius)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, 0f, radius);
        }
        static void RRect4(Rect r, Color c, float tl, float tr, float br, float bl)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c,
                Vector4.zero, new Vector4(tl, tr, br, bl));
        }
        // กล่องมุมโค้ง + เส้นขอบ 1px (ชัวร์: 2 rect ซ้อน ไม่พึ่ง border semantics)
        static void RBox(Rect r, Color fill, Color border, float radius)
        {
            RRect(r, border, radius);
            RRect(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), fill, Mathf.Max(0f, radius - 1f));
        }
        static void CenterLabel(Rect r, string text, Color c, int fontSize)
        {
            var st = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = fontSize, richText = true };
            st.normal.textColor = c;
            GUI.Label(r, text, st);
        }

        // ไอคอนส้ม ✦ บน dock tab — gen เป็น texture ในโค้ด (ไม่ต้องมีไฟล์ asset)
        static Texture2D _tabIcon;
        static Texture2D TabIcon()
        {
            if (_tabIcon != null) return _tabIcon;
            const int S = 32;
            float c = (S - 1) / 2f, half = S * 0.46f, corner = S * 0.26f, star = S * 0.30f;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = x - c, dy = y - c;
                // rounded square (SDF มุมโค้ง)
                float qx = Mathf.Max(Mathf.Abs(dx) - (half - corner), 0f);
                float qy = Mathf.Max(Mathf.Abs(dy) - (half - corner), 0f);
                bool inBg = Mathf.Sqrt(qx * qx + qy * qy) <= corner;
                // ดาว 4 แฉก (astroid): (|x|/r)^(2/3) + (|y|/r)^(2/3) <= 1
                float a = Mathf.Pow(Mathf.Abs(dx) / star, 2f / 3f) + Mathf.Pow(Mathf.Abs(dy) / star, 2f / 3f);
                t.SetPixel(x, S - 1 - y, !inBg ? Color.clear : (a <= 1f ? Color.white : ACCENT));
            }
            t.Apply();
            return t;
        }

        // ── Open ──────────────────────────────────────────────────────────
        [MenuItem("MCP Bridge/Chat _F12")]
        public static void Open() => GetWindow<MCPChatWindow>("MCP Bridge").minSize = new Vector2(440, 600);

        void OnEnable()
        {
            titleContent = new GUIContent("MCP Bridge", TabIcon());   // ไอคอนส้มบน dock tab
            wantsMouseMove = true;   // ให้ hover effect ใน custom dropdown/ปุ่ม ตอบสนองทันที
            _apiKey = EditorPrefs.GetString("DeltaMCP_ApiKey", "");
            _api.backend = 0;
            _cli.backend = 1;
            // NonSerialized fields อาจเป็น null หลัง domain reload — init ใหม่
            _api.Reinit(); _cli.Reinit();
            if (_api.messages.Count == 0) LoadHistory(_api);
            if (_cli.messages.Count == 0) LoadHistory(_cli);
            // bubble "กำลังคิด/รอคิว" ที่รอดข้าม domain reload = ซาก (task ตายไปกับ reload แล้ว) → ลบทิ้ง
            CleanupStalePlaceholders(_api);
            CleanupStalePlaceholders(_cli);
            CodebaseIndex.Refresh();
            SkillIndex.Refresh();
            PrefabIndex.RefreshAsync();   // A2: build reverse-index (script→prefab) บน background thread
        }

        void OnDisable() { SaveHistory(_api); SaveHistory(_cli); }

        // อัพเดทหน้าต่าง ~10/วิ ตอน CpuDeepCapture จับอยู่ → ปุ่ม ⏺ % เดินจนเสร็จ + แนบผลทันที
        void OnInspectorUpdate() { if (CpuDeepCapture.IsCapturing) Repaint(); }

        // ── persistence (แยกประวัติตาม backend) ──────────────────────────────
        static int CurrentBackend() => EditorPrefs.GetInt("DeltaMCP_Backend", 0);

        // เก็บใน Library/DeltaMCP/ (ไม่มี size limit, ไม่ถูก git track, อยู่รอดผ่าน domain reload)
        static string HistoryPath(int backend)
        {
            string dir = System.IO.Path.Combine(Application.dataPath, "..", "Library", "DeltaMCP");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, $"chat_{backend}.json");
        }

        [Serializable] class HistoryWrap { public List<ChatMessage> items; }

        void SaveHistory(ChatSession s)
        {
            try
            {
                // ไม่เซฟ placeholder "กำลังคิด/รอคิว" ลงไฟล์ — โหลดกลับมาก็เป็นซากที่ทำต่อไม่ได้อยู่ดี
                var keep = s.messages.FindAll(m => !(m.Role == "assistant" && (m.Content == THINKING || m.Content == QUEUED)));
                System.IO.File.WriteAllText(HistoryPath(s.backend), JsonUtility.ToJson(new HistoryWrap { items = keep }));
            }
            catch { }
        }

        // ลบ bubble "กำลังคิด/รอคิว" ที่ค้างหลัง domain reload (เช่น recompile กลางคัน — งานโดนตัดแล้วแน่นอน)
        static void CleanupStalePlaceholders(ChatSession s)
        {
            int removed = s.messages.RemoveAll(m => m.Role == "assistant" && (m.Content == THINKING || m.Content == QUEUED));
            if (removed > 0)
                Debug.Log($"[MCP Bridge] ลบ bubble 'กำลังคิด' ค้าง {removed} อัน (โดนตัดตอน compile/reload) — พิมพ์ถามใหม่ได้เลย");
        }

        void LoadHistory(ChatSession s)
        {
            try
            {
                string path = HistoryPath(s.backend);
                string json = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";

                // migrate จาก EditorPrefs เดิม (ถ้าไม่มีไฟล์ให้ลองโหลดจาก EditorPrefs ก่อน)
                if (string.IsNullOrEmpty(json))
                    json = EditorPrefs.GetString($"DeltaMCP_ChatHistory_{s.backend}", "");

                var wrap = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<HistoryWrap>(json);
                s.messages = wrap?.items ?? new List<ChatMessage>();
                s.messages.RemoveAll(m => string.IsNullOrEmpty(m.Role) || m.Content == null);
                // migrate: ข้อความเก่าที่ฝัง ⏱ ในเนื้อหา → ย้ายไป Stat
                foreach (var m in s.messages)
                {
                    if (m.Role == "user" || !string.IsNullOrEmpty(m.Stat)) continue;
                    var mt = System.Text.RegularExpressions.Regex.Match(m.Content, @"\n*⏱[^\n]*$");
                    if (mt.Success) { m.Stat = mt.Value.Trim(); m.Content = m.Content.Substring(0, mt.Index).TrimEnd(); }
                }
            }
            catch { s.messages = new List<ChatMessage>(); }
        }

        void SwitchBackend(int target)
        {
            if (target == CurrentBackend()) return;
            EditorPrefs.SetInt("DeltaMCP_Backend", target);
            _showScriptList = false;
            GUI.FocusControl(null);
        }

        // คิวงานที่ต้องแก้ messages — apply เฉพาะตอน Layout (กัน control count เพี้ยนระหว่าง Layout/Repaint)
        readonly Queue<System.Action> _pending = new Queue<System.Action>();
        bool _refocusInput;   // true = ต้องดึง focus กลับช่อง prompt (ข้อความใหม่ทำให้หลุด)

        // ── GUI ───────────────────────────────────────────────────────────
        void OnGUI()
        {
            // ตรวจ role change → invalidate cached views ทุก message (ไม่งั้น bubble เก่าค้างอยู่)
            int curRoleNow = CurrentRole();
            if (curRoleNow != _lastRole)
            {
                _lastRole = curRoleNow;
                foreach (var m in _api.messages) m.InvalidateCaches();
                foreach (var m in _cli.messages) m.InvalidateCaches();
            }

            // apply การเปลี่ยน messages ตอน Layout เท่านั้น → Layout กับ Repaint เห็นโครงสร้างเดียวกัน
            if (Event.current.type == EventType.Layout && _pending.Count > 0)
            {
                // ถ้ากำลังพิมอยู่ในช่อง prompt → จำไว้ว่าต้อง re-focus หลังเพิ่มข้อความ
                // (ข้อความใหม่ทำให้ control id เลื่อน → keyboard focus หลุด ถ้าไม่ดึงกลับ)
                bool wasTyping = GUI.GetNameOfFocusedControl() == "PromptField";
                while (_pending.Count > 0) _pending.Dequeue()?.Invoke();
                if (wasTyping) _refocusInput = true;
            }

            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), BG_DARK);

            // hover effect เฉพาะหน้า Settings + ตอน picker (@/#//) เปิด — โซนอื่นเมาส์ขยับ = ไม่ repaint เลย
            // (repaint รัวบน transcript ยาวๆ คือต้นเหตุ Unity ค้างจนขึ้น "Hold on... MCPChatWindow.MouseDown")
            if ((_showSettings || _showScriptList || _showPrefabList || _showSkillList) &&
                Event.current.type == EventType.MouseMove)
                Repaint();

            DrawTabs();   // แถวบนแถวเดียว: tab pills ซ้าย + role/online/⚙ ชิดขวา
            // เส้นคั่นใต้แถวบน — แยกโซน chrome กับเนื้อหา (กัน tab กลืนกับ message)
            var tabSep = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(tabSep, BORDER_SOFT);
            EditorGUILayout.Space(6);

            // compile อยู่ → โชว์ loading card กลางหน้าต่างแทนทั้งหน้า (แชตพักชั่วคราว draft ไม่หาย)
            if (EditorApplication.isCompiling)
            {
                DrawCompilingOverlay();
                Repaint();
                return;
            }

            if (_showSettings) { DrawSettings(); return; }

            if (_activeTab == 2) { DrawMcpLog(); return; }
            DrawChatHistory();
            DrawInputArea();

            // ดึง focus กลับช่อง prompt ถ้าข้อความใหม่เพิ่งทำให้หลุด (ตอนผู้ใช้กำลังพิม)
            if (_refocusInput && Event.current.type == EventType.Repaint)
            {
                EditorGUI.FocusTextInControl("PromptField");
                _refocusInput = false;
                _caretToEndFrames = 2;   // focus กลับมา Unity จะ select-all → ต้องย้าย caret ตาม
                Repaint();
            }
            // ฆ่า select-all หลัง focus จับ: ย้าย caret ไปท้ายข้อความ (ไม่งั้นพิมพ์ตัวแรก = ลบทั้งช่อง)
            if (_caretToEndFrames > 0 && Event.current.type == EventType.Repaint)
            {
                if (GUI.GetNameOfFocusedControl() == "PromptField")
                {
                    var te = GUIUtility.QueryStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
                    if (te != null) { te.cursorIndex = te.text.Length; te.selectIndex = te.cursorIndex; }
                }
                _caretToEndFrames--;
                Repaint();
            }
        }

        void DrawTabs()
        {
            int backend = CurrentBackend();

            int logCount = MCPHandlers.Log.Count;
            bool srvOn = MCPServer.IsRunning;
            string srvDot = srvOn ? "● " : "○ ";
            var labels = new[] {
                "API Chat"     + Badge(_api),
                "Subscription" + Badge(_cli),
                srvDot + "Claude In" + (logCount > 0 ? $" ({logCount})" : ""),
            };

            if (_activeTab < 2 && _activeTab != backend) _activeTab = backend;

            // ── compact segmented pill tabs (ชิดซ้าย กระชับตาม label เหมือน mockup) ──
            var barR = EditorGUILayout.GetControlRect(false, 38, GUILayout.ExpandWidth(true));

            var lblStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize  = FONT_SIZE,
                alignment = TextAnchor.MiddleCenter,
            };

            const float SEG_PAD = 18f;   // ระยะซ้าย-ขวาในแต่ละ pill
            const float TRK_PAD = 4f;    // ขอบใน track
            var segW = new float[labels.Length];
            float totalW = 0f;
            for (int i = 0; i < labels.Length; i++)
            {
                lblStyle.fontStyle = FontStyle.Bold;   // วัดด้วย bold (active กว้างสุด) กัน label ขยับตอนสลับ
                segW[i] = Mathf.Ceil(lblStyle.CalcSize(new GUIContent(labels[i])).x) + SEG_PAD * 2;
                totalW += segW[i];
            }

            var track = new Rect(barR.x + 12, barR.y + 4, totalW + TRK_PAD * 2, barR.height - 8);
            RBox(track, BG_SURFACE, BORDER_SOFT, 9f);

            int picked = _activeTab;
            float sx = track.x + TRK_PAD;
            for (int ti = 0; ti < labels.Length; ti++)
            {
                bool isActive  = _activeTab == ti;
                var  segR      = new Rect(sx, track.y + 3, segW[ti], track.height - 6);
                var  tabR      = new Rect(sx, barR.y, segW[ti], barR.height);

                if (Event.current.type == EventType.Repaint)
                {
                    if (isActive) RRect(segR, ACCENT, 7f);

                    lblStyle.fontStyle = isActive ? FontStyle.Bold : FontStyle.Normal;
                    lblStyle.normal.textColor = isActive ? Color.white : TEXT_MUTE;
                    GUI.Label(segR, labels[ti], lblStyle);
                }

                if (GUI.Button(tabR, GUIContent.none, GUIStyle.none))
                    picked = ti;

                sx += segW[ti];
            }

            // ── ชิดขวาแถวเดียวกัน: role chip · online · ⚙ (ย้ายมาจาก header เดิม) ──
            bool srvOn2     = MCPServer.IsRunning;
            bool onClaudeIn = _activeTab == 2;
            float rightX = barR.xMax - 12;

            var gear = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE + 3 };
            gear.normal.textColor = _showSettings ? ACCENT : TEXT_MUTE;
            var gearR = new Rect(rightX - 22, barR.y + 7, 22, 24);
            GUI.Label(gearR, "⚙", gear);
            if (GUI.Button(gearR, GUIContent.none, GUIStyle.none)) { _showSettings = !_showSettings; Repaint(); }
            rightX -= 32;

            {
                Color liveC = srvOn2 ? ONLINE : new Color(0.85f, 0.45f, 0.40f);
                const float pw = 86f;
                var pillR = new Rect(rightX - pw, barR.y + 8, pw, 22);
                RRect(pillR, new Color(liveC.r, liveC.g, liveC.b, 0.13f), 11f);
                RRect(new Rect(pillR.x + 11, pillR.y + 7, 8, 8), liveC, 4f);
                var pillStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft, fontSize = FONT_SIZE - 1 };
                pillStyle.normal.textColor = liveC;
                GUI.Label(new Rect(pillR.x + 26, pillR.y, pw - 26, pillR.height), srvOn2 ? "online" : "offline", pillStyle);
                rightX -= pw + 10;
            }

            // role chip — คลิก = สลับ Dev↔Art ตรงๆ (เลิก GenericMenu ที่หน้าตาไม่เข้าธีม)
            if (!onClaudeIn)
            {
                int curRole = CurrentRole();
                var roleR = new Rect(rightX - 66, barR.y + 8, 66, 22);
                RBox(roleR, BG_RAISED, BORDER, 8f);
                Color sq = curRole == 0 ? ACCENT : new Color(0.878f, 0.627f, 0.753f);
                RRect(new Rect(roleR.x + 10, roleR.y + 8, 7, 7), sq, 2f);
                var roleTxt = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft, fontSize = FONT_SIZE - 2 };
                roleTxt.normal.textColor = TEXT_WHITE;
                GUI.Label(new Rect(roleR.x + 22, roleR.y, 30, roleR.height), curRole == 0 ? "Dev" : "Art", roleTxt);
                var swap = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 3 };
                swap.normal.textColor = TEXT_HINT;
                GUI.Label(new Rect(roleR.xMax - 18, roleR.y, 14, roleR.height), "⇄", swap);
                if (GUI.Button(roleR, GUIContent.none, GUIStyle.none))
                {
                    int newRole = curRole == 0 ? 1 : 0;
                    EditorPrefs.SetInt("DeltaMCP_Role", newRole);
                    // ล้าง cache view ทันที — เนื้อหา role เก่าไม่ค้าง (ไม่ต้องรอสลับ tab)
                    foreach (var m in _api.messages) m.InvalidateCaches();
                    foreach (var m in _cli.messages) m.InvalidateCaches();
                    _lastRole = newRole;
                    Repaint();
                    // abort เฟรมนี้ → Unity เริ่ม Layout+Repaint ใหม่ด้วย role ใหม่ (กัน rect เก่า/เนื้อใหม่ ปนกันจนเอ๋อ)
                    GUIUtility.ExitGUI();
                }
                rightX -= 76;

                // 🧪 ทดสอบ — health check MCP (ตอบทันทีในแชต ไม่เรียก AI)
                var testR = new Rect(rightX - 78, barR.y + 8, 78, 22);
                RBox(testR, BG_RAISED, BORDER, 8f);
                var testTxt = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 2 };
                testTxt.normal.textColor = new Color(0.60f, 0.82f, 0.62f);
                GUI.Label(testR, "🧪 ทดสอบ", testTxt);
                if (GUI.Button(testR, GUIContent.none, GUIStyle.none))
                {
                    S.draft = "ทดสอบ";
                    Enqueue();
                    // เพิ่ม message กลางเฟรม (ก่อน history วาด) → ตัดเฟรมเริ่มใหม่ กัน Invalid GUILayout state
                    GUIUtility.ExitGUI();
                }
            }

            if (picked != _activeTab)
            {
                _activeTab = picked;
                _showSettings = false;   // คลิก tab ขณะอยู่หน้า Settings = ปิด Settings กลับ tab นั้น
                if (picked < 2) SwitchBackend(picked);
                // ExitGUI: abort OnGUI ปัจจุบัน → Unity เริ่ม Layout+Repaint ใหม่ด้วย _activeTab ที่ update แล้ว
                // ป้องกัน "Invalid GUILayout state" เพราะ render control ต่างกันตาม _activeTab
                GUIUtility.ExitGUI();
            }
        }

        public static int CurrentRole() => EditorPrefs.GetInt("DeltaMCP_Role", 0);

        // ── Tab 3 (index 2): MCP Log + Server controls ────────────────────────
        void DrawMcpLog()
        {
            var log = MCPHandlers.Log;
            bool srvOn = MCPServer.IsRunning;

            // ── Server control bar ──
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // status dot + label
            var dotStyle = new GUIStyle(EditorStyles.toolbarButton) { fontSize = FONT_SIZE - 1 };
            dotStyle.normal.textColor = srvOn ? new Color(0.3f, 0.9f, 0.3f) : new Color(0.9f, 0.4f, 0.4f);
            GUILayout.Label(srvOn ? $"● {MCPServer.Label}  port {MCPServer.Port}" : $"○ {MCPServer.Label}  stopped", dotStyle, GUILayout.Width(170));

            // Start / Stop
            var btnStyle = new GUIStyle(EditorStyles.toolbarButton) { fontSize = FONT_SIZE - 1 };
            btnStyle.normal.textColor = srvOn ? new Color(1f, 0.5f, 0.4f) : new Color(0.4f, 0.9f, 0.5f);
            if (GUILayout.Button(srvOn ? "⏹ Stop" : "▶ Start", btnStyle, GUILayout.Width(62)))
            {
                if (srvOn) MCPServer.Stop(); else MCPServer.Start();
            }

            GUILayout.Space(8);

            // Allow Writes toggle
            bool curAllow = MCPHandlers.AllowWrites;
            var allowStyle = new GUIStyle(EditorStyles.toolbarButton) { fontSize = FONT_SIZE - 1 };
            allowStyle.normal.textColor = curAllow ? ACCENT : new Color(0.55f, 0.55f, 0.55f);
            bool newAllow = GUILayout.Toggle(curAllow, curAllow ? "✏ Write ON" : "✏ Write OFF", allowStyle, GUILayout.Width(88));
            if (newAllow != curAllow) MCPHandlers.AllowWrites = newAllow;

            GUILayout.FlexibleSpace();

            // quick stats
            int errCount = 0;
            lock (log) foreach (var e in log) if (e.IsError) errCount++;
            var statStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2 };
            statStyle.normal.textColor = new Color(0.5f, 0.5f, 0.55f);
            GUILayout.Label($"{log.Count} cmds  {errCount} err", statStyle);

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(44)))
                MCPHandlers.ClearLog();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            if (log.Count == 0)
            {
                EditorGUILayout.Space(20);
                var empty = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = FONT_SIZE };
                GUILayout.Label("ยังไม่มีคำสั่ง — เริ่ม chat แล้วสั่ง Claude ทำงานกับ Unity ก่อน", empty);
                return;
            }

            // ── styles: เวลา/ms = mono (Consolas) · ชื่อคำสั่ง = UI font (อ่านไทยชัด) ──
            var timeStyle = new GUIStyle(EditorStyles.miniLabel)
                { font = LogFont, fontSize = FONT_SIZE - 2 };
            timeStyle.normal.textColor = new Color(0.55f, 0.51f, 0.46f);

            var pathStyleOk = new GUIStyle(EditorStyles.label)
                { font = UiFont, fontSize = FONT_SIZE, richText = false };
            pathStyleOk.normal.textColor = TEXT_WHITE;
            var pathStyleErr = new GUIStyle(pathStyleOk);
            pathStyleErr.normal.textColor = new Color(1f, 0.48f, 0.45f);

            var arrowStyle = new GUIStyle(EditorStyles.miniLabel)
                { font = LogFont, fontSize = FONT_SIZE - 1 };

            var monoStyle = new GUIStyle(EditorStyles.label)
            {
                font = LogFont, fontSize = FONT_SIZE - 2,
                wordWrap = true, richText = false,
                padding = new RectOffset(0, 0, 2, 2),
            };

            var msStyle = new GUIStyle(EditorStyles.miniLabel)
                { font = LogFont, fontSize = FONT_SIZE - 2 };

            // ไม่โชว์ scrollbar — เลื่อนด้วย wheel (เนื้อหาเกินจอก็ scroll ได้ปกติ)
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, false, false, GUIStyle.none, GUIStyle.none, GUIStyle.none);

            // แสดงจากใหม่ → เก่า  (snapshot กัน lock ค้างระหว่าง draw)
            List<MCPHandlers.MCPLogEntry> snapshot;
            lock (log) { snapshot = new List<MCPHandlers.MCPLogEntry>(log); }

            for (int i = snapshot.Count - 1; i >= 0; i--)
            {
                var e = snapshot[i];
                Color bgRow   = e.IsError ? new Color(0.22f, 0.10f, 0.09f) : BG_SURFACE;
                Color accent  = e.IsError ? new Color(0.85f, 0.34f, 0.30f) : ACCENT;
                Color respCol = e.IsError ? new Color(1f, 0.48f, 0.45f)    : ONLINE;

                // ── header row (คลิก = toggle expand) — การ์ดมุมโค้ง อ่านง่าย ──
                var hdrFull = GUILayoutUtility.GetRect(0, 27, GUILayout.ExpandWidth(true));
                var hdr = new Rect(hdrFull.x + 6, hdrFull.y, hdrFull.width - 12, 25);
                if (Event.current.type == EventType.Repaint)
                {
                    RRect(hdr, bgRow, 7f);
                    RRect4(new Rect(hdr.x, hdr.y, 3, hdr.height), accent, 7f, 0f, 0f, 7f);
                }

                arrowStyle.normal.textColor = TEXT_HINT;
                GUI.Label(new Rect(hdr.x + 9,  hdr.y + 4, 14, 16), e.Expanded ? "▼" : "▶", arrowStyle);

                GUI.Label(new Rect(hdr.x + 25, hdr.y + 4, 58, 16), e.Time, timeStyle);

                string friendlyLabel = FriendlyPath(e.Path);
                GUI.Label(new Rect(hdr.x + 88, hdr.y + 3, hdr.width - 150, 19),
                    friendlyLabel, e.IsError ? pathStyleErr : pathStyleOk);

                msStyle.normal.textColor = e.Ms > 200 ? new Color(1f, 0.72f, 0.28f)
                                         : e.Ms > 50  ? new Color(0.85f, 0.85f, 0.45f)
                                         : new Color(0.46f, 0.70f, 0.50f);
                GUI.Label(new Rect(hdr.xMax - 56, hdr.y + 4, 48, 16), $"{e.Ms}ms", msStyle);

                if (GUI.Button(hdrFull, GUIContent.none, GUIStyle.none)) { e.Expanded = !e.Expanded; Repaint(); }

                // ── expanded: request body + response (pretty-printed) ──
                if (e.Expanded)
                {
                    EditorGUILayout.BeginVertical();
                    GUILayout.Space(2);

                    // raw path (เล็กๆ สีจาง)
                    var rawStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 3 };
                    rawStyle.normal.textColor = new Color(0.45f, 0.42f, 0.38f);
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(31);
                    GUILayout.Label(e.Path, rawStyle);
                    EditorGUILayout.EndHorizontal();
                    GUILayout.Space(2);

                    if (!string.IsNullOrEmpty(e.Body) && e.Body != "{}")
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(31);
                        arrowStyle.normal.textColor = TEXT_MUTE;
                        GUILayout.Label("→", arrowStyle, GUILayout.Width(14));
                        monoStyle.normal.textColor  = TEXT_MUTE;
                        GUILayout.Label(JsonToReadable(e.Body), monoStyle);
                        EditorGUILayout.EndHorizontal();
                        GUILayout.Space(2);
                    }

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(31);
                    arrowStyle.normal.textColor = respCol;
                    GUILayout.Label("←", arrowStyle, GUILayout.Width(14));
                    monoStyle.normal.textColor  = respCol;
                    GUILayout.Label(JsonToReadable(e.Response), monoStyle);
                    EditorGUILayout.EndHorizontal();
                    GUILayout.Space(4);

                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.Space(3);   // ช่องว่างระหว่างการ์ด (แทนเส้น divider)
            }

            EditorGUILayout.EndScrollView();
        }

        // แปลง MCP path → ชื่อที่คนอ่านได้ + icon
        static string FriendlyPath(string path) => path switch
        {
            "/ping"                    => "🔌  Ping Unity",
            "/compile"                 => "⚙️  Compile Scripts",
            "/compile-status"          => "⚙️  เช็คสถานะ Compile",
            "/server/stop"             => "⏹  ปิด MCP Server",
            "/atlas/create"            => "🖼  สร้าง Sprite Atlas",
            "/diagnose/exceptions-clear" => "🚨  ล้าง Exceptions",
            "/gameobject/create"       => "➕  สร้าง GameObject",
            "/gameobject/delete"       => "🗑  ลบ GameObject",
            "/object/add-component"    => "🧩  Add Component",
            "/object/set-property"     => "✏️  Set Property",
            "/object/set-transform"    => "📐  Set Transform",
            "/object/inspect"          => "🔍  Inspect Object",
            "/scene/hierarchy"         => "🌳  อ่าน Scene Hierarchy",
            "/scene/list"              => "📋  List Scenes",
            "/scene/open"              => "📂  เปิด Scene",
            "/scene/save"              => "💾  บันทึก Scene",
            "/scene/count"             => "🔢  นับ Components",
            "/prefab/create"           => "📦  สร้าง Prefab",
            "/prefab/place"            => "📌  วาง Prefab",
            "/script/create"           => "📝  สร้าง Script",
            "/script/read"             => "📖  อ่าน Script",
            "/code/run"                => "⚡  Run C# (live)",
            "/ui/create"               => "🖼  สร้าง UI",
            "/ui/optimize"             => "⚡  Optimize UI",
            "/material/create"         => "🎨  สร้าง Material",
            "/terrain/create"          => "🏔  สร้าง Terrain",
            "/terrain/set-heights"     => "🏔  ตั้งค่า Terrain",
            "/asset/find"              => "🔎  ค้นหา Asset",
            "/console/read"            => "📟  อ่าน Console",
            "/console/logfile"         => "📄  อ่าน Log File",
            "/console/clear"           => "🧹  ล้าง Console",
            "/console/logs"            => "📟  ดึง Logs",
            "/perf/audit"              => "📊  Perf Audit",
            "/perf/worst"              => "📊  Worst Frames",
            "/diagnose/state"          => "🩺  Capture State",
            "/diagnose/deep"           => "🔬  Deep Diagnose",
            "/diagnose/memory"         => "💾  Memory Snapshot",
            "/diagnose/fusion"         => "🌐  Fusion Stats",
            "/diagnose/exceptions"     => "🚨  อ่าน Exceptions",
            "/hot-reload"              => "🔥  Hot Reload",
            "/play/control"            => "▶️  Play Control",
            "/selection/get"           => "🖱  Get Selection",
            "/selection/set"           => "🖱  Set Selection",
            "/watch/add"               => "👁  Watch Add",
            "/watch/get"               => "👁  Watch Get",
            "/watch/clear"             => "👁  Watch Clear",
            "/audit/textures"          => "🖼  Audit Textures",
            "/audit/unused"            => "🗂  Audit Unused",
            "/audit/empty-folders"     => "📁  Audit Folders",
            "/code/refactor-audit"     => "♻️  Refactor Audit",
            _                          => path   // fallback = raw path
        };

        // แปลง JSON → tree ที่อ่านง่าย (recursive — object/array ซ้อนแตกเป็นกิ่ง ไม่กองเป็นบรรทัดเดียว)
        const int TREE_MAX_DEPTH = 3;     // ความลึกสูงสุดของกิ่ง
        const int TREE_MAX_ITEMS = 6;     // element ที่โชว์ต่อ array (เกิน = "…อีก N")
        const int TREE_MAX_VAL   = 500;   // ตัวอักษรสูงสุดต่อค่า

        static string JsonToReadable(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            string s = json.Trim();
            if (!s.StartsWith("{") && !s.StartsWith("["))
                return s.Length > 300 ? s.Substring(0, 300) + "…" : s;

            var sb = new System.Text.StringBuilder();
            RenderJsonNode(s, sb, "", 0);
            string outp = sb.ToString().TrimEnd('\n');
            if (outp.Length == 0) return PrettyJson(json);
            return outp.Length > 6000 ? outp.Substring(0, 6000) + "\n…(truncated)" : outp;
        }

        static void RenderJsonNode(string raw, System.Text.StringBuilder sb, string prefix, int depth)
        {
            raw = raw.Trim();
            if (raw.StartsWith("{"))
            {
                var pairs = ParseJsonPairs(raw);
                for (int i = 0; i < pairs.Count; i++)
                    RenderJsonPair(pairs[i].Key, pairs[i].Value, sb, prefix, i == pairs.Count - 1, depth);
            }
            else if (raw.StartsWith("["))
            {
                var items = SplitJsonArray(raw);
                int show = Mathf.Min(items.Count, TREE_MAX_ITEMS);
                for (int i = 0; i < show; i++)
                    RenderJsonPair($"[{i}]", items[i], sb, prefix, i == items.Count - 1, depth);
                if (items.Count > TREE_MAX_ITEMS)
                    sb.Append(prefix).Append("└ …อีก ").Append(items.Count - TREE_MAX_ITEMS).Append(" รายการ\n");
            }
        }

        static void RenderJsonPair(string key, string val, System.Text.StringBuilder sb, string prefix, bool last, int depth)
        {
            string branch = last ? "└ " : "├ ";
            string child  = prefix + (last ? "   " : "│  ");
            val = val.Trim();

            bool nested = (val.StartsWith("{") && val.Length > 2) || (val.StartsWith("[") && val.Length > 2);
            if (nested && depth < TREE_MAX_DEPTH)
            {
                string count = val.StartsWith("[") ? $"  ({SplitJsonArray(val).Count})" : "";
                sb.Append(prefix).Append(branch).Append(key).Append(count).Append('\n');
                RenderJsonNode(val, sb, child, depth + 1);
                return;
            }

            // scalar (หรือลึกเกิน) — unescape + clamp + ตัดบรรทัด
            string v = val.Replace("\\n", "\n").Replace("\\t", "  ").Replace("\\r", "").Replace("\\\"", "\"");
            if (v.Length > TREE_MAX_VAL) v = v.Substring(0, TREE_MAX_VAL) + "…";
            if (v.Contains('\n'))
            {
                sb.Append(prefix).Append(branch).Append(key).Append(":\n");
                foreach (var line in v.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    sb.Append(child).Append(line.TrimEnd()).Append('\n');
                }
            }
            else
                sb.Append(prefix).Append(branch).Append(key).Append(": ").Append(v).Append('\n');
        }

        // แตก top-level elements ของ array (เคารพ string + bracket ซ้อน)
        static System.Collections.Generic.List<string> SplitJsonArray(string raw)
        {
            var items = new System.Collections.Generic.List<string>();
            int len = raw.Length, depth = 0, start = 1; bool inStr = false;
            for (int i = 1; i < len - 1; i++)
            {
                char c = raw[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    string it = raw.Substring(start, i - start).Trim();
                    if (it.Length > 0) items.Add(it);
                    start = i + 1;
                }
            }
            if (len >= 2)
            {
                string tail = raw.Substring(start, Mathf.Max(0, len - 1 - start)).Trim();
                if (tail.Length > 0) items.Add(tail);
            }
            return items;
        }

        // Character-by-character JSON flat-object parser — ไม่ใช้ regex กัน catastrophic backtracking
        static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,string>>
            ParseJsonPairs(string s)
        {
            var pairs = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,string>>();
            int i = 1, len = s.Length;   // เริ่มหลัง {

            while (i < len - 1)
            {
                while (i < len && s[i] != '"' && s[i] != '}') i++;
                if (i >= len - 1 || s[i] == '}') break;

                // อ่าน key
                string key = ReadJsonString(s, ref i);

                // หา :
                while (i < len && s[i] != ':') i++;
                i++;
                while (i < len && char.IsWhiteSpace(s[i])) i++;

                // อ่าน value
                string val = ReadJsonValue(s, ref i);
                pairs.Add(new System.Collections.Generic.KeyValuePair<string,string>(key, val));

                // ข้าม ,
                while (i < len && s[i] != ',' && s[i] != '}') i++;
                if (i < len && s[i] == ',') i++;
            }
            return pairs;
        }

        static string ReadJsonString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return "";
            i++;   // ข้าม "
            var sb = new System.Text.StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i]); sb.Append(s[i+1]); i += 2; }
                else { sb.Append(s[i]); i++; }
            }
            if (i < s.Length) i++;   // ข้าม "
            return sb.ToString();
        }

        static string ReadJsonValue(string s, ref int i)
        {
            if (i >= s.Length) return "";
            char c = s[i];
            if (c == '"') return ReadJsonString(s, ref i);

            // array หรือ object — คืน raw ทั้งก้อน (นับ bracket แบบข้าม string ข้างใน) ให้ tree renderer recurse ต่อ
            if (c == '[' || c == '{')
            {
                char open = c, close = c == '[' ? ']' : '}';
                int depth = 0, start = i; bool inStr = false;
                while (i < s.Length)
                {
                    char ch = s[i];
                    if (inStr) { if (ch == '\\') i++; else if (ch == '"') inStr = false; }
                    else if (ch == '"') inStr = true;
                    else if (ch == open) depth++;
                    else if (ch == close && --depth == 0) { i++; break; }
                    i++;
                }
                return s.Substring(start, i - start);
            }

            // number / bool / null
            int vs = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']') i++;
            return s.Substring(vs, i - vs).Trim();
        }

        // JSON pretty-printer เบาๆ สำหรับ GUI (ไม่ใช้ parser เต็ม)
        static string PrettyJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            const int MAX = 4000;
            var sb = new System.Text.StringBuilder();
            int indent = 0; bool inStr = false;
            for (int i = 0; i < Mathf.Min(json.Length, MAX); i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inStr = !inStr;
                if (inStr) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[':
                        sb.Append(c); indent++;
                        sb.Append('\n'); sb.Append(' ', indent * 2); break;
                    case '}': case ']':
                        indent = Mathf.Max(0, indent - 1);
                        sb.Append('\n'); sb.Append(' ', indent * 2);
                        sb.Append(c); break;
                    case ',':
                        sb.Append(c);
                        sb.Append('\n'); sb.Append(' ', indent * 2); break;
                    case ':': sb.Append(": "); break;
                    default:  sb.Append(c); break;
                }
            }
            if (json.Length > MAX) sb.Append("\n…(truncated)");
            return sb.ToString();
        }

        static string Badge(ChatSession s)
        {
            int pending = s.queue.Count + (s.isLoading ? 1 : 0);
            if (pending <= 0) return "";
            return s.isLoading ? $" ⏳{pending}" : $" •{pending}";
        }

        // loading card กลางหน้าต่าง — โชว์ระหว่าง Unity compile (แทนแถบ helpBox บนหัว)
        void DrawCompilingOverlay()
        {
            var area = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            const float W = 460f, H = 170f;
            var card = new Rect(area.x + (area.width - W) * 0.5f, area.y + (area.height - H) * 0.5f, W, H);
            RBox(card, BG_RAISED, BORDER, 14f);

            // spinner หมุนตามเวลา (glyph 4 เฟรม)
            string[] frames = { "◐", "◓", "◑", "◒" };
            int fi = (int)(EditorApplication.timeSinceStartup * 8) % frames.Length;
            var sp = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE + 18 };
            sp.normal.textColor = ACCENT;
            GUI.Label(new Rect(card.x, card.y + 18, card.width, 42), frames[fi], sp);

            CenterLabel(new Rect(card.x, card.y + 70, card.width, 30), "<b>Compiling scripts…</b>", TEXT_WHITE, FONT_SIZE + 7);
            CenterLabel(new Rect(card.x, card.y + 108, card.width, 24), "แชตหยุดชั่วคราว — ข้อความที่พิมพ์ไว้ยังอยู่", TEXT_MUTE, FONT_SIZE + 2);
        }

        // ── Claude models ทั้งหมด — id ใช้ได้ทั้ง API และ CLI (--model) ──
        static readonly string[] CLAUDE_MODEL_IDS =
        {
            "claude-fable-5", "claude-opus-4-8", "claude-opus-4-7",
            "claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5",
        };
        static readonly string[] CLAUDE_MODEL_LABELS =
        {
            "Fable 5 — ฉลาดสุด", "Opus 4.8 — ล่าสุด", "Opus 4.7",
            "Opus 4.6", "Sonnet 4.6 — สมดุล", "Haiku 4.5 — เร็วสุด",
        };
        const int CLAUDE_MODEL_DEFAULT = 4;   // Sonnet 4.6

        // ── Settings UI helpers (warm theme) ─────────────────────────────────
        static void SettingsLabel(string text)
        {
            var st = new GUIStyle(EditorStyles.boldLabel) { fontSize = FONT_SIZE - 1 };
            st.normal.textColor = TEXT_MUTE;
            EditorGUILayout.LabelField(text, st);
        }

        // segmented pill selector (แทน GUILayout.Toolbar) — คืน index ที่เลือก
        static int SegRow(int cur, string[] labels)
        {
            var r = EditorGUILayout.GetControlRect(false, 30, GUILayout.ExpandWidth(true));
            RBox(r, BG_SURFACE, BORDER_SOFT, 9f);
            float w = (r.width - 8f) / labels.Length;
            int picked = cur;
            var st = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1, alignment = TextAnchor.MiddleCenter };
            for (int i = 0; i < labels.Length; i++)
            {
                var seg = new Rect(r.x + 4f + i * w, r.y + 3f, w, r.height - 6f);
                bool active = i == cur;
                bool hover  = seg.Contains(Event.current.mousePosition);
                if (Event.current.type == EventType.Repaint)
                {
                    if (active)     RRect(seg, ACCENT, 7f);
                    else if (hover) RRect(seg, new Color(1f, 1f, 1f, 0.04f), 7f);
                    st.fontStyle = active ? FontStyle.Bold : FontStyle.Normal;
                    st.normal.textColor = active ? Color.white : hover ? TEXT_WHITE : TEXT_MUTE;
                    GUI.Label(seg, labels[i], st);
                }
                if (GUI.Button(seg, GUIContent.none, GUIStyle.none)) picked = i;
            }
            return picked;
        }

        // text field พื้นมุมโค้งเข้าธีม (รองรับ password) — คืนค่าใหม่
        static string ThemedTextField(string value, bool password = false)
        {
            var box = EditorGUILayout.GetControlRect(false, 28);
            RBox(box, BG_SURFACE, BORDER, 8f);
            var st = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE, alignment = TextAnchor.MiddleLeft };
            st.normal.textColor = TEXT_WHITE; st.focused.textColor = TEXT_WHITE; st.hover.textColor = TEXT_WHITE;
            var inner = new Rect(box.x + 10, box.y + 2, box.width - 20, box.height - 4);
            return password ? GUI.PasswordField(inner, value ?? "", '•', st)
                            : GUI.TextField(inner, value ?? "", st);
        }

        // dropdown เข้าธีม (generic — ใช้กับ Model/Effort/อะไรก็ได้) กางเป็น panel ในหน้า ไม่ใช้ GenericMenu ขาวของ OS
        string _openDrop;   // prefKey ของ dropdown ที่กางอยู่ (null = ปิด)
        void ThemedDropdown(string prefKey, string[] ids, string[] labels, int defIdx)
        {
            string cur = EditorPrefs.GetString(prefKey, ids[defIdx]);
            int idx = Array.IndexOf(ids, cur); if (idx < 0) idx = defIdx;
            bool open = _openDrop == prefKey;

            var r = EditorGUILayout.GetControlRect(false, 28);
            RBox(r, BG_SURFACE, open ? ACCENT : BORDER, 8f);
            var st = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE, alignment = TextAnchor.MiddleLeft };
            st.normal.textColor = TEXT_WHITE;
            GUI.Label(new Rect(r.x + 10, r.y, r.width - 40, r.height), labels[idx], st);
            var caret = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 3 };
            caret.normal.textColor = open ? ACCENT : TEXT_MUTE;
            GUI.Label(new Rect(r.xMax - 24, r.y, 18, r.height), open ? "▴" : "▾", caret);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { _openDrop = open ? null : prefKey; Repaint(); }
            if (!open) return;

            // option list (กางใต้ช่อง — ดันเนื้อหาลง ไม่ใช่ overlay)
            const float rowH = 26f;
            int n = ids.Length;
            EditorGUILayout.Space(2);
            var panel = EditorGUILayout.GetControlRect(false, n * rowH + 8);
            RBox(panel, BG_RAISED, BORDER, 8f);
            var rowSt = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE, alignment = TextAnchor.MiddleLeft };
            for (int i = 0; i < n; i++)
            {
                var row = new Rect(panel.x + 4, panel.y + 4 + i * rowH, panel.width - 8, rowH);
                bool sel = i == idx;
                bool hov = row.Contains(Event.current.mousePosition);
                if (Event.current.type == EventType.Repaint)
                {
                    if (sel)      RRect(row, new Color(ACCENT.r, ACCENT.g, ACCENT.b, 0.20f), 6f);
                    else if (hov) RRect(row, new Color(1f, 1f, 1f, 0.045f), 6f);
                    rowSt.normal.textColor = sel ? new Color(0.95f, 0.72f, 0.60f) : hov ? TEXT_WHITE : TEXT_MUTE;
                    GUI.Label(new Rect(row.x + 26, row.y, row.width - 30, row.height), labels[i], rowSt);
                    if (sel) CenterLabel(new Rect(row.x + 4, row.y, 20, row.height), "✓", ACCENT, FONT_SIZE - 1);
                }
                if (GUI.Button(row, GUIContent.none, GUIStyle.none))
                {
                    EditorPrefs.SetString(prefKey, ids[i]);
                    _openDrop = null;
                    Repaint();
                }
            }
        }

        // กล่อง info เข้าธีม (แทน HelpBox เทา)
        void InfoCard(string text)
        {
            var st = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1, wordWrap = true, padding = new RectOffset(12, 12, 8, 8) };
            st.normal.textColor = TEXT_MUTE;
            float h = st.CalcHeight(new GUIContent(text), position.width - 32);
            var r = EditorGUILayout.GetControlRect(false, h);
            RBox(r, new Color(0.102f, 0.090f, 0.078f), BORDER_SOFT, 8f);
            GUI.Label(r, text, st);
        }

        void DrawSettings()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);
            EditorGUILayout.BeginVertical();

            EditorGUILayout.Space(10);
            var title = new GUIStyle(EditorStyles.boldLabel) { fontSize = FONT_SIZE + 3 };
            title.normal.textColor = TEXT_WHITE;
            EditorGUILayout.LabelField("Settings", title);
            var sub = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1 };
            sub.normal.textColor = TEXT_HINT;
            EditorGUILayout.LabelField("ตั้งค่า backend · model · พฤติกรรมของ MCP Bridge", sub);
            EditorGUILayout.Space(12);

            SettingsLabel("Backend");
            int backend = CurrentBackend();
            int newBackend = SegRow(backend, new[] { "API Key", "Claude CLI (subscription)" });
            if (newBackend != backend) { SwitchBackend(newBackend); backend = newBackend; }

            EditorGUILayout.Space(10);

            if (backend == 0)
            {
                SettingsLabel("Anthropic API Key");
                string newKey = ThemedTextField(_apiKey, password: true);
                if (newKey != _apiKey) { _apiKey = newKey; EditorPrefs.SetString("DeltaMCP_ApiKey", _apiKey); }

                EditorGUILayout.Space(10);
                SettingsLabel("Model — เลือก Claude ที่จะใช้");
                ThemedDropdown("DeltaMCP_ApiModel", CLAUDE_MODEL_IDS, CLAUDE_MODEL_LABELS, CLAUDE_MODEL_DEFAULT);

                EditorGUILayout.Space(10);
                InfoCard("Sonnet/Haiku เร็ว+ถูก เหมาะงาน interactive · Opus/Fable ฉลาดสุดแต่แพง/ช้ากว่า · key เก็บใน EditorPrefs ไม่ขึ้น git");
            }
            else
            {
                SettingsLabel("Claude CLI command");
                string cmd = EditorPrefs.GetString("DeltaMCP_ClaudeCmd", "claude");
                string newCmd = ThemedTextField(cmd);
                if (newCmd != cmd) EditorPrefs.SetString("DeltaMCP_ClaudeCmd", newCmd);

                EditorGUILayout.Space(10);
                SettingsLabel("Model — เลือก Claude ที่จะใช้");
                ThemedDropdown("DeltaMCP_CliModel", CLAUDE_MODEL_IDS, CLAUDE_MODEL_LABELS, CLAUDE_MODEL_DEFAULT);

                EditorGUILayout.Space(10);
                SettingsLabel("Effort (คิดลึก vs เร็ว)");
                ThemedDropdown("DeltaMCP_CliEffort",
                    new[] { "low", "medium", "high", "max" },
                    new[] { "Low — เร็วสุด", "Medium — สมดุล (default)", "High — คิดลึก", "Max — ลึกสุด แต่ช้า" }, 1);

                EditorGUILayout.Space(10);
                SettingsLabel("Experimental flags (เปิดถ้าไม่แฮงค์)");
                var togSt = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1 };
                togSt.normal.textColor = TEXT_WHITE;
                bool useEffort = EditorPrefs.GetBool("DeltaMCP_CliUseEffort", false);
                bool newUseEffort = EditorGUILayout.ToggleLeft(new GUIContent(" ส่ง --effort ตามที่เลือก (บาง CLI ไม่รองรับ → แฮงค์)"), useEffort, togSt);
                if (newUseEffort != useEffort) EditorPrefs.SetBool("DeltaMCP_CliUseEffort", newUseEffort);
                bool fast = EditorPrefs.GetBool("DeltaMCP_CliFast", false);
                bool newFast = EditorGUILayout.ToggleLeft(new GUIContent(" Fast mode: ปิดโหลด MCP (--strict-mcp-config) เร็วขึ้นแต่เสี่ยงแฮงค์บน Windows"), fast, togSt);
                if (newFast != fast) EditorPrefs.SetBool("DeltaMCP_CliFast", newFast);

                EditorGUILayout.Space(10);
                InfoCard("ใช้ Claude Code CLI (subscription/Max) — ไม่กิน API Key\nต้องติดตั้ง Claude Code + login ก่อน\nช้ากว่า API เพราะ cold-start ทุกครั้ง — เลือก Haiku/Sonnet ให้เร็วขึ้น");
            }

            EditorGUILayout.Space(14);
            var backR = EditorGUILayout.GetControlRect(false, 30);
            RBox(backR, BG_RAISED, BORDER, 9f);
            bool backHover = backR.Contains(Event.current.mousePosition);
            CenterLabel(backR, "←  Back", backHover ? TEXT_WHITE : TEXT_MUTE, FONT_SIZE);
            if (GUI.Button(backR, GUIContent.none, GUIStyle.none)) _showSettings = false;

            EditorGUILayout.EndVertical();
            GUILayout.Space(12);
            EditorGUILayout.EndHorizontal();
        }

        void DrawChatHistory()
        {
            var s = S;
            float reserved = 116 + _inputHeight + (s.images.Count > 0 ? 48 : 0); // +2 สำหรับ border รอบ input box
            if (_showScriptList || _showPrefabList || _showSkillList) reserved += SCRIPT_LIST_HEIGHT;
            if (_showLive) reserved += 44;
            if (_showKeywords) reserved += 92;
            float historyHeight = Mathf.Max(100, position.height - reserved);

            // ── smooth scroll: ดัก wheel เอง → ตั้ง target → lerp เข้าหา ──
            var ev = Event.current;
            // โซน history จริง: ใต้แถว tabs(38)+เส้น(1)+ช่องว่าง(6) ลงมาแค่ historyHeight
            // (สูตรเดิมกินเกินลงไปถึง picker/input → wheel บน picker โดนแย่งไป scroll แชต)
            const float histTop = 45f;
            bool overHistory = ev.mousePosition.y >= histTop && ev.mousePosition.y < histTop + historyHeight;
            if (ev.type == EventType.ScrollWheel && overHistory)
            {
                if (ev.delta.y < 0) _stickBottom = false;  // scroll ขึ้น = เลิกตามล่าง
                if (!_scrollAnim) _scrollTarget = s.chatScroll.y;
                _scrollTarget = Mathf.Max(0, _scrollTarget + ev.delta.y * 30f);
                _scrollAnim = true;
                ev.Use();
            }
            // lerp เข้าหา target ทีละเฟรม (หยุดเมื่อใกล้พอ)
            if (_scrollAnim)
            {
                float ny = Mathf.Lerp(s.chatScroll.y, _scrollTarget, 0.35f);
                if (Mathf.Abs(ny - _scrollTarget) < 0.5f) { ny = _scrollTarget; _scrollAnim = false; }
                s.chatScroll.y = Mathf.Max(0, ny);
                Repaint();
            }

            float wantY = s.chatScroll.y;
            // ไม่โชว์ scrollbar — smooth wheel scroll จัดการให้อยู่แล้ว
            s.chatScroll = EditorGUILayout.BeginScrollView(s.chatScroll,
                false, false, GUIStyle.none, GUIStyle.none, GUIStyle.none,
                GUILayout.Height(historyHeight), GUILayout.ExpandWidth(true));
            // โดน clamp (ถึงขอบบน/ล่าง) → ค่าจริงต่างจากที่ตั้ง → หยุด animate
            if (_scrollAnim && Mathf.Abs(s.chatScroll.y - wantY) > 0.5f)
            {
                // ถ้าตั้งจะเลื่อนลง (wantY มากกว่า) แต่ถูก clamp = ถึงล่างสุด → กลับมาตามล่าง
                if (wantY > s.chatScroll.y + 0.5f) _stickBottom = true;
                _scrollTarget = s.chatScroll.y;
                _scrollAnim = false;
            }

            float bubbleWidth = position.width - 36;

            // textStyle หนัก (ใช้ CalcHeight) → cache. แต่ตัว text color set ใหม่ทุกเฟรม กัน Hot Reload ไม่อัปเดต
            if (_msgTextStyle == null)
                _msgTextStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true, richText = true, fontSize = MSG_FONT,
                    padding = new RectOffset(12, 12, 9, 9)
                };
            _msgTextStyle.fontSize = MSG_FONT;   // set ทุกเฟรม กัน style ค้างข้าม domain reload
            _msgTextStyle.font = UiFont;
            _msgTextStyle.normal.textColor = TEXT_WHITE;

            // style เล็ก (role/stat) — สร้างใหม่ทุกเฟรม + จัดกึ่งกลางแนวตั้ง ให้อยู่บรรทัดเดียวกัน
            _roleUser   = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = FONT_SIZE - 1, richText = true };
            _roleClaude = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = FONT_SIZE - 1, richText = true };
            _roleUser.normal.textColor   = new Color(0.80f, 0.62f, 0.50f);
            _roleClaude.normal.textColor = ACCENT;
            var textStyle = _msgTextStyle;

            for (int mi = 0; mi < s.messages.Count; mi++)
            {
                var msg = s.messages[mi];

                // ── ข้าม AI response ถ้า user message ก่อนหน้า collapsed ──
                if (msg.Role == "assistant" && mi > 0 && s.messages[mi - 1].Role == "user" && s.messages[mi - 1].collapsed)
                    continue;

                // bubble "กำลังคิด" / "รอคิว" — มีปุ่มยกเลิกต่ออัน
                if (msg.Content == THINKING || msg.Content == QUEUED)
                {
                    bool thinking = msg.Content == THINKING;
                    var think = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE };
                    think.normal.textColor = TEXT_MUTE;
                    string t;
                    if (thinking)
                    {
                        double sec = EditorApplication.timeSinceStartup - s.requestStart;
                        t = $"◌ กำลังคิด...  ({FmtTime(sec)}";
                        if (s.backend == 1 && ClaudeCliClient.LiveOutputTokens > 0) t += $" · {ClaudeCliClient.LiveOutputTokens:N0} tokens";
                        t += ")";
                    }
                    else t = "⏳ รอคิว...";

                    // header avatar เหมือน assistant ปกติ → เห็นปุ๊บรู้ว่าเป็น "คำตอบที่กำลังมา" ของ prompt ข้างบน
                    var thRow = GUILayoutUtility.GetRect(bubbleWidth, 24);
                    var thAv = new Rect(thRow.x + 8, thRow.y + 1, 20, 20);
                    RRect(thAv, ACCENT, 10f);
                    var thAvSt = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 1 };
                    thAvSt.normal.textColor = Color.white;
                    GUI.Label(thAv, "✦", thAvSt);
                    var thName = new GUIStyle(_roleClaude) { alignment = TextAnchor.MiddleLeft };
                    GUI.Label(new Rect(thAv.xMax + 9, thRow.y, 200, thRow.height), "MCP Bridge", thName);

                    // bubble ทรงเดียวกับ assistant (มุม 4/12 + แถบ accent ซ้าย, inset 8 ตรงขอบการ์ด)
                    var rrFull = GUILayoutUtility.GetRect(bubbleWidth, 28);
                    var rr = new Rect(rrFull.x + 8, rrFull.y, rrFull.width - 16, 26);
                    RRect4(rr, BG_SURFACE, 4f, 12f, 12f, 12f);
                    RRect4(new Rect(rr.x, rr.y, 3, rr.height), ACCENT, 3f, 0f, 0f, 3f);
                    GUI.Label(new Rect(rr.x + 12, rr.y, rr.width - 84, rr.height), t, think);
                    // ปุ่มยกเลิก ✕ ต่ออัน
                    var xr = new Rect(rr.xMax - 62, rr.y + 3, 54, rr.height - 6);
                    RRect(xr, new Color(0.46f, 0.28f, 0.26f), 6f);
                    var xStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 3 };
                    xStyle.normal.textColor = new Color(1f, 0.86f, 0.82f);
                    GUI.Label(xr, "✕ ยกเลิก", xStyle);
                    if (GUI.Button(xr, GUIContent.none, GUIStyle.none))
                    {
                        if (thinking) { s.cts?.Cancel(); s.cliSessionId = null; s.cliTurnCount = 0; }  // ยกเลิกตัวที่กำลังคิด → ล้าง session (turn ถูกตัด)
                        else CancelQueued(s, mi);             // เอาออกจากคิว (ไม่แตะ session — ยังไม่ได้รัน)
                    }
                    EditorGUILayout.Space(6);
                    // ขยับ spinner/timer ตอน "กำลังคิด" — throttle ไม่ให้ repaint ทุกเฟรม (กันแย่ง frame เกมตอน play)
                    if (thinking && Event.current.type == EventType.Repaint)
                    {
                        double now = EditorApplication.timeSinceStartup;
                        double interval = Application.isPlaying ? 0.5 : 0.25;   // play: ช้าลง ลดผลกระทบเกม
                        if (now - _lastThinkRepaint > interval) { _lastThinkRepaint = now; Repaint(); }
                    }
                    continue;
                }

                // ── เส้นแบ่งระหว่าง pair — วาดทั้ง collapsed/expanded (จังหวะแนวตั้งเท่ากัน toggle แล้วแถวไม่เด้ง) ──
                if (msg.Role == "user" && mi > 0)
                {
                    EditorGUILayout.Space(2);
                    var pairDiv = GUILayoutUtility.GetRect(bubbleWidth, 1);
                    EditorGUI.DrawRect(new Rect(pairDiv.x + 8, pairDiv.y, pairDiv.width - 16, 1), BORDER_SOFT);
                    EditorGUILayout.Space(4);
                }

                // ── Collapsed: user message แสดง compact row แทน full bubble ──
                if (msg.Role == "user" && msg.collapsed)
                {
                    var crFull = GUILayoutUtility.GetRect(bubbleWidth, 34);
                    var cr = new Rect(crFull.x + 8, crFull.y, crFull.width - 16, 31);
                    RRect(cr, BG_RAISED, 8f);
                    RRect4(new Rect(cr.x, cr.y, 2, cr.height), ACCENT, 8f, 0f, 0f, 8f);
                    // ลูกศรในกล่อง 16px จัดกึ่งกลาง — ▶/▼ glyph กว้างไม่เท่ากัน ถ้าชิดซ้าย text จะขยับ
                    var toggleR = new Rect(cr.x + 9, cr.y, 16, cr.height);
                    var toggleStyle = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE, alignment = TextAnchor.MiddleCenter };
                    toggleStyle.normal.textColor = ACCENT;
                    GUI.Label(toggleR, "▶", toggleStyle);
                    // preview text — เอาแค่บรรทัดแรก (ตัด note metadata ที่ append ด้วย \n)
                    string preview = msg.Content;
                    int _nl = preview.IndexOf('\n');
                    if (_nl >= 0) preview = preview.Substring(0, _nl);
                    if (preview.Length > 80) preview = preview.Substring(0, 77) + "...";
                    var previewStyle = new GUIStyle(EditorStyles.label) { font = UiFont, fontSize = MSG_FONT, alignment = TextAnchor.MiddleLeft };
                    previewStyle.normal.textColor = TEXT_WHITE;
                    GUI.Label(new Rect(cr.x + 30, cr.y, cr.width - 38, cr.height), preview, previewStyle);
                    if (GUI.Button(cr, GUIContent.none, GUIStyle.none)) { msg.collapsed = false; Repaint(); }
                    EditorGUILayout.Space(4);
                    continue;
                }

                // fade-in นุ่มๆ ตอนข้อความใหม่โผล่ (overlay สี bg จางหายไป)
                float fade = msg.FadeAlpha(EditorApplication.timeSinceStartup);
                var fadeGroup = EditorGUILayout.BeginVertical();

                // เลือก view ตาม role ปัจจุบัน (Dev/Art) — user message คืน msg เดิมเสมอ
                var displayMsg = msg.RoleView(CurrentRole());
                bool isUser = displayMsg.Role == "user";
                Color accent = isUser ? new Color(0.55f, 0.50f, 0.45f) : ACCENT;
                Color bg     = new Color(0.122f, 0.110f, 0.092f);   // user/assistant พื้นสีเดียวกัน

                // ── ป้ายชื่อ (tag) ──
                if (isUser)
                {
                    // header card ตอนกาง — กรอบเดียวกับตอนหุบ (inset 8) เนื้อหาข้างล่างอยู่ในขอบเดียวกัน
                    var hrFull = GUILayoutUtility.GetRect(bubbleWidth, 34);
                    var hr = new Rect(hrFull.x + 8, hrFull.y, hrFull.width - 16, 31);
                    RRect(hr, BG_RAISED, 8f);
                    RRect4(new Rect(hr.x, hr.y, 2, hr.height), ACCENT, 8f, 0f, 0f, 8f);
                    var toggleStyle2 = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE, alignment = TextAnchor.MiddleCenter };
                    toggleStyle2.normal.textColor = ACCENT;
                    GUI.Label(new Rect(hr.x + 9, hr.y, 16, hr.height), "▼", toggleStyle2);
                    // prompt preview ข้างๆ ▼ — เอาแค่บรรทัดแรก (ตัด note metadata ที่ append ด้วย \n)
                    string hdrPreview = msg.Content;
                    int _hdrNl = hdrPreview.IndexOf('\n');
                    if (_hdrNl >= 0) hdrPreview = hdrPreview.Substring(0, _hdrNl);
                    float hdrMaxW = hr.width - 60f;
                    if (hdrPreview.Length > 55) hdrPreview = hdrPreview.Substring(0, 52) + "...";
                    var hdrPreviewStyle = new GUIStyle(EditorStyles.label) { font = UiFont, fontSize = MSG_FONT, alignment = TextAnchor.MiddleLeft };
                    hdrPreviewStyle.normal.textColor = TEXT_WHITE;
                    GUI.Label(new Rect(hr.x + 30, hr.y, hdrMaxW, hr.height), hdrPreview, hdrPreviewStyle);
                    // ทั้งการ์ดคลิกเพื่อหุบได้ (header สะอาด ไม่มี chip ปน)
                    if (GUI.Button(hr, GUIContent.none, GUIStyle.none)) { msg.collapsed = true; Repaint(); }

                    // ชื่อ user — ข้อความเปล่าๆ สี clay เหนือ bubble ฝั่งขวา (ไม่มีพื้นหลัง/ดาว)
                    var tagRow = GUILayoutUtility.GetRect(bubbleWidth, 22);
                    var tagSt = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleRight, font = UiFont, fontSize = MSG_FONT, fontStyle = FontStyle.Bold };
                    tagSt.normal.textColor = ACCENT;
                    GUI.Label(new Rect(tagRow.x, tagRow.y, tagRow.width - 12, 22), "พี่สุดหล่อ", tagSt);
                }
                else
                {
                    var hrow = GUILayoutUtility.GetRect(bubbleWidth, 24);
                    // avatar วงกลม clay + ✦
                    var avR = new Rect(hrow.x + 8, hrow.y + 1, 20, 20);
                    RRect(avR, ACCENT, 10f);
                    var avStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 1 };
                    avStyle.normal.textColor = Color.white;
                    GUI.Label(avR, "✦", avStyle);
                    // ชื่อ
                    string roleTag = CurrentRole() == 1 ? "Art" : "Dev";
                    var nameStyle = new GUIStyle(_roleClaude) { alignment = TextAnchor.MiddleLeft };
                    GUI.Label(new Rect(avR.xMax + 9, hrow.y, bubbleWidth - 200, hrow.height), $"MCP Bridge  ·  {roleTag}", nameStyle);
                    // ปุ่ม Copy All (ขวาสุด — inset 8 ให้ตรงขอบการ์ด)
                    var copyR = new Rect(hrow.xMax - 70, hrow.y + 3, 58, 18);
                    RBox(copyR, BG_RAISED, BORDER, 6f);
                    CenterLabel(copyR, "Copy All", TEXT_MUTE, FONT_SIZE - 3);
                    if (GUI.Button(copyR, GUIContent.none, GUIStyle.none))
                    {
                        EditorGUIUtility.systemCopyBuffer = displayMsg.DisplayContent;
                        Debug.Log("[MCP Bridge] Copied AI response to clipboard.");
                    }
                    // stat (ซ้ายของปุ่ม Copy)
                    if (!string.IsNullOrEmpty(displayMsg.Stat))
                    {
                        var statStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 4, alignment = TextAnchor.MiddleRight };
                        statStyle.normal.textColor = new Color(0.42f, 0.44f, 0.50f);
                        GUI.Label(new Rect(hrow.x, hrow.y, copyR.x - hrow.x - 8, hrow.height), displayMsg.Stat, statStyle);
                    }
                }
                EditorGUILayout.Space(3);   // กัน label ทับกล่อง (เหมือน tag)

                if (displayMsg.HasRich)
                {
                    // ── มี code block หรือ ตาราง → render แบบ segment ──
                    DrawSegments(displayMsg, accent);
                }
                else
                {
                    // ── ข้อความปกติ — fast path (manual rect + cache) ──
                    // กว้างอิง bubbleWidth + inset 8 ทั้งคู่ → อยู่ในขอบเดียวกับ header card เป๊ะ
                    string rich = displayMsg.Rich();
                    float availEst = bubbleWidth - 16f;
                    float cw_est = isUser ? availEst * 0.80f : availEst;
                    float h = displayMsg.Height(textStyle, cw_est - 6);
                    Rect row = GUILayoutUtility.GetRect(bubbleWidth, h);
                    float avail = row.width - 16f;
                    float cw = isUser ? avail * 0.80f : avail;
                    float x  = isUser ? row.x + 8f + (avail - cw) : row.x + 8f;
                    Rect box = new Rect(x, row.y, cw, h);

                    if (isUser)
                    {
                        RRect4(box, bg, 12f, 12f, 4f, 12f);
                        RRect4(new Rect(box.xMax - 3, box.y, 3, box.height), accent, 0f, 3f, 3f, 0f);
                    }
                    else
                    {
                        RRect4(box, bg, 4f, 12f, 12f, 12f);
                        RRect4(new Rect(box.x, box.y, 3, box.height), accent, 3f, 0f, 0f, 3f);
                    }

                    EditorGUI.SelectableLabel(new Rect(box.x + 6, box.y, box.width - 10, box.height), rich, textStyle);
                }
                EditorGUILayout.Space(8);

                EditorGUILayout.EndVertical();
                // overlay สีพื้นจางๆ ทับ แล้วค่อยๆ โปร่งใส = fade-in
                if (fade < 1f)
                {
                    if (Event.current.type == EventType.Repaint)
                        EditorGUI.DrawRect(fadeGroup, new Color(BG_DARK.r, BG_DARK.g, BG_DARK.b, 1f - fade));
                    Repaint();
                }
            }

            // (สถานะ "กำลังคิด" + เวลา ย้ายไปอยู่ในกล่อง bubble แล้ว — ไม่มีข้อความกลางจอ)
            // วาด label ว่างเสมอ เพื่อ control count คงที่ (กัน "Invalid GUILayout state")
            var loading = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = FONT_SIZE - 2 };
            loading.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
            EditorGUILayout.LabelField(s.queue.Count > 0 ? $"({s.queue.Count} in queue)" : "", loading);

            EditorGUILayout.EndScrollView();

            // auto-scroll ลงล่างสุด — เฉพาะตอน "อยู่ล่างสุด" (ไม่เด้งถ้า scroll ขึ้นไปอ่าน)
            if (_autoScroll && Event.current.type == EventType.Repaint)
            {
                if (_stickBottom) { _scrollTarget = 100000f; _scrollAnim = true; }
                _autoScroll = false;
            }
        }

        GUIStyle _codeStyle, _codeHeaderStyle, _segTextStyle, _tableCellStyle, _tableHeadStyle;
        Font _monoFont;

        // วาดข้อความที่มี code block — text ปกติ + code box (header path + highlight + copy)
        void DrawSegments(ChatMessage msg, Color accent)
        {
            if (_segTextStyle == null)
            {
                _segTextStyle = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true, fontSize = MSG_FONT, padding = new RectOffset(10, 10, 6, 6) };
                _segTextStyle.normal.textColor = TEXT_WHITE;
                _monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Menlo", "Courier New", "monospace" }, MSG_FONT);
                _codeStyle = new GUIStyle(EditorStyles.label) { wordWrap = false, richText = true, fontSize = MSG_FONT - 1, font = _monoFont, padding = new RectOffset(10, 10, 8, 8) };
                _codeStyle.normal.textColor = new Color(0.812f, 0.780f, 0.733f);
                _codeHeaderStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2, padding = new RectOffset(8, 8, 3, 3), richText = true };
                // หมายเหตุ: _tableCellStyle/_tableHeadStyle สร้างใน DrawTable ที่เดียว (กันตั้ง alignment ซ้ำ/ชน)
            }
            _segTextStyle.fontSize = MSG_FONT;   // set ทุกเฟรม กัน style ค้างข้าม domain reload
            _segTextStyle.font = UiFont;
            _segTextStyle.normal.textColor = TEXT_WHITE;
            _codeStyle.fontSize = MSG_FONT - 1;
            _codeStyle.normal.textColor = new Color(0.812f, 0.780f, 0.733f);

            float w = position.width - 62;   // ขอบขวาตรงกับ header card (inset 8)
            // แถบสี + เนื้อหา (เว้นซ้าย 8 + gap 8 หลังแถบ กันข้อความชนเส้น accent)
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            var barRect = GUILayoutUtility.GetRect(2, 2, GUILayout.Width(2), GUILayout.ExpandHeight(true));
            GUILayout.Space(8);
            EditorGUILayout.BeginVertical();

            foreach (var seg in msg.Segments())
            {
                if (seg.Table)
                {
                    // header bar (toggle) — เหมือน code box
                    DrawSegHeader(seg, w, "[=]", "table", null);
                    if (!seg.Collapsed) DrawTable(seg, w);
                }
                else if (!seg.Code)
                {
                    float h = _segTextStyle.CalcHeight(new GUIContent(seg.Rendered), w);
                    var r = GUILayoutUtility.GetRect(w, h);
                    RRect(r, BG_SURFACE, 8f);
                    EditorGUI.SelectableLabel(r, seg.Rendered, _segTextStyle);
                }
                else
                {
                    // header bar (toggle) + ปุ่ม copy
                    DrawSegHeader(seg, w, "</>", seg.Header, seg.Raw);
                    if (!seg.Collapsed)
                    {
                        // code body (พื้นดำเข้ม + highlight + เลือกได้)
                        float ch = _codeStyle.CalcHeight(new GUIContent(seg.Rendered), w);
                        var cr = GUILayoutUtility.GetRect(w, ch);
                        RRect4(cr, new Color(0.063f, 0.055f, 0.047f), 0f, 0f, 8f, 8f);
                        EditorGUI.SelectableLabel(cr, seg.Rendered, _codeStyle);
                    }
                }
                EditorGUILayout.Space(2);
            }
            EditorGUILayout.Space(6);   // padding ล่าง — กันข้อความบรรทัดสุดท้ายติดขอบ
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            // วาดแถบ accent ทับ (หลังรู้ความสูง)
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, 2, GUILayoutUtility.GetLastRect().yMax - barRect.y), accent);
        }

        // แถบหัว box (code/table) — มีลูกศรพับ + ชื่อ + ปุ่ม Copy (ถ้ามี copyText) คลิกแถบ = toggle
        void DrawSegHeader(Seg seg, float w, string icon, string title, string copyText)
        {
            var hbar = GUILayoutUtility.GetRect(w, 24);
            RRect4(hbar, new Color(0.102f, 0.090f, 0.078f), 8f, 8f, 0f, 0f);
            string arrow = seg.Collapsed ? "▶" : "▼";
            GUI.Label(new Rect(hbar.x + 6, hbar.y, hbar.width - 76, hbar.height), $"<color=white>{arrow}  {icon} {title}</color>", _codeHeaderStyle);

            float clickW = hbar.width;
            if (!string.IsNullOrEmpty(copyText))
            {
                clickW = hbar.width - 72;   // เว้นพื้นที่ปุ่ม Copy ไม่ให้โดน toggle
                if (GUI.Button(new Rect(hbar.xMax - 66, hbar.y + 1, 60, 18), "Copy", EditorStyles.miniButton))
                {
                    EditorGUIUtility.systemCopyBuffer = copyText;
                    Debug.Log($"[MCP Bridge] Copied {title} to clipboard.");
                }
            }
            // คลิกแถบหัว (ส่วนที่ไม่ใช่ปุ่ม Copy) = พับ/กาง
            if (GUI.Button(new Rect(hbar.x, hbar.y, clickW, hbar.height), GUIContent.none, GUIStyle.none))
                seg.Collapsed = !seg.Collapsed;
        }

        // วาด markdown table เป็นกริดจริง — กว้างตามสัดส่วนตัวอักษร, wrap ในเซลล์, แถวแรกเป็น header
        void DrawTable(Seg seg, float w)
        {
            // สร้าง style ใหม่ทุกครั้ง (ไม่ cache) — กัน Hot Reload เก็บ style เก่าที่ alignment ผิด
            var cellStyle = new GUIStyle(EditorStyles.label) { font = UiFont, wordWrap = true, richText = true, fontSize = FONT_SIZE - 1, padding = new RectOffset(8, 8, 6, 6), alignment = TextAnchor.MiddleLeft };
            cellStyle.normal.textColor = Color.white;
            var headStyle = new GUIStyle(cellStyle) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            headStyle.normal.textColor = Color.white;
            _tableCellStyle = cellStyle; _tableHeadStyle = headStyle;

            int cols = seg.Cols;
            var rows = seg.Rows;

            // ── เลย์เอาต์: การ์ดหุ้มนอก (margin) + padding ในกล่อง → ตารางอยู่ข้างใน ไม่ชนขอบ ──
            const float OUTER = 0f;    // ชิดขอบ box (มี header bar เป็นกรอบบนแล้ว)
            const float PAD   = 10f;   // padding ในการ์ด (กรอบ→ตาราง)
            float boxW = w - OUTER * 2;
            float gridW = boxW - PAD * 2;   // ความกว้างตารางจริง

            // ความกว้างคอลัมน์ตามสัดส่วนความยาวข้อความสูงสุดของแต่ละคอลัมน์ (clamp ขั้นต่ำ)
            var weight = new float[cols];
            foreach (var row in rows)
                for (int c = 0; c < cols; c++)
                    if (c < row.Length) weight[c] = Mathf.Max(weight[c], Mathf.Max(3, row[c].Length));
            float sumW = 0f;
            for (int c = 0; c < cols; c++) { if (weight[c] < 3) weight[c] = 3; sumW += weight[c]; }
            var cw = new float[cols];
            for (int c = 0; c < cols; c++) cw[c] = gridW * weight[c] / sumW;

            // คำนวณความสูงแต่ละแถว (max ของเซลล์ที่ wrap)
            var rowH = new float[rows.Count];
            float totalH = 0f;
            for (int r = 0; r < rows.Count; r++)
            {
                float hMax = r == 0 ? 26f : 24f;
                var st = r == 0 ? _tableHeadStyle : _tableCellStyle;
                for (int c = 0; c < cols; c++)
                {
                    string txt = c < rows[r].Length ? rows[r][c] : "";
                    hMax = Mathf.Max(hMax, st.CalcHeight(new GUIContent(txt), cw[c]));
                }
                rowH[r] = hMax;
                totalH += hMax;
            }

            var line   = new Color(1f, 0.96f, 0.90f, 0.14f);   // เส้นตารางจาง (warm)
            var cardBg = new Color(0.063f, 0.055f, 0.047f);    // พื้นการ์ด
            var cellBg = new Color(0.110f, 0.098f, 0.082f);    // พื้นเซลล์

            // จองพื้นที่ = การ์ด + margin บน-ล่าง
            float boxH = totalH + PAD * 2;
            var slot = GUILayoutUtility.GetRect(w, boxH + OUTER * 2);
            var box  = new Rect(slot.x + OUTER, slot.y + OUTER, boxW, boxH);
            RRect4(box, cardBg, 0f, 0f, 8f, 8f);

            // กริดตารางอยู่ในการ์ด เยื้องเข้ามา PAD
            float gx = box.x + PAD, gy = box.y + PAD;
            var grid = new Rect(gx, gy, gridW, totalH);
            EditorGUI.DrawRect(grid, cellBg);

            // ข้อความในเซลล์ — บังคับขาวด้วย color tag (กัน style เพี้ยนตอน Hot Reload/unfocus)
            float y = gy;
            for (int r = 0; r < rows.Count; r++)
            {
                float x = gx;
                var st = r == 0 ? _tableHeadStyle : _tableCellStyle;
                for (int c = 0; c < cols; c++)
                {
                    string raw = c < rows[r].Length ? rows[r][c] : "";
                    string txt = r == 0 ? $"<b><color=#FFFFFF>{raw}</color></b>" : $"<color=#FFFFFF>{raw}</color>";
                    // GUI.Label = จัด alignment ได้จริง (SelectableLabel ไม่สน alignment)
                    GUI.Label(new Rect(x + 6, y, cw[c] - 12, rowH[r]), txt, st);
                    x += cw[c];
                }
                y += rowH[r];
            }

            // เส้น grid ขาวบางๆ (ทับทีหลัง) — แนวนอน + แนวตั้ง อยู่ในขอบ grid
            void HLine(float yy) => EditorGUI.DrawRect(new Rect(grid.x, Mathf.Min(yy, grid.yMax - 1), gridW, 1), line);
            void VLine(float xx) => EditorGUI.DrawRect(new Rect(Mathf.Min(xx, grid.xMax - 1), grid.y, 1, totalH), line);
            float yy2 = grid.y; HLine(yy2);
            for (int r = 0; r < rows.Count; r++) { yy2 += rowH[r]; HLine(yy2); }
            float xx2 = grid.x; VLine(xx2);
            for (int c = 0; c < cols; c++) { xx2 += cw[c]; VLine(xx2); }
        }

        void DrawAttachToolbar()
        {
            var s = S;
            EditorGUILayout.BeginHorizontal();

            var small = new GUIStyle(GUI.skin.button) { fontSize = FONT_SIZE - 2 };

            if (GUILayout.Button("+ Image", small, GUILayout.Height(20), GUILayout.Width(72)))
                BrowseImages();

            // ── ปุ่ม profiler (📍GC / 🔬Deep / 📈Live) ซ่อนชั่วคราว: คู่กับ ProfilerReader.ENABLED=false (ลด overhead ตอน Play) ──
            const bool SHOW_PROFILER_UI = false;
            if (SHOW_PROFILER_UI)
            {
            // 📍 GC — toggle ดัก GC allocation callstack (กดได้เฉพาะตอน Play เหมือนปุ่ม Deep)
            // → ดูบรรทัดที่ alloc ผ่าน keyword gc/perf (auto-gather รวม Snapshot ให้)
            var gcStyle = new GUIStyle(small);
            if (ProfilerReader.AllocCallstacks) gcStyle.normal.textColor = new Color(1f, 0.6f, 0.3f);
            else if (!Application.isPlaying) gcStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);   // หรี่ตอนยังไม่ Play (กดไม่ได้)
            string gcLabel = ProfilerReader.AllocCallstacks ? "📍 GC+" : "📍 GC";
            if (GUILayout.Button(new GUIContent(gcLabel, "toggle ดัก GC allocation callstack\nเล่นอยู่ → กดเปิด → พิมพ์/กด keyword gc หรือ perf → เห็นบรรทัดที่ alloc จริง\nดักเฉพาะ alloc ที่เกิดตอนเปิด (ไม่ย้อนหลัง) · ต้องกด Play ก่อน · recompile = ปิดเอง"), gcStyle, GUILayout.Height(20), GUILayout.Width(54)))
            {
                if (!Application.isPlaying)
                    ShowNotification(new GUIContent("กด Play ก่อน — 📍 GC ดักได้เฉพาะตอนเล่นอยู่"));
                else
                    ProfilerReader.AllocCallstacks = !ProfilerReader.AllocCallstacks;
            }

            // 🔬 Deep — จับ Deep Profile 5 วิ → CPU method + GC บรรทัด + Network bandwidth ราย object → ส่งอัตโนมัติ
            var deepStyle = new GUIStyle(small);
            string deepLabel;
            if (CpuDeepCapture.IsCapturing) { deepStyle.normal.textColor = new Color(1f, 0.4f, 0.4f); deepLabel = $"⏺ {CpuDeepCapture.SecondsLeft}s"; }
            else deepLabel = "🔬 Deep";
            if (GUILayout.Button(new GUIContent(deepLabel, "จับเชิงลึก 5 วิ → CPU (method+บรรทัด) + GC (บรรทัดที่ alloc) + Network (bandwidth ราย object) → ส่งให้ AI อัตโนมัติ\nกดแล้วเล่นให้เกิดอาการหน่วงระหว่างนับถอยหลัง 5 วิ\n(ถ้าพิมพ์คำถามไว้ในช่อง จะส่งคำถามนั้น · ต้องกด Play ก่อน · หนักเฉพาะ 5 วิ ปิดเอง)"), deepStyle, GUILayout.Height(20), GUILayout.Width(64)))
            {
                if (CpuDeepCapture.IsCapturing) { /* กำลังจับอยู่ — กดซ้ำไม่ทำอะไร */ }
                else if (!Application.isPlaying)
                    ShowNotification(new GUIContent("กด Play ก่อน — 🔬 Deep จับ CPU ได้เฉพาะตอนเล่นอยู่"));
                else
                    CpuDeepCapture.Start(5f, report =>
                    {
                        // ถูกเรียกหลังจับครบ 5 วิเท่านั้น → แนบ + ส่งอัตโนมัติ (เคารพคำถามที่พิมพ์ไว้)
                        S.attached["Deep Analysis"] = report;
                        if (string.IsNullOrEmpty(S.draft.Trim()))
                            S.draft = "Analyze the attached Deep profiler data (CPU method-level + GC callstacks + Network bandwidth per object). ชี้ method+บรรทัดที่กิน CPU, บรรทัดที่ alloc GC, และ NetworkObject ที่ sync เปลือง bandwidth, จัดลำดับความเสี่ยง, เสนอวิธีแก้";
                        Enqueue();
                    });
            }

            // toggle แผง Live (real-time)
            var liveStyle = new GUIStyle(small);
            if (_showLive) liveStyle.normal.textColor = new Color(0.4f, 0.9f, 0.5f);
            if (GUILayout.Button(_showLive ? "🟢 Live" : "📈 Live", liveStyle, GUILayout.Height(20), GUILayout.Width(60)))
                _showLive = !_showLive;
            } // SHOW_PROFILER_UI

            // toggle keyword panel
            var kwStyle = new GUIStyle(small);
            if (_showKeywords) kwStyle.normal.textColor = ACCENT;
            if (GUILayout.Button("🔑 Keys", kwStyle, GUILayout.Height(20), GUILayout.Width(60)))
                _showKeywords = !_showKeywords;

            // toggle Realtime Monitor (background — จับ memory สูง/ค้าง → log)
            var monStyle = new GUIStyle(small);
            if (RealtimeMonitor.IsOn) monStyle.normal.textColor = new Color(1f, 0.5f, 0.4f);
            string monLabel = RealtimeMonitor.IsOn ? "🔴 Monitor" : "🩺 Monitor";
            if (GUILayout.Button(new GUIContent(monLabel, "ตรวจสุขภาพ Unity แบบ real-time (memory/ค้าง) → Library/DeltaMCP/monitor.log"), monStyle, GUILayout.Height(20), GUILayout.Width(78)))
                RealtimeMonitor.Toggle();

            if (s.images.Count > 0)
            {
                var lbl = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2 };
                GUILayout.Label($"{s.images.Count} img", lbl, GUILayout.Width(45));
                if (GUILayout.Button("✕", small, GUILayout.Height(20), GUILayout.Width(24)))
                    s.images.Clear();
            }

            GUILayout.FlexibleSpace();
            var tip = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 3, alignment = TextAnchor.MiddleRight };
            GUILayout.Label("@ = script  •  # = prefab  •  / = skill  •  Ctrl+V = paste image", tip);
            EditorGUILayout.EndHorizontal();

            if (s.images.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                for (int i = 0; i < s.images.Count; i++)
                {
                    if (GUILayout.Button(s.images[i].Texture, GUILayout.Width(40), GUILayout.Height(40)))
                    {
                        s.images.RemoveAt(i);
                        break;
                    }
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
        }

        void DrawLivePanel()
        {
            if (!_showLive) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (ProfilerReader.IsLive)
            {
                float fps = ProfilerReader.CurrentFps();
                // สีตาม FPS: เขียว >=55, เหลือง >=30, แดง < 30
                Color c = fps >= 55 ? new Color(0.4f, 0.9f, 0.5f)
                        : fps >= 30 ? new Color(0.95f, 0.85f, 0.4f)
                        : new Color(1f, 0.45f, 0.45f);
                var style = new GUIStyle(EditorStyles.label) { fontSize = FONT_SIZE - 1, richText = true };
                style.normal.textColor = c;
                string bound = ProfilerReader.BoundStatus();
                GUILayout.Label($"● LIVE [{bound}]  " + ProfilerReader.LiveStats(), style);
                // throttle repaint — ตอน play ช้าลง (0.5s) กันแย่ง frame เกม, ตอน edit เร็วได้ (0.2s)
                double now = EditorApplication.timeSinceStartup;
                double liveInterval = Application.isPlaying ? 0.5 : 0.2;
                if (now - _lastLiveRepaint > liveInterval) { _lastLiveRepaint = now; Repaint(); }
            }
            else
            {
                var style = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2 };
                GUILayout.Label("📈 Live — กด Play เพื่อดูค่า Profiler แบบ real-time", style);
            }
            EditorGUILayout.EndVertical();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  KEYWORD REGISTRY — แหล่งความจริงเดียว (chip ที่แสดง + auto-gather + tooltip)
        //  เพิ่ม keyword ใหม่ = เพิ่ม 1 บรรทัดที่นี่ที่เดียว
        //  Path = tool ที่ดึงข้อมูลอัตโนมัติ (null = ไม่ auto-gather เช่น action/สแกนหนัก/ต้องมี argument)
        //  Chip = แสดงเป็นปุ่มในแผง keyword (false = alias ใช้ auto-gather อย่างเดียว)
        // ══════════════════════════════════════════════════════════════════════
        enum KwG { Dev, Art, Both }
        sealed class Kw
        {
            public readonly string Word; public readonly KwG Group;
            public readonly string Path; public readonly string Desc; public readonly bool Chip;
            public Kw(string word, KwG group, string path = null, string desc = "", bool chip = true)
            { Word = word; Group = group; Path = path; Desc = desc; Chip = chip; }
        }

        static readonly Kw[] _keywords =
        {
            // 💻 DEV
            new Kw("gc",         KwG.Dev,  "/perf/audit",          "GC alloc/frame + top allocators"),
            new Kw("spike",      KwG.Dev,  "/perf/audit",          "FPS drop + ต้นเหตุแต่ละ spike"),
            new Kw("net",        KwG.Dev,  "/perf/audit",          "network: ping/jitter/bandwidth"),
            new Kw("physics",    KwG.Dev,  "/perf/audit",          "rigidbody + non-convex collider"),
            new Kw("console",    KwG.Dev,  "/console/read",        "error/warning ล่าสุดใน console"),
            new Kw("log",        KwG.Dev,  "/console/logfile",     "Editor.log + stack trace เต็ม"),
            new Kw("state",      KwG.Dev,  "/diagnose/state",      "runtime snapshot (fps/freeze/network)"),
            new Kw("exceptions", KwG.Dev,  "/diagnose/exceptions", "runtime exceptions + stack trace"),
            new Kw("profiler",   KwG.Dev,  "/perf/audit",          "call-tree → method ตัวการ"),
            new Kw("memory",     KwG.Dev,  "/diagnose/memory",     "memory snapshot (heap/native/GFX/GC gen)"),
            new Kw("fusion",     KwG.Dev,  "/diagnose/fusion",     "Fusion 2: tick/RTT/bandwidth/resim (ต้อง Play)"),
            new Kw("refactor",   KwG.Dev,  null,                   "สแกน script ที่ควร refactor (AI สั่งเอง — สแกนหนัก)"),
            new Kw("code",       KwG.Dev,  null,                   "วิเคราะห์โค้ด (ใช้คู่กับ @script)"),
            new Kw("script",     KwG.Dev,  null,                   "อ่าน source: script <ชื่อ>"),
            new Kw("watch",      KwG.Dev,  null,                   "ดูค่า field สดตอนเล่น (Play): watch <obj> <component> <field> · ดูค่า=wv · ล้าง=watchclear"),
            // 🎨 ART
            new Kw("draw",       KwG.Art,  "/perf/audit",          "draw calls + SetPass + batching"),
            new Kw("batches",    KwG.Art,  "/perf/audit",          "batch count"),
            new Kw("setpass",    KwG.Art,  "/perf/audit",          "SetPass calls"),
            new Kw("overdraw",   KwG.Art,  "/perf/audit",          "transparent overdraw"),
            new Kw("shader",     KwG.Art,  "/perf/audit",          "multi-pass / GrabPass shader"),
            new Kw("instancing", KwG.Art,  "/perf/audit",          "GPU instancing status"),
            new Kw("lod",        KwG.Art,  "/perf/audit",          "LOD group coverage"),
            new Kw("particle",   KwG.Art,  "/perf/audit",          "particle system count"),
            new Kw("shadow",     KwG.Art,  "/perf/audit",          "shadow caster count"),
            new Kw("light",      KwG.Art,  "/perf/audit",          "realtime light count"),
            new Kw("tex",        KwG.Art,  null,                   "audit texture (AI สั่งเอง — สแกนหนัก)"),
            new Kw("unused",     KwG.Art,  null,                   "asset ที่ไม่ได้ใช้ (AI สั่งเอง — สแกนหนัก)"),
            // ⚡ BOTH
            new Kw("fps",        KwG.Both, "/perf/audit",          "FPS + frame stats + CPU/GPU-bound"),
            new Kw("perf",       KwG.Both, "/perf/audit",          "health check รวมทั้งหมด"),
            new Kw("audit",      KwG.Both, "/perf/audit",          "health check รวมทั้งหมด"),
            new Kw("hier",       KwG.Both, "/scene/hierarchy",     "tree structure ของ scene"),
            new Kw("scene",      KwG.Both, null,                   "scene <ชื่อ> เพื่อ list/เปิด scene"),
            new Kw("find",       KwG.Both, null,                   "ค้นหา asset: find <ชื่อ>"),
            new Kw("play",       KwG.Both, null,                   "เข้า Play Mode"),
            new Kw("stop",       KwG.Both, null,                   "ออก Play Mode"),
            new Kw("pause",      KwG.Both, null,                   "pause Play Mode"),
            new Kw("clear",      KwG.Both, null,                   "ล้าง console"),

            // ── alias (ไม่โชว์เป็น chip แต่พิมพ์แล้ว auto-gather ได้) ──
            new Kw("stutter",  KwG.Dev,  "/perf/audit",          "", false),
            new Kw("worst",    KwG.Dev,  "/perf/worst",          "", false),   // เจาะ spike แย่สุด (ไม่ใช่ audit รวม)
            new Kw("deep",     KwG.Dev,  null,                   "", false),   // ⚠️ /diagnose/deep หนัก (เดิน profiler ทุกเฟรม 0.5-5s) — ห้าม auto-run, ให้ AI สั่งเองผ่าน command
            new Kw("network",  KwG.Dev,  "/perf/audit",          "", false),
            new Kw("ping",     KwG.Dev,  "/perf/audit",          "", false),
            new Kw("rtt",      KwG.Dev,  "/perf/audit",          "", false),
            new Kw("bandwidth",KwG.Dev,  "/perf/audit",          "", false),
            new Kw("bw",       KwG.Dev,  "/perf/audit",          "", false),
            new Kw("mem",      KwG.Both, "/diagnose/memory",     "", false),
            // ── Thai aliases (ทีมพิมพ์ไทย — จับแบบ substring เพราะไทยไม่มีเว้นวรรคตัดคำ) ──
            new Kw("เฟรมตก",  KwG.Both, "/perf/audit",          "", false),
            new Kw("กระตุก",   KwG.Both, "/perf/audit",          "", false),
            new Kw("แลค",     KwG.Both, "/perf/audit",          "", false),
            new Kw("เฟรม",    KwG.Both, "/perf/audit",          "", false),
            new Kw("แรม",     KwG.Both, "/diagnose/memory",     "", false),
            new Kw("เมมโมรี่",  KwG.Both, "/diagnose/memory",     "", false),
            new Kw("เออเรอ",  KwG.Dev,  "/console/read",        "", false),
            new Kw("เออเร่อ",  KwG.Dev,  "/console/read",        "", false),
            new Kw("drawcalls",KwG.Art,  "/perf/audit",          "", false),
            new Kw("tris",     KwG.Art,  "/perf/audit",          "", false),
            new Kw("errors",   KwG.Dev,  "/console/read",        "", false),
            new Kw("debug",    KwG.Dev,  "/console/read",        "", false),
            new Kw("err",      KwG.Dev,  "/console/read",        "", false),
            new Kw("exc",      KwG.Dev,  "/diagnose/exceptions", "", false),
            new Kw("hierarchy",KwG.Both, "/scene/hierarchy",     "", false),
            new Kw("sel",      KwG.Both, "/selection/get",       "", false),
            new Kw("selection",KwG.Both, "/selection/get",       "", false),
            new Kw("watches",  KwG.Dev,  "/watch/get",           "", false),
            new Kw("wv",       KwG.Dev,  "/watch/get",           "", false),
            new Kw("watchget", KwG.Dev,  "/watch/get",           "", false),
            new Kw("watchclear",KwG.Dev, "/watch/clear",         "", false),
            new Kw("unwatch",  KwG.Dev,  "/watch/clear",         "", false),
        };

        // ── AUTO-GATHER map (derive จาก _keywords ที่มี Path) — single source ไม่ drift ──
        //    keyword ส่วนใหญ่ → perf_audit ตัวเดียว → พิมพ์ "net gc fps" → dedupe → รันครั้งเดียว
        //    ⚠️ refactor/tex/unused = Path null (สแกนหนัก ปล่อยให้ AI สั่งเอง กัน freeze)
        static readonly Dictionary<string, string> _kwAutoGather = BuildAutoGatherMap();
        static Dictionary<string, string> BuildAutoGatherMap()
        {
            var d = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var k in _keywords)
                if (!string.IsNullOrEmpty(k.Path)) d[k.Word] = k.Path;
            return d;
        }
        static readonly Dictionary<string, string> _pathLabel = new Dictionary<string, string>
        {
            {"/perf/audit","perf_audit"}, {"/console/read","console"}, {"/console/logfile","logfile"},
            {"/diagnose/exceptions","exceptions"}, {"/diagnose/state","state"}, {"/audit/textures","textures"},
            {"/audit/unused","unused"}, {"/code/refactor-audit","refactor"}, {"/scene/hierarchy","hierarchy"},
            {"/selection/get","selection"}, {"/watch/get","watches"},
            {"/diagnose/memory","memory_snapshot"}, {"/diagnose/fusion","fusion_stats"},
            {"/diagnose/deep","deep_analysis"}, {"/perf/worst","worst_spike"},
        };

        // ดึงข้อมูลตาม keyword ที่เจอในข้อความ — dedupe path (รัน tool เดียวกันครั้งเดียว)
        // เรียกบน main thread (Enqueue) → MCPHandlers.Dispatch รันตรงๆ ไม่ deadlock
        List<KeyValuePair<string, string>> AutoGather(string prompt)
        {
            var results = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(prompt)) return results;
            var paths = new List<string>();   // เรียงตามที่เจอ + ไม่ซ้ำ
            var tokens = System.Text.RegularExpressions.Regex.Split(prompt.ToLowerInvariant(), @"[^a-z0-9]+");
            foreach (var t in tokens)
                if (!string.IsNullOrEmpty(t) && _kwAutoGather.TryGetValue(t, out string p) && !paths.Contains(p))
                    paths.Add(p);
            // keyword ไทยไม่ถูกตัดเป็น token (ไทยไม่มีเว้นวรรคตัดคำ) → จับแบบ substring ตรงๆ
            foreach (var kv in _kwAutoGather)
                if (kv.Key.Length > 0 && kv.Key[0] >= 'ก' && prompt.Contains(kv.Key) && !paths.Contains(kv.Value))
                    paths.Add(kv.Value);
            foreach (var path in paths)
            {
                try
                {
                    string data = MCPHandlers.Dispatch(path, "{}");
                    string label = _pathLabel.TryGetValue(path, out var l) ? l : path;
                    results.Add(new KeyValuePair<string, string>(label, data));
                }
                catch (System.Exception e) { UnityEngine.Debug.LogWarning($"[MCP] auto-gather {path}: {e.Message}"); }
            }
            return results;
        }

        void DrawKeywordPanel()
        {
            if (!_showKeywords) return;
            var s = S;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var hint = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 3 };
            hint.normal.textColor = new Color(0.55f, 0.55f, 0.55f);
            GUILayout.Label("กดปุ่มเพื่อใส่ keyword ในช่องพิมพ์ · ชี้เมาส์ค้างเพื่อดูคำอธิบาย", hint);

            DrawKwRow(s, "Dev",  KwG.Dev,  new Color(0.89f, 0.58f, 0.42f));
            DrawKwRow(s, "Art",  KwG.Art,  new Color(0.95f, 0.66f, 0.80f));
            DrawKwRow(s, "⚡ Both", KwG.Both, new Color(0.95f, 0.78f, 0.45f));

            EditorGUILayout.EndVertical();
        }

        // render keyword 1 กลุ่ม (เฉพาะ Chip==true) จาก _keywords — label สี + tooltip
        void DrawKwRow(ChatSession s, string label, KwG group, Color col)
        {
            EditorGUILayout.BeginHorizontal();
            var lbl = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = FONT_SIZE - 2 };
            lbl.normal.textColor = col;
            GUILayout.Label(label, lbl, GUILayout.Width(52));

            var chip = new GUIStyle(EditorStyles.miniButton) { fontSize = FONT_SIZE - 2, padding = new RectOffset(8, 8, 2, 2) };
            chip.normal.textColor = col;
            foreach (var k in _keywords)
            {
                if (k.Group != group || !k.Chip) continue;
                if (GUILayout.Button(new GUIContent(k.Word, k.Desc), chip, GUILayout.Height(19)))
                {
                    s.draft += (s.draft.Length > 0 ? " " : "") + k.Word;
                    GUI.FocusControl("PromptField");
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void DrawInputArea()
        {
            var s = S;
            DrawAttachToolbar();
            DrawLivePanel();
            DrawKeywordPanel();

            // ── autocomplete picker (@/#//) — แสดง "เหนือ" ช่องพิมพ์ (เดิมอยู่ใต้ ชิดขอบจอ มองยาก) ──
            if (_showScriptList)      DrawScriptList();
            else if (_showPrefabList) DrawPrefabList();
            else if (_showSkillList)  DrawSkillList();

            // พื้นโปร่งใส (background = null) → ให้กล่องมุมโค้ง (warm) ที่วาดข้างหลังโชว์แทน
            // ห้ามใช้ cached Texture2D — EditorWindow serialize ข้าม domain reload → ค้างสีเก่า
            var inputStyle = new GUIStyle(EditorStyles.textArea)
            {
                fontSize = FONT_SIZE, wordWrap = true,
                padding  = new RectOffset(10, 10, 8, 8),
            };
            inputStyle.normal.textColor   = TEXT_WHITE;
            inputStyle.focused.textColor  = TEXT_WHITE;
            inputStyle.hover.textColor    = TEXT_WHITE;
            inputStyle.normal.background  = null;
            inputStyle.focused.background = null;
            inputStyle.active.background  = null;
            inputStyle.hover.background   = null;

            TryPasteImage();

            float vsbWEst  = GUI.skin.verticalScrollbar.fixedWidth + 2;   // estimate scrollbar width
            float textWEst = (position.width - 36) - vsbWEst;             // approx available text width
            float contentH = inputStyle.CalcHeight(new GUIContent(s.draft + " "), textWEst) + 6;
            _inputHeight = Mathf.Clamp(contentH, INPUT_MIN, INPUT_MAX);

            // border + bg รอบ input box
            var boxR  = EditorGUILayout.GetControlRect(false, _inputHeight + 2);
            var innerR = new Rect(boxR.x + 1, boxR.y + 1, boxR.width - 2, boxR.height - 2);
            if (Event.current.type == EventType.Repaint)
            {
                bool inputFocused = GUI.GetNameOfFocusedControl() == "PromptField";
                Color borderCol = inputFocused ? ACCENT : BORDER;
                RRect(boxR, borderCol, 11f);
                RRect(innerR, inputFocused ? new Color(0.130f, 0.116f, 0.098f) : new Color(0.110f, 0.098f, 0.082f), 10f);
            }

            // vertical scroll เท่านั้น — ไม่มี horizontal bar ข้างล่าง
            float vsbW  = GUI.skin.verticalScrollbar.fixedWidth + 2;
            float textW = innerR.width - vsbW;
            float textH = Mathf.Max(innerR.height, contentH);

            _inputScroll = GUI.BeginScrollView(innerR, _inputScroll,
                new Rect(0, 0, textW, textH),
                false, false,
                GUIStyle.none,
                GUI.skin.verticalScrollbar);
            GUI.SetNextControlName("PromptField");
            EditorGUI.BeginChangeCheck();
            s.draft = GUI.TextArea(new Rect(0, 0, textW, textH), s.draft, inputStyle);
            if (EditorGUI.EndChangeCheck())
                UpdateScriptMention();
            GUI.EndScrollView();

            // placeholder วาดทับหลัง TextArea (Repaint only, ตอนว่างและยังไม่ focus)
            if (string.IsNullOrEmpty(s.draft) && Event.current.type == EventType.Repaint
                && GUI.GetNameOfFocusedControl() != "PromptField")
            {
                var phStyle = new GUIStyle(inputStyle) { padding = new RectOffset(11, 10, 9, 8) };
                phStyle.normal.textColor = TEXT_HINT;
                phStyle.normal.background = null;
                GUI.Label(innerR, "พิมพ์คำถาม... (Enter ส่ง · Shift+Enter ขึ้นบรรทัด)", phStyle);
            }

            HandleDragDrop(GUILayoutUtility.GetLastRect());

            // ระหว่าง picker เปิด: รายการ filter เปลี่ยนทุก keystroke → control ID เลื่อน → focus หลุดเงียบๆ
            // บังคับ focus อยู่ช่องพิมพ์ตลอด (ใน panel ไม่มีอะไรต้องพิมพ์อยู่แล้ว — มีแต่คลิกเลือก)
            if ((_showScriptList || _showPrefabList || _showSkillList) &&
                Event.current.type == EventType.Repaint &&
                GUI.GetNameOfFocusedControl() != "PromptField")
            {
                _refocusInput = true;
                Repaint();
            }

            // ── ปุ่ม Send/Stop/Clear ── (ส่งได้แม้กำลังโหลด → เข้า queue)
            var btnRow = GUILayoutUtility.GetRect(0, 30, GUILayout.ExpandWidth(true));
            bool busy = s.Busy;
            bool canSend = !EditorApplication.isCompiling && !string.IsNullOrEmpty(s.draft.Trim());

            float rx = btnRow.xMax;

            var clearR = new Rect(rx - 64, btnRow.y, 64, btnRow.height);
            RBox(clearR, BG_RAISED, BORDER, 8f);
            CenterLabel(clearR, "Clear", TEXT_MUTE, FONT_SIZE - 1);
            if (GUI.Button(clearR, GUIContent.none, GUIStyle.none))
            {
                // ยกเลิกงานที่ค้างก่อน (กัน pump รันต่อ + index เพี้ยนหลังลบข้อความ)
                s.queue.Clear();
                s.cts?.Cancel();
                s.messages.Clear();
                s.images.Clear();
                s.draft = "";
                s.cliSessionId = null;   // เริ่มบทสนทนา CLI ใหม่ (ไม่ resume ของเก่า)
                s.cliTurnCount = 0;
                try { System.IO.File.Delete(HistoryPath(s.backend)); } catch { }
            }
            rx -= 72;

            // ปุ่มยกเลิก — โผล่เมื่อกำลังทำงาน
            if (busy)
            {
                var stopR = new Rect(rx - 76, btnRow.y, 76, btnRow.height);
                RRect(stopR, new Color(0.42f, 0.24f, 0.22f), 8f);
                CenterLabel(stopR, "⛔ Stop", new Color(1f, 0.72f, 0.68f), FONT_SIZE - 1);
                if (GUI.Button(stopR, GUIContent.none, GUIStyle.none))
                    StopSession(s);
                rx -= 84;
            }

            var sendR = new Rect(btnRow.x, btnRow.y, Mathf.Max(80f, rx - btnRow.x), btnRow.height);
            string sendLabel = busy ? $"＋ Queue ({s.queue.Count + (s.isLoading ? 1 : 0)})" : "Send  ↑";
            RRect(sendR, canSend ? ACCENT : new Color(ACCENT.r, ACCENT.g, ACCENT.b, 0.32f), 8f);
            CenterLabel(sendR, sendLabel, canSend ? Color.white : new Color(1f, 1f, 1f, 0.55f), FONT_SIZE - 1);
            if (canSend && GUI.Button(sendR, GUIContent.none, GUIStyle.none))
                Enqueue();

            // Enter = ส่ง/queue (Shift+Enter = บรรทัดใหม่)
            if (Event.current.type == EventType.KeyDown &&
                Event.current.keyCode == KeyCode.Return &&
                !Event.current.shift &&
                GUI.GetNameOfFocusedControl() == "PromptField")
            {
                Enqueue();
                Event.current.Use();
            }
        }

        void TryPasteImage()
        {
            var e = Event.current;
            bool paste = e.type == EventType.KeyDown && e.keyCode == KeyCode.V && (e.control || e.command);
            if (!paste) return;

            // ถ้า clipboard มี text อยู่แล้ว = paste ข้อความ → ปล่อยให้ paste ปกติ
            // (ไม่ spawn PowerShell เช็ครูป — ตัวการที่ทำให้กระตุกตอน Ctrl+V)
            if (!string.IsNullOrEmpty(EditorGUIUtility.systemCopyBuffer)) return;

            string path = ClipboardImage.TryGetImagePath();
            if (!string.IsNullOrEmpty(path))
            {
                AddImage(path);
                e.Use();
                Repaint();
            }
        }

        // ── @mention script picker ─────────────────────────────────────────
        string CurrentMentionQuery()
        {
            string draft = S.draft;
            int at = draft.LastIndexOf('@');
            if (at < 0) return null;
            string tail = draft.Substring(at + 1);
            if (tail.Contains(' ') || tail.Contains('\n')) return null;
            return tail;
        }

        void UpdateScriptMention()
        {
            bool wasOpen = _showScriptList || _showPrefabList || _showSkillList;
            string draft = S.draft;
            // '@' = script · '#' = prefab — เลือกตัวที่ token อยู่ท้ายสุด (ตัวที่กำลังพิมพ์)
            string sq = CurrentMentionQuery();        // '@'
            string pq = CurrentTokenQuery('#');       // '#'
            int atIdx = draft.LastIndexOf('@');
            int hashIdx = draft.LastIndexOf('#');
            if (sq != null && (pq == null || atIdx > hashIdx))
            {
                _scriptQuery = sq; _showScriptList = true; _showPrefabList = false;
            }
            else if (pq != null)
            {
                _prefabQuery = pq; _showPrefabList = true; _showScriptList = false;
            }
            else { _showScriptList = false; _showPrefabList = false; }

            // '/' = skill (เฉพาะ Subscription/CLI mode — CLI รันสกิลได้จริง)
            if (CurrentBackend() == 1)
            {
                string sk = CurrentTokenQuery('/');
                if (sk != null) { _skillQuery = sk; _showSkillList = true; }
                else _showSkillList = false;
            }
            else _showSkillList = false;

            // panel โผล่/หาย = control ID เลื่อน → focus ช่องพิมพ์หลุด → ดึงกลับ (พิมพ์ต่อได้ไม่สะดุด)
            bool nowOpen = _showScriptList || _showPrefabList || _showSkillList;
            if (wasOpen != nowOpen) { _refocusInput = true; Repaint(); }
        }

        // ดึง query หลังตัวอักษรนำ (เช่น '/') ตัวล่าสุด ถ้าอยู่ต้นบรรทัด/หลังเว้นวรรค และไม่มี space ตาม
        string CurrentTokenQuery(char lead)
        {
            string draft = S.draft;
            int idx = draft.LastIndexOf(lead);
            if (idx < 0) return null;
            if (idx > 0 && draft[idx - 1] != ' ' && draft[idx - 1] != '\n') return null;
            string tail = draft.Substring(idx + 1);
            if (tail.Contains(' ') || tail.Contains('\n')) return null;
            return tail;
        }

        void DrawSkillList()
        {
            var results = SkillIndex.Search(_skillQuery, 12);
            var items = new List<PickerItem>(results.Count);
            foreach (var sk in results)
            {
                var name = sk.Name;
                items.Add(new PickerItem { Name = "/" + sk.Name, Desc = sk.Description, Pick = () => InsertSkillMention(name) });
            }
            DrawPickerPanel("/ skill — รันสกิล (โหมด Subscription)", items, ref _skillScroll, "ไม่พบ skill ที่ตรงกับที่พิมพ์");
        }

        void InsertSkillMention(string skillName)
        {
            var s = S;
            int at = s.draft.LastIndexOf('/');
            if (at < 0) return;
            s.draft = s.draft.Substring(0, at) + "/" + skillName + " ";
            _showSkillList = false;
            _refocusInput = true;   // กลับไปพิมพ์ต่อได้เลย
            Repaint();
        }

        // ── Picker panel (@/#//) เข้าธีม — แสดงเหนือช่องพิมพ์, zebra rows, เลื่อนล้อเมาส์ ──
        struct PickerItem { public string Name, Desc; public Action Pick; }

        void DrawPickerPanel(string title, List<PickerItem> items, ref Vector2 scroll, string emptyText)
        {
            const float rowH = 26f;
            var panelR = EditorGUILayout.GetControlRect(false, SCRIPT_LIST_HEIGHT);
            var panel = new Rect(panelR.x + 4, panelR.y, panelR.width - 8, panelR.height - 4);
            RBox(panel, BG_RAISED, BORDER, 10f);

            var tSt = new GUIStyle(EditorStyles.miniLabel) { fontSize = FONT_SIZE - 2 };
            tSt.normal.textColor = TEXT_HINT;
            GUI.Label(new Rect(panel.x + 12, panel.y + 4, panel.width - 24, 15), title, tSt);

            var inner = new Rect(panel.x + 4, panel.y + 22, panel.width - 8, panel.height - 27);
            if (items == null || items.Count == 0)
            {
                var eSt = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = FONT_SIZE - 1 };
                eSt.normal.textColor = TEXT_HINT;
                GUI.Label(inner, emptyText, eSt);
                return;
            }

            var rowSt = new GUIStyle(EditorStyles.label) { font = UiFont, fontSize = MSG_FONT - 1, alignment = TextAnchor.MiddleLeft, richText = true };
            scroll = GUI.BeginScrollView(inner, scroll, new Rect(0, 0, inner.width - 4, items.Count * rowH),
                false, false, GUIStyle.none, GUIStyle.none);
            for (int i = 0; i < items.Count; i++)
            {
                var row = new Rect(2, i * rowH, inner.width - 8, rowH);
                bool hov = row.Contains(Event.current.mousePosition);   // ใน scrollview พิกัดเป็น content space ตรงกับ row
                if (Event.current.type == EventType.Repaint)
                {
                    if (hov)
                    {
                        RRect(row, new Color(ACCENT.r, ACCENT.g, ACCENT.b, 0.18f), 6f);   // hover — เห็นชัดว่าชี้ตัวไหน
                        RRect4(new Rect(row.x, row.y, 2, row.height), ACCENT, 6f, 0f, 0f, 6f);
                    }
                    else if ((i & 1) == 1)
                        RRect(row, new Color(1f, 1f, 1f, 0.03f), 6f);   // zebra
                }
                EditorGUIUtility.AddCursorRect(row, MouseCursor.Link);   // cursor มือชี้ = คลิกได้
                string desc = string.IsNullOrEmpty(items[i].Desc) ? "" :
                    "  <color=#9C948A>" + items[i].Desc.Replace("<", "«").Replace(">", "»") + "</color>";
                GUI.Label(new Rect(row.x + 10, row.y, row.width - 16, rowH),
                    $"<color={(hov ? "#FFFFFF" : "#E8A87F")}>{items[i].Name}</color>{desc}", rowSt);
                // ใช้ MouseDown ตรงๆ แทน GUI.Button — ไม่กิน control ID (กัน ID เลื่อนจน focus ช่องพิมพ์หลุด)
                if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                    items[i].Pick();
                    GUIUtility.ExitGUI();   // pick เปลี่ยน draft/ปิด list กลางเฟรม → ตัดเฟรมเริ่มใหม่
                }
            }
            GUI.EndScrollView();
        }

        void DrawScriptList()
        {
            var results = CodebaseIndex.Search(_scriptQuery, 12);
            var items = new List<PickerItem>(results.Count);
            foreach (var sc in results)
            {
                var name = sc.Name;
                items.Add(new PickerItem { Name = "@" + sc.Name, Desc = sc.Path, Pick = () => InsertScriptMention(name) });
            }
            DrawPickerPanel("@ script — คลิกเพื่อแนบไฟล์ให้ AI อ่าน", items, ref _scriptScroll, "ไม่พบ script ที่ตรงกับที่พิมพ์");
        }

        void InsertScriptMention(string scriptName)
        {
            var s = S;
            int at = s.draft.LastIndexOf('@');
            if (at < 0) return;
            s.draft = s.draft.Substring(0, at) + "@" + scriptName + " ";
            _showScriptList = false;
            _refocusInput = true;   // กลับไปพิมพ์ต่อได้เลย
            Repaint();
        }

        void DrawPrefabList()
        {
            if (PrefabIndex.Building && !PrefabIndex.Ready)
            {
                DrawPickerPanel("# prefab", null, ref _prefabScroll, "กำลัง build prefab index… (รอสักครู่)");
                return;
            }
            var results = PrefabIndex.Search(_prefabQuery, 12);
            var items = new List<PickerItem>(results.Count);
            foreach (var pf in results)
            {
                var name = pf.Name;
                items.Add(new PickerItem { Name = "# " + pf.Name, Desc = pf.Path, Pick = () => InsertPrefabMention(name) });
            }
            DrawPickerPanel("# prefab — คลิกเพื่อแนบเนื้อใน prefab", items, ref _prefabScroll, "ไม่พบ prefab ที่ตรงกับที่พิมพ์");
        }

        void InsertPrefabMention(string prefabName)
        {
            var s = S;
            int at = s.draft.LastIndexOf('#');
            if (at < 0) return;
            // ชื่อมีช่องว่าง/อักขระพิเศษ → ครอบ [] ให้ parse กลับได้ (เช่น #[creep super])
            string token = System.Text.RegularExpressions.Regex.IsMatch(prefabName, @"^[A-Za-z0-9_]+$")
                ? prefabName : $"[{prefabName}]";
            s.draft = s.draft.Substring(0, at) + "#" + token + " ";
            _showPrefabList = false;
            _refocusInput = true;   // กลับไปพิมพ์ต่อได้เลย
            Repaint();
        }

        string BuildPromptWithScripts(string prompt, out List<string> primaryNames, out List<string> depNames)
        {
            primaryNames = new List<string>();   // ไฟล์ที่ @ เอง
            depNames = new List<string>();        // ไฟล์ที่ auto-add (dependency)
            var matches = System.Text.RegularExpressions.Regex.Matches(prompt, @"@([\w/]+\.cs)");
            if (matches.Count == 0) return prompt;

            var seen = new HashSet<string>();
            var attachedPaths = new List<string>();   // path ที่แนบแล้ว (ไว้ scan dependency + กันซ้ำ)
            var sb = new System.Text.StringBuilder(prompt);
            sb.Append("\n\n--- Referenced scripts (full source) ---\n");

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string name = m.Groups[1].Value;
                if (!seen.Add(name)) continue;
                string path = CodebaseIndex.ResolvePath(name);
                if (path == null) continue;
                string content = CodebaseIndex.ReadContent(path);
                if (content == null) continue;
                sb.Append($"\n// FILE: {path}\n```csharp\n{content}\n```\n");
                attachedPaths.Add(path);
                primaryNames.Add(System.IO.Path.GetFileName(path));
            }

            // ── A1: ถามเชิงวิเคราะห์ → ตามไฟล์ที่ script อ้างถึงมาแนบด้วย (ลึก 1 ชั้น, cap 6) ──
            //    @เฉยๆ (ถามสั้นๆ) จะไม่ดึง dependency เพื่อประหยัด context
            if (primaryNames.Count > 0 && IsAnalysisIntent(prompt))
            {
                const int MAX_DEPS = 12;   // เผื่อ partial class แตกหลายไฟล์ (เช่น NetworkTrait = 6) + dep อื่นๆ
                var depSeen = new HashSet<string>(attachedPaths, System.StringComparer.OrdinalIgnoreCase);
                var deps = new List<CodebaseIndex.ScriptEntry>();

                // (1) referenced types ที่ script @ อ้างถึงตรงๆ (เช่น Actor, Trait, ColiderEvent)
                foreach (var p in attachedPaths)
                {
                    string src = CodebaseIndex.ReadContent(p);
                    foreach (var dep in CodebaseIndex.ResolveReferencedScripts(src, p, 12))
                    {
                        if (!depSeen.Add(dep.Path)) continue;   // ไม่ซ้ำกับที่แนบแล้ว/ที่เพิ่งเพิ่ม
                        deps.Add(dep);
                        if (deps.Count >= MAX_DEPS) break;
                    }
                    if (deps.Count >= MAX_DEPS) break;
                }

                // (2) Smart 1: ตามสาย inheritance/interface ของไฟล์ที่แนบทั้งหมด (BFS ลึก 2) →
                //     ให้ AI เห็นว่า member/property มาจาก base/interface ไหน (กันฟันธงผิดว่า "member หาย")
                //     เช่น ColiderEvent : INetworkActor → ดึง INetworkActor มาด้วย → รู้ว่า .Actor มาจาก interface
                if (deps.Count < MAX_DEPS)
                {
                    var toScan = new Queue<string>();
                    foreach (var p in attachedPaths) toScan.Enqueue(p);
                    foreach (var d in new List<CodebaseIndex.ScriptEntry>(deps)) toScan.Enqueue(d.Path);
                    int guard = 0;
                    while (toScan.Count > 0 && deps.Count < MAX_DEPS && guard++ < 40)
                    {
                        string src = CodebaseIndex.ReadContent(toScan.Dequeue());
                        foreach (var baseName in CodebaseIndex.ResolveBaseTypes(src))
                        {
                            string bp = CodebaseIndex.ResolvePath(baseName);
                            if (bp == null || !depSeen.Add(bp)) continue;
                            deps.Add(new CodebaseIndex.ScriptEntry { Name = baseName + ".cs", Path = bp });
                            toScan.Enqueue(bp);   // ตามสาย inheritance ต่อไปอีกชั้น
                            if (deps.Count >= MAX_DEPS) break;
                        }
                    }
                }

                if (deps.Count > 0)
                {
                    sb.Append("\n--- Referenced dependencies + inheritance chain (รวม base/interface ที่ member อาจมาจาก) ---\n");
                    foreach (var dep in deps)
                    {
                        string content = CodebaseIndex.ReadContent(dep.Path, 14000);   // cap ใหญ่พอให้ method ยาวๆ ครบทั้งตัว (กัน truncate ตัดครึ่ง → AI เดา "ลืมโค้ด")
                        if (content == null) continue;
                        sb.Append($"\n// DEP: {dep.Path}\n```csharp\n{content}\n```\n");
                        depNames.Add(System.IO.Path.GetFileName(dep.Path));
                    }
                }
            }

            return (primaryNames.Count + depNames.Count) > 0 ? sb.ToString() : prompt;
        }

        // ถามเชิงวิเคราะห์/แก้ไข? → ค่อยดึง dependency (กัน context บวมตอน @ ถามสั้นๆ)
        static bool IsAnalysisIntent(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return false;
            string p = prompt.ToLowerInvariant();
            string[] kw = { "refactor", "optimize", "optimise", "review", "improve",
                            "วิเคราะห์", "แก้", "ปรับ", "ตรวจ", "ปัญหา", "bug", "บั๊ก", "บัค", "ดูให้",
                            // debug-intent: "ทำไม X ไม่ Y" คือคำถาม debug ที่ใช้บ่อยสุด — ต้องดึง dependency ให้เห็นโค้ดจริง
                            "ทำไม", "why", "หาย", "ค้าง", "เพี้ยน", "ผิดปกติ", "พัง", "เจ๊ง",
                            "ไม่ลด", "ไม่ขึ้น", "ไม่เพิ่ม", "ไม่ทำงาน", "ไม่โดน", "ไม่เข้า", "ไม่ขยับ", "ไม่เปลี่ยน",
                            "crash", "error", "exception", "broken", "ไม่ทำ", "หาไม่เจอ", "ไล่โค้ด", "trace" };
            foreach (var k in kw) if (p.Contains(k)) return true;
            return false;
        }

        // เจตนาเชิง runtime/debug → trigger auto-watch (ดูค่า field สดตอนเล่น)
        static bool IsRuntimeWatchIntent(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return false;
            string p = prompt.ToLowerInvariant();
            string[] kw = { "watch", "runtime", "ค่า", "value", "state", "สถานะ", "ติดตาม", "debug",
                            "ทำไม", "ไม่ลด", "ไม่เพิ่ม", "ไม่เปลี่ยน", "ค้าง", "วิ่ง", "ตอนเล่น", "live",
                            "bug", "บั๊ก", "บัค", "hp", "mp", "mana", "เลือด", "ชีวิต", "มานา", "ดูค่า" };
            foreach (var k in kw) if (p.Contains(k)) return true;
            return false;
        }

        // แนบ/ถอด ส่วน profiler (toggle) — Profiler / Network / GC แนบพร้อมกันได้หลายอัน
        void AttachPart(string label, string data)
        {
            var s = S;
            if (s.attached.ContainsKey(label)) { s.attached.Remove(label); Repaint(); return; }   // กดซ้ำ = เอาออก
            s.attached[label] = data;
            if (string.IsNullOrEmpty(s.draft.Trim()))
                s.draft = "Analyze the attached Profiler data. Identify issues, rank by risk, and suggest fixes.";
            Repaint();
        }

        void BrowseImages()
        {
            string path = EditorUtility.OpenFilePanel("Select image (add more)", "", "png,jpg,jpeg,webp");
            if (!string.IsNullOrEmpty(path)) AddImage(path);
        }

        void HandleDragDrop(Rect dropArea)
        {
            var e = Event.current;
            if (!dropArea.Contains(e.mousePosition)) return;

            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var path in DragAndDrop.paths)
                    {
                        string ext = Path.GetExtension(path).ToLower();
                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp")
                            AddImage(path);
                    }
                }
                e.Use();
            }
        }

        void AddImage(string path)
        {
            var s = S;
            if (s.images.Count >= MAX_IMAGES)
            {
                EditorUtility.DisplayDialog("Images", $"Maximum {MAX_IMAGES} images allowed.", "OK");
                return;
            }
            if (s.images.Exists(im => im.Path == path)) return;

            byte[] data = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2);
            if (tex.LoadImage(data))
            {
                string ext = Path.GetExtension(path).ToLower();
                string mime = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg"
                            : ext == ".webp" ? "image/webp" : "image/png";
                s.images.Add(new AttachedImage { Path = path, Texture = tex, Mime = mime });
                Repaint();
            }
        }

        // ── ส่ง prompt: ส่วนเบา (UI) ทำทันทีตอนคลิก, งานหนักเลื่อนไป tick ถัดไป → คลิก Send ไม่ค้าง ──
        // คำตอบ health check ("ทดสอบ") — สถานะ server + รายการคำสั่งทั้งหมดจาก single source (ไม่เรียก AI)
        static string BuildHealthCheckReply()
        {
            var sb = new System.Text.StringBuilder();
            if (MCPServer.IsRunning)
            {
                sb.AppendLine($"🟢 **สถานะ MCP Server ถูกเปิดแล้ว** — {MCPServer.Label} · port {MCPServer.Port} · Write {(MCPHandlers.AllowWrites ? "ON ✏" : "OFF (read-only)")}");
                sb.AppendLine();
                var paths = MCPHandlers.CommandPaths();
                sb.AppendLine($"## 📋 คำสั่งที่ใช้ได้ทั้งหมด ({paths.Count})");
                sb.AppendLine("| # | คำสั่ง | path |");
                sb.AppendLine("|---|--------|------|");
                int i = 1;
                foreach (var p in paths)
                    sb.AppendLine($"| {i++} | {FriendlyPath(p)} | {p} |");
            }
            else
            {
                sb.AppendLine("🔴 **MCP Server ยังไม่เปิด**");
                sb.AppendLine("เปิดที่แท็บ **Claude In → กดปุ่ม ▶ Start** แล้วพิมพ์ \"ทดสอบ\" อีกครั้งเพื่อยืนยัน");
            }
            return sb.ToString().TrimEnd();
        }

        void Enqueue()
        {
            var s = S;
            string prompt = s.draft.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            _showScriptList = false;
            _showPrefabList = false;

            // ── "ทดสอบ" เดี่ยวๆ = health check ภายใน — เช็คสถานะ + รายการคำสั่งเอง ไม่ส่งเข้า Claude ──
            if (prompt == "ทดสอบ" || prompt.Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                s.messages.Add(new ChatMessage("user", prompt));
                s.messages.Add(new ChatMessage("assistant", BuildHealthCheckReply()));
                s.draft = "";
                _stickBottom = true;
                _autoScroll = true;
                SaveHistory(s);
                Repaint();
                return;
            }

            // ── Gate: ต้องต่อ MCP ก่อน (server เปิด) — ไม่งั้นตอบกลับให้ไปกด Start ที่ Unity ──
            if (!MCPServer.IsRunning)
            {
                s.messages.Add(new ChatMessage("user", prompt));
                s.messages.Add(new ChatMessage("assistant",
                    "🔴 **MCP ยังไม่ต่อ** — กดเปิดที่ Unity ก่อนนะครับ\n\n" +
                    "ไปที่หน้าต่าง **MCP Bridge → แท็บ \"Claude In\" → กดปุ่ม ▶ Start**\n" +
                    "(จุด ● บนหัวจะเปลี่ยนเป็น **online** สีเขียว) แล้วพิมพ์คำสั่งเดิมอีกครั้งได้เลย"));
                s.draft = "";
                s.images.Clear();
                s.attached.Clear();
                _stickBottom = true;
                _autoScroll = true;
                SaveHistory(s);
                Repaint();
                return;
            }

            // snapshot attachments (จะถูกล้างทันที) + history (ก่อนเพิ่มข้อความปัจจุบัน)
            var imagesSnap = new List<AttachedImage>(s.images);
            var attachedSnap = new Dictionary<string, string>(s.attached);
            var historyTurns = BuildHistoryTurns(s);

            // เพิ่มข้อความ user + placeholder ""ทันที"" → UI ตอบสนองเลย (note เติมตอน assemble เสร็จ)
            s.messages.Add(new ChatMessage("user", prompt));
            s.messages.Add(new ChatMessage("assistant", QUEUED));
            int userIndex = s.messages.Count - 2;
            int phIndex = s.messages.Count - 1;

            // ล้าง input ทันที (พิมพ์ต่อได้เลย)
            s.draft = "";
            s.images.Clear();
            s.attached.Clear();
            _stickBottom = true;
            _autoScroll = true;
            Repaint();

            // เลื่อนงานหนัก (scripts/auto-gather/prefab inspect/resize) ไป tick ถัดไป → ไม่ค้างจังหวะคลิก
            var sc = s;
            EditorApplication.delayCall += () => EnqueueHeavy(sc, prompt, imagesSnap, attachedSnap, historyTurns, userIndex, phIndex);
        }

        // งานหนัก — รัน tick ถัดไป (main thread, นอกจังหวะคลิก)
        void EnqueueHeavy(ChatSession s, string prompt, List<AttachedImage> images,
                          Dictionary<string, string> attached, List<ConversationTurn> historyTurns,
                          int userIndex, int phIndex)
        {
            string fullPrompt = BuildPromptWithScripts(prompt, out var primaryScripts, out var depScripts);
            bool hasProfiler = attached.Count > 0;
            if (hasProfiler)
                foreach (var kv in attached)
                    fullPrompt += $"\n\n--- Unity Profiler data: {kv.Key} ---\n```\n" + kv.Value + "\n```";

            // ข้าม auto-gather ถ้ามีข้อมูล profiler แนบมาแล้ว (เช่น Deep/Profiler) → ไม่ FindObjectsOfType ซ้ำ
            // (perf_audit สแกนทั้ง scene บน main thread = เกมค้าง 1 เฟรม · มีข้อมูลแนบแล้วก็ไม่ต้องสแกนซ้ำ)
            var gathered = hasProfiler ? new List<KeyValuePair<string, string>>() : AutoGather(prompt);
            foreach (var g in gathered)
                fullPrompt += $"\n\n--- Unity {g.Key} (auto-gathered) ---\n```json\n{g.Value}\n```";

            // ── A2: หา prefab ที่ใช้ script ที่ @ มา (เฉพาะตอนถามเชิงวิเคราะห์) ──
            var prefabNames = new List<string>();
            var inspectedPrefabs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (primaryScripts.Count > 0 && IsAnalysisIntent(prompt))
            {
                var pfPaths = new List<string>();
                foreach (var sn in primaryScripts)
                {
                    string sp = CodebaseIndex.ResolvePath(sn);
                    if (sp == null) continue;
                    string guid = UnityEditor.AssetDatabase.AssetPathToGUID(sp);
                    foreach (var pf in PrefabIndex.PrefabsUsing(guid))
                        if (!pfPaths.Contains(pf)) pfPaths.Add(pf);
                }
                if (pfPaths.Count > 0)
                {
                    const int INSPECT_CAP = 4;   // ขยายจาก 2 → 4
                    // smart select: เรียง prefab ตามความเกี่ยวข้องกับชื่อ script (ตรงชื่อ/ชื่อ script อยู่ในชื่อ prefab มาก่อน)
                    // → inspect ตัวที่เกี่ยวสุด ไม่ใช่ 2 ตัวแรกตามลำดับไฟล์ (ใช้ List.Sort เลี่ยง Linq)
                    pfPaths.Sort((a, b) =>
                    {
                        int ra = PrefabRelevance(System.IO.Path.GetFileNameWithoutExtension(a), primaryScripts);
                        int rb = PrefabRelevance(System.IO.Path.GetFileNameWithoutExtension(b), primaryScripts);
                        if (ra != rb) return rb - ra;   // มาก → น้อย (เกี่ยวสุดก่อน)
                        return System.IO.Path.GetFileNameWithoutExtension(a).Length
                             - System.IO.Path.GetFileNameWithoutExtension(b).Length;   // สั้นกว่าก่อน (canonical)
                    });
                    int inspected = 0;
                    foreach (var pf in pfPaths)
                    {
                        string pname = System.IO.Path.GetFileNameWithoutExtension(pf);
                        prefabNames.Add(pname);
                        if (inspected < INSPECT_CAP)
                        {
                            string report = PrefabInspector.Inspect(pf);
                            if (!string.IsNullOrEmpty(report))
                            {
                                fullPrompt += $"\n\n--- Prefab contents: {pname} (script {string.Join("/", primaryScripts)} แปะอยู่บน prefab นี้) ---\n```\n{report}\n```";
                                inspectedPrefabs.Add(pf);
                                inspected++;
                            }
                        }
                    }
                    if (pfPaths.Count > INSPECT_CAP)
                        fullPrompt += $"\n\n(+ prefab อื่นที่ใช้ script นี้อีก {pfPaths.Count - INSPECT_CAP} ตัว — inspect {INSPECT_CAP} ตัวที่เกี่ยวสุดกัน context บวม)";
                }
                else if (PrefabIndex.Building)
                {
                    fullPrompt += "\n\n(prefab index กำลัง build — ครั้งนี้ยังไม่มีข้อมูล prefab ถามซ้ำได้)";
                }
            }

            // ── Phase 2 #3: auto runtime-watch — ปิดไว้ (BFS อ่าน property getter ทั้ง graph = เสี่ยง crash) ──
            // TODO: ออกแบบใหม่ให้ปลอดภัย (ไม่ invoke getter เป็นชุด) ก่อนเปิดใช้
            // if (primaryScripts.Count > 0 && Application.isPlaying && IsRuntimeWatchIntent(prompt)) { ... WatchAuto.AutoWatch ... }

            // ── A3: #prefab mention → inspect เนื้อใน prefab ──
            var prefabMentions = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(prompt, @"#\[([^\]]+)\]|#([A-Za-z0-9_]+)"))
            {
                string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (prefabMentions.Contains(name)) continue;
                string path = PrefabIndex.ResolvePath(name);
                if (path == null) continue;
                prefabMentions.Add(name);
                if (inspectedPrefabs.Contains(path)) continue;
                string report = PrefabInspector.Inspect(path);
                if (string.IsNullOrEmpty(report)) continue;
                fullPrompt += $"\n\n--- Prefab contents: {name} (#mention) ---\n```\n{report}\n```";
                inspectedPrefabs.Add(path);
            }

            // เตรียมรูป (resize + base64)
            var payloadImages = new List<ClaudeImage>();
            foreach (var img in images)
            {
                try
                {
                    byte[] bytes = ImageOptimizer.ResizeForApi(img.Path, 1568, out string mime);
                    payloadImages.Add(new ClaudeImage { Base64 = Convert.ToBase64String(bytes), Mime = mime });
                }
                catch
                {
                    payloadImages.Add(new ClaudeImage { Base64 = Convert.ToBase64String(File.ReadAllBytes(img.Path)), Mime = img.Mime });
                }
            }

            // note → เติมเข้าข้อความ user ที่แสดงไปแล้ว
            string note = "";
            if (images.Count > 0) note += $"\n<i>[{images.Count} image(s) attached]</i>";
            if (primaryScripts.Count > 0)
            {
                string slabel = $"{string.Join(", ", primaryScripts)}";
                if (depScripts.Count > 0) slabel += $"  +  auto: {string.Join(", ", depScripts)}";
                note += $"\n<i>[{slabel}]</i>";
            }
            if (hasProfiler) note += $"\n<i>[profiler: {string.Join(", ", attached.Keys)} attached]</i>";
            if (gathered.Count > 0)
            {
                var names = new List<string>();
                foreach (var g in gathered) names.Add(g.Key);
                note += $"\n<i>[🔍 auto: {string.Join(", ", names)}]</i>";
            }
            if (prefabNames.Count > 0) note += $"\n<i>[🧩 uses prefab: {string.Join(", ", prefabNames)}]</i>";
            if (prefabMentions.Count > 0) note += $"\n<i>[🧩 #prefab: {string.Join(", ", prefabMentions)}]</i>";

            if (!string.IsNullOrEmpty(note) && userIndex >= 0 && userIndex < s.messages.Count && s.messages[userIndex].Role == "user")
                s.messages[userIndex] = new ChatMessage("user", prompt + note);

            s.queue.Enqueue(new QueuedItem { FullPrompt = fullPrompt, RawPrompt = prompt, Images = payloadImages, PlaceholderIndex = phIndex, History = historyTurns });
            _autoScroll = true;
            Repaint();

            if (!s.pumping) PumpQueue(s);
        }

        // สร้าง history เป็น proper turns สำหรับ multi-turn API (ล่าสุด N เทิร์น)
        // ต้อง user/assistant สลับกัน และต้องจบด้วย assistant (เพื่อให้ current user message ต่อได้)
        static List<ConversationTurn> BuildHistoryTurns(ChatSession s, int maxTurns = 6)
        {
            var result = new List<ConversationTurn>();
            if (s.messages.Count == 0) return result;

            int start = Mathf.Max(0, s.messages.Count - maxTurns);
            for (int i = start; i < s.messages.Count; i++)
            {
                var m = s.messages[i];
                // ข้าม placeholder/meta
                if (m.Content == THINKING || m.Content == QUEUED || m.Content.StartsWith("⏳")) continue;
                if (m.Role != "user" && (m.Content.StartsWith("✅") || m.Content.StartsWith("⚠️") || m.Content.StartsWith("❌")))
                    continue;

                // บังคับ alternating: ถ้า role ซ้ำกับตัวสุดท้าย → แทนที่ (เก็บตัวล่าสุดไว้)
                if (result.Count > 0 && result[result.Count - 1].Role == m.Role)
                    result.RemoveAt(result.Count - 1);

                result.Add(new ConversationTurn { Role = m.Role, Content = m.Content });
            }

            // history ต้องจบด้วย assistant → current user message จะ alternate ถูกต้อง
            while (result.Count > 0 && result[result.Count - 1].Role == "user")
                result.RemoveAt(result.Count - 1);

            return result;
        }

        // FireArtRequest ถูกลบ — ใช้ 1 request แทน AI ส่ง CATEGORIES: header มาเอง
        // Unity parse header → RoleView() ตัดสินว่า role ไหนเห็น

        // ── ประมวลผล queue ของ session ทีละอัน (แต่ละ tab pump แยกกัน) ──────
        async void PumpQueue(ChatSession s)
        {
            s.pumping = true;
            while (s.queue.Count > 0)
            {
                var item = s.queue.Dequeue();
                s.isLoading = true;
                s.requestStart = EditorApplication.timeSinceStartup;
                s.cts = new System.Threading.CancellationTokenSource();
                var token = s.cts.Token;
                _pending.Enqueue(() => SetMessage(s, item.PlaceholderIndex, "assistant", THINKING));  // marker (apply ตอน Layout)
                Repaint();

                // ── Single request — ส่ง role ปัจจุบันให้ API ใช้ system prompt ที่ถูก
                //    AI จัด CATEGORIES: header มาเอง Unity parse → แสดงตาม role ──
                int curRole = CurrentRole();
                ClaudeResponse response;
                try
                {
                    // ── CLI session rotation: จำได้ MAX_RESUME_TURNS turn แล้วเริ่ม session ใหม่ ──
                    //    follow-up ภายใน N turn ยังจำได้ / ครบ N → fresh → context รีเซ็ตกลับ baseline
                    //    (กัน perf_audit/profiler ส่ง JSON ก้อนใหญ่สะสมใน --resume เดิมไม่หยุด)
                    const int MAX_RESUME_TURNS = 5;   // warm resume เร็วกว่า cold ~2 เท่า — 5 turn ค่อยรีเซ็ต (สืบยาวไม่ลืมกลางทาง)
                    string resumeId = s.cliSessionId;
                    if (s.backend == 1 && s.cliTurnCount >= MAX_RESUME_TURNS)
                    {
                        resumeId = null;
                        s.cliTurnCount = 0;
                        UnityEngine.Debug.Log($"[MCP] CLI session หมุนใหม่ (ครบ {MAX_RESUME_TURNS} turn) → context รีเซ็ต");
                    }

                    response = s.backend == 1
                        ? await ClaudeCliClient.SendAsync(item.FullPrompt, item.Images, token, resumeId, curRole)
                        : await ClaudeAPIClient.SendAsync(item.FullPrompt, item.Images, token, curRole, item.History);
                    if (s.backend == 1 && !string.IsNullOrEmpty(response?.SessionId))
                    {
                        s.cliSessionId = response.SessionId;
                        s.cliTurnCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    response = new ClaudeResponse { Error = "ยกเลิกแล้ว (cancelled)" };
                }

                s.isLoading = false;
                s.cts?.Dispose();
                s.cts = null;

                // เติมคำตอบลง placeholder (ใต้ prompt ที่ถาม) + ผล execute ต่อท้ายในก้อนเดียว
                string content, stat = null;
                if (response.IsError)
                    content = $"❌ {response.Error}";
                else
                {
                    content = response.Text;
                    if (response.HasCommand)
                    {
                        // รันบน background thread (เหมือน HTTP server) → command หนักไม่ freeze GUI
                        string execResult = await System.Threading.Tasks.Task.Run(() => ExecuteCommand(s, response.CommandJson));
                        string cmdName = ExtractCommandName(response.CommandJson);
                        bool execErr = execResult.StartsWith("⚠️") || execResult.Contains("\"error\"");

                        if (!execErr && _dataCommands.Contains(cmdName))
                        {
                            // ── round 2: data command → ส่งผลกลับให้ AI สรุปอ่านง่าย (ไม่โชว์ JSON ดิบ) ──
                            _pending.Enqueue(() => SetMessage(s, item.PlaceholderIndex, "assistant", THINKING));
                            Repaint();

                            // capture_screenshot → แนบ "รูปจริง" ให้ AI เห็น (ไม่ใช่แค่ path)
                            List<ClaudeImage> followImages = null;
                            string fp;
                            if (cmdName == "capture_screenshot")
                            {
                                string shotPath = ExtractScreenshotPath(execResult);
                                followImages = BuildScreenshotImages(shotPath);
                                fp = $"คำถามเดิมของผู้ใช้: {item.RawPrompt}\n\n" +
                                     (followImages != null
                                        ? "นี่คือ screenshot จาก Unity (แนบรูปมาด้วย) — วิเคราะห์สิ่งที่เห็นในภาพเพื่อตอบคำถามเดิมของผู้ใช้ เป็นภาษาไทย ตามรูปแบบ Header(Dev)/Header(Art)"
                                        : $"จับ screenshot แล้วแต่โหลดรูปไม่ได้ ({EscapeForPrompt(execResult)}) — แจ้งผู้ใช้สั้นๆ");
                            }
                            else
                            {
                                fp = $"คำถามเดิมของผู้ใช้: {item.RawPrompt}\n\n" +
                                     $"นี่คือผลลัพธ์ JSON จากคำสั่ง {cmdName} ของ Unity:\n{execResult}\n\n" +
                                     "วิเคราะห์ผลนี้ \"โดยตอบคำถามเดิมของผู้ใช้\" เป็นภาษาไทย ตามรูปแบบ Header(Dev)/Header(Art) — " +
                                     "ห้ามแสดง JSON ดิบ ให้จัดกลุ่ม/นับ/ชี้ประเด็นที่น่าสนใจแทน";
                            }
                            ClaudeResponse follow;
                            try
                            {
                                follow = s.backend == 1
                                    ? await ClaudeCliClient.SendAsync(fp, followImages, token, s.cliSessionId, curRole)
                                    : await ClaudeAPIClient.SendAsync(fp, followImages, token, curRole, item.History);
                                if (s.backend == 1 && !string.IsNullOrEmpty(follow?.SessionId))
                                    s.cliSessionId = follow.SessionId;   // ไม่ ++cliTurnCount (round เดียวกับ user)
                            }
                            catch (OperationCanceledException) { follow = new ClaudeResponse { Error = "ยกเลิกแล้ว" }; }

                            if (follow != null && !follow.IsError && !string.IsNullOrEmpty(follow.Text))
                                content = string.IsNullOrEmpty(response.Text) ? follow.Text : response.Text + "\n\n" + follow.Text;
                            else
                                content += "\n\n" + execResult;   // สรุปไม่ได้ → fallback โชว์ผลดิบ
                        }
                        else
                        {
                            content += "\n\n" + execResult;   // action command → ✅ ตามเดิม
                        }
                    }

                    // สถิติ: เวลา + token (CLI) — เก็บแยก แสดงข้างชื่อ CLAUDE
                    double sec = EditorApplication.timeSinceStartup - s.requestStart;
                    stat = $"⏱ {FmtTime(sec)}";
                    if (s.backend == 1 && ClaudeCliClient.LiveOutputTokens > 0)
                        stat += $" · {ClaudeCliClient.LiveOutputTokens:N0} tokens";
                }
                // apply ตอน Layout เท่านั้น (กัน layout เพี้ยนระหว่างวาด)
                int idx = item.PlaceholderIndex; string c = content, st = stat;
                _pending.Enqueue(() => SetMessage(s, idx, "assistant", c, st));

                _autoScroll = true;
                Repaint();
            }
            s.pumping = false;
            SaveHistory(s);   // เซฟครั้งเดียวหลังคิวหมด (ไม่เขียน EditorPrefs ทุกข้อความ = ไม่กระตุก)
            Repaint();
        }

        // ยกเลิกงานปัจจุบัน + ล้างคิวทั้งหมดของ tab นั้น
        void StopSession(ChatSession s)
        {
            // mark queued placeholders เป็นยกเลิก
            foreach (var q in s.queue)
                if (q.PlaceholderIndex >= 0 && q.PlaceholderIndex < s.messages.Count)
                    s.messages[q.PlaceholderIndex] = new ChatMessage("assistant", "❌ ยกเลิกแล้ว");
            s.queue.Clear();
            s.cts?.Cancel();
            s.cliSessionId = null;   // turn ถูกตัดกลางคัน → ไม่ resume ต่อ (กัน context ค้างครึ่ง)
            s.cliTurnCount = 0;
            Repaint();
        }

        // ยกเลิก prompt ที่รอคิวอยู่ทีละอัน (ตาม placeholder index)
        void CancelQueued(ChatSession s, int placeholderIndex)
        {
            // rebuild queue ตัดอันที่ยกเลิกออก
            var keep = new Queue<QueuedItem>();
            while (s.queue.Count > 0)
            {
                var q = s.queue.Dequeue();
                if (q.PlaceholderIndex == placeholderIndex)
                    s.messages[placeholderIndex] = new ChatMessage("assistant", "❌ ยกเลิกแล้ว");
                else keep.Enqueue(q);
            }
            while (keep.Count > 0) s.queue.Enqueue(keep.Dequeue());
            Repaint();
        }

        // execute command → คืนผลเป็น string (ให้ pump เอาไปต่อท้ายคำตอบในก้อนเดียว)
        // command ที่ "คืนข้อมูล" (ต้องให้ AI สรุปอ่านง่าย) — ตรงข้ามกับ action (create/set → แค่ ✅)
        static readonly HashSet<string> _dataCommands = new HashSet<string>
        {
            "count_components","find_asset","inspect_object","scene_hierarchy","scene_list",
            "read_console","read_logfile","capture_state","perf_audit","perf_worst",
            "refactor_audit","audit_textures","audit_unused","audit_empty_folders",
            "memory_snapshot","fusion_stats","get_exceptions","watch_get","read_script",
            "capture_screenshot",   // round-2 แนบรูปจริงให้ AI วิเคราะห์ (ไม่ใช่แค่ path)
        };

        // คะแนนความเกี่ยวข้องของ prefab กับ script ที่ @ มา (ชื่อตรง/ชื่อ script อยู่ในชื่อ prefab = เกี่ยวมาก)
        // ใช้ smart-select prefab ที่จะ inspect → เลือกตัวที่น่าจะใช่ ไม่ใช่ 2 ตัวแรกตามลำดับไฟล์
        static int PrefabRelevance(string prefabName, List<string> scripts)
        {
            if (string.IsNullOrEmpty(prefabName) || scripts == null) return 0;
            string pn = prefabName.ToLowerInvariant();
            int best = 0;
            foreach (var s in scripts)
            {
                if (string.IsNullOrEmpty(s)) continue;
                string sb = s.ToLowerInvariant();
                if (sb.EndsWith(".cs")) sb = sb.Substring(0, sb.Length - 3);
                if (sb.Length < 2) continue;
                if (pn == sb)            best = Math.Max(best, 100);          // ชื่อตรงเป๊ะ
                else if (pn.Contains(sb)) best = Math.Max(best, 50 + sb.Length); // prefab มีชื่อ script (เจาะจง)
                else if (sb.Contains(pn)) best = Math.Max(best, 30);          // script มีชื่อ prefab
            }
            return best;
        }

        static string ExtractCommandName(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"command\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        string ExecuteCommand(ChatSession s, string cmdJson)
        {
            try
            {
                string result = MCPHandlers.Dispatch(CommandJsonToPath(cmdJson), cmdJson);
                return $"✅ Execute: {result}";
            }
            catch (Exception e)
            {
                return $"⚠️ Execute error: {e.Message}";
            }
        }

        // แทนที่ข้อความที่ index — ถ้า index หลุด (เช่นกด Clear ระหว่างคิด) → drop ทิ้ง (ไม่ append stray)
        static void SetMessage(ChatSession s, int index, string role, string content, string stat = null)
        {
            if (index < 0 || index >= s.messages.Count) return;
            var old = s.messages[index];
            s.messages[index] = new ChatMessage(role, content) { Stat = stat };
        }

        // single source ของ map ชื่อ→path อยู่ที่ MCPHandlers.CmdAlias (กัน drift กับ Dispatch)
        static string CommandJsonToPath(string json)
            => MCPHandlers.ResolvePath(ExtractCommandName(json));

        // ดึง path ของ screenshot จากผล execResult (JSON มี backslash escaped → unescape)
        static string ExtractScreenshotPath(string execResult)
        {
            if (string.IsNullOrEmpty(execResult)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(execResult, "\"screenshot\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
        }

        // อ่านไฟล์ PNG → ClaudeImage (resize ให้พอดี API) เพื่อแนบให้ AI วิเคราะห์ภาพ round-2
        static List<ClaudeImage> BuildScreenshotImages(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                byte[] bytes = ImageOptimizer.ResizeForApi(path, 1568, out string mime);
                return new List<ClaudeImage> { new ClaudeImage { Base64 = Convert.ToBase64String(bytes), Mime = mime } };
            }
            catch
            {
                try { return new List<ClaudeImage> { new ClaudeImage { Base64 = Convert.ToBase64String(File.ReadAllBytes(path)), Mime = "image/png" } }; }
                catch { return null; }
            }
        }

        static string EscapeForPrompt(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s.Substring(0, 300) + "…" : s);

        // ── Types ─────────────────────────────────────────────────────────
        [Serializable]
        class ChatSession
        {
            public int backend;
            public List<ChatMessage> messages = new List<ChatMessage>();
            public string draft = "";
            public Vector2 chatScroll;

            [NonSerialized] public bool isLoading;
            [NonSerialized] public bool pumping;
            [NonSerialized] public List<AttachedImage> images = new List<AttachedImage>();
            // ข้อมูล profiler ที่แนบ — แยกเป็นส่วนๆ (Profiler/Network/GC) แนบหลายอันใน prompt เดียวได้
            [NonSerialized] public Dictionary<string, string> attached = new Dictionary<string, string>();
            [NonSerialized] public string cliSessionId;   // session ของ CLI (--resume → warm, ไม่ cold start)
            [NonSerialized] public int cliTurnCount;       // นับ turn ที่ resume session เดิม — ครบ N → หมุน session ใหม่ (กัน context สะสม)
            [NonSerialized] public Queue<QueuedItem> queue = new Queue<QueuedItem>();
            [NonSerialized] public System.Threading.CancellationTokenSource cts;
            [NonSerialized] public double requestStart;   // เวลาเริ่ม request ปัจจุบัน

            public bool Busy => isLoading || queue.Count > 0;

            // เรียกหลัง domain reload — กัน NonSerialized fields เป็น null
            public void Reinit()
            {
                isLoading = false;
                pumping = false;
                if (attached == null) attached = new Dictionary<string, string>(); else attached.Clear();
                cts = null;
                if (images == null) images = new List<AttachedImage>();
                if (queue == null) queue = new Queue<QueuedItem>();
                else queue.Clear();
                if (messages == null) messages = new List<ChatMessage>();
            }
        }

        class QueuedItem
        {
            public string FullPrompt;
            public string RawPrompt;       // คำถามดิบของ user (ไม่มี attachment) — ให้ round-2 summarizer เห็นบริบท
            public List<ClaudeImage> Images;
            public int PlaceholderIndex;   // index ของ bubble คำตอบ (อยู่ใต้ prompt อันนี้)
            public List<ConversationTurn> History;
        }

        // ── Role parser: หา Header(Dev) หรือ Header(Art) แล้ว extract content ──
        // คืน null ถ้าไม่มี header ของ role นั้นใน response
        // ── Role parser v2 ──────────────────────────────────────────────────
        // marker หลัก: "Header(Dev)" บนบรรทัดของตัวเอง — line-anchored กัน "Header(" ในโค้ด/คำพูดหลอก parser
        // ยอม decoration: เว้นวรรคในวงเล็บ, **ตัวหนา**, ##, > นำหน้า/ตามหลัง
        static readonly System.Text.RegularExpressions.Regex _headerRe =
            new System.Text.RegularExpressions.Regex(
                @"(?m)^[ \t>*#]*Header\s*\(\s*(Dev|Art)\s*\)[ \t*:]*\r?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static bool HasHeaderMarkers(string content)
            => !string.IsNullOrEmpty(content) && _headerRe.IsMatch(content);

        // คืน section ของ role — รวมทุกก้อนถ้า role เดียวกันโผล่หลายครั้ง · null = ไม่มี section ของ role นี้
        static string ExtractHeaderContent(string content, string role)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var ms = _headerRe.Matches(content);
            if (ms.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ms.Count; i++)
            {
                if (!string.Equals(ms[i].Groups[1].Value, role, StringComparison.OrdinalIgnoreCase)) continue;
                int start = ms[i].Index + ms[i].Length;
                int end = i + 1 < ms.Count ? ms[i + 1].Index : content.Length;
                string part = content.Substring(start, end - start).Trim();
                if (part.Length > 0) sb.Append(part).Append("\n\n");
            }
            return sb.Length == 0 ? null : sb.ToString().Trim();
        }

        // ── Parser สำรอง: model บางทีไม่ส่ง Header() แต่ใช้หัวแบบ "💻 Dev — ..." / "## Art: ..." ──
        // prefix ได้เฉพาะ decoration (emoji/#/*/>/ช่องว่าง — ห้ามตัวอักษร/ไทย กัน "ฝั่ง Dev —" กลางประโยค)
        static readonly System.Text.RegularExpressions.Regex _altRoleRe =
            new System.Text.RegularExpressions.Regex(
                @"(?m)^[^\w฀-๿\r\n]{0,8}(Dev|Art)\b[*_]*\s*(?:[—–:：]|-\s)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);   // hyphen เปล่าต้องมี space ตาม (กัน "Dev-branch")

        // คืน null = ทั้งข้อความไม่มี marker เลย (caller fallback แสดงทั้งก้อน)
        // คืน ""   = มี marker แต่ไม่มี section ของ role นี้ (caller โชว์ "ไม่มีข้อมูล")
        static string ExtractAltRoleSection(string content, string roleName)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var ms = _altRoleRe.Matches(content);
            if (ms.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ms.Count; i++)
            {
                if (!string.Equals(ms[i].Groups[1].Value, roleName, StringComparison.OrdinalIgnoreCase)) continue;
                int start = ms[i].Index;                                        // รวมบรรทัดหัวไว้ (เป็น heading สี)
                int end = i + 1 < ms.Count ? ms[i + 1].Index : content.Length;  // ถึง marker ถัดไป/จบ
                string part = content.Substring(start, end - start).Trim();
                if (part.Length > 0) sb.Append(part).Append("\n\n");
            }
            return sb.Length == 0 ? "" : sb.ToString().Trim();
        }

        // เนื้อหาบางเกินจริง (ว่าง / มีแต่ markdown เปล่า) → ถือว่าไม่มีข้อมูล
        // คำตอบสั้นแต่มีสาระ (มีตัวเลข / status emoji เช่น "✅ ไม่มี error") = ไม่ thin
        static bool IsThinContent(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[0-9๐-๙]|✓|✔|✅|❌|⚠|🔴|🟡|🟢")) return false;
            string stripped = System.Text.RegularExpressions.Regex.Replace(
                s, @"Category\([^)]*\)|Header\([^)]*\)|[#*`\-:\s]", "");
            // thin = ไม่เหลือ "ตัวอักษร" เลย (คำตอบสั้นแต่มีสาระ เช่น "ปกติดี" ต้องรอด — ไทยสั้นเป็นปกติ)
            return !System.Text.RegularExpressions.Regex.IsMatch(stripped, @"\p{L}");
        }

        [Serializable]
        class ChatMessage
        {
            public string Role;
            public string Content;
            public string Stat;
            public bool   IsDual;   // ยังคงไว้เพื่อ backward compat (serialize เดิม)
            public string ArtContent;
            [NonSerialized] public bool collapsed;   // user message: พับซ่อน AI response ถัดไป
            [NonSerialized] string _rich;
            [NonSerialized] float _height = -1, _heightWidth = -1;
            [NonSerialized] ChatMessage _devView;   // cached filtered view for Dev
            [NonSerialized] ChatMessage _artView;   // cached filtered view for Art
            public ChatMessage(string role, string content) { Role = role; Content = content; }

            // Extract content ของ role นี้จาก Header(Dev)/Header(Art) blocks
            // ถ้าไม่มี Header ของ role นี้ → แสดง "ไม่มีข้อมูล"
            // ถ้าไม่มี Header เลย (fallback/old format) → แสดงทั้งหมด
            public ChatMessage RoleView(int role)
            {
                if (Role == "user") return this;

                var cached = role == 0 ? _devView : _artView;
                if (cached != null) return cached;

                string roleName = role == 0 ? "Dev" : "Art";
                bool hasAnyHeader = HasHeaderMarkers(Content);   // line-anchored — "Header(" ในโค้ด/คำพูดไม่หลอกแล้ว

                ChatMessage result;
                string extracted = hasAnyHeader
                    ? ExtractHeaderContent(Content, roleName)
                    : ExtractAltRoleSection(Content, roleName);   // ไม่มี Header() → ลองหัวแบบ "Dev —/Art —"

                if (extracted == null && !hasAnyHeader)
                {
                    result = this;  // ไม่มี marker รูปแบบไหนเลย → fallback แสดงทั้งหมด
                }
                else if (!IsThinContent(extracted))
                {
                    result = new ChatMessage("assistant", InjectSummaryTableIfMissing(extracted)) { Stat = Stat };
                }
                else
                {
                    string label = role == 1 ? "Visual (Art)" : "Technical (Dev)";
                    result = new ChatMessage(role.ToString(), "ℹ️ ไม่มีข้อมูลด้าน " + label + " สำหรับ prompt นี้") { Stat = Stat };
                }

                if (role == 0) _devView = result;
                else _artView = result;
                return result;
            }

            // ── ทาง B: ถ้า model ไม่ได้ทำตารางสรุป 🎯 มา → Unity สร้างจาก finding card เอง ──
            // parse "## 🔴 #N — title ✓" + **จุด:** + **ค่าจริง:** → ตาราง ranking แทรกบนสุด
            // → การันตีว่ามีตารางทุกครั้ง ไม่พึ่ง model (กันเคส CLI/model ข้าม instruction)
            static readonly System.Text.RegularExpressions.Regex _cardRe =
                new System.Text.RegularExpressions.Regex(@"^\s*#{1,4}\s*(🔴|🟡|🟢)\s*#?\d*\s*[—\-–]\s*(.+?)\s*$");

            static string InjectSummaryTableIfMissing(string content)
            {
                if (string.IsNullOrEmpty(content)) return content;
                // model ทำตารางสรุปมาแล้วไหม → ไม่ inject ซ้ำ
                // เช็ค "ตาราง ranking" จาก header แถวที่ขึ้นต้น | # | (ครอบทุก emoji: 🎯/📊/ไม่มี)
                if (System.Text.RegularExpressions.Regex.IsMatch(content, @"(?m)^\s*\|\s*(#|ลำดับ)\s*\|")) return content;
                if (content.IndexOf("เรียงตามความเสี่ยง", System.StringComparison.Ordinal) >= 0) return content;
                if (content.IndexOf("เสี่ยงมาก→น้อย", System.StringComparison.Ordinal) >= 0) return content;

                var lines = content.Replace("\r\n", "\n").Split('\n');
                var rows = new System.Collections.Generic.List<string[]>();   // {n, emoji, title, conf, val, loc}
                string emoji = null, title = null, conf = null, val = null, loc = null;

                void Flush()
                {
                    if (emoji != null && !string.IsNullOrEmpty(title))
                        rows.Add(new[] { (rows.Count + 1).ToString(), emoji, San(title), conf ?? "?", San(val ?? "—"), San(loc ?? "—") });
                    emoji = title = conf = val = loc = null;
                }

                foreach (var raw in lines)
                {
                    var m = _cardRe.Match(raw);
                    if (m.Success)
                    {
                        Flush();
                        emoji = m.Groups[1].Value;
                        string rest = m.Groups[2].Value;
                        conf = rest.Contains("✓") ? "✓" : rest.Contains("❌") ? "❌" : rest.Contains("⏸") ? "⏸️" : "?";
                        int cut = rest.IndexOfAny(new[] { '✓', '❌', '?', '⏸' });
                        title = (cut >= 0 ? rest.Substring(0, cut) : rest).Trim().TrimEnd('—', '-', '–', ' ');
                        continue;
                    }
                    if (emoji == null) continue;   // ยังไม่เข้า card แรก
                    string t = raw.Trim();
                    if (loc == null) loc = Field(t, "จุด");
                    if (val == null) val = Field(t, "ค่าจริง");
                }
                Flush();

                if (rows.Count < 2) return content;   // brain: ตารางเฉพาะเมื่อมี ≥ 2 finding

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("## 🎯 สรุป (เรียงตามความเสี่ยง)");
                sb.AppendLine("| # | ปัญหา | สถานะ | ค่าจริง / budget | มั่นใจ | จุด |");
                sb.AppendLine("|---|-------|------|------------------|-------|-----|");
                foreach (var r in rows)
                    sb.AppendLine($"| {r[0]} | {r[2]} | {r[1]} | {r[4]} | {r[3]} | {r[5]} |");
                sb.AppendLine();
                return sb.ToString() + content;
            }

            // ดึงค่าหลัง "**field:**" หรือ "field:" (รองรับ : และ ：)
            static string Field(string line, string field)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^\**\s*" + field + @"\s*\**\s*[:：]\s*(.+)$");
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }

            // ทำให้ลงตาราง markdown ได้ (ตัด | ** ` ออก + ตัดยาว)
            static string San(string s)
            {
                if (string.IsNullOrEmpty(s)) return "—";
                s = s.Replace("|", "/").Replace("**", "").Replace("`", "").Trim();
                if (s.Length > 48) s = s.Substring(0, 46) + "…";
                return s.Length == 0 ? "—" : s;
            }

            public void InvalidateCaches() { _devView = null; _artView = null; }

            // fade-in: จำเวลาที่โผล่ครั้งแรก แล้วคืน alpha 0→1 ใน ~0.28s
            [NonSerialized] double _shownAt = -1;
            public float FadeAlpha(double now)
            {
                if (_shownAt < 0) _shownAt = now;
                double t = (now - _shownAt) / 0.28;
                return t >= 1.0 ? 1f : (float)t;
            }

            // ตัด Header(Dev)/Header(Art) markers ออกก่อนแสดง (ใช้ parse เท่านั้น ไม่โชว์ user)
            public string DisplayContent
            {
                get
                {
                    if (Role == "user" || string.IsNullOrEmpty(Content)) return Content;
                    // ตัด "Header(Dev)" / "Header(Art)" บรรทัดที่เป็น marker ออก
                    var lines = Content.Split('\n');
                    var kept = new System.Collections.Generic.List<string>(lines.Length);
                    foreach (var line in lines)
                    {
                        string t = line.Trim();
                        // marker (รวมแบบแต่งหนา/เว้นวรรค) + บรรทัด CATEGORIES routing → ไม่โชว์ user
                        if (_headerRe.IsMatch(t) ||
                            t.StartsWith("CATEGORIES:", System.StringComparison.OrdinalIgnoreCase))
                            continue;
                        // marker ที่มีเนื้อหาตามบนบรรทัดเดียวกัน ("Header(Dev) สรุป: …") → ตัดเฉพาะ marker เก็บเนื้อหาไว้
                        if (t.StartsWith("Header(Dev)", System.StringComparison.OrdinalIgnoreCase) ||
                            t.StartsWith("Header(Art)", System.StringComparison.OrdinalIgnoreCase))
                        {
                            string rest = t.Substring(11).TrimStart(' ', '\t', ':', '*', '-', '—');
                            if (rest.Length > 0) kept.Add(rest);
                            continue;
                        }
                        // ตัด markdown header เปล่า (#, ##, ### ที่ไม่มีข้อความตาม) — artifact ท้าย response
                        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^#{1,6}\s*$")) continue;
                        kept.Add(line);
                    }
                    return string.Join("\n", kept).TrimStart('\r', '\n');
                }
            }

            public string Rich()
            {
                // ไม่ cache ถ้า DisplayContent ต่างจาก Content (มี CATEGORIES: header ที่ต้องตัด)
                if (_rich == null || (_cachedRichFor != DisplayContent))
                {
                    _cachedRichFor = DisplayContent;
                    _rich = Role == "user" ? Content : MarkdownColor.ToRichText(DisplayContent);
                    _segs = null;     // invalidate segment cache ด้วย
                    _height = -1;     // ⚠ ความสูงผูกกับเนื้อหาเก่า — ไม่ reset = กล่องโย่ง/หด text ไม่ตรง (บั๊กสลับ role แล้วเอ๋อ)
                }
                return _rich;
            }
            [NonSerialized] string _cachedRichFor;

            // cache ความสูง — คำนวณใหม่เฉพาะตอน width เปลี่ยน (ไม่ใช่ทุกเฟรม)
            public float Height(GUIStyle style, float width)
            {
                if (_height < 0 || !Mathf.Approximately(_heightWidth, width))
                {
                    _height = style.CalcHeight(new GUIContent(Rich()), width);
                    _heightWidth = width;
                }
                return _height;
            }

            // ── แยกข้อความเป็น segment: text ปกติ / code block (```...```) ──
            [NonSerialized] List<Seg> _segs;
            // ต้อง render แบบ segment (code box หรือ table) ไม่ใช่ fast-path label เดียว
            public bool HasRich { get { Parse(); return _hasCode || _hasTable; } }
            [NonSerialized] bool _hasCode, _hasTable;

            public List<Seg> Segments() { Parse(); return _segs; }

            void Parse()
            {
                if (_segs != null) return;
                _segs = new List<Seg>();
                if (Role == "user")
                {
                    _segs.Add(new Seg { Code = false, Rendered = Rich() });
                    return;
                }
                var parts = DisplayContent.Split(new[] { "```" }, System.StringSplitOptions.None);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i % 2 == 0) // text (อาจมีตารางปนอยู่)
                    {
                        AddTextSegs(parts[i]);
                    }
                    else // code
                    {
                        string body = parts[i];
                        int nl = body.IndexOf('\n');
                        string lang = nl > 0 ? body.Substring(0, nl).Trim() : "";
                        string code = nl > 0 ? body.Substring(nl + 1) : body;
                        code = code.TrimEnd('\n');
                        string header = ExtractHeader(code, lang);
                        _segs.Add(new Seg { Code = true, Raw = code, Rendered = CodeHighlight.Highlight(code), Header = header });
                        _hasCode = true;
                    }
                }
                if (_segs.Count == 0) _segs.Add(new Seg { Code = false, Rendered = Rich() });
            }

            // แยกข้อความปกติออกเป็น text seg กับ table seg (จับบล็อกบรรทัดที่มี '|' ติดกัน ≥2)
            void AddTextSegs(string text)
            {
                if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
                var lines = text.Split('\n');
                var buf = new List<string>();
                int i = 0;
                while (i < lines.Length)
                {
                    bool here = IsTableLine(lines[i]);
                    bool next = i + 1 < lines.Length && IsTableLine(lines[i + 1]);
                    if (here && next)
                    {
                        FlushText(buf);
                        var tbl = new List<string>();
                        while (i < lines.Length && IsTableLine(lines[i])) { tbl.Add(lines[i]); i++; }
                        var seg = BuildTable(tbl);
                        if (seg != null) { _segs.Add(seg); _hasTable = true; }
                    }
                    else { buf.Add(lines[i]); i++; }
                }
                FlushText(buf);
            }

            void FlushText(List<string> buf)
            {
                if (buf.Count == 0) return;
                string t = string.Join("\n", buf).Trim();
                if (t.Length > 0) _segs.Add(new Seg { Code = false, Rendered = MarkdownColor.ToRichText(t) });
                buf.Clear();
            }

            static bool IsTableLine(string line)
            {
                if (line == null) return false;
                string t = line.Trim();
                return t.Length > 0 && t.IndexOf('|') >= 0;
            }

            // แตกเซลล์จากบรรทัด: split '|' แล้ว trim, ตัด cell ว่างหัว/ท้าย (จาก pipe ขอบ)
            static List<string> SplitCells(string line)
            {
                var raw = new List<string>(line.Split('|'));
                for (int k = 0; k < raw.Count; k++) raw[k] = raw[k].Trim();
                if (raw.Count > 0 && raw[0].Length == 0) raw.RemoveAt(0);
                if (raw.Count > 0 && raw[raw.Count - 1].Length == 0) raw.RemoveAt(raw.Count - 1);
                return raw;
            }

            // บรรทัดคั่น header ของ markdown (|---|:--:|) → ข้าม
            static bool IsSeparatorRow(List<string> cells)
            {
                if (cells.Count == 0) return false;
                foreach (var c in cells)
                {
                    string t = c.Replace(":", "").Replace("-", "").Trim();
                    if (t.Length != 0 || c.IndexOf('-') < 0) return false;
                }
                return true;
            }

            // เซลล์ตาราง: ขาวล้วน อ่านง่าย — แปลงแค่ **bold** กับ `code`, ไม่ลงสี markdown เต็ม
            static string CleanCell(string s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\*\*(.+?)\*\*", "<b>$1</b>");
                s = s.Replace("`", "");
                return s;
            }

            static Seg BuildTable(List<string> lines)
            {
                var rows = new List<string[]>();
                int cols = 0;
                foreach (var ln in lines)
                {
                    var cells = SplitCells(ln);
                    if (IsSeparatorRow(cells)) continue;
                    if (cells.Count == 0) continue;
                    var arr = new string[cells.Count];
                    for (int c = 0; c < cells.Count; c++) arr[c] = CleanCell(cells[c]);
                    rows.Add(arr);
                    if (cells.Count > cols) cols = cells.Count;
                }
                if (rows.Count < 1 || cols < 1) return null;
                return new Seg { Table = true, Rows = rows, Cols = cols };
            }

            static string ExtractHeader(string code, string lang)
            {
                // หา path .cs จาก // FILE: หรือบรรทัดแรกที่เป็น path
                var m = System.Text.RegularExpressions.Regex.Match(code, @"//\s*FILE:\s*(\S+)");
                if (m.Success) return m.Groups[1].Value;
                var m2 = System.Text.RegularExpressions.Regex.Match(code, @"(Assets/[\w/]+\.cs)");
                if (m2.Success) return m2.Groups[1].Value;
                return "Code";
            }
        }

        class Seg
        {
            public bool Code;
            public bool Table;        // เป็นตาราง markdown (| .. | .. |)
            public bool Collapsed;    // พับ box อยู่ (toggle จาก header)
            public string Rendered;   // rich text (text หรือ highlighted code)
            public string Raw;        // code ดิบ (สำหรับ copy)
            public string Header;     // path/lang ของ code block
            public List<string[]> Rows;   // เซลล์ของตาราง (rich text แล้ว) แถวแรก = header
            public int Cols;
            public float Height = -1;
        }

        class AttachedImage
        {
            public string Path;
            public Texture2D Texture;
            public string Mime;
        }
    }
}
