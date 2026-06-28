using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace DeltaUnity.MCP
{
    /// <summary>
    /// เรียก Claude ผ่าน Claude Code CLI (print mode: `claude -p`)
    /// ใช้ subscription (Max plan) ที่ login ไว้ — ไม่ใช้ API Key, ไม่กิน token แยก
    /// ได้ context ของ codebase + skills ด้วยเพราะรันใน project directory
    /// ต้องมี: ติดตั้ง Claude Code CLI + login (claude login) ก่อน
    /// </summary>
    public static class ClaudeCliClient
    {
        public static async Task<ClaudeResponse> SendAsync(string prompt, List<ClaudeImage> images, CancellationToken token = default, string resumeSessionId = null, int role = 0)
        {
            // ไม่ต้อง inject role prefix อีกต่อไป — format ใหม่ใช้ Header(Dev)/Header(Art) แยกชัดเจน
            // อ่านค่า Unity API ทั้งหมดบน main thread ก่อน Task.Run (เรียกบน background ไม่ได้)
            string projectRoot = Application.dataPath.Replace("/Assets", "");
            string claudeCmd = EditorPrefs.GetString("DeltaMCP_ClaudeCmd", "claude");
            string model = EditorPrefs.GetString("DeltaMCP_CliModel", "claude-sonnet-4-6");
            string effort = EditorPrefs.GetString("DeltaMCP_CliEffort", "medium");   // low|medium|high|max
            bool isWindows = Application.platform == RuntimePlatform.WindowsEditor;

            // ── เร่งความเร็ว: แชตปกติใช้ --bare (ข้ามโหลด CLAUDE.md/skills/MCP/memory ทุกครั้ง = ช้า) ──
            // แต่ถ้าเป็นการรันสกิล (ขึ้นต้นด้วย /) ต้องโหลด context เต็ม → ไม่ bare
            bool isSkillRun = prompt != null && prompt.TrimStart().StartsWith("/");
            bool bare = EditorPrefs.GetBool("DeltaMCP_CliBare", true) && !isSkillRun;
            // max-turns: เผื่อ analyze-first (Read/Grep โค้ด + เปิด SKILL.md หลายไฟล์ระหว่างคิด)
            int maxTurns = EditorPrefs.GetInt("DeltaMCP_CliMaxTurns", isSkillRun ? 32 : 30);
            // อ่าน flag ทดลองบน main thread (เรียก EditorPrefs บน background thread ไม่ได้!)
            bool useEffort = EditorPrefs.GetBool("DeltaMCP_CliUseEffort", false);
            bool useFast   = EditorPrefs.GetBool("DeltaMCP_CliFast", false);
            bool debug     = EditorPrefs.GetBool("DeltaMCP_CliDebug", false);

            // เขียนรูปลงไฟล์ชั่วคราว แล้วอ้างอิง path ใน prompt (CLI อ่านไฟล์ภาพได้เอง)
            var tempFiles = new List<string>();
            string fullPrompt = prompt;
            if (images != null && images.Count > 0)
            {
                var sb = new StringBuilder(prompt);
                sb.Append("\n\nAttached images (read these files):");
                for (int i = 0; i < images.Count; i++)
                {
                    try
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), $"delta_mcp_{Guid.NewGuid():N}.png");
                        File.WriteAllBytes(tmp, Convert.FromBase64String(images[i].Base64));
                        tempFiles.Add(tmp);
                        sb.Append($"\n- {tmp}");
                    }
                    catch { /* ignore image */ }
                }
                fullPrompt = sb.ToString();
            }

            // ── SKILLS INDEX — คลัง playbook (Delta-Project + project + user) ให้ AI เห็นแล้วเลือกเปิดอ่านเองตอนวิเคราะห์ ──
            string skillsBlock = "";
            try
            {
                string idx = SkillIndex.PromptIndex();
                if (!string.IsNullOrEmpty(idx))
                    skillsBlock = "\n=== SKILLS INDEX (playbook เฉพาะทางในเครื่อง — ถ้า prompt เข้าข่ายตัวไหน ให้ Read SKILL.md ของมันมาประกอบการวิเคราะห์: " +
                                  "[project] = <repo>/.claude/skills/<ชื่อ>/SKILL.md · [delta-project] = ../Delta-Project/.claude/skills/ · [user] = ~/.claude/skills/) ===\n" +
                                  idx + "\n";
            }
            catch { }

            // CLI hint — เตือน workflow + format ไว้ทุก turn (กัน AI ลืมใน session ยาว) — ต้องตรงกับ brain ปัจจุบันเป๊ะ
            string cliRoleHint = "\n[WORKFLOW] วิเคราะห์ให้จบก่อน — ใช้ Read/Grep เปิดโค้ดจริงใน repo + เปิด SKILL.md ที่เกี่ยวจาก SKILLS INDEX ได้เต็มที่ " +
                "แล้วค่อยจัดคำตอบลง format ตอนท้ายสุด ห้ามให้ format บีบขั้นวิเคราะห์\n" +
                "[FORMAT] marker คือบรรทัดเดี่ยวล้วนๆ: Header(Dev) และ/หรือ Header(Art) (Dev มาก่อนถ้ามีทั้งคู่ · ห้ามพิมพ์คำว่า Header( ที่อื่นในคำตอบ) " +
                "ในแต่ละส่วน: ตาราง 🎯 สรุปเรียงความเสี่ยง (ถ้ามี ≥2 finding) → การ์ด ## 🔴/🟡/🟢 #N — ชื่อ ✓/? → 🧭 สรุปแก้อะไรก่อน " +
                "· เลขการ์ด #N ต่อเนื่องทั้ง response (ห้ามรีเซ็ตข้าม Header) · ปัญหาเดียวกันสองมุมให้อ้างโยงกัน เช่น (อาการของ #1) " +
                "· ถ้าจะส่ง JSON command ให้ส่งเป็นข้อความล้วนไม่มี Header ใดๆ\n";
            string promptWithRules = ClaudeAPIClient.BuildSystemPrompt(role, true) + skillsBlock + cliRoleHint + "\n\n=== User request ===\n" + fullPrompt;

            // ── --resume: base prompt ถูก cache ไว้แล้ว แต่ re-inject brain + skills index (อ่านสด) ทุก turn เสมอ ──
            string sendText = string.IsNullOrEmpty(resumeSessionId)
                ? promptWithRules
                : ClaudeAPIClient.BuildBrainSection() + skillsBlock + cliRoleHint + "\n=== User request ===\n" + fullPrompt;

            try
            {
                LastSessionId = null;
                string output = await Task.Run(() => RunProcess(claudeCmd, projectRoot, sendText, model, isWindows, maxTurns, bare, resumeSessionId, effort, useEffort, useFast, debug, token));
                var resp = BuildResponse(output);
                resp.SessionId = LastSessionId;
                return resp;
            }
            catch (OperationCanceledException)
            {
                return new ClaudeResponse { Error = "ยกเลิกแล้ว (cancelled)" };
            }
            catch (Exception e)
            {
                return new ClaudeResponse { Error = $"CLI error: {e.Message}\n(ตรวจว่าติดตั้ง Claude Code CLI + login แล้ว — ลองพิมพ์ 'claude' ใน terminal)" };
            }
            finally
            {
                foreach (var f in tempFiles) { try { File.Delete(f); } catch { } }
            }
        }

        // ── กัน Unity ค้างตอน domain reload ─────────────────────────────────
        // ตอน "กำลังคิด" มี thread block อยู่ใน proc.WaitForExit (native wait) — domain reload abort ไม่เข้า
        // → Unity ค้างที่ "Reloading Domain" จนกว่า process จะจบเอง · แก้: kill process ก่อน reload เสมอ
        static volatile Process _activeProc;

        [UnityEditor.InitializeOnLoadMethod]
        static void HookReload()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += KillActive;
        }

        public static void KillActive()
        {
            try { _activeProc?.Kill(); } catch { }   // exited/disposed แล้ว → เงียบ
            _activeProc = null;
        }

        // session id ของ run ล่าสุด (ดึงจาก result event) — ใช้ --resume ครั้งถัดไป
        public static volatile string LastSessionId;
        public static volatile int LiveToolCalls;   // จำนวน tool ที่เรียกใน run นี้ (activity indicator)

        static string RunProcess(string claudeCmd, string workingDir, string prompt, string model, bool isWindows, int maxTurns, bool bare, string resumeSessionId, string effort, bool useEffort, bool useFast, bool debug, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                // Windows: claude เป็น .cmd shim ต้องเรียกผ่าน cmd.exe
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                WorkingDirectory = workingDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            // stream-json = ได้ event ทีละบรรทัด → นับ output token สด + ดึง result ตอนจบ
            // --verbose จำเป็นสำหรับ stream-json ใน print mode
            // เร่งความเร็ว: ปิด auto-load MCP servers (.mcp.json) → ไม่ spawn node + connect ทุกครั้ง
            //   ใช้ --strict-mcp-config + --mcp-config ชี้ไฟล์ว่าง (ไม่ใช้ --bare เพราะมันทำ login หลุด)
            string fastFlag = "";
            if (bare)
            {
                string emptyMcp = Path.Combine(Path.GetTempPath(), "delta_empty_mcp.json");
                try { if (!File.Exists(emptyMcp)) File.WriteAllText(emptyMcp, "{\"mcpServers\":{}}"); } catch { }
                fastFlag = $" --strict-mcp-config --mcp-config \"{emptyMcp}\"";
            }
            // --resume <id> = ต่อ session เดิม → context warm, ไม่ re-parse settings = เร็วขึ้น ~2 เท่า
            string resumeFlag = string.IsNullOrEmpty(resumeSessionId) ? "" : $" --resume {resumeSessionId}";
            // BASELINE ที่เคยทำงานชัวร์ — เพิ่ม flag เสี่ยงทีละตัวเฉพาะเมื่อเปิดผ่าน EditorPref (กันแฮงค์)
            // --effort: เปิดผ่าน DeltaMCP_CliUseEffort=true   |  --strict-mcp-config: เปิดผ่าน DeltaMCP_CliFast=true
            string extra = "";
            if (useEffort && !string.IsNullOrEmpty(effort)) extra += $" --effort {effort}";
            if (useFast) extra += fastFlag;
            string flags = $"-p --output-format stream-json --verbose --permission-mode bypassPermissions --model {model} --max-turns {maxTurns}{extra}{resumeFlag}";
            if (isWindows)
                psi.Arguments = $"/c {claudeCmd} {flags}";
            else
                psi.Arguments = $"-lc \"{claudeCmd} {flags}\"";

            using var proc = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            string resultLine = null;          // บรรทัด event "result" (มีคำตอบสุดท้าย + usage จริง)
            LiveOutputTokens = 0;
            LiveToolCalls = 0;                 // reset ตัวนับ tool ของ run นี้
            long streamChars = 0;              // นับตัวอักษรที่ stream มา (ประมาณ token ระหว่างคิด)

            proc.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                string line = e.Data;

                // 1) ถ้ามี output_tokens จริงใน event → ใช้เลย (แม่นสุด)
                var m = System.Text.RegularExpressions.Regex.Match(line, "\"output_tokens\"\\s*:\\s*(\\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int tok) && tok > LiveOutputTokens)
                    LiveOutputTokens = tok;
                else
                {
                    // 2) ไม่มี usage ระหว่างทาง → ประมาณจาก "text" ที่ gen (~4 ตัวอักษร = 1 token)
                    //    (stream-json ส่ง text ทีละ message block — ตัวเลขขยับเป็นช่วงๆ ตาม turn)
                    var tm = System.Text.RegularExpressions.Regex.Match(line, "\"text\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                    if (tm.Success)
                    {
                        streamChars += tm.Groups[1].Value.Length;
                        int est = (int)(streamChars / 4);
                        if (est > LiveOutputTokens) LiveOutputTokens = est;   // แสดงเป็น ~ ประมาณ
                    }
                }
                // นับ tool call เป็น activity indicator (กัน "ดูเหมือนค้าง" ตอน Claude อ่านไฟล์/audit ที่ไม่มี text)
                // ใช้ substring match แบบ format-agnostic — ไม่ผูกรูปแบบ JSON ที่ยังไม่ชัวร์
                if (line.IndexOf("tool_use", StringComparison.Ordinal) >= 0) LiveToolCalls++;

                // DEBUG ชั่วคราว: ดู raw stream เพื่อยืนยัน format จริง (เปิดผ่าน EditorPref DeltaMCP_CliDebug)
                if (debug) UnityEngine.Debug.Log("[MCP stream] " + line);

                // เก็บ event result (คำตอบสุดท้าย + usage จริง)
                if (line.Contains("\"type\":\"result\"")) resultLine = line;
            };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            proc.Start();
            _activeProc = proc;   // ลงทะเบียนให้ beforeAssemblyReload ฆ่าได้ (กัน reload ค้าง)
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // ยกเลิก → kill process ทันที
            using var reg = token.Register(() => { try { proc.Kill(); } catch { } });

            // ส่ง prompt เข้า stdin เป็น UTF-8 bytes ตรงๆ (กันภาษาไทยเพี้ยน)
            byte[] inBytes = new UTF8Encoding(false).GetBytes(prompt);
            proc.StandardInput.BaseStream.Write(inBytes, 0, inBytes.Length);
            proc.StandardInput.BaseStream.Flush();
            proc.StandardInput.Close();

            // รอจบ (timeout 300 วิ — CLI agent อาจเรียก Unity MCP tools หลายขั้น)
            if (!proc.WaitForExit(300000))
            {
                try { proc.Kill(); } catch { }
                _activeProc = null;
                throw new Exception("timeout (300s) — agent ทำงานนานเกิน (งานใน Unity อาจเสร็จแล้วก็ได้ ลองเช็ค scene) " +
                                    "เร่งได้โดยสลับ model เป็น haiku ใน Settings");
            }
            _activeProc = null;

            token.ThrowIfCancellationRequested();

            // ดึงคำตอบจาก event result (field "result")
            string outText = resultLine != null ? ExtractJsonString(resultLine, "result") : null;
            if (string.IsNullOrEmpty(outText))
            {
                string err = stderr.ToString().Trim();
                throw new Exception(string.IsNullOrEmpty(err) ? "no output" : err);
            }

            // ผลลัพธ์สุดท้าย: ใช้ output_tokens จริงจาก result event (ทับค่าประมาณ)
            if (resultLine != null)
            {
                var fm = System.Text.RegularExpressions.Regex.Match(resultLine, "\"output_tokens\"\\s*:\\s*(\\d+)");
                if (fm.Success && int.TryParse(fm.Groups[1].Value, out int finalTok))
                    LiveOutputTokens = finalTok;
                // เก็บ session_id → ใช้ --resume ครั้งถัดไป
                LastSessionId = ExtractJsonString(resultLine, "session_id");
            }
            return outText;
        }

        // จำนวน output token ที่ AI gen ออกมาแล้ว (อัปเดตสดระหว่างคิด)
        public static volatile int LiveOutputTokens;

        // ดึงค่า string ของ key จาก JSON บรรทัดเดียว + unescape
        static string ExtractJsonString(string json, string key)
        {
            int k = json.IndexOf($"\"{key}\":\"");
            if (k < 0) return null;
            int start = k + key.Length + 4;
            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[++i];
                    sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\0', '"' => '"', '\\' => '\\', _ => n });
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString().Replace("\0", "");
        }

        // แยก command JSON ออกจากคำตอบ (เหมือนฝั่ง API)
        static ClaudeResponse BuildResponse(string text)
        {
            // ข้าม {"command"} ที่อยู่ใน ```code fence``` (ตัวอย่าง ไม่ใช่คำสั่งจริง) — ใช้ helper ร่วมกับฝั่ง API
            int cmdStart = ClaudeAPIClient.FindRealCommandStart(text);
            if (cmdStart >= 0)
            {
                int cmdEnd = text.IndexOf('}', cmdStart) + 1;
                if (cmdEnd > cmdStart)
                {
                    string cmdJson = text.Substring(cmdStart, cmdEnd - cmdStart);
                    return new ClaudeResponse { Text = text, CommandJson = cmdJson };
                }
            }
            return new ClaudeResponse { Text = text };
        }
    }
}
