using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// คอยจับ FPS drop / frame spike อัตโนมัติระหว่าง Play Mode (ไม่ต้องกดเอง)
    /// เฟรมไหนช้ากว่าเกณฑ์ → บันทึกเป็น spike event พร้อม frame index + GC
    /// ดูผลได้ตอนกด 📊 Attach Profiler (Snapshot จะแนบ spike report ให้)
    /// </summary>
    [InitializeOnLoad]
    public static class SpikeMonitor
    {
        struct Spike
        {
            public double Ms;
            public long Gc;
            public int FrameIndex;
            public string Cause;   // ตัวการ (top GC/CPU ของเฟรมนั้น)
        }

        static ProfilerRecorder _mainThread, _gcAlloc;
        static readonly List<Spike> _spikes = new List<Spike>();
        static bool _active;
        static int _lastFrame = -1;
        const int MAX_SPIKES = 40;

        static SpikeMonitor()
        {
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static float ThresholdMs => EditorPrefs.GetFloat("DeltaMCP_SpikeMs", 33.3f); // < 30fps = spike

        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _spikes.Clear();
                _lastFrame = -1;
                _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
                _gcAlloc    = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
                _active = true;
                EditorApplication.update += Sample;
            }
            else if (state == PlayModeStateChange.ExitingPlayMode && _active)
            {
                EditorApplication.update -= Sample;
                _mainThread.Dispose();
                _gcAlloc.Dispose();
                _active = false;
            }
        }

        static void Sample()
        {
            if (!_active || !Application.isPlaying) return;

            double ms = _mainThread.LastValue * 1e-6; // ns → ms
            if (ms < ThresholdMs) return;

            // กันบันทึกเฟรมเดิมซ้ำ
            int frame = ProfilerDriver.lastFrameIndex;
            if (frame == _lastFrame) return;
            _lastFrame = frame;

            // บันทึกแบบเบาๆ เท่านั้น (ms/gc/frame) — ห้ามอ่าน call-tree ที่นี่!
            // (เดิมเรียก TopContributor ทุก spike → ตอน FPS ต่ำทุกเฟรมเป็น spike → วน loop ทำ FPS ตกหนัก)
            _spikes.Add(new Spike { Ms = ms, Gc = _gcAlloc.LastValue, FrameIndex = frame, Cause = null });
            if (_spikes.Count > MAX_SPIKES) _spikes.RemoveAt(0);
        }

        /// <summary>worst เท่านั้น — spike หนักสุด + โค้ดตัวการ (สำหรับปุ่ม/keyword "worst")</summary>
        public static string WorstReport()
        {
            if (_spikes.Count == 0)
                return "=== Worst Spike ===\n(ยังไม่พบ FPS drop ระหว่างเล่นรอบนี้ — ดี! เกณฑ์ > " +
                       ThresholdMs.ToString("F0") + " ms)";
            int worstIdx = 0;
            for (int i = 1; i < _spikes.Count; i++)
                if (_spikes[i].Ms > _spikes[worstIdx].Ms) worstIdx = i;
            var w = _spikes[worstIdx];

            var sb = new StringBuilder("=== Worst Spike (หนักสุดของรอบเล่นนี้) ===\n");
            sb.AppendLine($"{w.Ms:F1} ms (~{1000.0 / w.Ms:F0} FPS), GC {Bytes(w.Gc)} @frame #{w.FrameIndex}");
            sb.AppendLine($"(จับได้ {_spikes.Count} spike รวม — นี่คืออันหนักสุด)");
            string src = ProfilerDeepReader.FrameCulpritWithSource(w.FrameIndex);
            if (!string.IsNullOrEmpty(src)) { sb.AppendLine("\n-- ตัวการ + โค้ดจริง --"); sb.Append(src); }
            else sb.AppendLine("(เฟรมนี้หา call-tree ไม่ได้แล้ว — ลองกด worst ใหม่ตอนเพิ่งเกิด spike)");
            return sb.ToString();
        }

        /// <summary>รายงาน spike ที่จับได้ + เจาะลึกตัวการของเฟรมที่แย่สุด</summary>
        public static string GetReport()
        {
            if (_spikes.Count == 0)
                return "\n=== Auto Spike Monitor ===\n(ยังไม่พบ FPS drop — เกณฑ์ปัจจุบัน > " + ThresholdMs.ToString("F0") +
                       " ms. กด Play แล้วเล่นให้เกิดการกระตุกก่อน)";

            var sb = new StringBuilder();
            sb.AppendLine($"\n=== Auto Spike Monitor — พบ {_spikes.Count} spike ระหว่างเล่น ===");
            sb.AppendLine($"(เกณฑ์: เฟรม > {ThresholdMs:F0} ms = ต่ำกว่า {1000f / ThresholdMs:F0} FPS)");

            // หา spike ที่แย่สุด
            int worstIdx = 0;
            for (int i = 1; i < _spikes.Count; i++)
                if (_spikes[i].Ms > _spikes[worstIdx].Ms) worstIdx = i;
            var worst = _spikes[worstIdx];

            // อ่านตัวการ "ตอนนี้" (on-demand) เฉพาะเฟรมแย่สุด — ไม่ทำใน Sample loop
            string worstCause = ProfilerDeepReader.TopContributor(worst.FrameIndex);
            sb.AppendLine($"\nแย่สุด: {worst.Ms:F1} ms (~{1000.0 / worst.Ms:F0} FPS), GC {Bytes(worst.Gc)} @frame #{worst.FrameIndex}");
            if (!string.IsNullOrEmpty(worstCause))
                sb.AppendLine($"  ตัวการ → {worstCause}");

            // list spike (เรียงแย่สุดก่อน เอา 8 อัน) — คำนวณ cause เฉพาะ 3 อันแรกพอ (กันช้า)
            sb.AppendLine("\nSpike ที่จับได้:");
            var sorted = new List<Spike>(_spikes);
            sorted.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            for (int i = 0; i < Mathf.Min(sorted.Count, 8); i++)
            {
                var sp = sorted[i];
                string cause = i < 3 ? ProfilerDeepReader.TopContributor(sp.FrameIndex) : null;
                string tail = string.IsNullOrEmpty(cause) ? $"GC {Bytes(sp.Gc)}" : cause;
                sb.AppendLine($"  {sp.Ms:F0}ms (~{1000.0 / sp.Ms:F0}fps)  ←  {tail}");
            }

            // ping ปัจจุบัน
            string net = ProfilerDeepReader.NetworkLine();
            if (!string.IsNullOrEmpty(net))
                sb.AppendLine($"\nNetwork: {net}");

            return sb.ToString();
        }

        static string Bytes(long b)
        {
            if (b <= 0) return "0 B";
            if (b > 1 << 20) return $"{b / (double)(1 << 20):F2} MB";
            if (b > 1 << 10) return $"{b / (double)(1 << 10):F1} KB";
            return $"{b} B";
        }
    }
}
