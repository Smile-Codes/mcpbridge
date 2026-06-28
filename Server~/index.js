import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const TIMEOUT_MS  = 8000;   // timeout ต่อ request
const MAX_RETRIES = 3;       // retry สูงสุด
const RETRY_DELAY = 600;     // ms ระหว่าง retry

// ── Multi-instance registry: <delta-unity>/Library/DeltaMCP/instances/<pid>.json ──
// env-independent: คำนวณ root จาก path ของ script เอง (index.js อยู่ <root>/Tools/unity-mcp-server/)
// → bridge กับ Unity ชี้ที่เดียวกันแน่นอน ไม่ผูก %TEMP%/HOME ที่ Claude Code spawn อาจเห็นคนละตัว
// (Library ถูก gitignore แล้ว) — Unity clone ตัด _clone_N → ได้ original root เดียวกัน
const DELTA_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const INSTANCES_DIR = path.join(DELTA_ROOT, "Library", "DeltaMCP", "instances");
let selectedPort = null;   // target ที่เลือกไว้ (per-session ของ bridge นี้)

function listInstances() {
  try {
    if (!fs.existsSync(INSTANCES_DIR)) return [];
    return fs.readdirSync(INSTANCES_DIR)
      .filter(f => f.endsWith(".json"))
      .map(f => {
        try {
          // strip UTF-8 BOM (C# Encoding.UTF8 ใส่ BOM นำหน้า → JSON.parse พังถ้าไม่ตัด)
          let txt = fs.readFileSync(path.join(INSTANCES_DIR, f), "utf8");
          if (txt.charCodeAt(0) === 0xFEFF) txt = txt.slice(1);
          return JSON.parse(txt);
        } catch { return null; }
      })
      .filter(Boolean);
  } catch { return []; }
}

// เลือก port เป้าหมาย — เฉพาะ instance ที่ serverOn (off คุยไม่ได้)
// override ผ่าน env > ตัวที่ select ไว้ > "Main" > port ต่ำสุด
function resolvePort() {
  if (process.env.UNITY_MCP_PORT) return Number(process.env.UNITY_MCP_PORT);
  const on = listInstances().filter(i => i.serverOn);
  if (!on.length) return null;
  if (selectedPort && on.some(i => i.port === selectedPort)) return selectedPort;
  const main = on.find(i => String(i.label || "").toLowerCase() === "main");
  return (main || on.sort((a, b) => a.port - b.port)[0]).port;
}

// ── Helper: sleep ─────────────────────────────────────────────────────────
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

// ── Helper: call Unity Editor (resolve target port + retry + timeout) ──────
async function callUnity(path_, body = {}) {
  const port = resolvePort();
  if (port == null)
    return { error: "ไม่พบ Unity instance ที่เปิด MCP server — กด Start ใน Delta AI (ดูด้วย unity_list_instances)" };
  const base = `http://localhost:${port}`;

  let lastErr;
  for (let attempt = 1; attempt <= MAX_RETRIES; attempt++) {
    try {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
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
      if (attempt < MAX_RETRIES) {
        const delay = RETRY_DELAY * attempt;   // backoff: 600, 1200 ms
        console.error(`[MCP] ${reason} (port ${port}) → retry ${attempt}/${MAX_RETRIES - 1} in ${delay}ms`);
        await sleep(delay);
      }
    }
  }
  // หมด retry แล้วยังไม่ได้ → ส่ง error กลับแทน throw (กัน Claude ค้าง)
  return { error: `Unity MCP Server (port ${port}) ไม่ตอบสนอง (${lastErr?.name ?? "unknown"}) — กด Start ใน Delta AI` };
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
    .map(i => ({ label: i.label, project: i.project, port: i.port, serverOn: !!i.serverOn, active: !!i.serverOn && i.port === active }));
  if (out.length) return { content: [{ type: "text", text: JSON.stringify(out, null, 2) }] };
  return { content: [{ type: "text", text: JSON.stringify({ instances: [], note: "ไม่มี Unity editor เปิดอยู่ (ที่มี Delta AI)", _debug: { registryDir: INSTANCES_DIR, exists: fs.existsSync(INSTANCES_DIR) } }, null, 2) }] };
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
  server.tool(cmd.tool, cmd.description, toZodShape(cmd.params), async (args) => {
    const r = await callUnity(cmd.path, args || {});
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  });
}
console.error(`[MCP] registered ${_cmds.length} Unity tools from manifest + 3 instance tools`);

// ── Start ─────────────────────────────────────────────────────────────────
const transport = new StdioServerTransport();
await server.connect(transport);
