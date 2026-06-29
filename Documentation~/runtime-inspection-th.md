# เช็คเกมขณะเล่น — Watcher & เครื่องมือ Runtime (MCP Bridge)

คู่มือ "ดู/ตรวจสอบสิ่งที่เกิดขึ้นในเกมตอน Play" โดยไม่ต้องแก้โค้ดเกม
ทุกอย่างเรียกได้ 2 ทาง: **พิมพ์ในแชต (F12)** หรือ **สั่งผ่าน Claude Code CLI** (tool `unity_*`)

> เครื่องมือกลุ่มนี้ "อ่านอย่างเดียว" (observe) — ไม่แก้ scene/asset จึงไม่ต้องเปิด Allow Write
> ส่วนใหญ่ทำงานเฉพาะตอน **Play Mode** (กด Play แล้ว)

---

## สารบัญ
1. [👁 Watcher — ดูค่าตัวแปรสด](#1)
2. [🔔 watch_alert — เตือนเมื่อค่าเพี้ยน](#2)
3. [🎞 watch_animator — ดู Animation สด](#3)
4. [💥 event_log — ดักชน/trigger](#4)
5. [🐢 slow-mo (timescale)](#5)
6. [เครื่องมือ runtime อื่นที่มีอยู่](#6)
7. [ตัวอย่าง workflow แก้บั๊กตอนเล่น](#7)

---

<a name="1"></a>
## 1. 👁 Watcher — ดูค่าตัวแปรสด + trend

อ่านค่า field/property บน GameObject **ทุก 0.5 วินาที** ระหว่าง Play → โชว์ค่าปัจจุบัน + ทิศทาง (↑ ↓ =) + history 10 ค่า
เหมาะกับ "ค่าควรเปลี่ยน แต่สงสัยว่ามันเปลี่ยนจริงไหม / ค้างไหม / เพี้ยนไหม"

### วิธีที่ง่ายสุด — แผง 👁 Watch (คลิกล้วน)
1. กดปุ่ม **👁 Watch** บนแถบเครื่องมือในหน้าต่างแชต → เปิดแผง
2. เลือก GameObject ใน Hierarchy → ชื่อจะโผล่ในแผง
3. พิมพ์แค่ **ชื่อ field** (เช่น `currentHp`) แล้วกด **＋ Watch** หรือ Enter
4. กด Play → ค่าขยับสด + มี **sparkline** (กราฟแท่งเล็ก) ของค่าตัวเลข
5. **✕** = ลบทีละตัว · **Clear all** = ล้างหมด

> component หาให้อัตโนมัติ (สแกนหา component ที่มี field นั้น สคริปต์เกมก่อน) — **ไม่ต้องรู้ชื่อ component**

### สั่งผ่านแชต / AI
```
watch currentHp                     ← ระบุแค่ field! object = ตัวที่เลือก, component = auto
```
หรือ JSON command:
```json
{"command":"watch_add","field":"currentHp"}
{"command":"watch_add","objectName":"Player","field":"Damageable.Hp.Value"}   // ระบุ object + nested path
{"command":"watch_get"}      // ดูค่าปัจจุบันทั้งหมด + trend + history
{"command":"watch_clear"}    // ลบ watch ทั้งหมด
```

| อาการ | watch อะไร |
|---|---|
| HP/Mana ไม่ลด/ไม่ขึ้น | `Hp.Value` / `Mp.Value` |
| โดนตีแล้ว HP ไม่ขยับ (shield ดูดไหม?) | watch ทั้ง HP + shield พร้อมกัน |
| เดินช้า/เร็วผิด | `MoveSpeed.Value` |
| state ค้าง | ชื่อ field state ตรงๆ |
| network ไม่ sync | watch ค่าเดียวกันบน P1 เทียบ P2 |

---

<a name="2"></a>
## 2. 🔔 watch_alert — เตือนเมื่อค่าเข้าเงื่อนไข

เหมือน watch แต่ตั้ง **เงื่อนไข** ไว้ → พอค่า "กลายเป็นจริง" (ขอบขาขึ้น) ระบบ **log warning + นับจำนวนครั้ง**
เหมาะจับบั๊กที่เกิดแวบเดียวจนตาไม่ทัน เช่น ค่าติดลบชั่วขณะ, พุ่งเกิน cap, เปลี่ยนผิดจังหวะ

```json
{"command":"watch_alert","field":"currentHp","op":"lt","value":0}     // HP < 0 เมื่อไหร่ เตือน
{"command":"watch_alert","field":"comboCount","op":"changed"}          // ค่าเปลี่ยนเมื่อไหร่ เตือน
```
- **op:** `lt` `lte` `gt` `gte` `eq` `ne` (เทียบกับ `value`) หรือ `changed` (ค่าเปลี่ยน) — ใส่สัญลักษณ์ `< <= > >= == !=` ก็ได้
- ผลโชว์ใน `watch_get` (`alertCount`, `alerting`) และในแผง 👁 Watch เป็น **🔔 + จำนวนครั้ง** (แดงเมื่อเคยทริก)
- ทุกครั้งที่ทริกจะมี warning ใน Console: `[Watch alert] <key> < 0 → -5 (ครั้งที่ 2)`

---

<a name="3"></a>
## 3. 🎞 watch_animator — ดู Animation สด

ดู **state ปัจจุบัน** (ชื่อ clip + เวลา + บอกตอน transition) หรือ **ค่า parameter** ของ Animator
เหมาะกับ animation ค้าง / ไม่ทริกเกอร์ / blend ผิด ที่ field ปกติ watch ไม่ถึง

```json
{"command":"watch_animator","objectName":"Player"}                  // ไม่ใส่ param = ดู state ปัจจุบัน
{"command":"watch_animator","objectName":"Player","param":"Speed"}  // ดูค่า parameter (Float/Int/Bool/Trigger)
```
ผลโผล่ในแผง 👁 Watch / `watch_get` เหมือน watch ปกติ เช่น `Run t=0.42` หรือ `Idle →(transition)`

---

<a name="4"></a>
## 4. 💥 event_log — ดักการชน / trigger สด

แปะ "probe" ชั่วคราวลง object → ดัก `OnCollisionEnter/Exit` และ `OnTriggerEnter/Exit` สด
ตอบคำถาม "ทำไมไม่โดน / โดนซ้ำ / trigger ไม่ทำงาน" โดยไม่ต้องแก้โค้ด

```json
{"command":"event_log","name":"Player"}   // ไม่ใส่ name = ตัวที่เลือก · แปะ probe
{"command":"event_log_get"}                // ดูเหตุการณ์ล่าสุด (time, kind, self, other)
{"command":"event_log_clear"}              // ถอด probe + ล้าง buffer
```
- object ต้องมี **Collider** (+ **Rigidbody** สำหรับ collision · `isTrigger` สำหรับ trigger)
- probe ถูก **ถอดอัตโนมัติเมื่อออก Play** (เป็น debug tool ไม่ค้าง ไม่เข้า build)

---

<a name="5"></a>
## 5. 🐢 slow-mo (timescale)

ปรับความเร็วเวลาเพื่อดูเหตุการณ์เร็วๆ (hit / knockback / spawn) แบบสโลว์ ระหว่างที่ watch ยังเก็บค่าไปด้วย
```json
{"command":"play_control","action":"timescale","scale":0.2}   // 0.2 = ช้าลง 5 เท่า
{"command":"play_control","action":"timescale","scale":1}     // กลับปกติ (exit ก็คืนให้)
```

---

<a name="6"></a>
## 6. เครื่องมือ runtime อื่นที่มีอยู่แล้ว

| เครื่องมือ | เช็คอะไรตอนเล่น |
|---|---|
| `capture_state` | snapshot: isPlaying/paused/timeScale/**frameCount**/fps/network/spikes — เรียก 2 ครั้งดู frameCount ขยับไหม = จับ **ค้าง/freeze** |
| `get_exceptions` | exception/error สด 50 รายการ + stack + นับซ้ำ |
| `perf_audit` / `perf_worst` | FPS / census / spike + ตัวการ สดๆ |
| `diagnose_deep` (🔬 Deep 5s) | CPU method+บรรทัด + GC + Network ราย object |
| `memory_snapshot` | mono / native / GFX / GC gen |
| `fusion_stats` | tick / RTT / bandwidth / resim (multiplayer) |
| `inspect_object` (deep=true) | private field + property ทุกตัว ณ จังหวะนั้น |
| `count_components` | นับ active vs inactive (pool) |
| `play_control` | pause / **step** / resume — หยุดเฟรมแล้ว inspect |
| `capture_screenshot` | เห็นจอเกมจริง (+overlay UI) |
| `hot_reload` | แก้โค้ดระหว่างเล่นไม่ต้องหยุด |

---

<a name="7"></a>
## 7. ตัวอย่าง workflow แก้บั๊กตอนเล่น

**เคส: "โดนตีแล้ว HP บางทีติดลบ แล้วเด้งกลับ"**
1. เลือก Player → เปิดแผง 👁 Watch → พิมพ์ `currentHp` กด ＋
2. ตั้ง alert กันพลาด: `{"command":"watch_alert","field":"currentHp","op":"lt","value":0}`
3. กด Play → เล่นให้โดนตี → ดู sparkline + 🔔 (ถ้าทริก = เคยติดลบจริง)
4. อยากเห็นชัด → `{"command":"play_control","action":"timescale","scale":0.15}` แล้วโดนตีอีกที
5. เจอจังหวะติดลบ → `read_script` ดูโค้ดจุดหัก HP → แก้ด้วย `edit_script` → `compile` → ทดสอบซ้ำ

**เคส: "skill ไม่โดนศัตรู"**
1. `{"command":"event_log","name":"SkillHitbox"}` → กด Play → ปล่อยสกิล
2. `{"command":"event_log_get"}` → ถ้าไม่มี `triggerEnter` เลย = collider/layer/ติด isTrigger ผิด
3. `inspect_object` ดู Collider + layer ของ hitbox/ศัตรู → แก้

---

> เอกสารฉบับเต็มของทุก tool: ดู `Server~/commands.json` (รายการ + พารามิเตอร์) และ `CHANGELOG.md`
