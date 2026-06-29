using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MCPBridge
{
    // Apply / Edit Pack — ให้ AI "ลงมือแก้" ได้จริง ไม่ใช่แค่วินิจฉัย:
    //   edit_script (แก้ .cs เดิม), assign_reference (ต่อสาย object/asset เข้า field),
    //   run_batch (หลาย action ใน turn เดียว), delete_asset, set_import_settings,
    //   capture_screenshot, build_player, git_status
    public static partial class MCPHandlers
    {
        // ── Edit Script — แก้ไฟล์ .cs ที่มีอยู่แบบ find/replace (Apply primitive) ──
        // ต่างจาก create_script ที่ overwrite ทั้งไฟล์ — อันนี้แก้เฉพาะจุด ปลอดภัยกว่าเวลาแก้บั๊ก
        static string EditScript(string body)
        {
            var data = ParseReq<EditScriptRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                // resolve path: รับได้ทั้งชื่อ script หรือ asset path ตรงๆ
                string assetPath = !string.IsNullOrEmpty(data.path) ? data.path : CodebaseIndex.ResolvePath(data.name);
                if (string.IsNullOrEmpty(assetPath))
                    return $"{{\"error\":\"script not found: {EscapeJson(data.name)}\"}}";

                string absPath = ToAbsolutePath(assetPath);
                if (!File.Exists(absPath))
                    return $"{{\"error\":\"file not found on disk: {EscapeJson(assetPath)}\"}}";

                if (string.IsNullOrEmpty(data.find))
                    return "{\"error\":\"'find' required — the exact existing text to replace. Use create_script to overwrite a whole file.\"}";

                string content = File.ReadAllText(absPath);
                int occurrences = CountOccurrences(content, data.find);
                if (occurrences == 0)
                    return $"{{\"error\":\"'find' text not present in {EscapeJson(assetPath)} — read_script first to get the exact text\"}}";
                if (occurrences > 1 && !data.all)
                    return $"{{\"error\":\"'find' matches {occurrences} places — pass all=true to replace every occurrence, or include more surrounding context to make it unique\"}}";

                string replace = data.replace ?? "";
                string updated = data.all
                    ? content.Replace(data.find, replace)
                    : ReplaceFirst(content, data.find, replace);

                File.WriteAllText(absPath, updated, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                int replaced = data.all ? occurrences : 1;
                return $"{{\"edited\":\"{EscapeJson(assetPath)}\",\"replacements\":{replaced}," +
                       $"\"note\":\"แก้ไฟล์แล้ว — call unity_compile แล้ว poll compile_status จน ready ก่อนทำงานต่อ\"}}";
            });
        }

        // ── Assign Reference — ต่อสาย object/asset เข้า field ของ component ──
        // เติมช่องว่างของ set_property (ทำได้แค่ค่า primitive) → ตอนนี้ AI ต่อ reference เองได้
        static string AssignReference(string body)
        {
            var data = ParseReq<AssignRefRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";

                Component comp = string.IsNullOrEmpty(data.component)
                    ? go.transform
                    : go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name.Equals(data.component, StringComparison.OrdinalIgnoreCase));
                if (comp == null) return $"{{\"error\":\"Component not found: {EscapeJson(data.component)}\"}}";

                var so = new SerializedObject(comp);
                var prop = so.FindProperty(data.property) ?? FindByDisplayName(so, data.property);
                if (prop == null) return $"{{\"error\":\"Property not found: {EscapeJson(data.property)}\"}}";
                if (prop.propertyType != SerializedPropertyType.ObjectReference)
                    return $"{{\"error\":\"'{EscapeJson(data.property)}' ไม่ใช่ object reference — ใช้ set_property สำหรับค่า primitive\"}}";

                // หา type ที่ field ต้องการ (เพื่อหยิบ component ที่ถูกตัวจาก target GameObject)
                Type fieldType = ResolveFieldType(comp.GetType(), prop.name);

                UnityEngine.Object target = ResolveReferenceTarget(data.target, data.asset, fieldType);
                if (target == null)
                    return $"{{\"error\":\"target ไม่เจอ/แปลงไม่ได้: {EscapeJson(data.target)}\"}}";

                Undo.RecordObject(comp, "MCP AssignReference");
                prop.objectReferenceValue = target;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(comp);
                return $"{{\"assigned\":\"{EscapeJson(data.property)}\",\"target\":\"{EscapeJson(target.name)}\",\"type\":\"{EscapeJson(target.GetType().Name)}\"}}";
            });
        }

        // resolve target: scene GameObject (+ pick component) หรือ asset (path/ชื่อ)
        static UnityEngine.Object ResolveReferenceTarget(string target, bool asset, Type fieldType)
        {
            if (string.IsNullOrEmpty(target)) return null;

            // 1) asset โดยตรง — path เต็ม หรือชื่อ (ค้นด้วย AssetDatabase)
            if (asset || target.StartsWith("Assets/"))
            {
                string path = target.StartsWith("Assets/") ? target : null;
                if (path == null)
                {
                    var guids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(target));
                    if (guids.Length > 0) path = AssetDatabase.GUIDToAssetPath(guids[0]);
                }
                if (string.IsNullOrEmpty(path)) return null;
                Type loadType = (fieldType != null && typeof(UnityEngine.Object).IsAssignableFrom(fieldType)) ? fieldType : typeof(UnityEngine.Object);
                return AssetDatabase.LoadAssetAtPath(path, loadType) ?? AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            }

            // 2) scene object
            var go = GameObject.Find(target);
            if (go == null) return null;
            if (fieldType == null || fieldType == typeof(GameObject)) return go;
            if (typeof(Component).IsAssignableFrom(fieldType))
            {
                var c = go.GetComponent(fieldType);
                if (c != null) return c;
            }
            // field รับ Component base / interface → ลองตัวแรกที่เข้ากันได้
            if (typeof(UnityEngine.Object).IsAssignableFrom(fieldType))
            {
                foreach (var c in go.GetComponents<Component>())
                    if (c != null && fieldType.IsInstanceOfType(c)) return c;
            }
            return go;
        }

        // หา field type จาก serialized property name (ไล่ขึ้น base class ด้วย)
        static Type ResolveFieldType(Type t, string fieldName)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var fi = cur.GetField(fieldName, F);
                if (fi != null) return fi.FieldType;
            }
            return null;
        }

        // ── Run Batch — หลาย command ใน response เดียว (ลด round-trip สร้าง scene ทั้งชุด) ──
        // แต่ละ sub-command วิ่งผ่าน Dispatch ปกติ → ผ่าน write-guard/rate-limit ของตัวเองครบ
        static string RunBatch(string body)
        {
            string arrayRaw = ExtractJsonArrayRaw(body, "commands");
            if (arrayRaw == null)
                return "{\"error\":\"'commands' array required — e.g. {\\\"command\\\":\\\"run_batch\\\",\\\"commands\\\":[{...},{...}]}\"}";

            var items = SplitTopLevelObjects(arrayRaw);
            if (items.Count == 0)
                return "{\"error\":\"'commands' is empty\"}";
            if (items.Count > 50)
                return $"{{\"error\":\"batch too large ({items.Count}) — สูงสุด 50 ต่อครั้ง\"}}";

            var sb = new StringBuilder("{\"batch\":[");
            int ok = 0, fail = 0;
            for (int i = 0; i < items.Count; i++)
            {
                string itemJson = items[i];
                string cmdName = ExtractCommandNameLocal(itemJson);
                string result;
                if (string.IsNullOrEmpty(cmdName))
                    result = "{\"error\":\"missing 'command' in batch item\"}";
                else
                    result = Dispatch(cmdName, itemJson, rateLimited: false);   // re-dispatch: ผ่าน write-guard ปกติ, ข้าม rate-limit (batch = 1 action)

                bool isErr = result != null && result.Contains("\"error\"");
                if (isErr) fail++; else ok++;
                if (i > 0) sb.Append(",");
                sb.Append($"{{\"command\":\"{EscapeJson(cmdName)}\",\"ok\":{(isErr ? "false" : "true")},\"result\":{WrapJson(result)}}}");
            }
            sb.Append($"],\"total\":{items.Count},\"ok\":{ok},\"failed\":{fail}}}");
            return sb.ToString();
        }

        // ── Delete Asset — ลบไฟล์ asset (write-gated, มี guard) ──
        static string DeleteAsset(string body)
        {
            var data = ParseReq<PathRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                if (string.IsNullOrEmpty(data.path))
                    return "{\"error\":\"path required (e.g. Assets/Prefabs/Old.prefab)\"}";
                if (!data.path.StartsWith("Assets/"))
                    return "{\"error\":\"path ต้องอยู่ใต้ Assets/ เท่านั้น (กันลบนอกโปรเจกต์)\"}";
                if (IsThirdParty(data.path))
                    return $"{{\"error\":\"ปฏิเสธ: '{EscapeJson(data.path)}' เป็น third-party/plugin — ลบเองไม่ปลอดภัย\"}}";
                if (AssetDatabase.IsValidFolder(data.path))
                    return "{\"error\":\"นี่คือโฟลเดอร์ — เครื่องมือนี้ลบเฉพาะไฟล์ asset (กันลบทั้งโฟลเดอร์โดยไม่ตั้งใจ)\"}";
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(data.path) == null && !File.Exists(ToAbsolutePath(data.path)))
                    return $"{{\"error\":\"asset not found: {EscapeJson(data.path)}\"}}";

                bool ok = AssetDatabase.MoveAssetToTrash(data.path);   // ไป OS trash — กู้คืนได้ ปลอดภัยกว่า DeleteAsset
                return ok
                    ? $"{{\"deleted\":\"{EscapeJson(data.path)}\",\"note\":\"ย้ายไป trash ของ OS — กู้คืนได้\"}}"
                    : $"{{\"error\":\"ลบไม่สำเร็จ: {EscapeJson(data.path)}\"}}";
            });
        }

        // ── Set Import Settings — แก้ texture importer ตามที่ audit_textures แนะนำ ──
        // 0 / "" = ไม่แตะ field นั้น (แก้เฉพาะที่ระบุ)
        static string SetImportSettings(string body)
        {
            var data = ParseReq<ImportSettingsRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                if (string.IsNullOrEmpty(data.path))
                    return "{\"error\":\"path required (Assets/.../tex.png)\"}";
                var imp = AssetImporter.GetAtPath(data.path) as TextureImporter;
                if (imp == null)
                    return $"{{\"error\":\"ไม่ใช่ texture หรือไม่พบ importer: {EscapeJson(data.path)}\"}}";

                var changed = new List<string>();
                if (data.maxSize > 0)               { imp.maxTextureSize = data.maxSize; changed.Add($"maxSize={data.maxSize}"); }
                if (!string.IsNullOrEmpty(data.compression))
                {
                    imp.textureCompression = data.compression.ToLowerInvariant() switch
                    {
                        "none" or "uncompressed" => TextureImporterCompression.Uncompressed,
                        "low"                    => TextureImporterCompression.CompressedLQ,
                        "high"                   => TextureImporterCompression.CompressedHQ,
                        _                         => TextureImporterCompression.Compressed,
                    };
                    changed.Add($"compression={imp.textureCompression}");
                }
                if (!string.IsNullOrEmpty(data.readable))  { imp.isReadable = ParseBool(data.readable); changed.Add($"readable={imp.isReadable}"); }
                if (!string.IsNullOrEmpty(data.mipmaps))   { imp.mipmapEnabled = ParseBool(data.mipmaps); changed.Add($"mipmaps={imp.mipmapEnabled}"); }
                if (!string.IsNullOrEmpty(data.crunch))    { imp.crunchedCompression = ParseBool(data.crunch); changed.Add($"crunch={imp.crunchedCompression}"); }

                if (changed.Count == 0)
                    return "{\"error\":\"ไม่มี setting ที่ระบุ — ส่ง maxSize / compression / readable / mipmaps / crunch อย่างน้อย 1\"}";

                imp.SaveAndReimport();
                return $"{{\"path\":\"{EscapeJson(data.path)}\",\"changed\":\"{EscapeJson(string.Join(", ", changed))}\"}}";
            });
        }

        // ── Capture Screenshot — render Game/Scene camera เป็น PNG (ให้ AI เห็นผลจริง) ──
        static string CaptureScreenshot(string body)
        {
            var data = ParseReq<ScreenshotRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                string which = string.IsNullOrEmpty(data.view) ? "game" : data.view.ToLowerInvariant();
                Camera cam = which == "scene"
                    ? (SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null)
                    : (Camera.main ?? UnityEngine.Object.FindObjectOfType<Camera>());
                if (cam == null)
                    return $"{{\"error\":\"ไม่พบกล้องสำหรับ view '{which}' (game ต้องมี Camera ใน scene / scene ต้องเปิด Scene View)\"}}";

                int w = data.width  > 0 ? Mathf.Clamp(data.width, 64, 4096)  : 1280;
                int h = data.height > 0 ? Mathf.Clamp(data.height, 64, 4096) : 720;

                var rt = new RenderTexture(w, h, 24);
                var prevTarget = cam.targetTexture;
                var prevActive = RenderTexture.active;
                Texture2D tex = null;
                try
                {
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    tex.Apply();
                }
                finally
                {
                    cam.targetTexture = prevTarget;
                    RenderTexture.active = prevActive;
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }

                byte[] png = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);

                string dir = Path.Combine(Application.dataPath, "..", "Library", "DeltaMCP", "screenshots");
                Directory.CreateDirectory(dir);
                string file = Path.GetFullPath(Path.Combine(dir, $"shot_{which}_{DateTime.Now:HHmmss}_{Time.frameCount}.png"));
                File.WriteAllBytes(file, png);
                return $"{{\"screenshot\":\"{EscapeJson(file)}\",\"view\":\"{which}\",\"size\":[{w},{h}],\"bytes\":{png.Length}}}";
            });
        }

        // ── Build Player — trigger build (blocking; ใช้จากแชตดีกว่า bridge เพราะนานเกิน timeout) ──
        static string BuildPlayer(string body)
        {
            var data = ParseReq<BuildRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                if (EditorApplication.isPlaying || EditorApplication.isPaused)
                    return "{\"error\":\"ออก Play Mode ก่อน build\"}";
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    return "{\"error\":\"Unity กำลัง compile/import — รอ ready ก่อน build\"}";
                if (string.IsNullOrEmpty(data.path))
                    return "{\"error\":\"path required — โฟลเดอร์/ไฟล์ output (เช่น Builds/win/Game.exe)\"}";

                BuildTarget target = ParseBuildTarget(data.target);
                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);

                // scenes: ใช้ที่ระบุ หรือ fallback เป็น scene ที่ enabled ใน Build Settings
                string[] scenes;
                if (!string.IsNullOrEmpty(data.scenes))
                    scenes = data.scenes.Split(';', ',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                else
                    scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                if (scenes.Length == 0)
                    return "{\"error\":\"ไม่มี scene — เพิ่มใน Build Settings หรือส่ง scenes (คั่นด้วย ;)\"}";

                // ต้องสลับ active target ก่อน build ถ้าไม่ตรง (ไม่งั้น BuildPlayer error)
                if (EditorUserBuildSettings.activeBuildTarget != target &&
                    !EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
                    return $"{{\"error\":\"สลับ build target เป็น {target} ไม่ได้ — module อาจยังไม่ติดตั้ง\"}}";

                var opts = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = data.path,
                    target = target,
                    targetGroup = group,
                    options = data.dev ? BuildOptions.Development : BuildOptions.None,
                };

                var report = BuildPipeline.BuildPlayer(opts);
                var summary = report.summary;
                bool ok = summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;
                return $"{{\"build\":\"{summary.result}\",\"ok\":{(ok ? "true" : "false")}," +
                       $"\"target\":\"{target}\",\"output\":\"{EscapeJson(data.path)}\"," +
                       $"\"sizeMB\":{summary.totalSize / 1048576f:F1},\"errors\":{summary.totalErrors},\"warnings\":{summary.totalWarnings}," +
                       $"\"timeSec\":{summary.totalTime.TotalSeconds:F1},\"scenes\":{scenes.Length}}}";
            });
        }

        // ── Git Status — ให้ AI เห็นว่าแก้อะไรไปบ้าง ก่อนแนะนำ commit (read-only) ──
        static string GitStatus(string body)
        {
            return ExecuteOnMainThread(() =>
            {
                string repo = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string branchOut = RunGit("rev-parse --abbrev-ref HEAD", repo, out _);
                string statusOut = RunGit("status --porcelain", repo, out int code);
                if (code < 0)
                    return $"{{\"error\":\"รัน git ไม่ได้ — ติดตั้ง git + อยู่ใน repo หรือเปล่า? ({EscapeJson(branchOut)})\"}}";

                var lines = statusOut.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
                var sb = new StringBuilder("[");
                int shown = Math.Min(lines.Count, 100);
                for (int i = 0; i < shown; i++)
                {
                    if (i > 0) sb.Append(",");
                    string code2 = lines[i].Length >= 2 ? lines[i].Substring(0, 2).Trim() : "";
                    string file = lines[i].Length > 3 ? lines[i].Substring(3) : lines[i];
                    sb.Append($"{{\"status\":\"{EscapeJson(code2)}\",\"file\":\"{EscapeJson(file)}\"}}");
                }
                sb.Append("]");
                return $"{{\"branch\":\"{EscapeJson(branchOut.Trim())}\",\"changedCount\":{lines.Count},\"changes\":{sb}}}";
            });
        }

        static string RunGit(string args, string workDir, out int exitCode)
        {
            exitCode = -1;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", args)
                {
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return "git ไม่ start";
                string outp = p.StandardOutput.ReadToEnd();
                string errp = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return "git timeout"; }
                exitCode = p.ExitCode;
                return string.IsNullOrEmpty(outp) ? errp : outp;
            }
            catch (Exception e) { return e.Message; }
        }

        // ── Helpers (เฉพาะไฟล์นี้) ──────────────────────────────────────────
        static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int n = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { n++; idx += needle.Length; }
            return n;
        }

        static string ReplaceFirst(string s, string find, string replace)
        {
            int idx = s.IndexOf(find, StringComparison.Ordinal);
            return idx < 0 ? s : s.Substring(0, idx) + replace + s.Substring(idx + find.Length);
        }

        static bool ParseBool(string v) => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);

        static BuildTarget ParseBuildTarget(string s) => (s ?? "").ToLowerInvariant() switch
        {
            "android"            => BuildTarget.Android,
            "ios"                => BuildTarget.iOS,
            "webgl"              => BuildTarget.WebGL,
            "win" or "win64" or "windows" => BuildTarget.StandaloneWindows64,
            "win32"              => BuildTarget.StandaloneWindows,
            "mac" or "osx" or "macos"     => BuildTarget.StandaloneOSX,
            "linux" or "linux64" => BuildTarget.StandaloneLinux64,
            ""                   => EditorUserBuildSettings.activeBuildTarget,
            _                    => EditorUserBuildSettings.activeBuildTarget,
        };

        // นำ raw result (อาจเป็น JSON object/array) มาฝังใน response — ถ้าไม่ใช่ JSON ห่อเป็น string
        static string WrapJson(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "null";
            string t = raw.TrimStart();
            return (t.StartsWith("{") || t.StartsWith("[")) ? raw : $"\"{EscapeJson(raw)}\"";
        }

        // ดึง command name จาก json ของ batch item (เลี่ยงพึ่ง private ของ MCPChatWindow)
        static string ExtractCommandNameLocal(string json)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"command\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ดึง raw ของ array (รวมเนื้อใน [ ]) ตามชื่อ key — brace/bracket aware
        static string ExtractJsonArrayRaw(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*\\[");
            if (!m.Success) return null;
            int start = json.IndexOf('[', m.Index);
            if (start < 0) return null;
            int depth = 0; bool inStr = false; bool esc = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') inStr = true;
                else if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) return json.Substring(start, i - start + 1); }
            }
            return null;
        }

        // แยก top-level object ใน array string "[ {..}, {..} ]" → list ของ "{..}"
        static List<string> SplitTopLevelObjects(string arrayRaw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(arrayRaw)) return list;
            int depth = 0; bool inStr = false; bool esc = false; int objStart = -1;
            for (int i = 0; i < arrayRaw.Length; i++)
            {
                char c = arrayRaw[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; }
                else if (c == '{') { if (depth == 0) objStart = i; depth++; }
                else if (c == '}') { depth--; if (depth == 0 && objStart >= 0) { list.Add(arrayRaw.Substring(objStart, i - objStart + 1)); objStart = -1; } }
            }
            return list;
        }

        // ── Request models ────────────────────────────────────────────────────
        [Serializable] class EditScriptRequest     { public string name; public string path; public string find; public string replace; public bool all; }
        [Serializable] class AssignRefRequest       { public string name; public string component; public string property; public string target; public bool asset; }
        [Serializable] class ImportSettingsRequest  { public string path; public int maxSize; public string compression; public string readable; public string mipmaps; public string crunch; }
        [Serializable] class ScreenshotRequest      { public string view; public int width; public int height; }
        [Serializable] class BuildRequest           { public string target; public string path; public string scenes; public bool dev; }
    }
}
