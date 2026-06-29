using System;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace MCPBridge
{
    // run_tests / get_test_results — drive the Unity Test Runner from the MCP.
    //
    // ⚠️ ASMDEF ISOLATION: this lives in its OWN assembly (MCPBridge.Editor.TestRunner) that references
    //   UnityEditor.TestRunner. If com.unity.test-framework is NOT installed, only THIS assembly fails to
    //   load (a console warning) — the main MCPBridge.Editor (all other tools) is untouched. The main
    //   assembly never references this one; we wire in via delegates set on MCPHandlers at load time.
    //
    // Results arrive asynchronously over many frames (and PlayMode tests trigger a domain reload), so we
    //   persist progress in SessionState (survives reloads within the session). run_tests starts a run and
    //   returns immediately; poll get_test_results until status == "done".
    [InitializeOnLoad]
    static class MCPTestRunner
    {
        const string KEY_STATUS = "MCPBridge.Tests.Status";   // "idle" | "running" | "done"
        const string KEY_MODE   = "MCPBridge.Tests.Mode";
        const string KEY_COUNTS = "MCPBridge.Tests.Counts";   // "pass,fail,skip,total"
        const string KEY_FAILS  = "MCPBridge.Tests.Fails";    // entries joined by REC_SEP, name/msg by FLD_SEP
        const int    MAX_FAILS  = 50;

        // ASCII control chars as delimiters — can't appear in a test name/message
        const char REC_SEP = '';   // between failure entries
        const char FLD_SEP = '';   // between name and message

        static TestRunnerApi _api;

        static MCPTestRunner()
        {
            // register once per domain load — callbacks fire for any Execute (registry is global)
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.RegisterCallbacks(new Callbacks());

            // hand the handlers to the main assembly (it only holds nullable delegates)
            MCPHandlers.RunTestsHandler       = RunTests;
            MCPHandlers.GetTestResultsHandler = GetResults;
        }

        // ── /tests/run ──────────────────────────────────────────────────────
        static string RunTests(string body)
        {
            try
            {
                string mode = ExtractField(body, "mode") ?? "edit";
                string filter = ExtractField(body, "filter");
                bool play = mode.StartsWith("play", StringComparison.OrdinalIgnoreCase);

                if (SessionState.GetString(KEY_STATUS, "idle") == "running")
                    return "{\"error\":\"มีชุดเทสต์กำลังรันอยู่ — poll get_test_results จน status=done ก่อน\"}";

                // reset state
                SessionState.SetString(KEY_STATUS, "running");
                SessionState.SetString(KEY_MODE, play ? "play" : "edit");
                SessionState.SetString(KEY_COUNTS, "");
                SessionState.SetString(KEY_FAILS, "");

                var f = new Filter { testMode = play ? TestMode.PlayMode : TestMode.EditMode };
                if (!string.IsNullOrEmpty(filter)) f.groupNames = new[] { filter };

                _api.Execute(new ExecutionSettings(f));
                return $"{{\"started\":true,\"mode\":\"{(play ? "play" : "edit")}\"," +
                       (string.IsNullOrEmpty(filter) ? "" : $"\"filter\":\"{MCPHandlers.EscapeJsonPublic(filter)}\",") +
                       "\"note\":\"poll get_test_results จน status=done\"}";
            }
            catch (Exception e)
            {
                SessionState.SetString(KEY_STATUS, "idle");
                return $"{{\"error\":\"{MCPHandlers.EscapeJsonPublic(e.Message)}\"}}";
            }
        }

        // ── /tests/results ──────────────────────────────────────────────────
        static string GetResults(string body)
        {
            string status = SessionState.GetString(KEY_STATUS, "idle");
            string mode   = SessionState.GetString(KEY_MODE, "");
            string fails  = SessionState.GetString(KEY_FAILS, "");

            var sb = new StringBuilder("{");
            sb.Append($"\"status\":\"{status}\",\"mode\":\"{mode}\",");

            if (status == "done")
            {
                var c = SessionState.GetString(KEY_COUNTS, "").Split(',');
                sb.Append($"\"passed\":{NumOr0(c, 0)},\"failed\":{NumOr0(c, 1)},\"skipped\":{NumOr0(c, 2)},\"total\":{NumOr0(c, 3)},");
            }

            sb.Append("\"failures\":[");
            if (!string.IsNullOrEmpty(fails))
            {
                var entries = fails.Split(REC_SEP);
                bool first = true;
                foreach (var e in entries)
                {
                    if (string.IsNullOrEmpty(e)) continue;
                    var parts = e.Split(FLD_SEP);
                    if (!first) sb.Append(",");
                    sb.Append($"{{\"test\":\"{MCPHandlers.EscapeJsonPublic(parts[0])}\",\"message\":\"{MCPHandlers.EscapeJsonPublic(parts.Length > 1 ? parts[1] : "")}\"}}");
                    first = false;
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string NumOr0(string[] arr, int i) => (arr.Length > i && int.TryParse(arr[i], out var n)) ? n.ToString() : "0";

        // tiny field extractor (avoid pulling MCPHandlers.ParseReq which is private)
        static string ExtractField(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ── Test Runner callbacks (fire on the main thread over frames) ───────
        class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                SessionState.SetString(KEY_STATUS, "running");
                SessionState.SetString(KEY_FAILS, "");
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                int pass = result.PassCount, fail = result.FailCount, skip = result.SkipCount;
                int incon = result.InconclusiveCount;
                int total = pass + fail + skip + incon;
                SessionState.SetString(KEY_COUNTS, $"{pass},{fail},{skip},{total}");
                SessionState.SetString(KEY_STATUS, "done");
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.Test.IsSuite) return;   // นับเฉพาะ leaf test
                if (result.TestStatus != TestStatus.Failed) return;

                string acc = SessionState.GetString(KEY_FAILS, "");
                int count = string.IsNullOrEmpty(acc) ? 0 : acc.Split(REC_SEP).Length;
                if (count >= MAX_FAILS) return;

                string name = result.Test != null ? result.Test.FullName : "(unknown)";
                string msg  = Trunc(result.Message, 300);
                string line = name + FLD_SEP + msg;
                SessionState.SetString(KEY_FAILS, string.IsNullOrEmpty(acc) ? line : acc + REC_SEP + line);
            }

            static string Trunc(string s, int max) =>
                string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s.Substring(0, max) + "…" : s);
        }
    }
}
