using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DeltaUnity.MCP
{
    /// <summary>
    /// คอยเก็บ RTT (ping) ต่อเนื่องระหว่าง Play → คำนวณ jitter, min/avg/max, ping spike
    /// jitter (ping แกว่ง) สำคัญกว่า ping เฉลี่ย — แกว่งทำให้เกมกระตุก/rubber-band
    /// </summary>
    [InitializeOnLoad]
    public static class NetMonitor
    {
        static readonly List<double> _rtt = new List<double>();   // ms, ~last 60 samples
        static int _spikeCount;
        static double _lastSample;
        static bool _active;
        const int MAX = 60;
        const double SAMPLE_INTERVAL = 0.5;  // วินาที
        const double SPIKE_FACTOR = 1.8;     // เกิน avg 1.8 เท่า = ping spike

        static NetMonitor()
        {
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _rtt.Clear(); _spikeCount = 0; _lastSample = 0; _active = true;
                EditorApplication.update += Sample;
            }
            else if (state == PlayModeStateChange.ExitingPlayMode && _active)
            {
                EditorApplication.update -= Sample;
                _active = false;
            }
        }

        static void Sample()
        {
            if (!_active || !Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastSample < SAMPLE_INTERVAL) return;
            _lastSample = now;

            double rtt = ProfilerDeepReader.LocalRttMs();
            if (rtt < 0) return;  // ยังไม่ connect

            // ตรวจ spike เทียบ avg ที่ผ่านมา
            if (_rtt.Count >= 5)
            {
                double avg = 0; foreach (var v in _rtt) avg += v; avg /= _rtt.Count;
                if (rtt > avg * SPIKE_FACTOR && rtt > 80) _spikeCount++;
            }

            _rtt.Add(rtt);
            if (_rtt.Count > MAX) _rtt.RemoveAt(0);
        }

        public static string GetReport()
        {
            if (_rtt.Count == 0) return null;  // offline / ยังไม่มีข้อมูล net

            double min = double.MaxValue, max = 0, sum = 0;
            foreach (var v in _rtt) { if (v < min) min = v; if (v > max) max = v; sum += v; }
            double avg = sum / _rtt.Count;

            // jitter = ส่วนเบี่ยงเบนมาตรฐานของ RTT
            double var = 0; foreach (var v in _rtt) var += (v - avg) * (v - avg);
            double jitter = Math.Sqrt(var / _rtt.Count);

            var sb = new StringBuilder();
            sb.AppendLine($"\n=== Network Monitor ({_rtt.Count} samples ~{_rtt.Count * SAMPLE_INTERVAL:F0}s) ===");
            sb.AppendLine($"RTT (ping): avg {avg:F0} ms  |  min {min:F0}  |  max {max:F0} ms");
            sb.AppendLine($"Jitter: {jitter:F0} ms  ← {(jitter > 30 ? "สูง! ping แกว่ง = กระตุก/rubber-band" : "นิ่งดี")}");
            sb.AppendLine($"Ping spikes: {_spikeCount}  {(_spikeCount > 3 ? "← network ไม่เสถียร" : "")}");
            return sb.ToString();
        }
    }
}
