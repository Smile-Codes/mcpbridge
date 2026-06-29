using System.Text.RegularExpressions;

namespace MCPBridge
{
    /// <summary>
    /// แปลง markdown ของ Claude เป็น Unity rich text พร้อมสี
    /// - หัวข้อ (#, ##) → เขียว ตัวหนา
    /// - **ตัวหนา/สำคัญ** → เหลือง
    /// - `code` / ชื่อ script → ฟ้า
    /// - ข้อความปกติ → ขาว (กำหนดผ่าน style)
    /// </summary>
    public static class MarkdownColor
    {
        // ── Midnight Indigo palette (เข้าธีม accent #7C6CFF / พื้น #0F1117) — โทนเย็น อ่านคม ──
        const string CODE   = "#A5B4FC"; // light indigo — code / script / ไฟล์
        const string STRONG = "#F4F6FA"; // near-white — สำคัญ (เด่นด้วยน้ำหนัก ไม่ใช่สีตะโกน)
        const string HEADER = "#A99BFF"; // bright indigo — หัวข้อ (โทนเดียวกับ accent)
        const string BULLET = "#9AA0AD"; // cool muted — bullet

        public static string ToRichText(string md)
        {
            if (string.IsNullOrEmpty(md)) return md;

            // normalize circled numbers ①②③ (U+2460 series) → Unity font ไม่มี glyph พวกนี้
            for (int ci = 0; ci < 20; ci++)
                md = md.Replace(((char)(0x2460 + ci)).ToString(), $"{ci + 1}.");

            // กัน '<' ที่ไม่ใช่ tag ทำ rich text เพี้ยน (เช่น generic List<T>)
            // ใช้ «» (U+00AB/BB, Latin-1) แทน ‹› (U+2039/3A) ซึ่ง Unity built-in font ไม่มี
            md = md.Replace("<", "«").Replace(">", "»");

            // หัวข้อที่ติดบรรทัดก่อนหน้า → แทรกบรรทัดว่าง (ให้เนื้อหามีอากาศ ไม่พรืด)
            md = Regex.Replace(md, @"([^\n])\n(#{1,6}\s)", "$1\n\n$2");

            // `inline code` → clay-peach
            md = Regex.Replace(md, "`([^`]+)`", $"<color={CODE}>$1</color>");

            // **bold** → cream ตัวหนา
            md = Regex.Replace(md, @"\*\*([^*]+)\*\*", $"<b><color={STRONG}>$1</color></b>");

            // หัวข้อ ## / ### ต้นบรรทัด → clay ตัวหนา ใหญ่ขึ้นนิด (อ่านสแกนง่าย)
            md = Regex.Replace(md, @"(?m)^\s*#{1,6}\s*(.+)$", $"<size=14><b><color={HEADER}>$1</color></b></size>");

            // bullet (- หรือ *) ต้นบรรทัด → จุด warm muted
            md = Regex.Replace(md, @"(?m)^(\s*)[-*]\s+", $"$1<color={BULLET}>•</color> ");

            // ชื่อไฟล์ .cs ที่โผล่ลอยๆ → clay-peach
            md = Regex.Replace(md, @"(?<![\w/>])(\w+\.cs)\b", $"<color={CODE}>$1</color>");

            // ยุบเส้นคั่น --- และบรรทัดว่างซ้อนกัน → กระชับ ไม่เปลืองพื้นที่
            md = Regex.Replace(md, @"(?m)^\s*---\s*$", "");          // ตัดเส้น --- ออก
            md = Regex.Replace(md, @"\n{3,}", "\n\n");               // บรรทัดว่าง 3+ → 1
            md = md.Trim();

            // ── อากาศระหว่างบรรทัด (IMGUI ไม่มี line-height → แทรก "บรรทัดจิ๋ว" คั่นแทน) ──
            // ก่อนบรรทัด label หนา (จุด:/ทำไม:/แก้:/Impact: ฯลฯ) = ช่องเล็ก 5px
            md = Regex.Replace(md, @"\n(?=<b><color=)", "\n<size=5> </size>\n");
            // ก่อนหัวข้อใหญ่ (## …) = ช่องใหญ่ขึ้น 9px — เห็นเป็นคนละ section ชัด
            md = Regex.Replace(md, @"\n(?=<size=14>)", "\n<size=9> </size>\n");

            // บรรทัดสถิติ (⏱ ...) ท้ายคำตอบ → เล็ก จาง
            md = Regex.Replace(md, @"(?m)^⏱ .+$", m => $"<size=9><color=#5C6370>{m.Value}</color></size>");

            // ห่อทั้งก้อนด้วยสีฐานธีม (#EEF0F4 = TEXT_WHITE ของหน้าต่าง)
            // (tag สีข้างใน เช่น bold/หัวข้อ ยัง override ได้ปกติ)
            return "<color=#EEF0F4>" + md + "</color>";
        }
    }
}
