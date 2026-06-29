using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// RuntimeWatch — sample field/property ของ GameObject ทุก 0.5s ระหว่าง Play Mode
    /// ดู trend (↑/↓/=) + history 10 ค่า เพื่อให้ AI วิเคราะห์ state change ได้
    /// </summary>
    [InitializeOnLoad]
    public static class RuntimeWatch
    {
        class WatchEntry
        {
            public string objectName;
            public string componentType;
            public string fieldName;
            public readonly List<string> history = new List<string>();   // last 10 values
            public readonly List<string> timestamps = new List<string>();
            public bool everFound;     // เคย resolve object เจอมั้ย (กันลบ watch ที่ชื่อพิมพ์ผิดตั้งแต่แรก)
            public int missStreak;     // หาย (not found) ติดกันกี่ครั้ง → ใช้ auto-clear ตอน despawn
        }

        static readonly List<WatchEntry> _watches = new List<WatchEntry>();
        static readonly object _lock = new object();
        static double _lastSample;
        static int _sampleCount;
        const int MAX_HISTORY = 10;
        const double SAMPLE_INTERVAL = 0.5;
        const string NOT_FOUND = "(not found)";   // object resolve ไม่เจอ (object-level)
        const int DESPAWN_MISS = 10;              // หายติดกัน 10 ครั้ง (~5 วิ) + เคยเจอ → auto-clear (despawn)

        static RuntimeWatch()
        {
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _lastSample = 0;
                _sampleCount = 0;
                EditorApplication.update += Sample;
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                EditorApplication.update -= Sample;
            }
        }

        // EditorApplication.update รันบน main thread — Unity API เรียกตรงได้
        static void Sample()
        {
            if (!Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastSample < SAMPLE_INTERVAL) return;
            _lastSample = now;
            _sampleCount++;

            string ts = DateTime.Now.ToString("HH:mm:ss");
            lock (_lock)
            {
                List<WatchEntry> despawned = null;
                foreach (var w in _watches)
                {
                    string val = SampleValue(w);

                    if (val == NOT_FOUND)
                    {
                        w.missStreak++;
                        // เคยเจอแล้วหายติดกันนาน = object ตาย/despawn → ลบ watch ออกอัตโนมัติ
                        // (ถ้าไม่เคยเจอเลย = ชื่อผิด → ปล่อยให้โชว์ "(not found)" เป็น feedback ไม่ลบ)
                        if (w.everFound && w.missStreak >= DESPAWN_MISS)
                            (despawned ??= new List<WatchEntry>()).Add(w);
                    }
                    else
                    {
                        w.everFound = true;
                        w.missStreak = 0;
                    }

                    w.history.Add(val);
                    w.timestamps.Add(ts);
                    if (w.history.Count > MAX_HISTORY) { w.history.RemoveAt(0); w.timestamps.RemoveAt(0); }
                }
                if (despawned != null) foreach (var w in despawned) _watches.Remove(w);
            }
        }

        static string SampleValue(WatchEntry w)
        {
            try
            {
                var go = ResolveObject(w.objectName);
                if (go == null) return NOT_FOUND;

                Component comp = null;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    if (c.GetType().Name == w.componentType || c.GetType().FullName == w.componentType)
                    { comp = c; break; }
                }
                if (comp == null) return "(component not found)";

                // รองรับ nested path "a.b.c" — เดินไล่ field/property ทีละชั้น (เช่น Damageable.Hp.Value)
                object cur = comp;
                foreach (var seg in w.fieldName.Split('.'))
                {
                    if (cur == null) return "null";
                    if (!TryGetMember(cur, seg, out cur)) return $"(not found: {seg})";
                }
                return FormatValue(cur);
            }
            catch (Exception e) { return $"(error: {e.Message})"; }
        }

        // แปลงค่าเป็น string — ถ้าเป็น collection (List/array/Dictionary) โชว์ count + รายการ
        // (กัน ToString ของ List ที่ได้ "System.Collections...List`1" ไร้ประโยชน์)
        // → ใช้ดู status effect ค้าง: watch m_statusEffects → "count=3 [stun, burn, slow]"
        static string FormatValue(object v)
        {
            if (v == null) return "null";
            if (!(v is string) && v is System.Collections.ICollection col)
            {
                var items = new List<string>();
                int i = 0;
                foreach (var it in col)
                {
                    if (i++ >= 8) { items.Add("…"); break; }   // cap กัน list ยาว
                    items.Add(ItemLabel(it));
                }
                return $"count={col.Count} [{string.Join(", ", items)}]";
            }
            return v.ToString();
        }

        // label item ใน collection — โชว์ ID/ชื่อ + tag passive/เวลาเหลือ (ถ้า object มี member พวกนี้)
        // → status effect: "stun(1.2s)" = temporary · "bloodlust(passive)" = ถาวร
        //   ทำให้แยก "ตัวที่ควรหายแต่ค้าง (บั๊ก)" ออกจาก "passive ติดถาวร (ปกติ)" ได้
        static string ItemLabel(object it)
        {
            try
            {
                if (it == null) return "null";
                var t = it.GetType();
                if (t.IsPrimitive || it is string) return it.ToString();

                // identity: ลอง ID → Name → Key → ชื่อ type
                string id = GetMember(it, "ID")?.ToString() ?? GetMember(it, "Name")?.ToString()
                          ?? GetMember(it, "Key")?.ToString() ?? t.Name;

                // tag: HasDuration=false → passive · true → เวลาเหลือ (TimeLeft)
                object hasDur = GetMember(it, "HasDuration");
                if (hasDur != null)
                {
                    string hs = hasDur.ToString().ToLowerInvariant();
                    if (hs == "false" || hs == "0") return id + "(passive)";   // ถาวร — ไม่ใช่บั๊ก
                    object tl = GetMember(it, "TimeLeft");
                    if (tl != null) return $"{id}({tl}s)";                       // temporary — มีเวลาเหลือ
                }
                return id;
            }
            catch { return it?.ToString() ?? "null"; }
        }

        // อ่าน member แบบ crash-safe (ใช้ TryGetMember = field/property getter ผ่าน GetGetMethod.Invoke)
        static object GetMember(object obj, string name)
            => TryGetMember(obj, name, out var v) ? v : null;

        // หา GameObject แบบยืดหยุ่น (networked object spawn runtime ชื่อมักเป็น "X(Clone)" / มี prefix)
        // ลำดับ: ชื่อตรงเป๊ะ → ชื่อตรง (ไม่สน case) → ชื่อ "contains" → ตัวที่เลือกใน Hierarchy
        static GameObject ResolveObject(string name)
        {
            var sel = Selection.activeGameObject;
            if (string.IsNullOrEmpty(name)) return sel;

            // ถ้าผู้ใช้คลิกเลือก object ที่ชื่อตรง/contains query ไว้ → ใช้ตัวนั้นเลย
            // (ชี้ชัดสุด — กันสับสนตอนมีหลายตัวชื่อคล้ายกัน เช่น hero P1 vs P2 ใน MPPM)
            if (sel != null && sel.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return sel;

            var exact = GameObject.Find(name);   // active + ตรงเป๊ะ (เร็วสุด)
            if (exact != null) return exact;

            // fuzzy: ไล่ทุก Transform ในฉาก (active) — เจอ "base_avatar(Clone)" จากคำว่า "base_avatar"
            GameObject contains = null;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                string n = t.gameObject.name;
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return t.gameObject;   // ตรง (ไม่สน case)
                if (contains == null && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) contains = t.gameObject;
            }
            if (contains != null) return contains;

            // ระบุชื่อมาแต่หาไม่เจอ → null (→ "(not found)") เด็ดขาด
            // ไม่ fallback ไปตัวที่คลิก (กันโชว์ค่าตัวผิดตอน target ตาย/despawn)
            return null;
        }

        // อ่าน member (field ก่อน แล้ว property) — public + private
        static bool TryGetMember(object obj, string name, out object val)
        {
            val = null;
            if (obj == null || string.IsNullOrEmpty(name)) return false;
            var t = obj.GetType();
            var fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null) { val = fi.GetValue(obj); return true; }
            var pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0)
            {
                // ใช้ getter MethodInfo.Invoke แทน pi.GetValue() —
                // เลี่ยง CreateGetterDelegate → mono_class_get_methods_by_name ที่ assert/crash
                // บน Fusion [Networked] property บางตัว (เคยทำ Unity crash ดิบมาแล้ว)
                var getter = pi.GetGetMethod(true);
                if (getter == null) return false;
                val = getter.Invoke(obj, null);
                return true;
            }
            return false;
        }

        // ── Trend detection ───────────────────────────────────────────────────
        static string Trend(List<string> history)
        {
            if (history.Count < 2) return "=";
            string a = history[history.Count - 2];
            string b = history[history.Count - 1];
            if (a == b) return "=";
            if (double.TryParse(a, out double da) && double.TryParse(b, out double db))
                return db > da ? "↑" : "↓";
            return "changed";
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>เพิ่ม watch — คืน error string หรือ null ถ้าสำเร็จ</summary>
        public static string AddWatch(string objectName, string componentType, string fieldName)
        {
            if (string.IsNullOrEmpty(objectName)) return "objectName required";
            if (string.IsNullOrEmpty(componentType)) return "component required";
            if (string.IsNullOrEmpty(fieldName)) return "field required";

            lock (_lock)
            {
                // กัน duplicate key
                string key = $"{objectName}.{componentType}.{fieldName}";
                foreach (var w in _watches)
                    if ($"{w.objectName}.{w.componentType}.{w.fieldName}" == key)
                        return $"watch already exists: {key}";

                _watches.Add(new WatchEntry
                {
                    objectName = objectName,
                    componentType = componentType,
                    fieldName = fieldName
                });
            }
            return null;
        }

        /// <summary>JSON รายงานสถานะปัจจุบันของ watch ทั้งหมด</summary>
        public static string GetReport()
        {
            var sb = new StringBuilder();
            sb.Append($"{{\"isPlaying\":{Application.isPlaying.ToString().ToLower()},");
            sb.Append($"\"sampleCount\":{_sampleCount},\"watches\":[");

            lock (_lock)
            {
                for (int i = 0; i < _watches.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var w = _watches[i];
                    string key = $"{w.objectName}.{w.componentType}.{w.fieldName}";
                    string cur = w.history.Count > 0 ? w.history[w.history.Count - 1] : "n/a";
                    string prev = w.history.Count > 1 ? w.history[w.history.Count - 2] : cur;
                    string trend = Trend(w.history);

                    // history array
                    var histSb = new StringBuilder("[");
                    for (int h = 0; h < w.history.Count; h++)
                    {
                        if (h > 0) histSb.Append(",");
                        histSb.Append($"\"{MCPHandlers.EscapeJsonPublic(w.history[h])}\"");
                    }
                    histSb.Append("]");

                    string status = cur.StartsWith("(") ? "error" : "ok";
                    sb.Append($"{{\"key\":\"{MCPHandlers.EscapeJsonPublic(key)}\",");
                    sb.Append($"\"object\":\"{MCPHandlers.EscapeJsonPublic(w.objectName)}\",");
                    sb.Append($"\"component\":\"{MCPHandlers.EscapeJsonPublic(w.componentType)}\",");
                    sb.Append($"\"field\":\"{MCPHandlers.EscapeJsonPublic(w.fieldName)}\",");
                    sb.Append($"\"value\":\"{MCPHandlers.EscapeJsonPublic(cur)}\",");
                    sb.Append($"\"prev\":\"{MCPHandlers.EscapeJsonPublic(prev)}\",");
                    sb.Append($"\"trend\":\"{MCPHandlers.EscapeJsonPublic(trend)}\",");
                    sb.Append($"\"history\":{histSb},");
                    sb.Append($"\"status\":\"{status}\"}}");
                }
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>ลบ watch ทั้งหมด</summary>
        public static void ClearAll()
        {
            lock (_lock) { _watches.Clear(); }
            _sampleCount = 0;
        }
    }
}
