using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// Deep capture "ชั่วคราว" (time-boxed) — เจาะลึก CPU + GC + Network ในรอบเดียว
    ///
    /// กด 🔬 Deep → เปิด Deep Profile + GC callstack + Net monitor → จับ N วินาที → ปิดเอง → callback ผล:
    ///   • CPU: self-time ระดับ method จริง → ไฟล์:บรรทัด + โค้ด (ProfilerDeepReader.CpuDeepReport)
    ///   • GC : บรรทัดที่ alloc จริง (ProfilerDeepReader.GcCallstackReport)
    ///   • Net: bandwidth ราย NetworkObject/prefab (byte จริงจาก Fusion — NetStatsReader)
    ///
    /// Deep Profile หนักมาก (instrument ทุก method call) → "ห้ามเปิดค้าง" ต้องจับแวบเดียวแล้วปิด
    /// ทำงานเฉพาะ Editor Play Mode (Mono instrument live ได้). ไม่กระทบ build.
    /// (toggle 📍 GC = ดัก GC ต่อเนื่องแบบเบา สำหรับ keyword gc — คนละ use case กับปุ่มนี้)
    /// </summary>
    [InitializeOnLoad]
    public static class CpuDeepCapture
    {
        // reset deep profiling ก่อน domain reload ทุกครั้ง
        // ถ้าไม่ทำ: ProfilerDriver.deepProfiling ค้าง true ข้ามรอบ → domain reload ช้า 2+ นาที
        static CpuDeepCapture()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;

            // self-heal: ทุกครั้งที่ Unity โหลด/reload → ถ้า deepProfiling ค้าง true (เช่นเคย force-close
            // ตอนค้าง หรือ state persist ข้าม session) → reset ทันที กันค้างซ้ำตั้งแต่ compile แรก
            // (ตอนโหลด IsCapturing = false เสมอ → ปลอดภัยที่จะปิด)
            try { if (ProfilerDriver.deepProfiling) ProfilerDriver.deepProfiling = false; } catch { }
        }

        static void OnBeforeReload()
        {
            if (!IsCapturing) return;
            EditorApplication.update -= Tick;
            IsCapturing = false;
            Restore();
            try { NetStatsReader.Cancel(); } catch { }   // กัน net monitor ค้างข้าม reload
            _onDone = null;
        }

        public static bool  IsCapturing { get; private set; }
        public static float Progress01  { get; private set; }   // 0..1 สำหรับโชว์ที่ปุ่ม
        // วินาทีที่เหลือ (นับถอยหลัง) สำหรับโชว์ที่ปุ่ม เช่น "⏺ 3s"
        public static int   SecondsLeft => Mathf.Max(0, Mathf.CeilToInt(_duration * (1f - Progress01)));

        static double _startTime;
        static float  _duration;
        static int    _startFrame;
        static bool   _prevDeep;
        static bool   _prevAllocCs;
        static Action<string> _onDone;

        /// <summary>เริ่มจับ deep CPU เป็นเวลา seconds วินาที แล้วเรียก onDone(report) เมื่อเสร็จ</summary>
        public static void Start(float seconds, Action<string> onDone)
        {
            // guard ทุกกรณี = return เงียบ (ไม่เรียก onDone) → onDone จะถูกเรียก "เฉพาะตอนจับครบ" เท่านั้น
            // → ฝั่ง UI ที่ auto-send ใน onDone จะไม่ส่งพรวดตอนยังไม่ได้เล่น/เปิด deep ไม่ได้
            if (IsCapturing) return;
            if (!Application.isPlaying) return;   // UI เช็ค isPlaying + เตือนผู้ใช้เองแล้ว

            try
            {
                _prevDeep    = ProfilerDriver.deepProfiling;
                _prevAllocCs = UnityEngine.Profiling.Profiler.enableAllocationCallstacks;
                ProfilerDriver.enabled = true;
                ProfilerDriver.deepProfiling = true;                              // CPU: instrument ทุก method
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = true; // GC: เก็บ callstack ทุก alloc
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CpuDeepCapture] เปิด Deep Profile ไม่ได้: " + e.Message);
                return;
            }

            try { NetStatsReader.BeginMonitor(); } catch { }   // Net: ดัก bandwidth ราย object (ถ้ามี Fusion runner)

            _onDone     = onDone;
            _duration   = Mathf.Max(1f, seconds);
            _startTime  = EditorApplication.timeSinceStartup;
            _startFrame = ProfilerDriver.lastFrameIndex;   // เฟรมก่อนเริ่ม deep (วิเคราะห์เฉพาะหลังจากนี้)
            IsCapturing = true;
            Progress01  = 0f;
            EditorApplication.update += Tick;
        }

        // คืนค่า deep profiling + alloc callstack กลับเป็นเดิม (กันค้าง/กัน overhead ค้าง)
        // ถ้า toggle 📍 GC เปิดอยู่ก่อน → _prevAllocCs = true → คืนค่าเป็น true (ไม่ไปปิด toggle ของ user)
        static void Restore()
        {
            try { ProfilerDriver.deepProfiling = _prevDeep; } catch { }
            try { UnityEngine.Profiling.Profiler.enableAllocationCallstacks = _prevAllocCs; } catch { }
        }

        static void Tick()
        {
            double elapsed = EditorApplication.timeSinceStartup - _startTime;
            Progress01 = Mathf.Clamp01((float)(elapsed / _duration));

            try { NetStatsReader.PumpCollect(); } catch { }   // ขับ Fusion ให้สะสม snapshot ทุกเฟรม (กัน snapNull)

            // ยังไม่ครบเวลา และยังเล่นอยู่ → จับต่อ
            if (elapsed < _duration && Application.isPlaying)
                return;

            // ครบเวลา (หรือออก Play Mode) → ปิด deep + วิเคราะห์ช่วงเฟรมที่จับได้
            EditorApplication.update -= Tick;
            IsCapturing = false;

            int endFrame = ProfilerDriver.lastFrameIndex;
            Restore();

            string report;
            try
            {
                // CPU method-level + GC บรรทัดที่ alloc + Network bandwidth ราย object (จากช่วงที่จับ)
                string cpu = ProfilerDeepReader.CpuDeepReport(_startFrame, endFrame);
                string gc  = ProfilerDeepReader.GcCallstackReport();
                string net = null;
                try { net = NetStatsReader.EndMonitorAndReport(); } catch { }
                report = cpu
                       + (string.IsNullOrEmpty(gc)  ? "" : "\n" + gc)
                       + (string.IsNullOrEmpty(net) ? "" : "\n" + net);
            }
            catch (Exception e) { report = "=== Deep Analysis ===\n(วิเคราะห์ไม่ได้: " + e.Message + ")"; }

            var cb = _onDone;
            _onDone = null;
            cb?.Invoke(report);
        }
    }
}
