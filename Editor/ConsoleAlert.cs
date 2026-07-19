using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// console_alert — ดัก Debug.Log/Warning/Error ที่ตรง pattern (substring, ไม่สน case)
    /// นับจำนวน + เก็บ 10 ข้อความล่าสุด. เหมือน watch_alert แต่กับ console.
    /// ใช้จับ error/warning ที่หลุดมาเป็นช่วงตอนเล่น (ที่ read_console อาจพลาดเพราะเลื่อนหาย).
    /// </summary>
    [InitializeOnLoad]
    public static class ConsoleAlert
    {
        class Pat
        {
            public string pattern;
            public string level;       // all | warning | error
            public int count;
            public readonly List<string> recent = new List<string>();   // 10 ข้อความล่าสุดที่ตรง
        }

        static readonly List<Pat> _pats = new List<Pat>();
        static readonly object _lock = new object();
        const int RECENT_MAX = 10;

        static ConsoleAlert()
        {
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            // ล้างตอนเข้า Play ใหม่ (เริ่มนับสด)
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.EnteredPlayMode)
                    lock (_lock) foreach (var p in _pats) { p.count = 0; p.recent.Clear(); }
            };
        }

        static void OnLog(string msg, string stack, LogType type)
        {
            if (string.IsNullOrEmpty(msg)) return;
            bool isErr = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
            bool isWarn = type == LogType.Warning;
            lock (_lock)
            {
                foreach (var p in _pats)
                {
                    if (p.level == "error" && !isErr) continue;
                    if (p.level == "warning" && !isErr && !isWarn) continue;
                    if (msg.IndexOf(p.pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    p.count++;
                    p.recent.Add($"[{type}] {(msg.Length > 200 ? msg.Substring(0, 200) + "…" : msg)}");
                    if (p.recent.Count > RECENT_MAX) p.recent.RemoveAt(0);
                }
            }
        }

        public static string Add(string pattern, string level)
        {
            level = (level ?? "all").Trim().ToLowerInvariant();
            if (level != "all" && level != "warning" && level != "error") level = "all";
            lock (_lock)
            {
                foreach (var p in _pats)
                    if (p.pattern == pattern) { p.level = level; return null; }   // อัปเดต level เดิม
                _pats.Add(new Pat { pattern = pattern, level = level });
            }
            return null;
        }

        public static string GetReport()
        {
            lock (_lock)
            {
                var sb = new StringBuilder($"{{\"isPlaying\":{Application.isPlaying.ToString().ToLower()},\"alerts\":[");
                for (int i = 0; i < _pats.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var p = _pats[i];
                    sb.Append($"{{\"pattern\":\"{MCPHandlers.EscapeJsonPublic(p.pattern)}\",\"level\":\"{p.level}\",\"count\":{p.count},\"recent\":[");
                    for (int r = 0; r < p.recent.Count; r++)
                    {
                        if (r > 0) sb.Append(",");
                        sb.Append($"\"{MCPHandlers.EscapeJsonPublic(p.recent[r])}\"");
                    }
                    sb.Append("]}");
                }
                return sb.Append("]}").ToString();
            }
        }

        public static void Clear()
        {
            lock (_lock) _pats.Clear();
        }
    }
}
