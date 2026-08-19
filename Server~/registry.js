// ── Unity instance registry (discovery ฝั่ง bridge) ────────────────────────
// ฝั่ง Unity (MCPServer.WritePresence) เขียน <pid>.json ลง 2 ที่:
//   1) <UnityProject>/Library/DeltaMCP/instances  — per-project
//   2) ~/.mcpbridge/instances                     — shared ทั้งเครื่อง
// ไฟล์นี้อ่านทั้งคู่แล้ว dedupe ด้วย pid — shared registry คือตัวที่ทำให้ discovery ทำงานได้
// แม้ package ถูกติดตั้งแบบ local "file:" ref (index.js อยู่นอก Unity project → คำนวณ project root
// จาก path ตัวเองไม่ได้เลย) ส่วน per-project อ่านไว้เผื่อ home dir ของ 2 ฝั่งไม่ตรงกัน
import fs from "fs";
import os from "os";
import path from "path";
import { fileURLToPath } from "url";

const BRIDGE_DIR = path.dirname(fileURLToPath(import.meta.url));
const SHARED_INSTANCES_DIR = path.join(os.homedir(), ".mcpbridge", "instances");
const UNITY_PROJECT_MARKERS = ["Assets", "ProjectSettings"];
const MAX_PARENT_WALK = 12;

// ParrelSync clone (<orig>_clone_N) ใช้ registry ของ original project ร่วมกัน
function stripCloneSuffix(projectRoot) {
  const m = /^(.*)_clone_\d+$/.exec(path.basename(projectRoot));
  return m ? path.join(path.dirname(projectRoot), m[1]) : projectRoot;
}

function findUnityProjectRoot(startDir) {
  if (!startDir) return null;
  let dir = path.resolve(startDir);
  for (let i = 0; i < MAX_PARENT_WALK; i++) {
    if (UNITY_PROJECT_MARKERS.every(marker => fs.existsSync(path.join(dir, marker)))) return dir;
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

// Unity project ที่ session นี้ทำงานด้วย — ใช้เลือก editor เมื่อเปิดหลายโปรเจกต์พร้อมกัน
//   UNITY_PROJECT_PATH (ตั้งเอง) > project ที่ครอบ bridge อยู่ (embedded package) > cwd ของ client
export function preferredProjectRoot() {
  const explicit = process.env.UNITY_PROJECT_PATH;
  if (explicit && fs.existsSync(explicit)) return path.resolve(explicit);
  return findUnityProjectRoot(BRIDGE_DIR) || findUnityProjectRoot(process.cwd());
}

export function instanceDirs() {
  const dirs = [SHARED_INSTANCES_DIR];
  const root = preferredProjectRoot();
  if (root) dirs.push(path.join(stripCloneSuffix(root), "Library", "DeltaMCP", "instances"));
  return [...new Set(dirs)];
}

function readPresenceFile(file) {
  try {
    // strip UTF-8 BOM (ถ้าฝั่ง C# เขียนมาพร้อม BOM → JSON.parse พังถ้าไม่ตัด)
    let txt = fs.readFileSync(file, "utf8");
    if (txt.charCodeAt(0) === 0xFEFF) txt = txt.slice(1);
    return JSON.parse(txt);
  } catch (e) {
    console.error(`[MCP] อ่าน presence ${file} ไม่ได้: ${e.message}`);
    return null;
  }
}

// entry ค้างจาก editor ที่ crash (ยังไม่มี editor ตัวไหนมากวาด) → คัดออก
// ไม่งั้น bridge จะยิงไป port ที่ไม่มีใครฟังแล้วรอจน timeout
function isProcessAlive(pid) {
  if (!pid) return true;
  try { process.kill(pid, 0); return true; }
  catch (e) { return e.code === "EPERM"; }
}

export function listInstances() {
  const byPid = new Map();
  for (const dir of instanceDirs()) {
    for (const file of presenceFilesIn(dir)) {
      const entry = readPresenceFile(file);
      if (!entry || byPid.has(entry.pid) || !isProcessAlive(entry.pid)) continue;
      byPid.set(entry.pid, entry);
    }
  }
  return [...byPid.values()];
}

function presenceFilesIn(dir) {
  try {
    if (!fs.existsSync(dir)) return [];
    return fs.readdirSync(dir).filter(f => f.endsWith(".json")).map(f => path.join(dir, f));
  } catch (e) {
    console.error(`[MCP] อ่าน registry ${dir} ไม่ได้: ${e.message}`);
    return [];
  }
}

function isSameProject(a, b) {
  if (!a || !b) return false;
  const normalize = p => stripCloneSuffix(path.resolve(p)).replace(/[\\/]+$/, "");
  const [x, y] = [normalize(a), normalize(b)];
  return process.platform === "win32" ? x.toLowerCase() === y.toLowerCase() : x === y;
}

// เปิดหลาย Unity project พร้อมกัน → เอาเฉพาะ editor ของ project ที่ session นี้ทำงานด้วย
// (รู้ project ไม่ได้ → คืน [] แล้วให้ผู้เรียกใช้รายการเต็มแทน)
function instancesOfPreferredProject(instances) {
  const root = preferredProjectRoot();
  if (!root) return [];
  return instances.filter(i => isSameProject(i.projectPath, root));
}

// เลือก editor เป้าหมาย — เฉพาะตัวที่ serverOn (off คุยไม่ได้)
// ตัวที่ select ไว้ > editor ของ project ปัจจุบัน > "Main" > port ต่ำสุด
export function pickInstancePort(instances, selectedPort = null) {
  const on = instances.filter(i => i.serverOn);
  if (!on.length) return null;
  if (selectedPort && on.some(i => i.port === selectedPort)) return selectedPort;
  const scoped = instancesOfPreferredProject(on);
  const pool = scoped.length ? scoped : on;
  const main = pool.find(i => String(i.label || "").toLowerCase() === "main");
  return (main || [...pool].sort((a, b) => a.port - b.port)[0]).port;
}
