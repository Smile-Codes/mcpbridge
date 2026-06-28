using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DeltaUnity.MCP
{
    /// <summary>
    /// Index ไฟล์ .cs ทั้งหมดในโปรเจกต์ สำหรับฟีเจอร์ @mention ในช่องแชต
    /// ใช้แนบเนื้อหา script ให้ Claude วิเคราะห์/แก้บั๊ก
    /// </summary>
    public static class CodebaseIndex
    {
        public struct ScriptEntry
        {
            public string Name;      // ชื่อไฟล์ เช่น PlayerHealth.cs
            public string Path;      // path เต็ม เช่น Assets/GameScripts/PlayerHealth.cs
        }

        static List<ScriptEntry> _cache;
        static Dictionary<string, string> _byName;   // "bush.cs" → path (lookup เร็ว O(1) สำหรับ resolve dependency)
        // type จริง → ทุก path ที่ประกาศ type นั้น (รองรับ partial class แตกหลายไฟล์
        //   เช่น NetworkTrait = NetworkTrait.cs + NetworkStat.cs + NetworkBonus.cs + ...)
        // build แบบ lazy ครั้งแรกที่ resolve dependency (อ่าน declaration ในทุก .cs)
        static Dictionary<string, List<string>> _byType;

        // อ่านทั้ง Assets (ครอบคลุม script นอก GameScripts ด้วย) แต่ตัด Packages/third-party ออก
        static readonly string[] IncludeRoots = { "Assets" };

        // ตัด third-party folder ใน Assets ที่ไม่ใช่ script ของเรา (ลด noise)
        static readonly string[] ExcludeContains =
        {
            "/UnityMCP/", "/PlayFabSDK/", "/Photon/", "/CBS/", "/GPUInstancer/",
            "/Plugins/", "/MeshBaker/", "/ProBuilder", "/Polybrush", "/TextMesh Pro/",
            "/NuGet/", "/StandaloneFileBrowser/", "/Hexasphere/", "/WorldMapStrategyKit/",
        };

        public static void Refresh()
        {
            _cache = new List<ScriptEntry>();
            _byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _byType = null;   // rebuild type-index ใหม่ครั้งหน้าที่ resolve
            // จำกัด search ใน folder ที่กำหนด — เร็วและไม่มี noise จาก Packages
            foreach (var guid in AssetDatabase.FindAssets("t:MonoScript", IncludeRoots))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsExcluded(path)) continue; // ข้าม third-party / tool เอง
                string fileName = System.IO.Path.GetFileName(path);
                _cache.Add(new ScriptEntry { Name = fileName, Path = path });
                if (!_byName.ContainsKey(fileName)) _byName[fileName] = path;   // first-wins (กันชื่อซ้ำคนละโฟลเดอร์)
            }
            _cache = _cache.OrderBy(e => e.Name).ToList();
        }

        static bool IsExcluded(string path)
        {
            foreach (var ex in ExcludeContains)
                if (path.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>ค้นหา script ตาม query (fuzzy เบื้องต้น) คืน top N</summary>
        public static List<ScriptEntry> Search(string query, int max = 8)
        {
            if (_cache == null) Refresh();
            if (string.IsNullOrEmpty(query))
                return _cache.Take(max).ToList();

            string q = query.ToLowerInvariant();
            return _cache
                .Where(e => e.Name.ToLowerInvariant().Contains(q))
                .OrderBy(e => e.Name.ToLowerInvariant().IndexOf(q))  // match ต้นชื่อมาก่อน
                .ThenBy(e => e.Name.Length)
                .Take(max)
                .ToList();
        }

        /// <summary>หา path จากชื่อไฟล์ (มี/ไม่มี .cs ก็ได้)</summary>
        public static string ResolvePath(string nameOrFile)
        {
            if (_cache == null) Refresh();
            // @mention อาจมี path นำหน้า (GameScripts/Bush.cs) → เอาเฉพาะชื่อไฟล์
            string fileOnly = System.IO.Path.GetFileName(nameOrFile);
            string n = fileOnly.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? fileOnly : fileOnly + ".cs";
            return _byName.TryGetValue(n, out string path) ? path : null;
        }

        // ── type-index: scan declaration จริงในทุก .cs → map ชื่อ type → path ──
        // (lazy build ครั้งแรก) — แก้ปัญหา "type อยู่ในไฟล์ชื่อไม่ตรง" เช่น INetworkActor ใน NetworkActor.cs
        static readonly Regex _declRe = new Regex(@"\b(?:class|interface|struct|enum)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

        static void EnsureTypeIndex()
        {
            if (_byType != null) return;
            if (_cache == null) Refresh();
            _byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // type C# case-sensitive
            foreach (var e in _cache)
            {
                string content = ReadContent(e.Path, 100000);
                if (content == null) continue;
                string code = StripCommentsAndStrings(content);
                foreach (Match m in _declRe.Matches(code))
                {
                    string t = m.Groups[1].Value;
                    if (!_byType.TryGetValue(t, out var paths)) { paths = new List<string>(); _byType[t] = paths; }
                    if (!paths.Contains(e.Path)) paths.Add(e.Path);   // partial class → เก็บทุกไฟล์
                }
            }
        }

        // ตัด comment + string ออก → ลด identifier ปลอมจากในคอมเมนต์/ข้อความ (over-include)
        static string StripCommentsAndStrings(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Regex.Replace(s, @"""[^""\r\n]*""", " ");   // string "..." (บรรทัดเดียว — พอสำหรับลด noise)
            s = Regex.Replace(s, @"/\*[\s\S]*?\*/", " ");   // block comment
            s = Regex.Replace(s, @"//.*", " ");             // line comment ถึงท้ายบรรทัด
            return s;
        }

        /// <summary>หา path เดียวของ type (partial ตัวแรก) — ใช้ declaration จริงก่อน แล้ว fallback ชื่อไฟล์</summary>
        public static string ResolveTypePath(string typeName)
        {
            var all = ResolveTypePaths(typeName);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>หา path "ทุกไฟล์" ของ type — รองรับ partial class แตกหลายไฟล์
        /// (เช่น NetworkTrait = 6 ไฟล์: Hp อยู่ NetworkStat.cs, ApplyStat อยู่ NetworkTrait.cs)</summary>
        public static List<string> ResolveTypePaths(string typeName)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(typeName)) return result;
            EnsureTypeIndex();
            if (_byType.TryGetValue(typeName, out var paths)) result.AddRange(paths);        // type จริง (ทุก partial)
            else if (_byName.TryGetValue(typeName + ".cs", out string p2)) result.Add(p2);   // fallback ชื่อไฟล์
            return result;
        }

        /// <summary>
        /// หา .cs ในโปรเจกต์ที่ source code อ้างถึง (dependency) — ลึก 1 ชั้น
        /// resolve ด้วย type-index จริง (ไม่เดาจากชื่อไฟล์) + ตัด comment/string กัน false dep
        /// </summary>
        public static List<ScriptEntry> ResolveReferencedScripts(string source, string selfPath, int max = 6)
        {
            var result = new List<ScriptEntry>();
            if (_cache == null) Refresh();
            if (string.IsNullOrEmpty(source)) return result;
            EnsureTypeIndex();

            string code = StripCommentsAndStrings(source);   // ตัด comment/string ก่อน → ไม่ดึง type จากในนั้น
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // candidate = identifier ขึ้นต้นตัวพิมพ์ใหญ่ ยาว >=3 (PascalCase type)
            foreach (Match m in Regex.Matches(code, @"\b[A-Z][A-Za-z0-9_]{2,}\b"))
            {
                string name = m.Value;
                if (!seen.Add(name)) continue;
                // ดึง "ทุกไฟล์" ของ type นี้ — partial class แตกหลายไฟล์ต้องเอามาครบ
                // (ไม่งั้น เช่น NetworkTrait จะได้แค่ partial แรก ที่อาจไม่มี Hp/ApplyStat)
                var paths = ResolveTypePaths(name);
                if (paths.Count == 0) continue;          // ไม่ใช่ type ของเรา (Unity/System) → ข้าม
                foreach (var path in paths)
                {
                    if (string.Equals(path, selfPath, StringComparison.OrdinalIgnoreCase)) continue;  // ข้ามตัวเอง
                    if (!seenPath.Add(path)) continue;   // กันไฟล์ซ้ำ
                    result.Add(new ScriptEntry { Name = System.IO.Path.GetFileName(path), Path = path });
                    if (result.Count >= max) break;
                }
                if (result.Count >= max) break;
            }
            return result;
        }

        /// <summary>
        /// หา base class / interface จาก declaration — `class X : Base, IFoo` → ["Base","IFoo"]
        /// (Smart 1: ตามสาย inheritance เพื่อให้ AI เห็นว่า member/property มาจาก base/interface ไหน)
        /// </summary>
        public static List<string> ResolveBaseTypes(string source)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(source)) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                source, @"(?:class|interface|struct)\s+[A-Za-z_]\w*(?:<[^>]*>)?\s*:\s*([^\{\r\n]+)"))
            {
                string baseList = m.Groups[1].Value;
                int w = baseList.IndexOf(" where ", StringComparison.Ordinal);   // ตัด generic constraint ออก
                if (w >= 0) baseList = baseList.Substring(0, w);
                foreach (var raw in baseList.Split(','))
                {
                    string name = raw.Trim();
                    int lt = name.IndexOf('<'); if (lt >= 0) name = name.Substring(0, lt);          // ตัด generic <...>
                    int dot = name.LastIndexOf('.'); if (dot >= 0) name = name.Substring(dot + 1);   // ตัด namespace prefix
                    name = name.Trim();
                    if (name.Length >= 2 && char.IsUpper(name[0]) && seen.Add(name))
                        result.Add(name);
                }
            }
            return result;
        }

        /// <summary>อ่านเนื้อหาไฟล์ (จำกัดขนาดกัน token บานปลาย)</summary>
        public static string ReadContent(string path, int maxChars = 16000)
        {
            try
            {
                string full = Path.Combine(Application.dataPath.Replace("Assets", ""), path);
                string text = File.ReadAllText(full);
                if (text.Length > maxChars)
                    text = text.Substring(0, maxChars) + "\n... (truncated)";
                return text;
            }
            catch { return null; }
        }
    }
}
