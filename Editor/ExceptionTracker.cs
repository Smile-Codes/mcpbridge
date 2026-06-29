using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// ExceptionTracker — บันทึก Exception/Error ที่เกิดขึ้น (rolling buffer 50 entries)
    /// dedup ถ้า message เดิมมาอีกภายใน 60s — ใช้วิเคราะห์ bug โดย AI
    /// </summary>
    [InitializeOnLoad]
    public static class ExceptionTracker
    {
        class Entry
        {
            public string message;
            public string stackTrace;
            public string logType;
            public DateTime firstSeen;
            public DateTime lastSeen;
            public int count;
        }

        static readonly List<Entry> _buffer = new List<Entry>();
        static readonly object _lock = new object();
        const int MAX_BUFFER = 50;
        const double DEDUP_SECONDS = 60;

        static ExceptionTracker()
        {
            Application.logMessageReceived += OnLog;
        }

        static void OnLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error) return;

            lock (_lock)
            {
                var now = DateTime.Now;
                // ตรวจ dedup — message เดิมภายใน 60s → increment count เท่านั้น
                foreach (var e in _buffer)
                {
                    if (e.message == message && (now - e.lastSeen).TotalSeconds <= DEDUP_SECONDS)
                    {
                        e.count++;
                        e.lastSeen = now;
                        return;
                    }
                }

                // entry ใหม่
                var entry = new Entry
                {
                    message = message,
                    stackTrace = stackTrace ?? "",
                    logType = type.ToString(),
                    firstSeen = now,
                    lastSeen = now,
                    count = 1
                };
                _buffer.Add(entry);
                if (_buffer.Count > MAX_BUFFER) _buffer.RemoveAt(0);
            }
        }

        // ── Extract helpers ───────────────────────────────────────────────────

        // ดึง type จาก message เช่น "NullReferenceException: Object ref..." → "NullReferenceException"
        static string ExtractType(string message)
        {
            if (string.IsNullOrEmpty(message)) return "Unknown";
            int colon = message.IndexOf(':');
            int space  = message.IndexOf(' ');
            int cut = colon >= 0 && (space < 0 || colon < space) ? colon : space;
            return cut > 0 ? message.Substring(0, cut) : message;
        }

        // ดึงบรรทัดแรกจาก stack trace ที่มี "Assets/"
        static string ExtractFirstLine(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            foreach (var line in stack.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Contains("Assets/")) return trimmed;
            }
            // fallback: บรรทัดแรก
            string first = stack.Split('\n')[0].Trim();
            return first.Length > 100 ? first.Substring(0, 100) + "…" : first;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>JSON ของ exceptions ล่าสุด เรียงตาม lastSeen desc</summary>
        public static string GetReport(int max = 20)
        {
            lock (_lock)
            {
                // เรียงตาม lastSeen ล่าสุดก่อน
                var sorted = new List<Entry>(_buffer);
                sorted.Sort((a, b) => b.lastSeen.CompareTo(a.lastSeen));

                int take = Math.Min(max, sorted.Count);
                var sb = new StringBuilder();
                sb.Append($"{{\"count\":{sorted.Count},\"exceptions\":[");

                for (int i = 0; i < take; i++)
                {
                    if (i > 0) sb.Append(",");
                    var e = sorted[i];
                    string exType   = ExtractType(e.message);
                    string firstLine = ExtractFirstLine(e.stackTrace);
                    // จำกัด stack ไม่ให้ยาวเกิน
                    string stack = e.stackTrace.Length > 400 ? e.stackTrace.Substring(0, 400) + "…" : e.stackTrace;

                    sb.Append("{");
                    sb.Append($"\"type\":\"{MCPHandlers.EscapeJsonPublic(exType)}\",");
                    sb.Append($"\"message\":\"{MCPHandlers.EscapeJsonPublic(e.message)}\",");
                    sb.Append($"\"firstLine\":\"{MCPHandlers.EscapeJsonPublic(firstLine)}\",");
                    sb.Append($"\"stack\":\"{MCPHandlers.EscapeJsonPublic(stack)}\",");
                    sb.Append($"\"logType\":\"{e.logType}\",");
                    sb.Append($"\"count\":{e.count},");
                    sb.Append($"\"lastSeen\":\"{e.lastSeen:HH:mm:ss}\"");
                    sb.Append("}");
                }

                sb.Append("]}");
                return sb.ToString();
            }
        }

        /// <summary>ล้าง buffer ทั้งหมด</summary>
        public static void Clear()
        {
            lock (_lock) { _buffer.Clear(); }
        }
    }
}
