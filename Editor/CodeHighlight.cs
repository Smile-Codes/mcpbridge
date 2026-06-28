using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DeltaUnity.MCP
{
    /// <summary>
    /// C# syntax highlight → Unity rich text (สำหรับ code block ในแชต)
    /// โทนสีคล้าย VS Code dark
    /// </summary>
    public static class CodeHighlight
    {
        const string KW = "#569CD6";   // keyword (ฟ้า)
        const string STR = "#CE9178";  // string (ส้ม)
        const string COM = "#6A9955";  // comment (เขียว)
        const string TYP = "#4EC9B0";  // type/class (เขียวฟ้า)
        const string NUM = "#B5CEA8";  // number

        static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "public","private","protected","internal","static","void","int","float","double","bool",
            "string","var","new","return","if","else","for","foreach","while","do","class","struct",
            "interface","using","namespace","this","null","true","false","const","readonly","override",
            "virtual","abstract","get","set","in","out","ref","enum","switch","case","break","continue",
            "default","try","catch","finally","throw","is","as","base","async","await","yield","params",
            "object","byte","long","short","uint","char","sealed","partial","event","delegate","lock"
        };

        public static string Highlight(string code)
        {
            code = code.Replace("<", "‹").Replace(">", "›");   // กัน rich text เพี้ยน
            var sb = new StringBuilder();
            foreach (var raw in code.Split('\n'))
            {
                string line = raw;
                string comment = null;
                int ci = FindLineComment(line);
                if (ci >= 0) { comment = line.Substring(ci); line = line.Substring(0, ci); }

                // strings → placeholder (กัน keyword ทับใน string)
                var strs = new List<string>();
                line = Regex.Replace(line, "\"(\\\\.|[^\"\\\\])*\"", m => { strs.Add(m.Value); return "\x01_" + (strs.Count - 1) + "_\x01"; });

                // identifier: keyword / type
                line = Regex.Replace(line, @"\b[A-Za-z_]\w*\b", m =>
                {
                    string w = m.Value;
                    if (Keywords.Contains(w)) return $"<color={KW}>{w}</color>";
                    if (w.Length > 0 && char.IsUpper(w[0])) return $"<color={TYP}>{w}</color>";
                    return w;
                });

                // numbers
                line = Regex.Replace(line, @"\b\d+\.?\d*[fFdL]?\b", m => $"<color={NUM}>{m.Value}</color>");

                // restore strings (สี)
                line = Regex.Replace(line, "\x01_(\\d+)_\x01", m => $"<color={STR}>{strs[int.Parse(m.Groups[1].Value)]}</color>");

                sb.Append(line);
                if (comment != null) sb.Append($"<color={COM}>{comment}</color>");
                sb.Append('\n');
            }
            // ห่อด้วยสีเทาอ่อนฐาน — โค้ดที่ไม่ใช่ token (ตัวแปร/ข้อความธรรมดา) จะสว่างชัดเสมอ
            // (token keyword/type/string/number ที่มี color tag ข้างใน ยัง override ได้)
            return "<color=#D4D4D4>" + sb.ToString().TrimEnd('\n') + "</color>";
        }

        // หา // ที่ไม่ได้อยู่ในสตริง
        static int FindLineComment(string line)
        {
            bool inStr = false;
            for (int i = 0; i < line.Length - 1; i++)
            {
                char c = line[i];
                if (c == '"' && (i == 0 || line[i - 1] != '\\')) inStr = !inStr;
                else if (!inStr && c == '/' && line[i + 1] == '/') return i;
            }
            return -1;
        }
    }
}
