using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace DeltaUnity.MCP
{
    /// <summary>
    /// Index แบบ reverse: script GUID → prefab ที่ใช้ script นั้น (MonoBehaviour แปะอยู่)
    /// build บน background thread (อ่าน .prefab เป็น text หา guid) → ไม่แตะ AssetDatabase ตอน scan
    /// → ไม่ freeze main thread. lookup ตอน query เป็น O(1) (dictionary)
    /// ใช้ใน A2: ถาม @script เชิงวิเคราะห์ → บอกได้ว่า script นี้ถูกใช้บน prefab ไหน
    /// </summary>
    public static class PrefabIndex
    {
        public struct PrefabEntry { public string Name; public string Path; }

        // scriptGUID → list ของ prefab path (relative: Assets/...)
        static Dictionary<string, List<string>> _map;
        static List<PrefabEntry> _all;   // ทุก prefab (สำหรับ # autocomplete)
        static volatile bool _building;

        public static bool Ready => _map != null;
        public static bool Building => _building;

        // m_Script: {fileID: 11500000, guid: <32 hex>, type: 3}  ← reference ไปยัง MonoScript
        static readonly Regex _scriptRef =
            new Regex(@"m_Script:\s*\{fileID:\s*11500000,\s*guid:\s*([0-9a-f]{32})", RegexOptions.Compiled);

        /// <summary>build บน background thread — เรียกจาก main thread (เช่น OnEnable)</summary>
        public static void RefreshAsync()
        {
            if (_building) return;
            _building = true;
            string assetsPath = Application.dataPath;   // ต้องอ่านบน main thread ก่อน
            Task.Run(() => Build(assetsPath));
        }

        static void Build(string assetsPath)
        {
            try
            {
                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var all = new List<PrefabEntry>();
                foreach (var file in Directory.EnumerateFiles(assetsPath, "*.prefab", SearchOption.AllDirectories))
                {
                    string rel = "Assets" + file.Substring(assetsPath.Length).Replace('\\', '/');
                    all.Add(new PrefabEntry { Name = Path.GetFileNameWithoutExtension(file), Path = rel });

                    string text;
                    try { text = File.ReadAllText(file); }
                    catch { continue; }
                    foreach (Match m in _scriptRef.Matches(text))
                    {
                        string guid = m.Groups[1].Value;
                        if (!map.TryGetValue(guid, out var list)) { list = new List<string>(); map[guid] = list; }
                        if (!list.Contains(rel)) list.Add(rel);
                    }
                }
                _all = all.OrderBy(e => e.Name).ToList();
                _map = map;   // assign atomic (reference) — ปลอดภัยข้าม thread
            }
            catch { _map = new Dictionary<string, List<string>>(); }
            finally { _building = false; }
        }

        /// <summary>หา prefab ที่ใช้ script (ส่ง guid เข้ามา — AssetPathToGUID ต้องเรียกบน main thread ก่อน)</summary>
        public static List<string> PrefabsUsing(string scriptGuid)
        {
            if (_map == null || string.IsNullOrEmpty(scriptGuid)) return new List<string>();
            return _map.TryGetValue(scriptGuid, out var list) ? new List<string>(list) : new List<string>();
        }

        /// <summary>ค้นหา prefab ตามชื่อ (สำหรับ # autocomplete) คืน top N</summary>
        public static List<PrefabEntry> Search(string query, int max = 8)
        {
            if (_all == null) return new List<PrefabEntry>();
            if (string.IsNullOrEmpty(query)) return _all.Take(max).ToList();
            string q = query.ToLowerInvariant();
            return _all
                .Where(e => e.Name.ToLowerInvariant().Contains(q))
                .OrderBy(e => e.Name.ToLowerInvariant().IndexOf(q))
                .ThenBy(e => e.Name.Length)
                .Take(max)
                .ToList();
        }

        /// <summary>หา path ของ prefab จากชื่อ (#mention → path)</summary>
        public static string ResolvePath(string name)
        {
            if (_all == null || string.IsNullOrEmpty(name)) return null;
            var hit = _all.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrEmpty(hit.Path) ? null : hit.Path;
        }
    }
}
