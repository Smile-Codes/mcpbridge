using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// event_log — ดักเหตุการณ์ physics สด (OnCollision/OnTrigger) ของ GameObject ที่เลือก
    /// โดยแปะ probe MonoBehaviour ชั่วคราวตอน Play แล้ว push เข้า ring buffer.
    /// ใช้ตอบ "ทำไมไม่โดน / โดนซ้ำ / trigger ไม่ทำงาน" โดยไม่ต้องแก้โค้ดเกม.
    /// probe ถูกถอด + buffer ถูกล้างอัตโนมัติเมื่อออก Play (เป็น debug tool — ไม่ persist/ไม่เข้า build).
    /// </summary>
    [InitializeOnLoad]
    public static class EventLog
    {
        struct Ev { public string time, kind, self, other; }

        static readonly List<Ev> _buf = new List<Ev>();
        static readonly object _lock = new object();
        static readonly List<MCPEventProbe> _probes = new List<MCPEventProbe>();
        const int MAX = 200;

        static EventLog()
        {
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingPlayMode) Clear();   // กัน probe/buffer ค้างข้าม session
            };
        }

        /// <summary>แปะ probe ลง object (ว่าง = ตัวที่เลือก) — คืน error หรือ null</summary>
        public static string Attach(string objectName)
        {
            var go = Resolve(objectName);
            if (go == null) return string.IsNullOrEmpty(objectName)
                ? "เลือก GameObject ใน Hierarchy ก่อน หรือส่ง objectName"
                : $"ไม่พบ object: {objectName}";

            if (go.GetComponent<MCPEventProbe>() != null) return null;   // มีแล้ว ไม่ถือเป็น error

            var p = go.AddComponent<MCPEventProbe>();
            p.hideFlags = HideFlags.DontSave;
            lock (_lock) _probes.Add(p);
            return null;
        }

        public static int ProbeCount { get { lock (_lock) return _probes.Count; } }

        public static void Push(string kind, string self, string other)
        {
            lock (_lock)
            {
                _buf.Add(new Ev { time = DateTime.Now.ToString("HH:mm:ss.fff"), kind = kind, self = self, other = other });
                if (_buf.Count > MAX) _buf.RemoveAt(0);
            }
        }

        /// <summary>JSON ของเหตุการณ์ล่าสุด (ใหม่สุดก่อน)</summary>
        public static string GetReport()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.Append($"{{\"isPlaying\":{Application.isPlaying.ToString().ToLower()},");
                sb.Append($"\"probes\":{_probes.Count},\"count\":{_buf.Count},\"events\":[");
                for (int i = _buf.Count - 1, n = 0; i >= 0 && n < 80; i--, n++)
                {
                    if (n > 0) sb.Append(",");
                    var e = _buf[i];
                    sb.Append($"{{\"time\":\"{e.time}\",\"kind\":\"{e.kind}\",");
                    sb.Append($"\"self\":\"{MCPHandlers.EscapeJsonPublic(e.self)}\",");
                    sb.Append($"\"other\":\"{MCPHandlers.EscapeJsonPublic(e.other)}\"}}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }

        /// <summary>ถอด probe ทั้งหมด + ล้าง buffer</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                foreach (var p in _probes)
                    if (p != null) UnityEngine.Object.DestroyImmediate(p);
                _probes.Clear();
                _buf.Clear();
            }
        }

        static GameObject Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return Selection.activeGameObject;
            var go = GameObject.Find(name);
            if (go != null) return go;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (t.gameObject.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return t.gameObject;
            return Selection.activeGameObject;
        }
    }

    /// <summary>probe ชั่วคราว — ดัก physics callback ส่งเข้า EventLog (editor debug เท่านั้น)</summary>
    [AddComponentMenu("")]   // ซ่อนจากเมนู Add Component
    public class MCPEventProbe : MonoBehaviour
    {
        void OnCollisionEnter(Collision c) => EventLog.Push("collisionEnter", name, c.gameObject.name);
        void OnCollisionExit(Collision c)  => EventLog.Push("collisionExit",  name, c.gameObject.name);
        void OnTriggerEnter(Collider c)    => EventLog.Push("triggerEnter",   name, c.name);
        void OnTriggerExit(Collider c)     => EventLog.Push("triggerExit",    name, c.name);
    }
}
