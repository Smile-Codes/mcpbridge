using System.Text;
using Unity.Profiling;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace DeltaUnity.MCP
{
    /// <summary>
    /// อ่านค่า Profiler จริงเป็นตัวเลข (ไม่ใช่จากรูป)
    /// - Runtime (Play Mode): อ่าน live จาก ProfilerRecorder
    /// - หลังหยุดเกม: อ่านจากเฟรมล่าสุดที่ Profiler capture ไว้ ผ่าน ProfilerDriver
    /// </summary>
    [InitializeOnLoad]
    public static class ProfilerReader
    {
        // recorder สำหรับค่า live ระหว่าง Play Mode
        static ProfilerRecorder _mainThread, _renderThread;
        static ProfilerRecorder _drawCalls, _setPassCalls, _batches, _triangles, _vertices;
        static ProfilerRecorder _gcAlloc, _gcReserved, _totalReserved, _totalUsed;
        static ProfilerRecorder _texMem, _meshMem;
        static bool _active;

        static ProfilerReader()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }

        static void OnBeforeReload()
        {
            // ProfilerDriver.enabled ค้างอยู่หลัง play session → ทำให้ profiler เก็บข้อมูลระหว่าง reload ด้วย
            if (_active) CacheLastValues();
            try { ProfilerDriver.enabled = false; } catch { }
        }

        static bool _hasCaptured;   // เคยเก็บค่าจาก Play Mode หรือยัง

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) StartRecorders();
            // ไม่ dispose ตอนหยุด — เก็บค่าเฟรมสุดท้ายไว้ให้ Claude วิเคราะห์ได้
            else if (state == PlayModeStateChange.EnteredEditMode && _active) CacheLastValues();
        }

        const int HISTORY = 300;   // เก็บ ~5 วินาทีที่ 60fps เพื่อคำนวณสถิติ + spike

        // ปิดระบบ profiler ชั่วคราว: ProfilerDriver.enabled=true + recorders ทำให้ Unity กิน CPU/RAM เพิ่มตอน Play
        // ตั้ง true เพื่อเปิดกลับ (live stats / perf keyword / 📍GC / 🔬Deep จะกลับมาทำงาน)
        const bool ENABLED = false;

        static void StartRecorders()
        {
            if (!ENABLED) return;   // ปิดชั่วคราว → ไม่เปิด Profiler / ไม่ start recorder = ไม่มี overhead ตอน Play
            // เปิด profiler recording เอง → ProfilerDriver มี CPU call-tree ให้ DeepAnalysis อ่าน
            // โดยไม่ต้องเปิดหน้าต่าง Profiler. (นี่คือ profiling ปกติ — ไม่ใช่ Deep Profile ตัวหนัก
            // ที่ทำเครื่องค้าง; deepProfiling ปล่อยเป็น false)
            try
            {
                UnityEditorInternal.ProfilerDriver.enabled = true;
                UnityEditorInternal.ProfilerDriver.deepProfiling = false;
                // เก็บ managed callstack ของทุก GC.Alloc → ชี้ method+บรรทัดที่ alloc จริงได้
                // (default ปิด เพื่อไม่ให้มี overhead ตอนเล่นเทสต์ปกติ — เปิดผ่าน toggle ข้างปุ่ม Profiler)
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = AllocCallstacks;
            }
            catch { }

            _mainThread    = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", HISTORY);
            _renderThread  = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "Render Thread", HISTORY);
            _drawCalls     = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "Draw Calls Count");
            _setPassCalls  = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "SetPass Calls Count");
            _batches       = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "Batches Count");
            _triangles     = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "Triangles Count");
            _vertices      = ProfilerRecorder.StartNew(ProfilerCategory.Render,   "Vertices Count");
            _gcAlloc       = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "GC Allocated In Frame", HISTORY);
            _gcReserved    = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "GC Reserved Memory");
            _totalReserved = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "Total Reserved Memory");
            _totalUsed     = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "Total Used Memory");
            _texMem        = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "Texture Memory");
            _meshMem       = ProfilerRecorder.StartNew(ProfilerCategory.Memory,   "Mesh Memory");
            _active = true;
        }

        static string _cachedSnapshot;  // ค่าเฟรมสุดท้ายก่อนหยุดเกม

        // เก็บค่าเป็นข้อความก่อน recorder จะ invalid แล้ว dispose
        static void CacheLastValues()
        {
            _cachedSnapshot = BuildLiveReport("Last frame before stop");
            _hasCaptured = true;

            _mainThread.Dispose(); _renderThread.Dispose();
            _drawCalls.Dispose(); _setPassCalls.Dispose(); _batches.Dispose();
            _triangles.Dispose(); _vertices.Dispose();
            _gcAlloc.Dispose(); _gcReserved.Dispose();
            _totalReserved.Dispose(); _totalUsed.Dispose();
            _texMem.Dispose(); _meshMem.Dispose();
            _active = false;
        }

        /// <summary>
        /// คืนค่า Profiler เป็นข้อความสรุป ส่งให้ Claude วิเคราะห์ได้เลย
        /// </summary>
        public static string Snapshot()
        {
            string summary;
            if (Application.isPlaying && _active)
                summary = BuildLiveReport("LIVE — Play Mode");
            else if (_hasCaptured && !string.IsNullOrEmpty(_cachedSnapshot))
                summary = _cachedSnapshot;
            else
                return "=== Unity Profiler ===\n(No data yet — press Play so the recorders can capture Profiler values, " +
                       "then click this button during play or after stopping.)";

            // ต่อด้วย: spike + network monitor (jitter/ping spike) + เจาะลึก call tree
            return summary + SpikeMonitor.GetReport() + (NetMonitor.GetReport() ?? "") + ProfilerDeepReader.DeepAnalysis();
        }

        // ── report เฉพาะ GC (ปุ่ม GC-only) — GC/frame ปัจจุบัน + top allocators + โค้ด ──
        public static string GcReport()
        {
            var sb = new StringBuilder("=== GC (เฉพาะ memory allocation) ===\n");
            if (IsLive)
                sb.AppendLine($"GC Allocated / frame: {Bytes(_gcAlloc.LastValue)}  (target: ~0 — alloc ทุกเฟรม = stutter)");
            if (!AllocCallstacks)
                sb.AppendLine("(หมายเหตุ: เปิด toggle 📍 GC ในแชต แล้วเล่นซ้ำจุดที่ alloc → จะชี้ method+บรรทัดที่ alloc จริงได้)");
            sb.Append(ProfilerDeepReader.GcReport());
            return sb.ToString();
        }

        // ── report เฉพาะ Network (ปุ่ม Net-only) — jitter monitor + RTT/bandwidth ──
        public static string NetworkReport()
        {
            var sb = new StringBuilder();
            string mon = NetMonitor.GetReport();
            if (!string.IsNullOrEmpty(mon)) sb.Append(mon).Append('\n');
            sb.Append(ProfilerDeepReader.NetworkReport());
            return sb.ToString();
        }

        // ── GC allocation callstack capture (toggle ข้างปุ่ม Profiler) ──────
        // default ปิด: เล่นเทสต์ปกติไม่มี overhead · เปิด: ทุก GC.Alloc เก็บ callstack
        //  → ProfilerDeepReader ชี้ method+บรรทัดที่ alloc จริงได้ (ไม่ต้อง Deep Profile)
        // flip ได้ตอน Play โดยไม่ต้องรีเพลย์ (callstack จะเก็บเฉพาะ alloc หลังเปิด)
        const string ALLOC_CS_PREF = "DeltaMCP_AllocCallstacks";
        public static bool AllocCallstacks
        {
            get => EditorPrefs.GetBool(ALLOC_CS_PREF, false);
            set
            {
                EditorPrefs.SetBool(ALLOC_CS_PREF, value);
                try { UnityEngine.Profiling.Profiler.enableAllocationCallstacks = value; } catch { }
            }
        }

        // GC toggle = บังคับปิดทุกครั้งที่ domain reload (เปิด Unity ใหม่ / recompile)
        // — เปิดได้เฉพาะตอน Play เท่านั้น (ดูปุ่มใน MCPChatWindow) จึงไม่คงสถานะข้าม reload
        [InitializeOnLoadMethod]
        static void ForceAllocCallstacksOffOnReload()
        {
            EditorPrefs.SetBool(ALLOC_CS_PREF, false);
            try { UnityEngine.Profiling.Profiler.enableAllocationCallstacks = false; } catch { }
        }

        // ── Live stats (สำหรับแผง real-time ในแชต) ──────────────────────────
        public static bool IsLive => Application.isPlaying && _active;

        // คืนค่าปัจจุบันแบบกระชับ 2 บรรทัด สำหรับแสดงสด
        public static string LiveStats()
        {
            if (!IsLive) return null;
            double ms = _mainThread.LastValue * 1e-6;
            double fps = ms > 0 ? 1000.0 / ms : 0;
            // ลด string length: ตัด RTT/ping, short names (DC=DrawCalls, GC=allocation)
            return
                $"FPS {fps:F0} | {ms:F1}ms | DC {_drawCalls.LastValue} | SetPass {_setPassCalls.LastValue}\n" +
                $"GC {Bytes(_gcAlloc.LastValue)} | Tris {(_triangles.LastValue / 1000):F1}K | Mem {Bytes(_totalUsed.LastValue)}";
        }

        // ค่า FPS ปัจจุบัน (ใช้ทำแถบสี)
        public static float CurrentFps()
        {
            if (!IsLive) return 0;
            double ms = _mainThread.LastValue * 1e-6;
            return ms > 0 ? (float)(1000.0 / ms) : 0;
        }

        static string BuildLiveReport(string tag)
        {
            var sb = new StringBuilder();

            // อ่าน history เป็น ms
            var frameMs = SamplesMs(_mainThread);
            var renderMs = SamplesMs(_renderThread);
            var gcBytes = SamplesRaw(_gcAlloc);

            int n = frameMs.Length;
            sb.AppendLine($"=== Unity Profiler ({tag}) ===");
            sb.AppendLine($"Sampled {n} frames (~{n / 60.0:F1}s window)");

            if (n > 0)
            {
                System.Array.Sort(frameMs);
                double avg = Mean(frameMs);
                double median = Percentile(frameMs, 50);
                double p95 = Percentile(frameMs, 95);
                double p99 = Percentile(frameMs, 99);
                double worst = frameMs[n - 1];
                double onePctLow = OnePercentLowFps(frameMs);
                double tenthPctLow = LowFps(frameMs, 0.001);  // 0.1% low (spike หนักสุด)

                // นับ stutter = เฟรมที่ช้ากว่า median 1.5 เท่า
                int stutters = 0;
                foreach (var f in frameMs) if (f > median * 1.5) stutters++;

                // CPU-bound vs GPU/Render-bound (เทียบ main vs render thread)
                double mainAvg = avg, renderAvg = Mean(renderMs);
                string bound = renderAvg > mainAvg * 1.15
                    ? $"GPU/Render-bound (render {renderAvg:F1}ms > main {mainAvg:F1}ms) → แก้ rendering: draw call/overdraw/shader/tris"
                    : $"CPU-bound (main {mainAvg:F1}ms ≥ render {renderAvg:F1}ms) → แก้ script/physics/GC";

                sb.AppendLine("\n-- Frame Time (Main Thread) --");
                sb.AppendLine($"Avg: {avg:F2} ms (~{1000.0 / avg:F0} FPS)  |  Median: {median:F2} ms");
                sb.AppendLine($"p95: {p95:F2} ms  |  p99: {p99:F2} ms  |  Worst: {worst:F2} ms");
                sb.AppendLine($"1% Low: {onePctLow:F0} FPS  |  0.1% Low: {tenthPctLow:F0} FPS  ← ตัวชี้ความลื่น");
                sb.AppendLine($"Stutter frames (>1.5x median): {stutters}/{n}  ({100.0 * stutters / n:F1}%)");
                sb.AppendLine($"Render Thread avg: {renderAvg:F2} ms");
                sb.AppendLine($"\n** Bound: {bound} **");   // ← บอกว่าควรแก้ฝั่งไหน
            }

            if (gcBytes.Length > 0)
            {
                double totalGc = 0; int gcFrames = 0; double maxGc = 0;
                foreach (var g in gcBytes) { totalGc += g; if (g > 0) gcFrames++; if (g > maxGc) maxGc = g; }
                sb.AppendLine("\n-- GC Allocation --");
                sb.AppendLine($"Frames with GC alloc: {gcFrames}/{gcBytes.Length}  ({100.0 * gcFrames / gcBytes.Length:F0}%)");
                sb.AppendLine($"Avg/frame: {Bytes((long)(totalGc / gcBytes.Length))}  |  Worst frame: {Bytes((long)maxGc)}");
                sb.AppendLine($"Total over window: {Bytes((long)totalGc)}  ← ยิ่งสูง = GC ทำงานบ่อย = กระตุก");
            }

            sb.AppendLine("\n-- Rendering (current) --");
            sb.AppendLine($"Draw Calls: {_drawCalls.LastValue}  |  SetPass: {_setPassCalls.LastValue}  |  Batches: {_batches.LastValue}");
            sb.AppendLine($"Triangles: {_triangles.LastValue:N0}  |  Vertices: {_vertices.LastValue:N0}");

            sb.AppendLine("\n-- Memory --");
            sb.AppendLine($"Total Used: {Bytes(_totalUsed.LastValue)}  |  Reserved: {Bytes(_totalReserved.LastValue)}");
            sb.AppendLine($"GC Reserved: {Bytes(_gcReserved.LastValue)}  |  Texture: {Bytes(_texMem.LastValue)}  |  Mesh: {Bytes(_meshMem.LastValue)}");
            return sb.ToString();
        }

        // ── Helpers ──────────────────────────────────────────────────────
        static double[] SamplesMs(ProfilerRecorder rec)
        {
            int c = rec.Count;
            var arr = new double[c];
            for (int i = 0; i < c; i++) arr[i] = rec.GetSample(i).Value * 1e-6; // ns → ms
            return arr;
        }

        static double[] SamplesRaw(ProfilerRecorder rec)
        {
            int c = rec.Count;
            var arr = new double[c];
            for (int i = 0; i < c; i++) arr[i] = rec.GetSample(i).Value;
            return arr;
        }

        static double Mean(double[] a)
        {
            if (a.Length == 0) return 0;
            double s = 0; foreach (var v in a) s += v; return s / a.Length;
        }

        // a ต้อง sort แล้ว
        static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            int idx = Mathf.Clamp((int)(p / 100.0 * sorted.Length), 0, sorted.Length - 1);
            return sorted[idx];
        }

        // เฉลี่ย FPS ของ 1% เฟรมที่แย่ที่สุด (มาตรฐานวัดความลื่น)
        static double OnePercentLowFps(double[] sorted) => LowFps(sorted, 0.01);

        // เฉลี่ย FPS ของ fraction เฟรมที่แย่สุด (0.01 = 1%, 0.001 = 0.1%)
        static double LowFps(double[] sorted, double fraction)
        {
            if (sorted.Length == 0) return 0;
            int count = Mathf.Max(1, (int)(sorted.Length * fraction));
            double sum = 0;
            for (int i = sorted.Length - count; i < sorted.Length; i++) sum += sorted[i];
            double avgMs = sum / count;
            return avgMs > 0 ? 1000.0 / avgMs : 0;
        }

        // CPU vs GPU bound แบบสั้น (สำหรับแผง Live)
        public static string BoundStatus()
        {
            if (!IsLive) return "";
            double mainMs = _mainThread.LastValue * 1e-6;
            double renderMs = _renderThread.LastValue * 1e-6;
            return renderMs > mainMs * 1.15 ? "GPU-bound" : "CPU-bound";
        }

        static string Bytes(long b)
        {
            if (b <= 0) return "0 B";
            if (b > 1 << 30) return $"{b / (double)(1 << 30):F2} GB";
            if (b > 1 << 20) return $"{b / (double)(1 << 20):F2} MB";
            if (b > 1 << 10) return $"{b / (double)(1 << 10):F2} KB";
            return $"{b} B";
        }
    }
}
