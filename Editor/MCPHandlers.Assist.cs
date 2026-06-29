using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;

namespace MCPBridge
{
    // Core Assist Pack — ให้ AI "มองเห็น" สถานะจริง + "ลงมือแก้" ของที่มีอยู่
    public static partial class MCPHandlers
    {
        // ── อ่าน Console (error/warning/log จริง) ผ่าน LogEntries reflection ──
        static string ReadConsole(string body)
        {
            var data = string.IsNullOrEmpty(body) ? new ConsoleRequest() : ParseReq<ConsoleRequest>(body);
            int max = data.max > 0 ? data.max : 30;

            return ExecuteOnMainThread(() =>
            {
                try
                {
                    var logEntries = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                    var logEntry = Type.GetType("UnityEditor.LogEntry,UnityEditor");
                    if (logEntries == null || logEntry == null)
                        return "{\"error\":\"LogEntries API not available in this Unity version\"}";

                    int count = (int)logEntries.GetMethod("StartGettingEntries").Invoke(null, null);
                    var getEntry = logEntries.GetMethod("GetEntryInternal");
                    var msgField = logEntry.GetField("message");
                    var modeField = logEntry.GetField("mode");
                    var entryObj = Activator.CreateInstance(logEntry);

                    var sb = new StringBuilder("[");
                    int start = Mathf.Max(0, count - max);
                    int added = 0;
                    for (int i = start; i < count; i++)
                    {
                        getEntry.Invoke(null, new object[] { i, entryObj });
                        string msg = (string)msgField.GetValue(entryObj);
                        int mode = (int)modeField.GetValue(entryObj);
                        string type = (mode & 1) != 0 || (mode & (1 << 1)) != 0 ? "error"
                                    : (mode & (1 << 9)) != 0 ? "warning" : "log";
                        if (added > 0) sb.Append(",");
                        sb.Append($"{{\"type\":\"{type}\",\"message\":\"{EscapeJson(msg)}\"}}");
                        added++;
                    }
                    sb.Append("]");
                    logEntries.GetMethod("EndGettingEntries").Invoke(null, null);
                    return $"{{\"count\":{added},\"entries\":{sb}}}";
                }
                catch (Exception e)
                {
                    return $"{{\"error\":\"{EscapeJson(e.Message)}\"}}";
                }
            });
        }

        // ── Inspect object — อ่าน component + ค่าทั้งหมด ──────────────────────
        // deep=true → ใช้ reflection อ่าน private field + public property ทุกตัว
        static string InspectObject(string body)
        {
            var data = ParseReq<InspectRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";

                var sb = new StringBuilder();
                sb.Append($"{{\"name\":\"{EscapeJson(go.name)}\",\"active\":{go.activeSelf.ToString().ToLower()},");
                sb.Append($"\"tag\":\"{EscapeJson(go.tag)}\",\"layer\":\"{EscapeJson(LayerMask.LayerToName(go.layer))}\",");
                var t = go.transform;
                sb.Append($"\"position\":[{t.localPosition.x},{t.localPosition.y},{t.localPosition.z}],");
                sb.Append($"\"rotation\":[{t.localEulerAngles.x},{t.localEulerAngles.y},{t.localEulerAngles.z}],");
                sb.Append($"\"scale\":[{t.localScale.x},{t.localScale.y},{t.localScale.z}],");
                if (data.deep) sb.Append("\"deep\":true,");
                sb.Append("\"components\":[");

                var comps = go.GetComponents<Component>();
                bool firstC = true;
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    if (!firstC) sb.Append(",");
                    string props = data.deep ? ComponentPropsDeep(c) : ComponentProps(c);
                    sb.Append($"{{\"type\":\"{EscapeJson(c.GetType().Name)}\",\"properties\":{props}}}");
                    firstC = false;
                }
                sb.Append("]}");
                return sb.ToString();
            });
        }

        // Deep reflection: อ่าน private/public fields + public properties (skip indexers + compiler-generated)
        static string ComponentPropsDeep(Component c)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            int n = 0;
            var type = c.GetType();

            // Fields (public + private instance)
            var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            foreach (var fi in type.GetFields(flags))
            {
                if (n >= 50) break;
                if (fi.Name.Contains("<")) continue;   // compiler-generated backing fields
                string val;
                try
                {
                    object raw = fi.GetValue(c);
                    val = raw == null ? "null" : raw.ToString();
                    if (val.Length > 200) val = val.Substring(0, 200) + "…";
                }
                catch { continue; }
                if (!first) sb.Append(",");
                sb.Append($"\"{EscapeJson(fi.Name)}\":\"{EscapeJson(val)}\"");
                first = false; n++;
            }

            // Public properties (skip indexers, skip anything that throws)
            foreach (var pi in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (n >= 80) break;
                if (pi.GetIndexParameters().Length > 0) continue;   // indexer
                if (pi.Name.Contains("<")) continue;
                string val;
                try
                {
                    object raw = pi.GetValue(c);
                    val = raw == null ? "null" : raw.ToString();
                    if (val.Length > 200) val = val.Substring(0, 200) + "…";
                }
                catch { continue; }
                if (!first) sb.Append(",");
                sb.Append($"\"{EscapeJson(pi.Name)}\":\"{EscapeJson(val)}\"");
                first = false; n++;
            }

            sb.Append("}");
            return sb.ToString();
        }

        // อ่าน serialized properties ของ component (จำกัดเพื่อกัน output ยาว)
        static string ComponentProps(Component c)
        {
            try
            {
                var so = new SerializedObject(c);
                var prop = so.GetIterator();
                var sb = new StringBuilder("{");
                bool first = true;
                int n = 0;
                prop.Next(true);
                while (prop.NextVisible(false) && n < 25)
                {
                    if (prop.name == "m_Script") continue;
                    string val = PropValue(prop);
                    if (val == null) continue;
                    if (!first) sb.Append(",");
                    sb.Append($"\"{EscapeJson(prop.displayName)}\":{val}");
                    first = false; n++;
                }
                sb.Append("}");
                return sb.ToString();
            }
            catch { return "{}"; }
        }

        static string PropValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:   return p.intValue.ToString();
                case SerializedPropertyType.Boolean:   return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:     return p.floatValue.ToString("0.###");
                case SerializedPropertyType.String:    return $"\"{EscapeJson(p.stringValue)}\"";
                case SerializedPropertyType.Enum:      return $"\"{EscapeJson(p.enumDisplayNames[Mathf.Clamp(p.enumValueIndex,0,p.enumDisplayNames.Length-1)])}\"";
                case SerializedPropertyType.Vector3:   return $"[{p.vector3Value.x},{p.vector3Value.y},{p.vector3Value.z}]";
                case SerializedPropertyType.Vector2:   return $"[{p.vector2Value.x},{p.vector2Value.y}]";
                case SerializedPropertyType.Color:     return $"\"{ColorUtility.ToHtmlStringRGBA(p.colorValue)}\"";
                case SerializedPropertyType.ObjectReference:
                    return $"\"{(p.objectReferenceValue ? EscapeJson(p.objectReferenceValue.name) : "null")}\"";
                default: return null;
            }
        }

        // ── Add component ────────────────────────────────────────────────────
        static string AddComponent(string body)
        {
            var data = ParseReq<ComponentRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";
                var type = FindComponentTypeAny(data.component);
                if (type == null) return $"{{\"error\":\"Component type not found: {EscapeJson(data.component)}\"}}";

                Undo.AddComponent(go, type);
                return $"{{\"added\":\"{EscapeJson(type.Name)}\",\"to\":\"{EscapeJson(go.name)}\"}}";
            });
        }

        // ── Set property — แก้ค่าใน component (HP=100, speed=5, ฯลฯ) ──────────
        static string SetProperty(string body)
        {
            var data = ParseReq<SetPropertyRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";

                Component comp = string.IsNullOrEmpty(data.component)
                    ? go.transform
                    : go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name.Equals(data.component, StringComparison.OrdinalIgnoreCase));
                if (comp == null) return $"{{\"error\":\"Component not found: {EscapeJson(data.component)}\"}}";

                var so = new SerializedObject(comp);
                var prop = so.FindProperty(data.property)
                        ?? FindByDisplayName(so, data.property);
                if (prop == null) return $"{{\"error\":\"Property not found: {EscapeJson(data.property)}\"}}";

                Undo.RecordObject(comp, "MCP SetProperty");
                if (!ApplyValue(prop, data.value)) return $"{{\"error\":\"unsupported property type for '{EscapeJson(data.property)}'\"}}";
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(comp);
                return $"{{\"set\":\"{EscapeJson(data.property)}\",\"value\":\"{EscapeJson(data.value)}\"}}";
            });
        }

        static SerializedProperty FindByDisplayName(SerializedObject so, string name)
        {
            var it = so.GetIterator();
            it.Next(true);
            while (it.NextVisible(false))
                if (it.displayName.Equals(name, StringComparison.OrdinalIgnoreCase) || it.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return it.Copy();
            return null;
        }

        static bool ApplyValue(SerializedProperty p, string v)
        {
            try
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer: p.intValue = int.Parse(v); return true;
                    case SerializedPropertyType.Boolean: p.boolValue = v == "1" || v.ToLower() == "true"; return true;
                    case SerializedPropertyType.Float:   p.floatValue = float.Parse(v); return true;
                    case SerializedPropertyType.String:  p.stringValue = v; return true;
                    case SerializedPropertyType.Enum:
                        int idx = Array.FindIndex(p.enumDisplayNames, n => n.Equals(v, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0 && int.TryParse(v, out int ei)) idx = ei;
                        if (idx < 0) return false;
                        p.enumValueIndex = idx; return true;
                    case SerializedPropertyType.Vector3:
                        var a = v.Split(','); p.vector3Value = new Vector3(float.Parse(a[0]), float.Parse(a[1]), float.Parse(a[2])); return true;
                    case SerializedPropertyType.Color:
                        if (ColorUtility.TryParseHtmlString(v, out var col)) { p.colorValue = col; return true; } return false;
                    default: return false;
                }
            }
            catch { return false; }
        }

        // ── Set transform — ย้าย/หมุน/scale ของที่มีอยู่ ─────────────────────
        static string SetTransform(string body)
        {
            var data = ParseReq<SetTransformRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";

                Undo.RecordObject(go.transform, "MCP SetTransform");
                string set = (data.set ?? "").ToLower();
                if (set.Contains("pos"))   go.transform.localPosition    = new Vector3(data.px, data.py, data.pz);
                if (set.Contains("rot"))   go.transform.localEulerAngles = new Vector3(data.rx, data.ry, data.rz);
                if (set.Contains("scale")) go.transform.localScale        = new Vector3(data.sx, data.sy, data.sz);
                return $"{{\"transformed\":\"{EscapeJson(go.name)}\",\"set\":\"{EscapeJson(set)}\"}}";
            });
        }

        // ── Selection ────────────────────────────────────────────────────────
        static string GetSelection() => ExecuteOnMainThread(() =>
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < Selection.gameObjects.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"\"{EscapeJson(Selection.gameObjects[i].name)}\"");
            }
            sb.Append("]");
            return $"{{\"count\":{Selection.gameObjects.Length},\"selected\":{sb}}}";
        });

        static string SetSelection(string body)
        {
            var data = ParseReq<NameRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {EscapeJson(data.name)}\"}}";
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
                return $"{{\"selected\":\"{EscapeJson(go.name)}\"}}";
            });
        }

        // ── Scene open / save ────────────────────────────────────────────────
        static string OpenScene(string body)
        {
            var data = ParseReq<PathRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                if (string.IsNullOrEmpty(data.path)) return "{\"error\":\"path required\"}";
                if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    var sc = EditorSceneManager.OpenScene(data.path);
                    return $"{{\"opened\":\"{EscapeJson(sc.path)}\"}}";
                }
                return "{\"error\":\"cancelled\"}";
            });
        }

        static string SaveScene() => ExecuteOnMainThread(() =>
        {
            bool ok = EditorSceneManager.SaveOpenScenes();
            return $"{{\"saved\":{ok.ToString().ToLower()}}}";
        });

        // หา Type ของ component (รวม non-Component เช่น ScriptableObject ไม่เอา)
        static Type FindComponentTypeAny(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name);
                if (t != null && typeof(Component).IsAssignableFrom(t)) return t;
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    if (t.Name == name && typeof(Component).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        // ── อ่าน Editor.log เต็ม (มี stack trace + ประวัติ Debug.Log) ──────────
        static string ReadLogFile(string body)
        {
            var data = string.IsNullOrEmpty(body) ? new ConsoleRequest() : ParseReq<ConsoleRequest>(body);
            int max = data.max > 0 ? data.max : 120;

            return ExecuteOnMainThread(() =>
            {
                try
                {
                    string path = Application.consoleLogPath; // Editor.log ปัจจุบัน
                    if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                        return "{\"error\":\"log file not found\"}";

                    // อ่านท้ายไฟล์ N บรรทัด (เปิดแบบ shared กัน lock)
                    string[] lines;
                    using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    using (var sr = new System.IO.StreamReader(fs))
                        lines = sr.ReadToEnd().Split('\n');

                    int start = Mathf.Max(0, lines.Length - max);
                    var sb = new StringBuilder();
                    for (int i = start; i < lines.Length; i++)
                        sb.Append(lines[i]).Append('\n');

                    return $"{{\"path\":\"{EscapeJson(path)}\",\"lines\":{lines.Length - start},\"tail\":\"{EscapeJson(sb.ToString())}\"}}";
                }
                catch (Exception e) { return $"{{\"error\":\"{EscapeJson(e.Message)}\"}}"; }
            });
        }

        // ── Capture runtime state — snapshot ตอนเกมค้าง/ไม่ไปต่อ ──────────────
        static string CaptureState() => ExecuteOnMainThread(() =>
        {
            var sb = new StringBuilder("{");
            sb.Append($"\"isPlaying\":{Application.isPlaying.ToString().ToLower()},");
            sb.Append($"\"isPaused\":{EditorApplication.isPaused.ToString().ToLower()},");
            sb.Append($"\"timeScale\":{Time.timeScale},");
            sb.Append($"\"frameCount\":{Time.frameCount},");           // เรียก 2 ครั้งเทียบ → รู้ว่าเฟรมเดินมั้ย
            sb.Append($"\"realtime\":{Time.realtimeSinceStartup:F1},");
            sb.Append($"\"scene\":\"{EscapeJson(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)}\",");

            float fps = ProfilerReader.CurrentFps();
            sb.Append($"\"fps\":{fps:F0},");

            string net = ProfilerDeepReader.NetworkLine();
            sb.Append($"\"network\":\"{EscapeJson(net ?? "n/a")}\",");

            // นับ MonoBehaviour ที่ active (ดูว่ามี manager ทำงานอยู่มั้ย)
            int behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>().Length;
            sb.Append($"\"activeMonoBehaviours\":{behaviours}");
            sb.Append("}");

            // แนบ spike ที่จับได้ด้วย (ถ้ามี)
            string spikes = SpikeMonitor.GetReport();
            return sb.ToString() + (string.IsNullOrEmpty(spikes) ? "" : "\n" + spikes);
        });

        // ── Performance audit — สำรวจ scene หาตัวการเกมหน่วง + heavy groups ────
        // worst spike เดียว + โค้ดตัวการ (keyword "worst" / ปุ่ม 🔥)
        static string PerfWorst() => ExecuteOnMainThread(() =>
            $"{{\"report\":\"{EscapeJson(SpikeMonitor.WorstReport())}\"}}");

        // คุม Hot Reload — action: "status" | "start"
        static string HotReload(string body) => ExecuteOnMainThread(() =>
        {
            var data = ParseReq<HotReloadRequest>(body);
            string action = string.IsNullOrEmpty(data?.action) ? "status" : data.action.ToLowerInvariant();
            if (action == "start")
            {
                bool ok = HotReloadControl.Start(out string msg);
                return $"{{\"started\":{(ok ? "true" : "false")},\"running\":{(HotReloadControl.IsRunning() ? "true" : "false")},\"message\":\"{EscapeJson(msg)}\"}}";
            }
            return $"{{\"running\":{(HotReloadControl.IsRunning() ? "true" : "false")}}}";
        });

        // สั่ง Unity compile scripts — มี guard ป้องกัน double-compile และ Play Mode
        static string Compile() => ExecuteOnMainThread(() =>
        {
            // guard 1: ถ้ากำลัง compile อยู่แล้ว → ห้าม trigger ซ้ำ (กัน Reload Domain ซ้อน)
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return "{\"status\":\"already_compiling\",\"message\":\"Unity กำลัง compile อยู่แล้ว — ให้ poll unity_compile_status จนได้ isCompiling:false แล้วค่อยทำงานต่อ ห้าม compile ซ้ำ\"}";

            // guard 2: ถ้าอยู่ใน Play Mode → ออกก่อน (compile ใน Play Mode ทำให้ domain reload ค้าง)
            if (EditorApplication.isPlaying || EditorApplication.isPaused)
            {
                EditorApplication.isPlaying = false;
                return "{\"status\":\"exiting_play_mode\",\"message\":\"กำลังออก Play Mode ก่อน — รอสักครู่แล้ว call unity_compile อีกครั้ง\"}";
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.None);
            return "{\"status\":\"compiling\",\"message\":\"compile triggered — ให้ poll unity_compile_status จนได้ isCompiling:false ก่อนทำงานต่อ\"}";
        });

        // เช็คว่า compile เสร็จหรือยัง — Claude poll จนได้ isCompiling:false ก่อนทำงานต่อ
        static string CompileStatus() => ExecuteOnMainThread(() =>
        {
            bool compiling = EditorApplication.isCompiling || EditorApplication.isUpdating;
            bool playing   = EditorApplication.isPlaying || EditorApplication.isPaused;
            string status  = playing ? "play_mode" : compiling ? "compiling" : "ready";
            return $"{{\"isCompiling\":{(compiling ? "true" : "false")},\"isPlaying\":{(playing ? "true" : "false")},\"status\":\"{status}\"}}";
        });

        // AI สั่งปิด MCP server — stop ตรงๆ บน main thread (ExecuteOnMainThread ทำงานได้แม้ editor background)
        // response ยัง flush ได้ เพราะ worker connection (ตัวที่ตอบ) แยกจาก listener thread ที่ถูกปิด
        static string ServerStop() => ExecuteOnMainThread(() =>
        {
            MCPServer.Stop();
            return "{\"stopped\":true,\"message\":\"AI สั่งปิด MCP server แล้ว (ping ครั้งถัดไปจะ fail = ปิดสำเร็จ)\"}";
        });

        static string PerfAudit() => ExecuteOnMainThread(() =>
        {
            var sb = new StringBuilder("{");

            // 1) frame stats (ถ้าเล่นอยู่)
            if (ProfilerReader.IsLive)
            {
                float fps = ProfilerReader.CurrentFps();
                sb.Append($"\"fps\":{fps:F0},");
            }

            // 2) census — นับของหนักในซีน (เฉพาะ active)
            int renderers = 0, skinned = 0, particles = 0, rtLights = 0, animators = 0,
                audio = 0, rigidbodies = 0, meshColliders = 0, canvases = 0, trails = 0;

            int transparentRenderers = 0, shadowCasters = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                renderers++;
                if (r is SkinnedMeshRenderer) skinned++;
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) shadowCasters++;
                foreach (var mat in r.sharedMaterials)
                    if (mat != null && mat.renderQueue >= 2450) { transparentRenderers++; break; }
            }
            particles   = UnityEngine.Object.FindObjectsOfType<ParticleSystem>().Length;
            animators   = UnityEngine.Object.FindObjectsOfType<Animator>().Length;
            audio       = UnityEngine.Object.FindObjectsOfType<AudioSource>().Length;
            rigidbodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>().Length;
            canvases    = UnityEngine.Object.FindObjectsOfType<Canvas>().Length;
            trails      = UnityEngine.Object.FindObjectsOfType<TrailRenderer>().Length;
            int dirLights = 0, pointLights = 0, spotLights = 0;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l.lightmapBakeType == LightmapBakeType.Baked) continue;
                rtLights++;
                switch (l.type) {
                    case LightType.Directional: dirLights++; break;
                    case LightType.Point:  pointLights++; break;
                    case LightType.Spot:   spotLights++; break;
                }
            }
            foreach (var mc in UnityEngine.Object.FindObjectsOfType<MeshCollider>())
                if (!mc.convex) meshColliders++;

            int activeCameras = 0;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
                if (c.enabled && c.gameObject.activeInHierarchy) activeCameras++;

            int rtReflProbes = 0;
            foreach (var rp in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>())
                if (rp.mode == UnityEngine.Rendering.ReflectionProbeMode.Realtime) rtReflProbes++;

            int lodGroups = UnityEngine.Object.FindObjectsOfType<LODGroup>().Length;

            sb.Append($"\"census\":{{\"renderers\":{renderers},\"skinnedMeshes\":{skinned},\"particleSystems\":{particles}," +
                      $"\"realtimeLights\":{rtLights},\"dirLights\":{dirLights},\"pointLights\":{pointLights},\"spotLights\":{spotLights}," +
                      $"\"animators\":{animators},\"audioSources\":{audio}," +
                      $"\"rigidbodies\":{rigidbodies},\"nonConvexMeshColliders\":{meshColliders},\"canvases\":{canvases},\"trailRenderers\":{trails}," +
                      $"\"transparentRenderers\":{transparentRenderers},\"shadowCasters\":{shadowCasters}," +
                      $"\"activeCameras\":{activeCameras},\"rtReflProbes\":{rtReflProbes},\"lodGroups\":{lodGroups}}},");

            // 3) heavy groups — จัดกลุ่ม object ตามชื่อ (ตัด (Clone)/เลขท้าย) → เจอ "ต้นไม้ x500"
            var groups = new Dictionary<string, int>();
            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (!go.activeInHierarchy) continue;
                string key = NormalizeName(go.name);
                groups[key] = groups.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            var top = groups.Where(g => g.Value >= 10).OrderByDescending(g => g.Value).Take(15).ToList();
            sb.Append("\"heavyGroups\":[");
            for (int i = 0; i < top.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"{{\"name\":\"{EscapeJson(top[i].Key)}\",\"count\":{top[i].Value}}}");
            }
            sb.Append("],");

            // 4) GPU instancing candidates — material ที่ใช้กับ 20+ renderer แต่ยังไม่ได้เปิด instancing
            var matUsage = new Dictionary<Material, int>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                foreach (var mat in r.sharedMaterials)
                    if (mat != null) matUsage[mat] = (matUsage.TryGetValue(mat, out var mu) ? mu : 0) + 1;

            var instanceCandidates = matUsage
                .Where(p => p.Value >= 20 && !p.Key.enableInstancing && p.Key.renderQueue < 2450)
                .OrderByDescending(p => p.Value).Take(5).ToList();

            sb.Append("\"gpuInstancing\":[");
            for (int i = 0; i < instanceCandidates.Count; i++) {
                if (i > 0) sb.Append(",");
                sb.Append($"{{\"mat\":\"{EscapeJson(instanceCandidates[i].Key.name)}\",\"uses\":{instanceCandidates[i].Value}}}");
            }
            sb.Append("],");

            // 5) shader complexity — multi-pass + GrabPass
            int multiPassMats = 0, grabPassMats = 0;
            var checkedShaders = new HashSet<Shader>();
            foreach (var pair in matUsage)
            {
                if (pair.Key.shader == null || !checkedShaders.Add(pair.Key.shader)) continue;
                if (pair.Key.shader.passCount > 1) multiPassMats++;
                if (pair.Key.shader.name.IndexOf("Grab", StringComparison.OrdinalIgnoreCase) >= 0) grabPassMats++;
            }
            sb.Append($"\"shaderComplexity\":{{\"multiPassShaders\":{multiPassMats},\"grabPassShaders\":{grabPassMats}}},");

            // 6) physics layer matrix — กี่ pair ที่ยัง collide กันอยู่
            var usedLayers = new List<int>();
            for (int i = 0; i < 32; i++)
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) usedLayers.Add(i);
            int activePairs = 0, maxPairs = usedLayers.Count * (usedLayers.Count + 1) / 2;
            for (int i = 0; i < usedLayers.Count; i++)
                for (int j = i; j < usedLayers.Count; j++)
                    if (!Physics.GetIgnoreLayerCollision(usedLayers[i], usedLayers[j])) activePairs++;
            sb.Append($"\"physicsMatrix\":{{\"usedLayers\":{usedLayers.Count},\"activePairs\":{activePairs},\"maxPairs\":{maxPairs}}},");

            // 7) batching analysis — dynamic eligible + static miss
            int dynamicBatchEligible = 0, staticMiss = 0;
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
                if (mf.sharedMesh != null && mf.gameObject.activeInHierarchy && mf.sharedMesh.vertexCount < 300)
                    dynamicBatchEligible++;
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (!mr.enabled || !mr.gameObject.activeInHierarchy || mr.gameObject.isStatic) continue;
                if (mr.GetComponentInParent<Rigidbody>() != null || mr.GetComponentInParent<Animator>() != null) continue;
                if (mr.sharedMaterials.Any(m => m != null && m.renderQueue >= 2450)) continue;
                staticMiss++;
            }
            sb.Append($"\"batching\":{{\"dynamicEligible\":{dynamicBatchEligible},\"staticMiss\":{staticMiss}}},");

            // 8) network bandwidth + ping (Fusion)
            string net = ProfilerDeepReader.NetworkLine();
            sb.Append($"\"network\":\"{EscapeJson(net ?? "n/a")}\",");

            // 5) heuristic warnings (ตัวการที่พบบ่อย)
            var warn = new List<string>();
            if (rtLights > 8)       warn.Add($"Realtime lights {rtLights} ตัว — แพง! bake แสงที่ไม่เคลื่อนไหว / ลดจำนวน");
            if (skinned > 60)       warn.Add($"SkinnedMesh {skinned} ตัว (ครีป/ตัวละครเยอะ) — ใช้ LOD + culling, ลด bone, GPU skinning");
            if (particles > 40)     warn.Add($"ParticleSystem {particles} ตัว — pool + ลด max particles + culling");
            if (meshColliders > 15) warn.Add($"Non-convex MeshCollider {meshColliders} — แพงตอนชน ใช้ primitive collider แทน");
            if (canvases > 10)      warn.Add($"Canvas {canvases} ตัว — แยก dynamic/static canvas กัน rebuild ทั้งจอ");
            if (animators > 80)            warn.Add($"Animator {animators} ตัว — ปิด animator ที่อยู่นอกจอ (culling mode) / ใช้ GPU animation");
            if (transparentRenderers > 50) warn.Add($"Transparent renderers {transparentRenderers} ตัว — Transparent ไม่ batch เกิด overdraw → จำกัดจำนวน หรือแทนด้วย opaque + alpha cutout");
            if (shadowCasters > 300)       warn.Add($"Shadow casters {shadowCasters} ตัว — ทุกตัวต้องวาด shadow map → ลด Shadow Distance หรือปิด Cast Shadows บน object ที่ห่างจากกล้อง");
            if (instanceCandidates.Count > 0)
                warn.Add($"GPU Instancing ปิดอยู่บน {instanceCandidates.Count} material ที่ใช้ 20+ renderer — เปิด Enable GPU Instancing ใน material inspector ลด draw calls ได้มาก");
            if (multiPassMats > 0)
                warn.Add($"Multi-pass shaders {multiPassMats} ตัว — แต่ละ pass = render scene ซ้ำ → ใช้ URP single-pass shader แทน");
            if (grabPassMats > 0)
                warn.Add($"GrabPass shaders {grabPassMats} ตัว — copy framebuffer ทุกครั้งที่ render = แพงมาก → ใช้ Camera Opaque Texture ใน URP Pipeline Asset แทน");
            if (activeCameras > 1)
                warn.Add($"Active cameras {activeCameras} ตัว — แต่ละตัว = full render pass → ปิดกล้องที่ไม่ใช้ หรือรวมใช้ Camera Stacking");
            if (rtReflProbes > 0)
                warn.Add($"Realtime Reflection Probes {rtReflProbes} ตัว — แพงมาก! เปลี่ยนเป็น Baked หรือ Custom Cubemap");
            if (pointLights > 4)
                warn.Add($"Point Lights {pointLights} ตัว — shadow = cube map 6 faces ต่อดวง แพงมาก → bake หรือใช้ Light Probe แทน");
            if (spotLights > 6)
                warn.Add($"Spot Lights {spotLights} ตัว — bake หรือใช้ cookie texture แทน realtime shadow");
            if (skinned > 20 && lodGroups < skinned / 3)
                warn.Add($"SkinnedMesh {skinned} ตัว แต่ LODGroup มีแค่ {lodGroups} — ควรมี LOD สำหรับ character/creep เพื่อลด bone/polygon เมื่อห่างจากกล้อง");
            if (usedLayers.Count > 4 && activePairs > maxPairs * 0.7f)
                warn.Add($"Physics layer matrix: {activePairs}/{maxPairs} pairs ยัง collide — ตรวจ Project Settings → Physics ปิด pair ที่ไม่จำเป็น");
            if (staticMiss > 50)
                warn.Add($"~{staticMiss} MeshRenderer อาจ static ได้แต่ยังไม่ mark — Mark Static → static batching + occlusion culling ทำงานได้");
            foreach (var g in top)
                if (g.Value >= 200) warn.Add($"'{g.Key}' มี {g.Value} ตัว — เยอะมาก! pooling + culling + LOD + GPU instancing");

            sb.Append("\"warnings\":[");
            for (int i = 0; i < warn.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"\"{EscapeJson(warn[i])}\"");
            }
            sb.Append("]}");

            // แนบ frame report (CPU/GPU bound, 0.1% low) + spike + network monitor — Snapshot รวมให้แล้ว
            string frame = ProfilerReader.IsLive ? ProfilerReader.Snapshot() : (SpikeMonitor.GetReport() ?? "");
            return sb.ToString() + "\n" + frame;
        });

        // ตัด "(Clone)" และเลข/ช่องว่างท้ายชื่อ → จัดกลุ่ม object ชนิดเดียวกัน
        static string NormalizeName(string name)
        {
            name = name.Replace("(Clone)", "");
            name = System.Text.RegularExpressions.Regex.Replace(name, @"[\s_]*\(?\d+\)?\s*$", "");
            return name.Trim();
        }

        // ── Clear console (ใช้ก่อน reproduce เพื่อ isolate error ใหม่) ─────────
        static string ClearConsole() => ExecuteOnMainThread(() =>
        {
            try
            {
                var logEntries = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                logEntries?.GetMethod("Clear")?.Invoke(null, null);
                return "{\"cleared\":true}";
            }
            catch (Exception e) { return $"{{\"error\":\"{EscapeJson(e.Message)}\"}}"; }
        });

        // ── Play control — ให้ AI reproduce bug ได้ (enter/exit/pause/step) ───
        static string PlayControl(string body)
        {
            var data = ParseReq<PlayRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                switch ((data.action ?? "").ToLower())
                {
                    case "enter": EditorApplication.isPlaying = true;  return "{\"play\":\"entering\"}";
                    case "exit":  EditorApplication.isPlaying = false; return "{\"play\":\"exiting\"}";
                    case "pause": EditorApplication.isPaused = true;   return "{\"play\":\"paused\"}";
                    case "resume":EditorApplication.isPaused = false;  return "{\"play\":\"resumed\"}";
                    case "step":  EditorApplication.Step();            return "{\"play\":\"stepped\"}";
                    default: return "{\"error\":\"action must be enter|exit|pause|resume|step\"}";
                }
            });
        }

        // ── อ่าน source ของ script (มีเลขบรรทัด) เพื่อให้ AI ชี้บรรทัดได้ ───────
        // รองรับ filter เฉพาะเมธอด (เช่น "Update") เพื่อตัด output ให้สั้น + ตรงจุด
        static string ReadScript(string body)
        {
            var data = ParseReq<ReadScriptRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                string path = CodebaseIndex.ResolvePath(data.name);
                if (path == null) return $"{{\"error\":\"script not found: {EscapeJson(data.name)}\"}}";

                string content = CodebaseIndex.ReadContent(path, 40000);
                if (content == null) return $"{{\"error\":\"cannot read: {EscapeJson(path)}\"}}";

                var lines = content.Split('\n');
                var sb = new StringBuilder();
                bool filter = !string.IsNullOrEmpty(data.method);
                int braceDepth = 0; bool inMethod = false; int emitted = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (filter)
                    {
                        // เริ่ม emit เมื่อเจอชื่อเมธอด แล้ว emit จนจบ block { }
                        if (!inMethod && line.Contains(data.method) && (line.Contains("(")))
                        {
                            inMethod = true; braceDepth = 0;
                        }
                        if (!inMethod) continue;
                        braceDepth += CountChar(line, '{') - CountChar(line, '}');
                        sb.Append($"{i + 1}: {line}\n");
                        emitted++;
                        if (braceDepth <= 0 && line.Contains("}")) break;
                        if (emitted > 200) break;
                    }
                    else
                    {
                        sb.Append($"{i + 1}: {line}\n");
                    }
                }
                if (filter && emitted == 0)
                    return $"{{\"error\":\"method '{EscapeJson(data.method)}' not found in {EscapeJson(path)}\"}}";

                return $"{{\"path\":\"{EscapeJson(path)}\",\"source\":\"{EscapeJson(sb.ToString())}\"}}";
            });
        }

        static int CountChar(string s, char c)
        {
            int n = 0; foreach (var ch in s) if (ch == c) n++; return n;
        }

        // /diagnose/deep — full profiler deep analysis (CPU + GC + suspicious + source code)
        // wraps ProfilerDeepReader.DeepAnalysis() which already exists
        static string DiagnoseDeep(string body)
        {
            var data = ParseReq<TopNRequest>(body);
            int n = data.topN > 0 ? data.topN : 8;
            return ExecuteOnMainThread(() =>
            {
                string report = ProfilerDeepReader.DeepAnalysis(n);
                return $"{{\"report\":\"{EscapeJson(report)}\"}}";
            });
        }

        // /diagnose/memory — managed + native + graphics memory snapshot
        static string MemorySnapshot() => ExecuteOnMainThread(() =>
        {
            var sb = new System.Text.StringBuilder("{");
            sb.Append($"\"monoUsedMB\":{Profiler.GetMonoUsedSizeLong() / 1048576f:F2},");
            sb.Append($"\"monoHeapMB\":{Profiler.GetMonoHeapSizeLong() / 1048576f:F2},");
            sb.Append($"\"unityAllocMB\":{Profiler.GetTotalAllocatedMemoryLong() / 1048576f:F2},");
            sb.Append($"\"unityReservedMB\":{Profiler.GetTotalReservedMemoryLong() / 1048576f:F2},");
            sb.Append($"\"unusedReservedMB\":{Profiler.GetTotalUnusedReservedMemoryLong() / 1048576f:F2},");
            sb.Append($"\"graphicsMB\":{Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576f:F2},");
            sb.Append($"\"gc0\":{GC.CollectionCount(0)},");
            sb.Append($"\"gc1\":{GC.CollectionCount(1)},");
            sb.Append($"\"gc2\":{GC.CollectionCount(2)},");
            sb.Append($"\"isPlaying\":{Application.isPlaying.ToString().ToLower()}");
            sb.Append("}");
            return sb.ToString();
        });

        // /diagnose/fusion — detailed Photon Fusion 2 stats via reflection
        static string FusionStats() => ExecuteOnMainThread(() =>
        {
            try
            {
                if (!Application.isPlaying)
                    return "{\"error\":\"ต้องกด Play ก่อน — Fusion runner ทำงานตอน runtime เท่านั้น\"}";

                Type runnerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    runnerType = asm.GetType("Fusion.NetworkRunner");
                    if (runnerType != null) break;
                }
                if (runnerType == null)
                    return "{\"error\":\"Fusion.NetworkRunner not found — ตรวจว่า Photon Fusion 2 ติดตั้งแล้ว\"}";

                var runner = UnityEngine.Object.FindObjectOfType(runnerType);
                if (runner == null)
                    return "{\"error\":\"NetworkRunner ไม่มีใน scene\"}";

                object Get(string prop) => runnerType.GetProperty(prop)?.GetValue(runner);

                var sb = new System.Text.StringBuilder("{");

                // Tick / simulation
                var tick     = Get("Tick");
                var tickRate = Get("TickRate");
                var simTime  = Get("SimulationTime");
                var isServer = Get("IsServer");
                var isClient = Get("IsClient");
                var isResim  = Get("IsResimulating");
                var connCount = Get("ActivePlayers");

                if (tick != null)     sb.Append($"\"tick\":{tick},");
                if (tickRate != null) sb.Append($"\"tickRate\":{tickRate},");
                if (simTime != null)  sb.Append($"\"simulationTimeSec\":{Convert.ToDouble(simTime):F3},");
                if (isServer != null) sb.Append($"\"isServer\":{isServer.ToString().ToLower()},");
                if (isClient != null) sb.Append($"\"isClient\":{isClient.ToString().ToLower()},");
                if (isResim != null)  sb.Append($"\"isResimulating\":{isResim.ToString().ToLower()},");

                // count active players
                int playerCount = 0;
                if (connCount is System.Collections.IEnumerable en)
                    foreach (var _ in en) playerCount++;
                sb.Append($"\"connectedPlayers\":{playerCount},");

                // RTT local player
                var localPlayer = Get("LocalPlayer");
                var rttMethod = runnerType.GetMethod("GetPlayerRtt");
                var perRtt = new System.Collections.Generic.List<string>();
                if (rttMethod != null && connCount is System.Collections.IEnumerable players2)
                {
                    foreach (var p in players2)
                    {
                        object rtt = rttMethod.Invoke(runner, new[] { p });
                        if (rtt is double d) perRtt.Add($"\"P{p}\":{d * 1000.0:F0}");
                        if (perRtt.Count >= 10) break;
                    }
                }
                sb.Append($"\"rttMs\":{{{string.Join(",", perRtt)}}},");

                // GetStats() — bandwidth / packet loss / resend
                try
                {
                    var statsMethod = runnerType.GetMethod("GetStats", Type.EmptyTypes);
                    object stats = statsMethod?.Invoke(runner, null);
                    if (stats != null)
                    {
                        var st = stats.GetType();
                        object Val(string m) => st.GetProperty(m)?.GetValue(stats) ?? st.GetField(m)?.GetValue(stats);
                        void AppNum(string key, string member)
                        {
                            var v = Val(member);
                            if (v != null) { double d = Convert.ToDouble(v); if (d > 0) sb.Append($"\"{key}\":{d:F1},"); }
                        }
                        AppNum("inKBps",       "InKBps");
                        AppNum("outKBps",      "OutKBps");
                        AppNum("inBandwidth",  "InBandwidth");
                        AppNum("outBandwidth", "OutBandwidth");
                        AppNum("packetLoss",   "PacketLoss");
                        AppNum("resendRate",   "ResendRate");

                        // Resimulation count (ถ้ามี)
                        var resimCount = Val("ResimulationCount") ?? Val("Resimulations");
                        if (resimCount != null) sb.Append($"\"resimCount\":{resimCount},");

                        // Snapshot delta size (ถ้ามี)
                        var snapSize = Val("SnapshotSize") ?? Val("StateDeltaSize");
                        if (snapSize != null) sb.Append($"\"snapshotDeltaBytes\":{snapSize},");
                    }
                }
                catch { }

                // trim trailing comma
                string result = sb.ToString().TrimEnd(',');
                return result + "}";
            }
            catch (Exception e)
            {
                return $"{{\"error\":\"{EscapeJson(e.Message)}\"}}";
            }
        });

        // ── Request models ────────────────────────────────────────────────────
        [Serializable] class ConsoleRequest      { public int max; }
        [Serializable] class PlayRequest         { public string action; }
        [Serializable] class ReadScriptRequest   { public string name; public string method; }
        [Serializable] class ComponentRequest    { public string name; public string component; }
        [Serializable] class SetPropertyRequest  { public string name; public string component; public string property; public string value; }
        [Serializable] class SetTransformRequest { public string name; public string set; public float px, py, pz, rx, ry, rz, sx, sy, sz; }
        [Serializable] class PathRequest         { public string path; }
        [Serializable] class TopNRequest         { public int topN; }
        [Serializable] class InspectRequest      { public string name; public bool deep; }
    }
}
