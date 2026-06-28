using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DeltaUnity.MCP
{
    /// <summary>
    /// สแกนสกิล/slash-command ที่มีในเครื่อง สำหรับ '/' picker ในแชต (โหมด Subscription/CLI)
    /// อ่านจาก .claude/skills/*/SKILL.md และ .claude/commands/*.md ของ project + Delta-Project + user home
    /// </summary>
    public static class SkillIndex
    {
        public struct SkillEntry
        {
            public string Name;
            public string Description;
            public string Source;   // project / delta-project / user
        }

        static List<SkillEntry> _cache;

        public static void Refresh()
        {
            _cache = new List<SkillEntry>();
            string projectRoot = Application.dataPath.Replace("/Assets", "");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var roots = new (string path, string tag)[]
            {
                (projectRoot, "project"),
                (Path.Combine(projectRoot, "..", "Delta-Project"), "delta-project"),
                (home, "user"),
            };

            var seen = new HashSet<string>();
            foreach (var (root, tag) in roots)
            {
                ScanSkills(Path.Combine(root, ".claude", "skills"), tag, seen);
                ScanCommands(Path.Combine(root, ".claude", "commands"), tag, seen);
            }
            _cache = _cache.OrderBy(e => e.Name).ToList();
        }

        static void ScanSkills(string dir, string tag, HashSet<string> seen)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                string skillFile = Path.Combine(sub, "SKILL.md");
                if (!File.Exists(skillFile)) continue;
                var (name, desc) = ParseFrontmatter(skillFile, Path.GetFileName(sub));
                if (seen.Add(name))
                    _cache.Add(new SkillEntry { Name = name, Description = desc, Source = tag });
            }
        }

        static void ScanCommands(string dir, string tag, HashSet<string> seen)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.md"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                var (_, desc) = ParseFrontmatter(file, name);
                if (seen.Add(name))
                    _cache.Add(new SkillEntry { Name = name, Description = desc, Source = tag });
            }
        }

        static (string name, string desc) ParseFrontmatter(string file, string fallbackName)
        {
            string name = fallbackName, desc = "";
            try
            {
                foreach (var raw in File.ReadLines(file).Take(20))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("name:")) name = line.Substring(5).Trim();
                    else if (line.StartsWith("description:")) desc = line.Substring(12).Trim();
                    if (line == "---" && desc.Length > 0) break;
                }
            }
            catch { }
            if (desc.Length > 90) desc = desc.Substring(0, 90) + "…";
            return (name, desc);
        }

        // index ทั้งคลังสำหรับแนบเข้า system prompt (CLI mode) — ให้ AI เห็นว่ามี playbook อะไร แล้วเลือกเปิดอ่านเอง
        public static string PromptIndex(int max = 48)
        {
            if (_cache == null) Refresh();
            if (_cache == null || _cache.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var e in _cache)
            {
                if (n++ >= max) { sb.Append($"- …และอีก {_cache.Count - max} ตัว\n"); break; }
                sb.Append("- ").Append(e.Name).Append(" [").Append(e.Source).Append(']');
                if (!string.IsNullOrEmpty(e.Description)) sb.Append(" — ").Append(e.Description);
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        public static List<SkillEntry> Search(string query, int max = 8)
        {
            if (_cache == null) Refresh();
            if (string.IsNullOrEmpty(query)) return _cache.Take(max).ToList();
            string q = query.ToLowerInvariant();
            return _cache
                .Where(e => e.Name.ToLowerInvariant().Contains(q))
                .OrderBy(e => e.Name.ToLowerInvariant().IndexOf(q))
                .Take(max).ToList();
        }
    }
}
