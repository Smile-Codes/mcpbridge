import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { instanceDirs, listInstances, pickInstancePort, preferredProjectRoot } from "./registry.js";

const TIMEOUT_MS  = 8000;   // timeout ต่อ request
const MAX_RETRIES = 3;       // retry สูงสุด
const RETRY_DELAY = 600;     // ms ระหว่าง retry

let selectedPort = null;   // target ที่เลือกไว้ (per-session ของ bridge นี้)

// เลือก port เป้าหมาย — UNITY_MCP_PORT (ตั้งเอง) ชนะทุกอย่าง ที่เหลือดู registry.js
function resolvePort() {
  if (process.env.UNITY_MCP_PORT) return Number(process.env.UNITY_MCP_PORT);
  return pickInstancePort(listInstances(), selectedPort);
}

// ── Helper: sleep ─────────────────────────────────────────────────────────
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

// ── Helper: call Unity Editor (resolve target port + retry + timeout) ──────
// opts.timeoutMs / opts.maxRetries override the defaults per command (set in commands.json) —
// long jobs (e.g. run_csharp compiling via Roslyn) need a longer timeout AND maxRetries:1, because
// retrying a non-idempotent command would re-run its side effects.
async function callUnity(path_, body = {}, opts = {}) {
  const timeoutMs  = opts.timeoutMs  ?? TIMEOUT_MS;
  const maxRetries = opts.maxRetries ?? MAX_RETRIES;
  const port = resolvePort();
  if (port == null)
    return { error: "ไม่พบ Unity instance ที่เปิด MCP server — เปิด Unity แล้วกด MCP Bridge → Server → Start (ดูรายการด้วย unity_list_instances)" };
  const base = `http://localhost:${port}`;

  let lastErr;
  for (let attempt = 1; attempt <= maxRetries; attempt++) {
    try {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeoutMs);
      const res = await fetch(`${base}${path_}`, {
        method:  "POST",
        headers: { "Content-Type": "application/json" },
        body:    JSON.stringify(body),
        signal:  controller.signal,
      });
      clearTimeout(timer);
      const text = await res.text();
      try { return JSON.parse(text); }
      catch { return { raw: text }; }
    } catch (err) {
      lastErr = err;
      const isTimeout = err.name === "AbortError";
      const reason    = isTimeout ? "timeout" : "connection refused";
      if (attempt < maxRetries) {
        const delay = RETRY_DELAY * attempt;   // backoff: 600, 1200 ms
        console.error(`[MCP] ${reason} (port ${port}) → retry ${attempt}/${MAX_RETRIES - 1} in ${delay}ms`);
        await sleep(delay);
      }
    }
  }
  // หมด retry แล้วยังไม่ได้ → ส่ง error กลับแทน throw (กัน Claude ค้าง)
  return { error: `Unity MCP Server (port ${port}) ไม่ตอบสนอง (${lastErr?.name ?? "unknown"}) — กด MCP Bridge → Server → Start ใน Unity` };
}

// ── Command manifest : SINGLE SOURCE (อ่านร่วมกับ Unity C# MCPHandlers.cs) ──
//    เพิ่ม/แก้ tool ที่ commands.json ที่เดียว → bridge (ไฟล์นี้) + แชตใน Unity เห็นพร้อมกัน
const COMMANDS_PATH = path.join(path.dirname(fileURLToPath(import.meta.url)), "commands.json");
function loadCommands() {
  try {
    let txt = fs.readFileSync(COMMANDS_PATH, "utf8");
    if (txt.charCodeAt(0) === 0xFEFF) txt = txt.slice(1);
    return JSON.parse(txt).commands || [];
  } catch (e) {
    console.error(`[MCP] โหลด commands.json ไม่ได้: ${e.message}`);
    return [];
  }
}
// แปลง param spec (plain JSON) → Zod raw shape ที่ server.tool ต้องการ
function toZodShape(params) {
  const shape = {};
  for (const [key, p] of Object.entries(params || {})) {
    let zt;
    switch (p.type) {
      case "number":   zt = z.number(); break;
      case "boolean":  zt = z.boolean(); break;
      case "enum":     zt = z.enum(p.values); break;
      case "number[]": zt = z.array(z.number()); break;
      case "object[]": zt = z.array(z.record(z.any())); break;   // batch sub-commands
      default:         zt = z.string();
    }
    if (p.desc) zt = zt.describe(p.desc);
    if (Object.prototype.hasOwnProperty.call(p, "default")) zt = zt.optional().default(p.default);
    else if (p.opt) zt = zt.optional();
    shape[key] = zt;
  }
  return shape;
}

// ── MCP Server ────────────────────────────────────────────────────────────
const server = new McpServer({
  name: "unity-mcp",
  version: "1.0.0",
});

// ── Instance-management tools (local registry logic — ไม่อยู่ใน manifest) ────

server.tool("unity_list_instances", "List ALL open Unity Editor instances (ParrelSync Main/Clone for multiplayer host/client testing), including ones with the MCP server OFF. Returns label, project, serverOn, and which is active. Use before open/close/switch to let the user pick an editor.", {}, async () => {
  const inst = listInstances();
  const active = resolvePort();
  const out = inst
    .sort((a, b) => a.port - b.port)
    .map(i => ({ label: i.label, project: i.project, projectPath: i.projectPath, port: i.port, serverOn: !!i.serverOn, active: !!i.serverOn && i.port === active }));
  if (out.length) return { content: [{ type: "text", text: JSON.stringify(out, null, 2) }] };
  const searched = instanceDirs().map(d => ({ dir: d, exists: fs.existsSync(d) }));
  return { content: [{ type: "text", text: JSON.stringify({
    instances: [],
    note: "ไม่มี Unity editor ที่ติดตั้ง MCP Bridge เปิดอยู่ (editor เขียน presence ตอนโหลด package)",
    _debug: { searched, preferredProject: preferredProjectRoot(), cwd: process.cwd() },
  }, null, 2) }] };
});

// helper: หา instance จาก label/pid/port
function findInstance(target) {
  const inst = listInstances();
  const t = String(target).toLowerCase();
  return inst.find(i => String(i.pid) === t || String(i.port) === t || String(i.label || "").toLowerCase() === t);
}

server.tool("unity_select_instance", "Select which Unity Editor (with server ON) subsequent unity_* commands target. Pass label ('Main','Clone 0') or port. Per-session — other sessions can target a different editor. If the target's server is OFF, use unity_start_instance first.", {
  target: z.string().describe("Instance label (e.g. 'Main', 'Clone 0') or port"),
}, async ({ target }) => {
  const pick = findInstance(target);
  if (!pick)
    return { content: [{ type: "text", text: JSON.stringify({ error: `ไม่พบ instance '${target}'`, available: listInstances().map(i => `${i.label}${i.serverOn ? "" : " (off)"}`) }) }] };
  if (!pick.serverOn)
    return { content: [{ type: "text", text: JSON.stringify({ error: `'${pick.label}' server ปิดอยู่ — ใช้ unity_start_instance ก่อน`, label: pick.label }) }] };
  selectedPort = pick.port;
  return { content: [{ type: "text", text: JSON.stringify({ selected: pick.label, port: pick.port, project: pick.project }) }] };
});

server.tool("unity_start_instance", "Start (activate) the MCP server on a specific Unity Editor that is open but server OFF — writes a trigger its watcher picks up (AI-driven Start). Pass label ('Main','Clone 0') or pid. After it comes online it becomes the selected target. Use when unity_list_instances shows serverOn:false.", {
  target: z.string().describe("Instance label (e.g. 'Main', 'Clone 0') or pid"),
}, async ({ target }) => {
  const pick = findInstance(target);
  if (!pick)
    return { content: [{ type: "text", text: JSON.stringify({ error: `ไม่พบ instance '${target}'`, available: listInstances().map(i => i.label) }) }] };
  if (pick.serverOn) { selectedPort = pick.port; return { content: [{ type: "text", text: JSON.stringify({ alreadyOn: pick.label, port: pick.port }) }] }; }
  if (!pick.projectPath)
    return { content: [{ type: "text", text: JSON.stringify({ error: `ไม่รู้ projectPath ของ '${pick.label}' — สั่ง start ไม่ได้` }) }] };
  // เขียน trigger ลง Library ของ editor นั้น → watcher ของมันจะ Start ให้
  try {
    const dir = path.join(pick.projectPath, "Library", "DeltaMCP");
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, "request-start"), "1");
  } catch (e) {
    return { content: [{ type: "text", text: JSON.stringify({ error: `เขียน trigger ไม่สำเร็จ: ${e.message}` }) }] };
  }
  // รอ watcher (1.5s) แล้ว poll presence ว่า serverOn ขึ้นไหม (สูงสุด ~7s)
  for (let i = 0; i < 9; i++) {
    await sleep(800);
    const e = findInstance(pick.pid);
    if (e && e.serverOn) { selectedPort = e.port; return { content: [{ type: "text", text: JSON.stringify({ started: e.label, port: e.port, project: e.project }) }] }; }
  }
  return { content: [{ type: "text", text: JSON.stringify({ error: `สั่ง start '${pick.label}' แล้วแต่ยังไม่ออนไลน์ — watcher อาจยังไม่ทำงาน (clone ยัง compile โค้ดใหม่ไม่เสร็จ?) ลองใหม่ หรือกด Start ที่ editor นั้นเอง` }) }] };
});

// ── Unity tools — register จาก manifest (single source) ทุกตัวยิง callUnity(path) ──
const _cmds = loadCommands();
for (const cmd of _cmds) {
  // per-command overrides (commands.json): timeoutMs for slow jobs, noRetry for non-idempotent ones.
  const opts = { timeoutMs: cmd.timeoutMs, maxRetries: cmd.noRetry ? 1 : undefined };
  const isScreenshot = cmd.command === "capture_screenshot";
  server.tool(cmd.tool, cmd.description, toZodShape(cmd.params), async (args) => {
    const r = await callUnity(cmd.path, args || {}, opts);
    // capture_screenshot: read the PNG off disk (bridge runs on the same machine as Unity) and
    // return it as an actual image block so Claude can SEE the result — not just a file path.
    if (isScreenshot && r && r.screenshot && !r.error) {
      try {
        const png = fs.readFileSync(r.screenshot);
        const meta = { screenshot: r.screenshot, view: r.view, mode: r.mode, size: r.size, bytes: r.bytes };
        return { content: [
          { type: "text", text: JSON.stringify(meta, null, 2) },
          { type: "image", data: png.toString("base64"), mimeType: "image/png" },
        ] };
      } catch (e) {
        return { content: [{ type: "text", text: JSON.stringify({ ...r, _imageReadError: e.message }, null, 2) }] };
      }
    }
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  });
}
console.error(`[MCP] registered ${_cmds.length} Unity tools from manifest + 3 instance tools`);

// ── Start ─────────────────────────────────────────────────────────────────
const transport = new StdioServerTransport();
await server.connect(transport);
