using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// Network pinpoint (Phase 1) — ชี้ว่า NetworkObject/prefab ตัวไหน "sync เปลือง bandwidth" ด้วย byte จริง
    ///
    /// อาศัยระบบ statistics ของ Fusion 2 (เข้าถึงผ่าน reflection — asmdef ไม่ ref Fusion):
    ///   • runner.TryGetFusionStatistics(out mgr)              → global snapshot (In/OutBandwidth, packets, RTT)
    ///   • mgr.ObjectStatisticsManager                          → per-object manager
    ///   • objMgr.MonitorNetworkObjectStatistics(NetworkId,bool) → เปิด/ปิด ดักราย object
    ///   • objMgr.GetNetworkObjectStatistics(NetworkId, out snap)→ NetworkObjectStatisticsSnapshot
    ///       snap.InBandwidth / OutBandwidth / In·OutPackets    → byte จริงต่อ object
    ///
    /// ใช้แบบ "capture window": BeginMonitor() ตอนเริ่ม → เล่นสักครู่ → EndMonitorAndReport()
    /// (ผูกเข้า window 5 วิ ของ 🔬 Deep) — ทุกอย่าง try/catch กันพังถ้า API/เวอร์ชันต่าง
    /// </summary>
    public static class NetStatsReader
    {
        // เก็บ id+name ของ object ที่กำลัง monitor (รอบ capture นี้)
        struct Monitored { public object Id; public string Name; }
        static readonly List<Monitored> _monitored = new List<Monitored>();
        static object _objMgr;        // Fusion.Statistics.NetworkObjectStatisticsManager
        static object _runner;        // Fusion.NetworkRunner
        static System.Reflection.MethodInfo _collectM;   // CollectStatistics() — cache ไว้ pump ทุกเฟรม
        static bool   _active;
        static string _diag;          // บอกว่า step ไหนสำเร็จ/พัง (โชว์ในผลถ้าไม่มีข้อมูล)

        const int MAX_MONITOR = 500;  // cap กัน overhead ตอน scene มี NetworkObject เยอะมาก

        // เริ่มดักราย object — คืน true ถ้าเริ่มได้ (มี runner + Fusion stats พร้อม)
        public static bool BeginMonitor()
        {
            Reset();
            try
            {
                if (!Application.isPlaying) { _diag = "ไม่ได้อยู่ Play Mode"; return false; }

                var runnerType = FindType("Fusion.NetworkRunner");
                if (runnerType == null) { _diag = "หา type Fusion.NetworkRunner ไม่เจอ"; return false; }
                _runner = UnityEngine.Object.FindObjectOfType(runnerType);
                if (_runner == null) { _diag = "หา NetworkRunner instance ในซีนไม่เจอ"; return false; }

                // TryGetFusionStatistics(out FusionStatisticsManager)
                var tryGet = runnerType.GetMethod("TryGetFusionStatistics");
                if (tryGet == null) { _diag = "method TryGetFusionStatistics ไม่มี (เวอร์ชัน Fusion ต่าง?)"; return false; }
                var a = new object[] { null };
                bool ok = false;
                try { ok = (bool)tryGet.Invoke(_runner, a); }
                catch (Exception e) { _diag = "TryGetFusionStatistics throw: " + e.Message; return false; }
                object mgr = a[0];
                if (!ok || mgr == null) { _diag = $"TryGetFusionStatistics คืน {ok} / mgr={(mgr==null?"null":"ok")} — Fusion stats ยังไม่ active บน runner"; return false; }

                _objMgr = mgr.GetType().GetProperty("ObjectStatisticsManager")?.GetValue(mgr);
                if (_objMgr == null) { _diag = "ObjectStatisticsManager เป็น null"; return false; }
                _collectM = _objMgr.GetType().GetMethod("CollectStatistics", Type.EmptyTypes);   // cache → pump ทุกเฟรม

                var noType = FindType("Fusion.NetworkObject");
                if (noType == null) { _diag = "หา type Fusion.NetworkObject ไม่เจอ"; return false; }
                var idProp = noType.GetProperty("Id");
                var monitorM = _objMgr.GetType().GetMethod("MonitorNetworkObjectStatistics");
                if (idProp == null || monitorM == null) { _diag = $"idProp={(idProp==null?"null":"ok")} monitorM={(monitorM==null?"null":"ok")}"; return false; }

                int totalFound = 0;
                foreach (var no in UnityEngine.Object.FindObjectsOfType(noType))
                {
                    totalFound++;
                    if (_monitored.Count >= MAX_MONITOR) break;
                    try
                    {
                        var go = (no as Component)?.gameObject;
                        if (go == null || !go.activeInHierarchy) continue;
                        object id = idProp.GetValue(no);
                        monitorM.Invoke(_objMgr, new object[] { id, true });
                        _monitored.Add(new Monitored { Id = id, Name = go.name });
                    }
                    catch (Exception e) { _diag = "monitor obj throw: " + e.Message; }
                }

                _active = _monitored.Count > 0;
                _diag = $"พบ NetworkObject {totalFound} ตัว, monitor {_monitored.Count} ตัว";
                if (!_active) _diag += " (ไม่มี object active ให้ดัก)";
                return _active;
            }
            catch (Exception e) { _diag = "BeginMonitor throw: " + e.Message; Reset(); return false; }
        }

        // ผลวินิจฉัยล่าสุด (เผื่อ BeginMonitor fail → CpuDeepCapture เอาไปโชว์)
        public static string LastDiag => _diag;

        // เรียกทุกเฟรมระหว่าง capture window (จาก CpuDeepCapture.Tick) → ขับให้ Fusion สะสม snapshot
        // (ถ้า collect แค่ตอนจบ snapshot จะว่าง = snapNull) — ต้อง pump ต่อเนื่องเหมือนที่ FusionStatistics panel ทำ
        public static void PumpCollect()
        {
            if (!_active || _collectM == null) return;
            try { _collectM.Invoke(_objMgr, null); } catch { }
        }

        // จบ capture → อ่าน per-object bandwidth → จัดกลุ่มตาม prefab → report + ปิด monitor
        public static string EndMonitorAndReport()
        {
            // BeginMonitor ไม่สำเร็จ → บอกสาเหตุ (เช่น ไม่ได้อยู่แมตช์ / Fusion stats ไม่ active)
            if (!_active)
                return "\n-- Network: ยังเก็บไม่ได้ (" + (_diag ?? "ไม่ได้เริ่ม monitor") + ") --";

            var sb = new StringBuilder();
            int read = 0, withData = 0, snapNull = 0, getThrow = 0; string firstSnapType = null;
            try
            {
                var objMgrType = _objMgr.GetType();
                try { _collectM?.Invoke(_objMgr, null); } catch { }   // collect ครั้งสุดท้าย (pump มาทุกเฟรมแล้ว)

                var getM = objMgrType.GetMethod("GetNetworkObjectStatistics");
                if (getM == null) return "\n-- Network pinpoint (debug) --\n  GetNetworkObjectStatistics method ไม่เจอ";

                // group ตามชื่อ prefab (ตัด (Clone)/เลขท้าย) → รวม byte
                var byPrefab = new Dictionary<string, (double inBw, double outBw, int count)>();
                foreach (var m in _monitored)
                {
                    try
                    {
                        var a = new object[] { m.Id, null };
                        getM.Invoke(_objMgr, a);            // ส่งคืนผ่าน out param a[1] (return อาจเป็น bool/void)
                        read++;
                        object snap = a[1];
                        if (snap == null) { snapNull++; continue; }
                        var st = snap.GetType();
                        if (firstSnapType == null) firstSnapType = st.Name;
                        double inBw  = ToD(st.GetProperty("InBandwidth")?.GetValue(snap));
                        double outBw = ToD(st.GetProperty("OutBandwidth")?.GetValue(snap));
                        if (inBw > 0 || outBw > 0) withData++;
                        string key = NormalizeName(m.Name);
                        byPrefab.TryGetValue(key, out var acc);
                        byPrefab[key] = (acc.inBw + inBw, acc.outBw + outBw, acc.count + 1);
                    }
                    catch { getThrow++; }
                }

                if (byPrefab.Count > 0 && withData > 0)
                {
                    sb.AppendLine("\n-- Network Bandwidth per prefab (byte จริงจาก Fusion — เรียงตาม Out) --");
                    sb.AppendLine("  prefab | จำนวน | In | Out");
                    foreach (var kv in byPrefab.OrderByDescending(x => x.Value.outBw).Take(12))
                        sb.AppendLine($"  {kv.Key} | x{kv.Value.count} | in {Bytes(kv.Value.inBw)} | out {Bytes(kv.Value.outBw)}");
                    sb.AppendLine("  (ตัวที่ Out สูง = sync เปลืองสุด → พิจารณาลด sync rate / ลด [Networked] / culling / interpolation)");
                }
                else if (snapNull > 0 || getThrow > 0)
                {
                    // อ่าน snapshot ไม่ได้จริงๆ (ผิดปกติ) → บอกตัวเลขให้ dev เช็ก
                    sb.AppendLine($"\n-- Network: อ่าน per-object ไม่ได้ (snapNull={snapNull} err={getThrow}/{_monitored.Count}) — แจ้ง dev --");
                }
                else
                {
                    // อ่านได้ แต่ทุก object bandwidth = 0 (network activity ต่ำในช่วงที่จับ)
                    sb.AppendLine($"\n-- Network: object sync bandwidth ต่ำมาก ({_monitored.Count} objects, ~0 B) — ลองจับตอน action เยอะกว่า --");
                }

                // global snapshot (เสริม) — RTT/bandwidth รวม/resimulations
                string global = GlobalLine();
                if (!string.IsNullOrEmpty(global)) { sb.AppendLine("\n-- Network global --"); sb.Append(global); }
            }
            catch (Exception e) { sb.AppendLine("\n(net stats error: " + e.Message + ")"); }
            finally { StopMonitor(); }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        // อ่าน global snapshot (In/OutBandwidth รวม, packets, resimulations) จาก FusionStatisticsManager
        static string GlobalLine()
        {
            try
            {
                var runnerType = _runner?.GetType();
                if (runnerType == null) return null;
                var a = new object[] { null };
                if (!(bool)runnerType.GetMethod("TryGetFusionStatistics").Invoke(_runner, a)) return null;
                object mgr = a[0];
                object snap = mgr?.GetType().GetProperty("CompleteSnapshot")?.GetValue(mgr);
                if (snap == null) return null;
                var st = snap.GetType();
                double inBw  = ToD(st.GetProperty("InBandwidth")?.GetValue(snap));
                double outBw = ToD(st.GetProperty("OutBandwidth")?.GetValue(snap));
                double inUpd = ToD(st.GetProperty("InObjectUpdates")?.GetValue(snap));
                double outUpd= ToD(st.GetProperty("OutObjectUpdates")?.GetValue(snap));
                double resim = ToD(st.GetProperty("Resimulations")?.GetValue(snap));
                double rtt   = ToD(st.GetProperty("RoundTripTime")?.GetValue(snap));
                var sb = new StringBuilder();
                sb.AppendLine($"  In {Bytes(inBw)} | Out {Bytes(outBw)} | objUpdates in/out {inUpd:F0}/{outUpd:F0} | resim {resim:F0} | RTT {rtt * 1000:F0}ms");
                return sb.ToString();
            }
            catch { return null; }
        }

        static void StopMonitor()
        {
            try
            {
                if (_objMgr != null)
                {
                    var t = _objMgr.GetType();
                    var clear = t.GetMethod("ClearMonitoredNetworkObjects");
                    if (clear != null) clear.Invoke(_objMgr, null);
                    else
                    {
                        var monitorM = t.GetMethod("MonitorNetworkObjectStatistics");
                        foreach (var m in _monitored)
                            try { monitorM?.Invoke(_objMgr, new object[] { m.Id, false }); } catch { }
                    }
                }
            }
            catch { }
            Reset();
        }

        // ยกเลิกการ monitor โดยไม่อ่านผล (ใช้ตอน domain reload กลางคัน — กัน monitor ค้าง)
        public static void Cancel() { if (_active) StopMonitor(); }

        static void Reset() { _monitored.Clear(); _objMgr = null; _runner = null; _collectM = null; _active = false; }

        // ── helpers ──
        static double ToD(object o)
        {
            try { return o == null ? 0 : Convert.ToDouble(o); } catch { return 0; }
        }

        // ตัด (Clone) + เลขท้าย → จัดกลุ่ม "Creep_1, Creep_2" เป็น "Creep"
        static string NormalizeName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "?";
            n = n.Replace("(Clone)", "").Trim();
            int i = n.Length;
            while (i > 0 && (char.IsDigit(n[i - 1]) || n[i - 1] == ' ' || n[i - 1] == '_' || n[i - 1] == '-')) i--;
            return i > 0 ? n.Substring(0, i) : n;
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        static string Bytes(double b)
        {
            if (b <= 0) return "0 B";
            if (b > 1 << 20) return $"{b / (1 << 20):F2} MB";
            if (b > 1 << 10) return $"{b / (1 << 10):F1} KB";
            return $"{b:F0} B";
        }
    }
}
