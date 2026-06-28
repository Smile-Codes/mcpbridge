using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DeltaUnity.MCP
{
    /// <summary>
    /// RefactorAudit — สแกน .cs ทั้งโปรเจกต์ รายงาน refactoring opportunities
    /// ใช้ regex + brace-matching เท่านั้น (ไม่ต้องพึ่ง Roslyn / full compiler)
    /// </summary>
    public static class RefactorAudit
    {
        // ── Third-party folders ที่ข้าม (เหมือน IsThirdParty ใน MCPHandlers) ──
        static readonly string[] ThirdPartyParts =
        {
            "/Plugins/", "/ThirdParty/", "/PhotonFusion/", "/Photon/", "/PlayFab/",
            "/CBS/", "/TextMesh Pro/", "/Mirror/", "/PackageCache/", "/GPUInstancer/",
            "/ProBuilder", "/Polybrush", "/MeshBaker/", "/NuGet/", "/UnityMCP/",
            "/PlayFabSDK/", "/StandaloneFileBrowser/", "/Hexasphere/", "/WorldMapStrategyKit/",
        };

        static bool IsThirdParty(string path)
        {
            foreach (var p in ThirdPartyParts)
                if (path.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ── Regex patterns ──────────────────────────────────────────────────
        static readonly Regex RxClass = new Regex(
            @"(?:public|internal|private)?\s*(?:abstract|static|sealed)?\s*class\s+(\w+)\s*(?::\s*([\w\s,<>]+?))?(?:\s*where|\s*\{)",
            RegexOptions.Compiled);

        static readonly Regex RxMethod = new Regex(
            @"^\s*((?:(?:public|private|protected|internal|static|async|override|virtual|abstract|sealed|extern|unsafe)\s+)+)" +
            @"([\w<>\[\],\s]+?)\s+(\w+)\s*\(",
            RegexOptions.Compiled);

        static readonly Regex RxPublicField = new Regex(
            @"^\s*public\s+(?!const\s)(?!static\s+readonly\s)(?!event\s)(?!class\s)(?!enum\s)(?!struct\s)(?!interface\s)" +
            @"(?!void\s)(?!.*\()[\w<>\[\]\.]+\s+([a-z_]\w*)\s*[;=,]",
            RegexOptions.Compiled);

        static readonly Regex RxTodo = new Regex(
            @"//.*\b(TODO|FIXME|HACK)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static readonly Regex RxMagicNum = new Regex(
            @"(?<![.\d])(-?\d+\.?\d*)(?![\d.])", RegexOptions.Compiled);

        static readonly Regex RxUsing = new Regex(
            @"^\s*using\s+([\w\.]+)\s*;", RegexOptions.Compiled);

        static readonly Regex RxGetComponent = new Regex(
            @"GetComponent(?:InChildren|InParent)?\s*<\s*(\w+)\s*>", RegexOptions.Compiled);

        static readonly Regex RxNewType = new Regex(
            @"\bnew\s+([A-Z]\w*)\s*[<(]", RegexOptions.Compiled);

        static readonly Regex RxFieldType = new Regex(
            @"^\s*(?:private|protected|public|internal|static|readonly)?\s*(?:readonly\s+)?" +
            @"([A-Z]\w*(?:<[\w,\s<>]+>)?(?:\[\])*)\s+\w+\s*[;=,]",
            RegexOptions.Compiled);

        // ── Types ที่ exclude ออกจาก dependency ────────────────────────────
        static readonly HashSet<string> CommonTypes = new HashSet<string>
        {
            "MonoBehaviour","NetworkBehaviour","ScriptableObject","Object","Component",
            "List","Dictionary","HashSet","String","GameObject","Transform",
            "Vector3","Vector2","Quaternion","Bool","Int","Float","String",
            "bool","int","float","string","Action","Func","Debug","IEnumerator",
            "Task","Void","void","Enum","Struct","Interface","Abstract","Sealed",
            "Static","Readonly","Event","Const","Override","Virtual","Extern",
        };

        // ── Base classes ที่หยุด inheritance chain ───────────────────────────
        static readonly HashSet<string> StopBases = new HashSet<string>
        {
            "MonoBehaviour","NetworkBehaviour","ScriptableObject","Object","Component",
        };

        // ── Keyword ที่ false-positive เป็น method ────────────────────────────
        static readonly HashSet<string> SkipKeywords = new HashSet<string>
        {
            "if","while","for","foreach","switch","catch","using","lock","when",
            "else","return","new","typeof","sizeof","nameof","default","throw",
            "await","yield","select","where","orderby","group","join","from","into",
        };

        // ── CsFileInfo — ข้อมูลหนึ่งไฟล์ ──────────────────────────────────────
        class CsFileInfo
        {
            public string Path;
            public string RelPath;
            public string ClassName;
            public string BaseClass;
            public List<string> Interfaces = new List<string>();
            public bool IsMonoBehaviour;
            public bool IsAbstract;
            public bool IsStatic;
            public int LineCount;
            public int PublicFieldCount;
            public int TodoCount;
            public int MagicNumberCount;
            public int MaxNestingDepth;
            public int InheritanceDepth;
            public int FanIn;
            public int FanOut;
            public HashSet<string> Dependencies = new HashSet<string>(StringComparer.Ordinal);
            public List<CsMethodInfo> Methods = new List<CsMethodInfo>();
            public int Severity;
            public List<string> Issues = new List<string>();
        }

        class CsMethodInfo
        {
            public string Name;
            public int LineCount;
            public int BranchCount;
            public int MaxNesting;
            public bool IsUpdate;
        }

        // ── Main entry point ─────────────────────────────────────────────────
        public static string Analyze(int topN = 10, string dataPath = null)
        {
            try
            {
                if (string.IsNullOrEmpty(dataPath)) dataPath = Application.dataPath;

                // 1) ค้นหาไฟล์ .cs ทั้งหมด
                var csFiles = Directory
                    .GetFiles(dataPath, "*.cs", SearchOption.AllDirectories)
                    .Select(f => f.Replace("\\", "/"))
                    .Where(f =>
                    {
                        string rel = "Assets" + f.Substring(dataPath.Replace("\\","/").Length);
                        return !IsThirdParty(rel);
                    })
                    .ToList();

                if (csFiles.Count == 0)
                    return "{\"error\":\"No .cs files found under Assets/\"}";

                var files = new List<CsFileInfo>();

                // 2) Parse แต่ละไฟล์
                foreach (var absPath in csFiles)
                {
                    string relPath = "Assets" + absPath.Substring(dataPath.Replace("\\","/").Length);
                    try
                    {
                        string content = File.ReadAllText(absPath);
                        var fi = ParseFile(relPath, content);
                        if (fi != null) files.Add(fi);
                    }
                    catch { /* ข้ามไฟล์ที่อ่านไม่ได้ */ }
                }

                // 3) Cross-file analysis
                ComputeCrossFile(files);

                // 4) Score ทุกไฟล์
                foreach (var fi in files)
                    ScoreFile(fi);

                // 5) Build output
                return BuildOutput(files, topN);
            }
            catch (Exception e)
            {
                return $"{{\"error\":\"{MCPHandlers.EscapeJsonPublic(e.Message)}\"}}";
            }
        }

        // ── Parse ไฟล์เดี่ยว ────────────────────────────────────────────────
        static CsFileInfo ParseFile(string relPath, string content)
        {
            var lines = content.Split('\n');
            var fi = new CsFileInfo
            {
                RelPath = relPath,
                LineCount = lines.Length,
            };

            // 2a) Class name + base
            foreach (var line in lines)
            {
                var m = RxClass.Match(line);
                if (m.Success)
                {
                    fi.ClassName = m.Groups[1].Value;
                    string baseStr = m.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(baseStr))
                    {
                        var parts = baseStr.Split(',').Select(p => p.Trim().Split('<')[0].Trim()).ToArray();
                        fi.BaseClass = parts[0];
                        for (int i = 1; i < parts.Length; i++)
                            if (!string.IsNullOrEmpty(parts[i])) fi.Interfaces.Add(parts[i]);
                    }
                    // flags
                    if (fi.BaseClass == "MonoBehaviour" || fi.BaseClass == "NetworkBehaviour")
                        fi.IsMonoBehaviour = true;
                    fi.IsAbstract = line.Contains(" abstract ");
                    fi.IsStatic   = line.Contains(" static ") && line.Contains(" class ");
                    break;
                }
            }

            if (string.IsNullOrEmpty(fi.ClassName))
                fi.ClassName = Path.GetFileNameWithoutExtension(relPath);

            // 2b) Per-line metrics + dependency collection
            int publicFields = 0, todoCount = 0, magicCount = 0;
            int maxDepth = 0, curDepth = 0;
            bool inBlockComment = false;

            foreach (var rawLine in lines)
            {
                string line = rawLine;

                // Block comment tracking
                if (inBlockComment)
                {
                    if (line.Contains("*/")) inBlockComment = false;
                    continue;
                }
                if (line.Contains("/*") && !line.Contains("*/"))
                {
                    inBlockComment = true;
                }

                // Strip inline comment for brace counting
                string stripped = StripLineComment(line);

                // Brace-based nesting
                foreach (char ch in stripped)
                {
                    if (ch == '{') { curDepth++; if (curDepth > maxDepth) maxDepth = curDepth; }
                    else if (ch == '}') { curDepth = Math.Max(0, curDepth - 1); }
                }

                // TODO/FIXME/HACK
                if (RxTodo.IsMatch(line)) todoCount++;

                // Public fields
                if (RxPublicField.IsMatch(line)) publicFields++;

                // Magic numbers (cap 99)
                if (magicCount < 99)
                {
                    foreach (Match mm in RxMagicNum.Matches(stripped))
                    {
                        if (mm.Groups[1].Value == "0" || mm.Groups[1].Value == "1" ||
                            mm.Groups[1].Value == "-1" || mm.Groups[1].Value == "2") continue;
                        // ข้ามในกลุ่ม string literal (ไม่สมบูรณ์แบบ แต่ดีพอ)
                        magicCount++;
                        if (magicCount >= 99) break;
                    }
                }

                // Dependencies — using
                var um = RxUsing.Match(line);
                if (um.Success)
                {
                    string ns = um.Groups[1].Value;
                    string last = ns.Split('.').Last();
                    if (!CommonTypes.Contains(last)) fi.Dependencies.Add(last);
                }

                // GetComponent<Type>
                foreach (Match gm in RxGetComponent.Matches(line))
                {
                    string t = gm.Groups[1].Value;
                    if (!CommonTypes.Contains(t) && t != fi.ClassName) fi.Dependencies.Add(t);
                }

                // new Type(  / new Type<
                foreach (Match nm in RxNewType.Matches(line))
                {
                    string t = nm.Groups[1].Value;
                    if (!CommonTypes.Contains(t) && t != fi.ClassName) fi.Dependencies.Add(t);
                }

                // Field type declarations
                var fm = RxFieldType.Match(line);
                if (fm.Success)
                {
                    string t = fm.Groups[1].Value.Split('<')[0].Trim();
                    if (!CommonTypes.Contains(t) && t != fi.ClassName && t.Length > 1)
                        fi.Dependencies.Add(t);
                }
            }

            fi.PublicFieldCount = publicFields;
            fi.TodoCount = todoCount;
            fi.MagicNumberCount = Math.Min(magicCount, 99);
            fi.MaxNestingDepth = maxDepth;

            // 2c) Method extraction
            fi.Methods = ExtractMethods(lines);

            return fi;
        }

        // ── Strip single-line comment (// ...) without touching string literals ──
        static string StripLineComment(string line)
        {
            bool inString = false; char strChar = '"';
            for (int i = 0; i < line.Length - 1; i++)
            {
                char c = line[i];
                if (!inString && (c == '"' || c == '\'')) { inString = true; strChar = c; }
                else if (inString && c == '\\') { i++; continue; }
                else if (inString && c == strChar) { inString = false; }
                else if (!inString && c == '/' && line[i + 1] == '/') return line.Substring(0, i);
            }
            return line;
        }

        // ── Method extraction (brace-matched) ────────────────────────────────
        static List<CsMethodInfo> ExtractMethods(string[] lines)
        {
            var methods = new List<CsMethodInfo>();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                var m = RxMethod.Match(line);
                if (!m.Success) continue;

                string methodName = m.Groups[3].Value;
                if (SkipKeywords.Contains(methodName)) continue;

                // ต้องมี { ในบรรทัดเดียวกันหรือถัดไป (กัน abstract/interface declaration)
                int braceStart = -1;
                for (int look = i; look <= Math.Min(i + 3, lines.Length - 1); look++)
                {
                    if (lines[look].Contains(";") && !lines[look].Contains("{")) break; // abstract/interface
                    if (lines[look].Contains("{")) { braceStart = look; break; }
                }
                if (braceStart < 0) continue;

                // Brace-match จาก braceStart
                int depth = 0; bool started = false;
                int startLine = braceStart;
                int endLine = startLine;
                int branchCount = 0;
                int maxNest = 0; int curNest = 0;

                for (int j = startLine; j < Math.Min(startLine + 400, lines.Length); j++)
                {
                    string ml = StripLineComment(lines[j]);
                    foreach (char ch in ml)
                    {
                        if (ch == '{') { depth++; curNest++; started = true; if (curNest > maxNest) maxNest = curNest; }
                        else if (ch == '}') { depth--; curNest = Math.Max(0, curNest - 1); }
                    }

                    // Count branches
                    branchCount += CountBranches(ml);

                    if (started && depth <= 0) { endLine = j; break; }
                    if (j == startLine + 399) { endLine = j; }
                }

                bool isUpdate = methodName == "Update" || methodName == "FixedUpdate" || methodName == "LateUpdate";

                methods.Add(new CsMethodInfo
                {
                    Name = methodName,
                    LineCount = endLine - i + 1,
                    BranchCount = branchCount,
                    MaxNesting = maxNest,
                    IsUpdate = isUpdate,
                });
            }
            return methods;
        }

        static int CountBranches(string line)
        {
            int n = 0;
            // if / else / for / foreach / while / case / catch / ternary ? / && / ||
            n += Regex.Matches(line, @"\bif\b").Count;
            n += Regex.Matches(line, @"\belse\b").Count;
            n += Regex.Matches(line, @"\bfor\b").Count;
            n += Regex.Matches(line, @"\bforeach\b").Count;
            n += Regex.Matches(line, @"\bwhile\b").Count;
            n += Regex.Matches(line, @"\bcase\b").Count;
            n += Regex.Matches(line, @"\bcatch\b").Count;
            // ternary ? (ไม่นับ ?. / ?? / generic type parameter)
            n += Regex.Matches(line, @"(?<![?<>\w])\?(?![?.\w])").Count;
            n += Regex.Matches(line, @"&&").Count;
            n += Regex.Matches(line, @"\|\|").Count;
            return n;
        }

        // ── Cross-file analysis ─────────────────────────────────────────────
        static void ComputeCrossFile(List<CsFileInfo> files)
        {
            // Build class → CsFileInfo map
            var classMap = new Dictionary<string, CsFileInfo>(StringComparer.Ordinal);
            foreach (var fi in files)
                if (!string.IsNullOrEmpty(fi.ClassName))
                    classMap[fi.ClassName] = fi;

            // Inheritance depth
            foreach (var fi in files)
                fi.InheritanceDepth = ComputeDepth(fi, classMap, new HashSet<string>(), 0);

            // Fan-in: count ไฟล์อื่นๆ ที่ reference class นี้
            foreach (var fi in files)
            {
                int fanIn = 0;
                foreach (var other in files)
                {
                    if (other == fi) continue;
                    if (other.Dependencies.Contains(fi.ClassName)) fanIn++;
                }
                fi.FanIn = fanIn;
            }

            // Fan-out: จำนวน unique external types
            foreach (var fi in files)
                fi.FanOut = fi.Dependencies.Count;
        }

        static int ComputeDepth(CsFileInfo fi, Dictionary<string, CsFileInfo> map, HashSet<string> visited, int current)
        {
            if (current >= 10) return current;
            if (string.IsNullOrEmpty(fi.BaseClass)) return current;
            if (StopBases.Contains(fi.BaseClass)) return current + 1;
            if (visited.Contains(fi.ClassName)) return current; // circular
            visited.Add(fi.ClassName);
            if (map.TryGetValue(fi.BaseClass, out var parent))
                return ComputeDepth(parent, map, visited, current + 1);
            return current + 1;
        }

        // ── Severity scoring ─────────────────────────────────────────────────
        static void ScoreFile(CsFileInfo fi)
        {
            int score = 0;

            // Line count
            if (fi.LineCount > 800) score += 30;
            else if (fi.LineCount > 500) score += 15;
            else if (fi.LineCount > 300) score += 5;

            // Methods
            foreach (var m in fi.Methods)
            {
                if (m.LineCount > 100) score += 20;
                else if (m.LineCount > 50) score += 10;

                if (m.BranchCount > 15) score += 15;
                else if (m.BranchCount > 10) score += 8;

                if (m.IsUpdate && m.LineCount > 20) score += 12;
            }

            // Public fields
            if (fi.PublicFieldCount > 10) score += 15;
            else if (fi.PublicFieldCount > 5) score += 8;

            // Coupling
            if (fi.FanOut > 15) score += 15;
            else if (fi.FanOut > 10) score += 8;

            if (fi.FanIn > 10) score += 12;

            // Inheritance
            if (fi.InheritanceDepth > 4) score += 10;

            // TODO
            if (fi.TodoCount > 5) score += 5;

            // Nesting
            if (fi.MaxNestingDepth > 6) score += 8;

            fi.Severity = score;

            // Build issues list
            BuildIssues(fi);
        }

        static void BuildIssues(CsFileInfo fi)
        {
            var issues = new List<string>();

            // Large class
            if (fi.LineCount > 500)
                issues.Add($"Large class: {fi.LineCount} lines — แบ่งออกเป็น class ย่อย");

            // Long methods (top 3 by length)
            var longMethods = fi.Methods.Where(m => m.LineCount > 50)
                .OrderByDescending(m => m.LineCount).Take(3);
            foreach (var m in longMethods)
                issues.Add($"Long method: {m.Name}() {m.LineCount} lines — แตกเป็น method ย่อย");

            // High complexity (top 3 by branch count)
            var complexMethods = fi.Methods.Where(m => m.BranchCount > 10)
                .OrderByDescending(m => m.BranchCount).Take(3);
            foreach (var m in complexMethods)
                issues.Add($"High complexity: {m.Name}() ~{m.BranchCount} branches — ยาก maintain/test");

            // Update methods
            var updateMethods = fi.Methods.Where(m => m.IsUpdate && m.LineCount > 20);
            foreach (var m in updateMethods)
                issues.Add($"Update()/FixedUpdate() {m.LineCount} lines — ควร < 20 lines ใน game loop");

            // Public fields
            if (fi.PublicFieldCount > 5)
                issues.Add($"Public fields: {fi.PublicFieldCount} ตัว — ใช้ [SerializeField] private แทน");

            // Magic numbers
            if (fi.MagicNumberCount > 3)
                issues.Add($"Magic numbers: {fi.MagicNumberCount} จุด — ใช้ const หรือ ScriptableObject แทน");

            // Deep nesting
            if (fi.MaxNestingDepth > 5)
                issues.Add($"Deep nesting: {fi.MaxNestingDepth} levels — ใช้ early return / guard clause");

            // Inheritance depth
            if (fi.InheritanceDepth > 4)
                issues.Add($"Inheritance depth: {fi.InheritanceDepth} — ลอง composition over inheritance");

            // No interface
            if (fi.IsMonoBehaviour && fi.Interfaces.Count == 0 && fi.LineCount > 200)
                issues.Add("ไม่มี interface — ยาก mock/test/swap");

            // Technical debt
            if (fi.TodoCount > 0)
                issues.Add($"Technical debt: {fi.TodoCount} TODO/FIXME/HACK");

            fi.Issues = issues;
        }

        // ── Build JSON output ─────────────────────────────────────────────────
        static string BuildOutput(List<CsFileInfo> files, int topN)
        {
            // Summary stats
            int filesOver500 = files.Count(f => f.LineCount > 500);
            int methodsOver50 = files.Sum(f => f.Methods.Count(m => m.LineCount > 50));
            int totalTodo = files.Sum(f => f.TodoCount);
            double avgBranch = files.Count > 0 && files.Any(f => f.Methods.Count > 0)
                ? files.SelectMany(f => f.Methods).DefaultIfEmpty().Average(m => m == null ? 0 : m.BranchCount)
                : 0;
            int highCoupling = files.Count(f => f.FanOut > 10 || f.FanIn > 10);

            // Top offenders by severity
            var topOffenders = files
                .Where(f => f.Severity > 0)
                .OrderByDescending(f => f.Severity)
                .Take(topN)
                .ToList();

            // Coupling hotspots
            var highFanIn = files.Where(f => f.FanIn > 5)
                .OrderByDescending(f => f.FanIn).Take(10).ToList();
            var highFanOut = files.Where(f => f.FanOut > 10)
                .OrderByDescending(f => f.FanOut).Take(10).ToList();

            // Structural issues
            var structural = files
                .Where(f => f.InheritanceDepth > 3 || (f.IsMonoBehaviour && f.Interfaces.Count == 0 && f.LineCount > 200))
                .Take(20).ToList();

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"scanned\":{files.Count},");

            // Summary
            sb.Append("\"summary\":{");
            sb.Append($"\"filesOver500Lines\":{filesOver500},");
            sb.Append($"\"methodsOver50Lines\":{methodsOver50},");
            sb.Append($"\"todoCount\":{totalTodo},");
            sb.Append($"\"avgBranchCount\":{avgBranch:F1},");
            sb.Append($"\"highCouplingFiles\":{highCoupling}");
            sb.Append("},");

            // Top offenders
            sb.Append("\"topOffenders\":[");
            for (int i = 0; i < topOffenders.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var fi = topOffenders[i];
                string severity = fi.Severity >= 50 ? "high" : fi.Severity >= 25 ? "medium" : "low";

                // Top 2 deps by fan-in (proxy for most-used)
                var topDeps = fi.Dependencies
                    .Take(10)
                    .Select(d => $"\"{EJ(d)}\"");

                sb.Append("{");
                sb.Append($"\"file\":\"{EJ(fi.RelPath)}\",");
                sb.Append($"\"class\":\"{EJ(fi.ClassName)}\",");
                sb.Append($"\"lines\":{fi.LineCount},");
                sb.Append($"\"severity\":\"{severity}\",");
                sb.Append($"\"score\":{fi.Severity},");
                sb.Append("\"issues\":[");
                for (int j = 0; j < fi.Issues.Count; j++)
                {
                    if (j > 0) sb.Append(",");
                    sb.Append($"\"{EJ(fi.Issues[j])}\"");
                }
                sb.Append("],");
                sb.Append($"\"coupling\":{{\"fanIn\":{fi.FanIn},\"fanOut\":{fi.FanOut},\"topDeps\":[{string.Join(",", topDeps)}]}},");
                sb.Append($"\"structure\":{{\"inheritanceDepth\":{fi.InheritanceDepth},\"interfaces\":{fi.Interfaces.Count},\"isMonoBehaviour\":{fi.IsMonoBehaviour.ToString().ToLower()}}}");
                sb.Append("}");
            }
            sb.Append("],");

            // Coupling hotspots
            sb.Append("\"couplingHotspots\":{");
            sb.Append("\"highFanIn\":[");
            for (int i = 0; i < highFanIn.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var fi = highFanIn[i];
                sb.Append($"{{\"class\":\"{EJ(fi.ClassName)}\",\"fanIn\":{fi.FanIn},\"note\":\"{fi.FanIn} files depend — เปลี่ยนระวัง\"}}");
            }
            sb.Append("],");
            sb.Append("\"highFanOut\":[");
            for (int i = 0; i < highFanOut.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var fi = highFanOut[i];
                sb.Append($"{{\"class\":\"{EJ(fi.ClassName)}\",\"fanOut\":{fi.FanOut},\"note\":\"depend on {fi.FanOut} types — coupled มาก\"}}");
            }
            sb.Append("]},");

            // Structural issues
            sb.Append("\"structuralIssues\":[");
            bool firstSI = true;
            foreach (var fi in structural)
            {
                var siIssues = new List<string>();
                if (fi.InheritanceDepth > 3)
                    siIssues.Add($"inheritance depth {fi.InheritanceDepth} — ลอง composition");
                if (fi.IsMonoBehaviour && fi.Interfaces.Count == 0 && fi.LineCount > 200)
                    siIssues.Add("ไม่มี interface — ยาก mock/test");
                if (siIssues.Count == 0) continue;

                if (!firstSI) sb.Append(",");
                firstSI = false;
                sb.Append($"{{\"class\":\"{EJ(fi.ClassName)}\",\"issues\":[");
                for (int j = 0; j < siIssues.Count; j++)
                {
                    if (j > 0) sb.Append(",");
                    sb.Append($"\"{EJ(siIssues[j])}\"");
                }
                sb.Append("]}");
            }
            sb.Append("]");
            sb.Append("}");

            return sb.ToString();
        }

        // ── EscapeJson alias (ใช้ EscapeJsonPublic ใน MCPHandlers) ──────────
        static string EJ(string s) => MCPHandlers.EscapeJsonPublic(s);
    }
}
