using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.UI;

namespace MCPBridge
{
    public static partial class MCPHandlers
    {
        // ── ความปลอดภัย: read-only default + rate limit ──
        // เปิด/ปิด write ผ่านเมนู MCP Bridge/Allow Write Commands (เก็บใน EditorPrefs)
        // thread-safe: Dispatch ถูกเรียกจาก background (HTTP server / Task.Run) ด้วย
        // → EditorPrefs.GetBool เรียกได้เฉพาะ main thread → cache ค่าไว้ อ่าน background ใช้ cache
        static bool _allowWritesCache;
        public static bool AllowWrites
        {
            get
            {
                if (System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                    _allowWritesCache = EditorPrefs.GetBool("DeltaMCP_AllowWrites", false);   // refresh สดบน main thread
                return _allowWritesCache;                                                      // background ใช้ค่าล่าสุด
            }
            set { EditorPrefs.SetBool("DeltaMCP_AllowWrites", value); _allowWritesCache = value; }
        }

        // คำสั่งที่ "แก้ไข state" ของโปรเจกต์/scene — ถูกบล็อกถ้า read-only
        static readonly HashSet<string> WritePaths = new HashSet<string>
        {
            "/object/add-component", "/object/set-property", "/object/set-transform",
            "/selection/set", "/scene/open", "/scene/save", "/console/clear",
            "/play/control", "/gameobject/create", "/gameobject/delete",
            "/prefab/create", "/prefab/place", "/ui/create", "/terrain/create",
            "/terrain/set-heights", "/script/create", "/ui/optimize",
            "/material/create", "/atlas/create",
            "/diagnose/exceptions-clear", "/code/run",
            // หมายเหตุ: /watch/add, /watch/clear = READ-ONLY (แค่ sample ค่า field ไม่แก้ scene/game)
            //   → ไม่อยู่ใน WritePaths เพื่อให้ดู runtime value ได้โดยไม่ต้องเปิด Allow Write
        };

        // ── command-name → canonical path : SINGLE SOURCE ของการ map ชื่อ→path ──
        //   ⚠️ เพิ่ม handler ใหม่ใน Dispatch switch (ด้านล่าง) → เพิ่ม alias ตรงนี้ด้วย (ไฟล์เดียวกัน กัน drift)
        //   ใช้ร่วมกัน: ช่องแชต (CommandJsonToPath → ResolvePath) + Dispatch (normalize หัวฟังก์ชัน)
        static readonly Dictionary<string, string> CmdAlias = BuildCmdAlias();

        [Serializable] class CmdManifest { public CmdEntry[] commands; }
        [Serializable] class CmdEntry { public string command; public string path; }

        static Dictionary<string, string> BuildCmdAlias()
        {
            // baseline — safety floor: ถ้าโหลด commands.json ไม่ได้ ยังใช้ชุดนี้ได้ (ไม่แย่กว่าเดิม)
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "create_script", "/script/create" },       { "create_prefab", "/prefab/create" },
                { "place_prefab", "/prefab/place" },          { "create_ui", "/ui/create" },
                { "create_gameobject", "/gameobject/create" },{ "delete_gameobject", "/gameobject/delete" },
                { "create_terrain", "/terrain/create" },      { "set_terrain_heights", "/terrain/set-heights" },
                { "optimize_ui", "/ui/optimize" },            { "create_material", "/material/create" },
                { "create_sprite_atlas", "/atlas/create" },   { "audit_textures", "/audit/textures" },
                { "audit_unused", "/audit/unused" },          { "audit_empty_folders", "/audit/empty-folders" },
                { "refactor_audit", "/code/refactor-audit" }, { "count_components", "/scene/count" },
                { "read_console", "/console/read" },          { "get_console_logs", "/console/logs" },
                { "console_logs", "/console/logs" },          { "inspect_object", "/object/inspect" },
                { "add_component", "/object/add-component" },  { "set_property", "/object/set-property" },
                { "set_transform", "/object/set-transform" },  { "get_selection", "/selection/get" },
                { "set_selection", "/selection/set" },         { "scene_hierarchy", "/scene/hierarchy" },
                { "hierarchy", "/scene/hierarchy" },           { "scene_list", "/scene/list" },
                { "list_scenes", "/scene/list" },              { "open_scene", "/scene/open" },
                { "save_scene", "/scene/save" },               { "find_asset", "/asset/find" },
                { "read_logfile", "/console/logfile" },        { "capture_state", "/diagnose/state" },
                { "perf_audit", "/perf/audit" },               { "perf_worst", "/perf/worst" },
                { "diagnose_deep", "/diagnose/deep" },         { "memory_snapshot", "/diagnose/memory" },
                { "fusion_stats", "/diagnose/fusion" },        { "hot_reload", "/hot-reload" },
                { "clear_console", "/console/clear" },          { "play_control", "/play/control" },
                { "read_script", "/script/read" },              { "watch_add", "/watch/add" },
                { "watch_get", "/watch/get" },                  { "watch_clear", "/watch/clear" },
                { "get_exceptions", "/diagnose/exceptions" },   { "clear_exceptions", "/diagnose/exceptions-clear" },
                { "diagnose_exceptions", "/diagnose/exceptions" },
                { "compile", "/compile" },                      { "compile_status", "/compile-status" },
                { "run_csharp", "/code/run" },                  { "ping", "/ping" },
            };
            // overlay จาก commands.json (SINGLE SOURCE ร่วมกับ bridge) — JSON ชนะ baseline
            try
            {
                string p = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "unity-mcp-server", "commands.json"));
                if (File.Exists(p))
                {
                    var m = JsonUtility.FromJson<CmdManifest>(File.ReadAllText(p));
                    if (m?.commands != null)
                        foreach (var e in m.commands)
                            if (!string.IsNullOrEmpty(e.command) && !string.IsNullOrEmpty(e.path))
                                d[e.command] = e.path;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[MCP] โหลด commands.json ไม่ได้ → ใช้ baseline: {ex.Message}"); }
            return d;
        }

        // รายการ path ทั้งหมด (distinct + เรียง) — ใช้โดย health check "ทดสอบ" ในหน้าต่างแชต
        public static List<string> CommandPaths()
        {
            var set = new HashSet<string>(CmdAlias.Values);
            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // รับได้ทั้ง command-name ("scene_hierarchy") และ path ตรง ("/scene/hierarchy")
        public static string ResolvePath(string cmdOrPath)
        {
            if (string.IsNullOrEmpty(cmdOrPath)) return "/unknown";
            if (cmdOrPath[0] == '/') return cmdOrPath;
            return CmdAlias.TryGetValue(cmdOrPath, out var p) ? p : "/" + cmdOrPath;
        }

        const int RATE_MAX_PER_SEC = 25;   // เพดานคำสั่ง/วินาที — กัน agent วนรัว (เคส workflow เปิดทุก scene)
        static readonly object _rlLock = new object();
        static readonly Queue<DateTime> _recent = new Queue<DateTime>();

        // ── MCP Command Log — แสดงใน "Claude In" tab + persist ใน Library/DeltaMCP/ ──
        [Serializable]
        public class MCPLogEntry
        {
            public string Time;
            public string Path;
            public string Body;
            public string Response;
            public long   Ms;
            public bool   IsError;
            [System.NonSerialized] public bool Expanded;   // UI state ไม่ save
        }

        [Serializable] class MCPLogWrap { public List<MCPLogEntry> items; }

        public static readonly List<MCPLogEntry> Log = new List<MCPLogEntry>();
        const int LOG_MAX = 500;

        static string LogFilePath()
        {
            string dir = System.IO.Path.Combine(Application.dataPath, "..", "Library", "DeltaMCP");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "mcp_log.json");
        }

        public static void SaveLog()
        {
            try
            {
                List<MCPLogEntry> snap;
                lock (Log) { snap = new List<MCPLogEntry>(Log); }
                System.IO.File.WriteAllText(LogFilePath(),
                    JsonUtility.ToJson(new MCPLogWrap { items = snap }));
            }
            catch { }
        }

        public static void LoadLog()
        {
            try
            {
                string p = LogFilePath();
                if (!System.IO.File.Exists(p)) return;
                var wrap = JsonUtility.FromJson<MCPLogWrap>(System.IO.File.ReadAllText(p));
                if (wrap?.items == null) return;
                lock (Log) { Log.Clear(); Log.AddRange(wrap.items); }
            }
            catch { }
        }

        public static void ClearLog()
        {
            lock (Log) { Log.Clear(); }
            SaveLog();
        }

        static int _logSinceLastSave;

        static void AppendLog(string path, string body, string result, long ms)
        {
            var entry = new MCPLogEntry
            {
                Time     = DateTime.Now.ToString("HH:mm:ss"),
                Path     = path,
                Body     = TruncLog(body, 200),
                Response = TruncLog(result, 400),
                Ms       = ms,
                IsError  = result != null && result.Contains("\"error\""),
            };
            lock (Log)
            {
                Log.Add(entry);
                while (Log.Count > LOG_MAX) Log.RemoveAt(0);
                _logSinceLastSave++;
            }
            if (_logSinceLastSave >= 10) { _logSinceLastSave = 0; SaveLog(); }
        }

        static string TruncLog(string s, int max) =>
            s == null ? "" : s.Length > max ? s.Substring(0, max) + "…" : s;

        public static string Dispatch(string path, string body)
        {
            path = ResolvePath(path);   // normalize: รับ command-name ("scene_hierarchy") หรือ path ตรง

            // 1) rate limit — บล็อกถ้ายิงเกิน N คำสั่งใน 1 วินาที
            lock (_rlLock)
            {
                var now = DateTime.UtcNow;
                while (_recent.Count > 0 && (now - _recent.Peek()).TotalSeconds > 1.0) _recent.Dequeue();
                if (_recent.Count >= RATE_MAX_PER_SEC)
                {
                    var rl = "{\"error\":\"rate limit: คำสั่งถี่เกิน (>25/วินาที) ถูกบล็อก — กัน agent/workflow วนรัว\"}";
                    AppendLog(path, body, rl, 0);
                    return rl;
                }
                _recent.Enqueue(now);
            }

            // 2) read-only guard — คำสั่งที่แก้ state ต้องเปิด Allow Write ก่อน
            if (WritePaths.Contains(path) && !AllowWrites)
            {
                var ro = $"{{\"error\":\"READ-ONLY mode: คำสั่ง '{path}' ถูกบล็อก. เปิดเมนู MCP Bridge/Allow Write Commands ก่อนถึงจะแก้ scene/asset ได้\"}}";
                AppendLog(path, body, ro, 0);
                return ro;
            }

            var _sw = System.Diagnostics.Stopwatch.StartNew();
            string _result = path switch
            {
                "/ping"              => Ping(),
                // ── Core Assist Pack (อ่าน + แก้ไขของที่มีอยู่) ──
                "/console/read"      => ReadConsole(body),
                "/object/inspect"    => InspectObject(body),
                "/object/add-component" => AddComponent(body),
                "/object/set-property"  => SetProperty(body),
                "/object/set-transform" => SetTransform(body),
                "/selection/get"     => GetSelection(),
                "/selection/set"     => SetSelection(body),
                "/scene/open"        => OpenScene(body),
                "/scene/save"        => SaveScene(),
                "/console/logfile"   => ReadLogFile(body),
                "/console/clear"     => ClearConsole(),
                "/diagnose/state"    => CaptureState(),
                "/perf/audit"        => PerfAudit(),
                "/perf/worst"        => PerfWorst(),
                "/hot-reload"        => HotReload(body),
                "/compile"           => Compile(),
                "/compile-status"    => CompileStatus(),
                "/server/stop"       => ServerStop(),
                "/play/control"      => PlayControl(body),
                "/script/read"       => ReadScript(body),
                "/scene/list"        => SceneList(),
                "/scene/hierarchy"   => SceneHierarchy(),
                "/scene/count"       => CountComponents(body),
                "/gameobject/create" => CreateGameObject(body),
                "/gameobject/delete" => DeleteGameObject(body),
                "/prefab/create"     => CreatePrefab(body),
                "/prefab/place"      => PlacePrefab(body),
                "/ui/create"         => CreateUI(body),
                "/terrain/create"    => CreateTerrain(body),
                "/terrain/set-heights" => SetTerrainHeights(body),
                "/asset/find"        => FindAsset(body),
                "/console/logs"      => GetConsoleLogs(),
                "/script/create"     => CreateScript(body),
                "/ui/optimize"       => OptimizeUI(),
                "/material/create"   => CreateMaterial(body),
                "/atlas/create"      => CreateSpriteAtlas(body),
                "/audit/textures"    => AuditTextures(),
                "/audit/unused"      => AuditUnusedAssets(),
                "/audit/empty-folders" => AuditEmptyFolders(),
                "/diagnose/deep"    => DiagnoseDeep(body),
                "/diagnose/memory"  => MemorySnapshot(),
                "/diagnose/fusion"  => FusionStats(),
                "/diagnose/exceptions" => ExecuteOnMainThread(() => ExceptionTracker.GetReport()),
                "/diagnose/exceptions-clear" => ExecuteOnMainThread(() => { ExceptionTracker.Clear(); return "{\"cleared\":true}"; }),
                "/watch/add"   => WatchAdd(body),
                "/watch/get"   => ExecuteOnMainThread(() => RuntimeWatch.GetReport()),
                "/watch/clear" => ExecuteOnMainThread(() => { RuntimeWatch.ClearAll(); return "{\"cleared\":true}"; }),
                "/code/refactor-audit" => RefactorAuditCmd(body),
                "/code/run"          => RunCsharp(body),
                _ => $"{{\"error\":\"Unknown command: {path}\"}}"
            };
            _sw.Stop();
            AppendLog(path, body, _result, _sw.ElapsedMilliseconds);
            return _result;
        }

        // ── Ping ────────────────────────────────────────────────────────────
        static string Ping() => "{\"status\":\"ok\",\"version\":\"1.0\"}";

        // ── Run C# ──────────────────────────────────────────────────────────
        // Escape hatch: compile + run arbitrary C# against the live Editor/scene via Roslyn
        // (RuntimeCompiler). Lets the AI do anything Unity exposes when no dedicated tool fits —
        // build prefabs from models, batch-edit assets, drive the importer, prototype gameplay live.
        // Put logic in `public static string Run()` (its return value is reported back) or a
        // MonoBehaviour (auto-attached to a fresh GameObject in the playing scene). Write-gated.
        static string RunCsharp(string body)
        {
            var data = ParseReq<RunCsharpRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                if (string.IsNullOrEmpty(data.code))
                    return "{\"error\":\"code required — a full C# source file. Put logic in 'public static string Run()' (return value is reported) or a MonoBehaviour (auto-attached in the live scene).\"}";
                bool ok = RuntimeCompiler.CompileAndRun(data.code, out string log);
                return $"{{\"ok\":{(ok ? "true" : "false")},\"log\":\"{EscapeJson(log)}\"}}";
            });
        }

        // ── Scene ───────────────────────────────────────────────────────────
        // ทุก handler ที่แตะ Unity API ต้อง wrap ExecuteOnMainThread
        // เพราะถูกเรียกจาก HTTP listener thread (background) ซึ่งเรียก Unity API ตรงๆ ไม่ได้
        static string SceneList() => ExecuteOnMainThread(() =>
        {
            var scenes = new System.Text.StringBuilder("[");
            var guids = AssetDatabase.FindAssets("t:Scene");
            for (int i = 0; i < guids.Length; i++)
            {
                if (i > 0) scenes.Append(",");
                scenes.Append($"\"{AssetDatabase.GUIDToAssetPath(guids[i])}\"");
            }
            scenes.Append("]");
            return $"{{\"scenes\":{scenes}}}";
        });

        static string SceneHierarchy() => ExecuteOnMainThread(() =>
        {
            int budget = 2000;   // จำกัดจำนวน node — กัน scene ใหญ่ serialize จน payload บวม/ช้า
            var sb = new System.Text.StringBuilder("[");
            bool first = true;
            foreach (GameObject go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (budget <= 0) break;
                if (!first) sb.Append(",");
                sb.Append(GameObjectJson(go, 0, ref budget));
                first = false;
            }
            sb.Append("]");
            return $"{{\"hierarchy\":{sb},\"truncated\":{(budget <= 0 ? "true" : "false")}}}";
        });

        // นับจำนวน component ตามชื่อ type ใน scene (รวม inactive)
        // เช่น Fusion.NetworkObject, NetworkBehaviour, Rigidbody, ฯลฯ
        static string CountComponents(string body)
        {
            var data = ParseReq<CountRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                if (string.IsNullOrEmpty(data.type))
                    return "{\"error\":\"type required (e.g. Fusion.NetworkObject, Rigidbody)\"}";

                // หา Type จากชื่อ (ลองทั้งชื่อเต็มและ short name ทุก assembly)
                Type t = FindComponentType(data.type);
                if (t == null)
                    return $"{{\"error\":\"type not found: {EscapeJson(data.type)}\"}}";

                var found = Resources.FindObjectsOfTypeAll(t)
                    .OfType<Component>()
                    .Where(c => c.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(c.gameObject))
                    .ToList();

                // แยก active (ใช้งานอยู่) vs inactive (อยู่ใน pool / SetActive false)
                int active = 0, inactive = 0, enabledCount = 0;
                var activeNames = new List<string>();
                var inactiveNames = new List<string>();
                foreach (var c in found)
                {
                    bool goActive = c.gameObject.activeInHierarchy;
                    if (goActive) { active++; if (activeNames.Count < 50) activeNames.Add(c.gameObject.name); }
                    else          { inactive++; if (inactiveNames.Count < 50) inactiveNames.Add(c.gameObject.name); }

                    // component ที่ enable อยู่ด้วย (Behaviour) — active จริงทั้ง GO + component
                    if (goActive && (!(c is Behaviour b) || b.enabled)) enabledCount++;
                }

                return $"{{\"type\":\"{EscapeJson(t.FullName)}\"," +
                       $"\"total\":{found.Count}," +
                       $"\"active\":{active},\"inactive\":{inactive}," +
                       $"\"activeAndEnabled\":{enabledCount}," +
                       $"\"activeObjects\":{JsonArray(activeNames)}," +
                       $"\"inactiveObjects\":{JsonArray(inactiveNames)}}}";
            });
        }

        static string JsonArray(System.Collections.Generic.List<string> items)
        {
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"\"{EscapeJson(items[i])}\"");
            }
            return sb.Append("]").ToString();
        }

        static Type FindComponentType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name);
                if (t != null && typeof(Component).IsAssignableFrom(t)) return t;
            }
            // fallback: match by short name
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    if (t.Name == name && typeof(Component).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        static string GameObjectJson(GameObject go, int depth, ref int budget)
        {
            budget--;   // นับ node ที่ใช้ไป (กันบวม)
            var sb = new System.Text.StringBuilder();
            sb.Append($"{{\"name\":\"{EscapeJson(go.name)}\",\"active\":{go.activeSelf.ToString().ToLower()},\"children\":[");
            if (depth < 4 && budget > 0)
            {
                bool first = true;
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    if (budget <= 0) break;
                    if (!first) sb.Append(",");
                    sb.Append(GameObjectJson(go.transform.GetChild(i).gameObject, depth + 1, ref budget));
                    first = false;
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ── GameObject ──────────────────────────────────────────────────────
        static string CreateGameObject(string body)
        {
            var data = ParseReq<CreateGORequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = string.IsNullOrEmpty(data.primitive)
                    ? new GameObject(data.name)
                    : GameObject.CreatePrimitive(ParsePrimitive(data.primitive));

                go.name = data.name;

                if (!string.IsNullOrEmpty(data.parent))
                {
                    var parent = GameObject.Find(data.parent);
                    if (parent != null) go.transform.SetParent(parent.transform);
                }

                go.transform.localPosition = new Vector3(data.x, data.y, data.z);
                Undo.RegisterCreatedObjectUndo(go, $"MCP Create {go.name}");
                return $"{{\"created\":\"{EscapeJson(go.name)}\",\"instanceId\":{go.GetInstanceID()}}}";
            });
        }

        static string DeleteGameObject(string body)
        {
            var data = ParseReq<NameRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = GameObject.Find(data.name);
                if (go == null) return $"{{\"error\":\"Not found: {data.name}\"}}";
                Undo.DestroyObjectImmediate(go);
                return $"{{\"deleted\":\"{EscapeJson(data.name)}\"}}";
            });
        }

        // ── Prefab ──────────────────────────────────────────────────────────
        static string CreatePrefab(string body)
        {
            var data = ParseReq<CreatePrefabRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var go = new GameObject(data.name);
                string dir = string.IsNullOrEmpty(data.folder) ? "Assets/Prefabs" : data.folder;

                if (!AssetDatabase.IsValidFolder(dir))
                {
                    string parent = Path.GetDirectoryName(dir).Replace("\\", "/");
                    string leaf = Path.GetFileName(dir);
                    AssetDatabase.CreateFolder(parent, leaf);
                }

                string prefabPath = $"{dir}/{data.name}.prefab";
                PrefabUtility.SaveAsPrefabAssetAndConnect(go, prefabPath, InteractionMode.AutomatedAction);
                UnityEngine.Object.DestroyImmediate(go);
                AssetDatabase.Refresh();
                return $"{{\"prefab\":\"{EscapeJson(prefabPath)}\"}}";
            });
        }

        // ── Place Prefab ─────────────────────────────────────────────────────
        static string PlacePrefab(string body)
        {
            var data = ParseReq<PlacePrefabRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var guids = AssetDatabase.FindAssets($"{data.name} t:Prefab");
                if (guids.Length == 0)
                    return $"{{\"error\":\"Prefab not found: {EscapeJson(data.name)}\"}}";

                string prefabPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabAsset == null)
                    return $"{{\"error\":\"Failed to load: {EscapeJson(prefabPath)}\"}}";

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
                instance.transform.position = new Vector3(data.x, data.y, data.z);
                Undo.RegisterCreatedObjectUndo(instance, $"MCP Place {instance.name}");
                return $"{{\"placed\":\"{EscapeJson(instance.name)}\",\"path\":\"{EscapeJson(prefabPath)}\",\"instanceId\":{instance.GetInstanceID()}}}";
            });
        }

        // ── Create Terrain — สร้าง Terrain ใหม่ + gen เนินด้วย Perlin ─────────
        static string CreateTerrain(string body)
        {
            var data = ParseReq<CreateTerrainRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                string name = string.IsNullOrEmpty(data.name) ? "Terrain" : data.name;
                float width  = data.width  > 0 ? data.width  : 500f;
                float length = data.length > 0 ? data.length : 500f;
                float height = data.height > 0 ? data.height : 100f;

                var td = new TerrainData { heightmapResolution = 513 };
                td.size = new Vector3(width, height, length);

                // gen เนินด้วย Perlin noise (ถ้า generate = true หรือ scale > 0)
                if (data.generate || data.scale > 0)
                {
                    float scale = data.scale > 0 ? data.scale : 0.01f;
                    int res = td.heightmapResolution;
                    var heights = new float[res, res];
                    for (int y = 0; y < res; y++)
                        for (int x = 0; x < res; x++)
                            heights[y, x] = Mathf.PerlinNoise(x * scale, y * scale) * (data.amplitude > 0 ? data.amplitude : 0.3f);
                    td.SetHeights(0, 0, heights);
                }

                // เซฟ TerrainData เป็น asset (Terrain ต้องมี data ที่ persist)
                EnsureFolder("Assets/TerrainData");
                string tdPath = AssetDatabase.GenerateUniqueAssetPath($"Assets/TerrainData/{name}.asset");
                AssetDatabase.CreateAsset(td, tdPath);

                var go = Terrain.CreateTerrainGameObject(td);
                go.name = name;
                Undo.RegisterCreatedObjectUndo(go, $"MCP Create Terrain {name}");
                AssetDatabase.SaveAssets();
                return $"{{\"terrain\":\"{EscapeJson(name)}\",\"data\":\"{EscapeJson(tdPath)}\",\"size\":[{width},{height},{length}]}}";
            });
        }

        // ── UI ───────────────────────────────────────────────────────────────
        static string CreateUI(string body)
        {
            var data = ParseReq<CreateUIRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                // Ensure Canvas
                var canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
                if (canvas == null)
                {
                    var canvasGO = new GameObject("Canvas");
                    canvas = canvasGO.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvasGO.AddComponent<CanvasScaler>();
                    canvasGO.AddComponent<GraphicRaycaster>();
                    Undo.RegisterCreatedObjectUndo(canvasGO, "MCP Create Canvas");
                }

                GameObject uiGO = data.type switch
                {
                    "button" => CreateButton(data.name, canvas.transform),
                    "text"   => CreateText(data.name, canvas.transform, data.text),
                    "image"  => CreateImage(data.name, canvas.transform),
                    "panel"  => CreatePanel(data.name, canvas.transform),
                    _        => new GameObject(data.name)
                };

                var rt = uiGO.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchoredPosition = new Vector2(data.x, data.y);
                    if (data.width > 0)  rt.sizeDelta = new Vector2(data.width, data.height);
                }

                Undo.RegisterCreatedObjectUndo(uiGO, $"MCP Create UI {data.name}");
                return $"{{\"ui\":\"{EscapeJson(uiGO.name)}\",\"type\":\"{data.type}\"}}";
            });
        }

        static GameObject CreateButton(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            go.AddComponent<Image>();
            go.AddComponent<Button>();

            var label = new GameObject("Label");
            label.transform.SetParent(go.transform, false);
            var txt = label.AddComponent<Text>();
            txt.text = name;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = Color.black;
            return go;
        }

        static GameObject CreateText(string name, Transform parent, string content)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var txt = go.AddComponent<Text>();
            txt.text = string.IsNullOrEmpty(content) ? name : content;
            txt.fontSize = 24;
            txt.color = Color.white;
            return go;
        }

        static GameObject CreateImage(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            go.AddComponent<Image>();
            return go;
        }

        static GameObject CreatePanel(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = new Color(0, 0, 0, 0.5f);
            return go;
        }

        // ── Terrain ─────────────────────────────────────────────────────────
        static string SetTerrainHeights(string body)
        {
            var data = ParseReq<TerrainRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var terrain = UnityEngine.Object.FindObjectOfType<Terrain>();
                if (terrain == null) return "{\"error\":\"No Terrain found in scene\"}";

                var td = terrain.terrainData;
                int res = td.heightmapResolution;
                float[,] heights = td.GetHeights(0, 0, res, res);

                // Apply simple noise if no data provided
                if (data.heights == null || data.heights.Length == 0)
                {
                    float scale = data.scale > 0 ? data.scale : 0.02f;
                    for (int y = 0; y < res; y++)
                        for (int x = 0; x < res; x++)
                            heights[y, x] = Mathf.PerlinNoise(x * scale, y * scale);
                }
                else
                {
                    int side = (int)Mathf.Sqrt(data.heights.Length);
                    for (int y = 0; y < Mathf.Min(side, res); y++)
                        for (int x = 0; x < Mathf.Min(side, res); x++)
                            heights[y, x] = data.heights[y * side + x];
                }

                td.SetHeights(0, 0, heights);
                return "{\"terrain\":\"heights applied\"}";
            });
        }

        // ── Asset ────────────────────────────────────────────────────────────
        static string FindAsset(string body)
        {
            var data = ParseReq<FindAssetRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                string filter = string.IsNullOrEmpty(data.type) ? data.name : $"{data.name} t:{data.type}";
                var guids = AssetDatabase.FindAssets(filter);
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < guids.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append($"\"{EscapeJson(AssetDatabase.GUIDToAssetPath(guids[i]))}\"");
                }
                sb.Append("]");
                return $"{{\"assets\":{sb}}}";
            });
        }

        // ── Console Logs ─────────────────────────────────────────────────────
        static string GetConsoleLogs() => "{\"note\":\"Use Unity Console window — log capture requires reflection hook\"}";

        // ── Create Script ─────────────────────────────────────────────────
        static string CreateScript(string body)
        {
            var data = ParseReq<CreateScriptRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                string folder = string.IsNullOrEmpty(data.folder) ? "Assets/GameScripts" : data.folder;
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    string parent = Path.GetDirectoryName(folder).Replace("\\", "/");
                    string leaf = Path.GetFileName(folder);
                    AssetDatabase.CreateFolder(parent, leaf);
                }

                string fileName = data.name.EndsWith(".cs") ? data.name : data.name + ".cs";
                string filePath = $"{folder}/{fileName}";
                string absPath = Path.Combine(Application.dataPath.Replace("Assets", ""), filePath);

                string code = string.IsNullOrEmpty(data.code)
                    ? GenerateDefaultScript(data.name)
                    : data.code;

                File.WriteAllText(absPath, code, System.Text.Encoding.UTF8);
                AssetDatabase.Refresh();
                return $"{{\"script\":\"{EscapeJson(filePath)}\"}}";
            });
        }

        static string GenerateDefaultScript(string name)
        {
            string className = Path.GetFileNameWithoutExtension(name);
            return $@"using UnityEngine;

public class {className} : MonoBehaviour
{{
    void Start()
    {{
    }}

    void Update()
    {{
    }}
}}";
        }

        // ── Optimize UI ───────────────────────────────────────────────────
        static string OptimizeUI()
        {
            return ExecuteOnMainThread(() =>
            {
                var report = new System.Text.StringBuilder();
                int fixes = 0;

                // 1. Disable Raycast Target on non-interactive elements
                foreach (var img in UnityEngine.Object.FindObjectsOfType<Image>())
                {
                    if (img.GetComponent<Button>() == null && img.GetComponent<Toggle>() == null && img.raycastTarget)
                    {
                        Undo.RecordObject(img, "MCP OptimizeUI");
                        img.raycastTarget = false;
                        fixes++;
                    }
                }
                foreach (var txt in UnityEngine.Object.FindObjectsOfType<Text>())
                {
                    if (txt.raycastTarget)
                    {
                        Undo.RecordObject(txt, "MCP OptimizeUI");
                        txt.raycastTarget = false;
                        fixes++;
                    }
                }

                // 2. Disable Pixel Perfect on Canvas (causes expensive redraws)
                foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (canvas.pixelPerfect)
                    {
                        Undo.RecordObject(canvas, "MCP OptimizeUI");
                        canvas.pixelPerfect = false;
                        report.Append("Disabled pixelPerfect on " + canvas.name + ". ");
                        fixes++;
                    }
                }

                // 3. Find LayoutGroups with many children (expensive)
                foreach (var lg in UnityEngine.Object.FindObjectsOfType<LayoutGroup>())
                {
                    if (lg.transform.childCount > 20)
                        report.Append($"Warning: {lg.name} has {lg.transform.childCount} children in LayoutGroup — consider virtualization. ");
                }

                // 4. Find Canvases not set to Screen Space Overlay with no camera
                foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera == null)
                        report.Append($"Warning: Canvas '{canvas.name}' is ScreenSpaceCamera but has no camera assigned. ");
                }

                string summary = fixes > 0
                    ? $"Applied {fixes} optimizations. "
                    : "No auto-fixable issues found. ";

                return $"{{\"optimized\":{fixes},\"report\":\"{EscapeJson(summary + report.ToString())}\"}}";
            });
        }

        // ── Create Material ───────────────────────────────────────────────
        static string CreateMaterial(string body)
        {
            var data = ParseReq<CreateMaterialRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                string shaderName = string.IsNullOrEmpty(data.shader) ? "Universal Render Pipeline/Lit" : data.shader;
                var shader = Shader.Find(shaderName);
                if (shader == null) shader = Shader.Find("Standard");
                if (shader == null) return "{\"error\":\"no shader found\"}";

                var mat = new Material(shader);
                if (!string.IsNullOrEmpty(data.color) && ColorUtility.TryParseHtmlString(data.color, out var col))
                {
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", col);
                    else if (mat.HasProperty("_Color")) mat.SetColor("_Color", col);
                }

                string folder = string.IsNullOrEmpty(data.folder) ? "Assets/Materials" : data.folder;
                EnsureFolder(folder);
                string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{data.name}.mat");
                AssetDatabase.CreateAsset(mat, path);
                AssetDatabase.SaveAssets();
                return $"{{\"material\":\"{EscapeJson(path)}\"}}";
            });
        }

        // ── Create Sprite Atlas (รวม 2D ลด draw call) ──────────────────────
        static string CreateSpriteAtlas(string body)
        {
            var data = ParseReq<CreateAtlasRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                string folder = string.IsNullOrEmpty(data.folder) ? "Assets/Textures" : data.folder;
                EnsureFolder("Assets/Atlases");
                string path = AssetDatabase.GenerateUniqueAssetPath($"Assets/Atlases/{data.name}.spriteatlas");

                var atlas = new SpriteAtlasAsset();
                // หา sprite/texture ในโฟลเดอร์ที่ระบุ
                var guids = AssetDatabase.FindAssets("t:Sprite", new[] { folder });
                var objs = new List<UnityEngine.Object>();
                foreach (var g in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    var spr = AssetDatabase.LoadAssetAtPath<Sprite>(p);
                    if (spr != null) objs.Add(spr);
                }
                if (objs.Count == 0) return $"{{\"error\":\"no sprites found in {EscapeJson(folder)}\"}}";

                atlas.Add(objs.ToArray());
                SpriteAtlasAsset.Save(atlas, path);
                AssetDatabase.Refresh();
                return $"{{\"atlas\":\"{EscapeJson(path)}\",\"sprites\":{objs.Count}}}";
            });
        }

        // ── Audit: textures ที่ควร optimize ────────────────────────────────
        static string AuditTextures()
        {
            return ExecuteOnMainThread(() =>
            {
                var sb = new System.Text.StringBuilder();
                var guids = AssetDatabase.FindAssets("t:Texture2D");
                int flagged = 0;
                sb.Append("[");
                foreach (var g in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(g);
                    if (IsThirdParty(path)) continue;
                    var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (imp == null) continue;

                    var reasons = new List<string>();
                    if (imp.maxTextureSize >= 4096) reasons.Add("very large (>=4096)");
                    if (imp.textureCompression == TextureImporterCompression.Uncompressed) reasons.Add("uncompressed");
                    if (imp.mipmapEnabled && imp.textureType == TextureImporterType.Sprite) reasons.Add("mipmap on sprite (waste)");
                    if (imp.isReadable) reasons.Add("read/write enabled (2x memory)");
                    if (imp.anisoLevel > 4 && imp.textureType == TextureImporterType.Default)
                        reasons.Add($"aniso={imp.anisoLevel} (>4 แพง — ใช้เฉพาะ floor/road)");
                    if (!imp.mipmapEnabled && imp.textureType == TextureImporterType.Default && imp.maxTextureSize >= 512)
                        reasons.Add("mipmap disabled on 3D texture (≥512px) — เปิด mipmap ลด aliasing + GPU cache miss");
                    var tex2d = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (tex2d != null && imp.textureType != TextureImporterType.Sprite)
                    {
                        bool npot = (tex2d.width & (tex2d.width - 1)) != 0 || (tex2d.height & (tex2d.height - 1)) != 0;
                        if (npot) reasons.Add($"non-power-of-2 ({tex2d.width}x{tex2d.height}) — บาง platform บีบอัดไม่ได้");
                    }

                    if (reasons.Count > 0)
                    {
                        if (flagged > 0) sb.Append(",");
                        sb.Append($"{{\"path\":\"{EscapeJson(path)}\",\"issues\":\"{EscapeJson(string.Join(", ", reasons))}\"}}");
                        flagged++;
                        if (flagged >= 100) break;
                    }
                }
                sb.Append("]");
                return $"{{\"flagged\":{flagged},\"textures\":{sb}}}";
            });
        }

        // ── Audit: asset ที่อาจไม่ได้ใช้ (REPORT ONLY — ไม่ลบ) ──────────────
        static string AuditUnusedAssets()
        {
            return ExecuteOnMainThread(() =>
            {
                // รวม dependency ของทุก scene + ทุก prefab ใน Resources/Addressables (กันพลาด)
                var used = new HashSet<string>();
                var roots = new List<string>();
                // Scenes
                roots.AddRange(AssetDatabase.FindAssets("t:Scene").Select(AssetDatabase.GUIDToAssetPath));
                // Resources — โหลด runtime ด้วยชื่อ นับทุกอย่างเป็น used
                foreach (var g in AssetDatabase.FindAssets("", new[] { "Assets/Resources" }))
                    roots.Add(AssetDatabase.GUIDToAssetPath(g));
                // StreamingAssets
                if (AssetDatabase.IsValidFolder("Assets/StreamingAssets"))
                    foreach (var g in AssetDatabase.FindAssets("", new[] { "Assets/StreamingAssets" }))
                        roots.Add(AssetDatabase.GUIDToAssetPath(g));
                // Addressables (ถ้ามี) — อ่าน entries ผ่าน reflection ไม่ผูก assembly
                try {
                    if (AssetDatabase.IsValidFolder("Assets/AddressableAssetsData"))
                        foreach (var g in AssetDatabase.FindAssets("t:AddressableAssetGroup", new[] { "Assets/AddressableAssetsData" }))
                        {
                            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AssetDatabase.GUIDToAssetPath(g));
                            if (obj == null) continue;
                            var entries = obj.GetType().GetProperty("entries")?.GetValue(obj) as System.Collections.IEnumerable;
                            if (entries == null) continue;
                            foreach (var entry in entries) {
                                var ap = entry.GetType().GetProperty("AssetPath")?.GetValue(entry) as string;
                                if (!string.IsNullOrEmpty(ap)) roots.Add(ap);
                            }
                        }
                } catch { }

                foreach (var dep in AssetDatabase.GetDependencies(roots.ToArray(), true))
                    used.Add(dep);

                var candidates = new List<string>();
                foreach (var g in AssetDatabase.FindAssets("t:Texture2D t:Material t:Mesh t:AudioClip t:Prefab"))
                {
                    string path = AssetDatabase.GUIDToAssetPath(g);
                    if (IsThirdParty(path)) continue;
                    if (path.Contains("/Resources/") || path.Contains("/StreamingAssets/")) continue;
                    if (used.Contains(path)) continue;
                    candidates.Add(path);
                    if (candidates.Count >= 150) break;
                }

                // จัดกลุ่มตาม type เพื่ออ่านง่ายขึ้น
                var byType = new Dictionary<string, int>();
                foreach (var p in candidates)
                {
                    string ext = System.IO.Path.GetExtension(p).ToLower();
                    string t = ext switch {
                        ".png" or ".jpg" or ".jpeg" or ".tga" or ".psd" or ".exr" => "texture",
                        ".mat" => "material",
                        ".fbx" or ".obj" or ".blend" => "mesh",
                        ".mp3" or ".wav" or ".ogg" or ".aiff" => "audio",
                        ".prefab" => "prefab",
                        _ => "other"
                    };
                    byType[t] = (byType.TryGetValue(t, out var n) ? n : 0) + 1;
                }
                var typeSb = new System.Text.StringBuilder("{");
                bool firstT = true;
                foreach (var kv in byType) {
                    if (!firstT) typeSb.Append(",");
                    firstT = false;
                    typeSb.Append($"\"{kv.Key}\":{kv.Value}");
                }
                typeSb.Append("}");

                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append($"\"{EscapeJson(candidates[i])}\"");
                }
                sb.Append("]");
                return $"{{\"warning\":\"REPORT ONLY — ตรวจด้วยตาก่อนลบ! string-load/runtime-generated มองไม่เห็น\"," +
                       $"\"maybe_unused\":{candidates.Count},\"byType\":{typeSb},\"assets\":{sb}}}";
            });
        }

        // ── Audit: folder ว่าง ─────────────────────────────────────────────
        static string AuditEmptyFolders()
        {
            return ExecuteOnMainThread(() =>
            {
                var empty = new List<string>();
                foreach (var dir in Directory.GetDirectories(Application.dataPath, "*", SearchOption.AllDirectories))
                {
                    bool hasAsset = Directory.GetFiles(dir).Any(f => !f.EndsWith(".meta"));
                    bool hasSub = Directory.GetDirectories(dir).Length > 0;
                    if (!hasAsset && !hasSub)
                    {
                        string rel = "Assets" + dir.Replace(Application.dataPath, "").Replace("\\", "/");
                        empty.Add(rel);
                    }
                    if (empty.Count >= 100) break;
                }
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < empty.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append($"\"{EscapeJson(empty[i])}\"");
                }
                sb.Append("]");
                return $"{{\"empty_folders\":{empty.Count},\"folders\":{sb}}}";
            });
        }

        // ── RuntimeWatch: add a watch entry (main-thread safe) ──────────────
        static string WatchAdd(string body)
        {
            var data = ParseReq<WatchAddRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                string err = RuntimeWatch.AddWatch(data.objectName, data.component, data.field);
                if (err != null) return $"{{\"error\":\"{EscapeJson(err)}\"}}";
                string key = data.objectName + "." + data.component + "." + data.field;
                return $"{{\"added\":\"{EscapeJson(key)}\"}}";
            });
        }

        // ── Refactor Audit — สแกน .cs ทุกไฟล์ รายงาน refactoring opportunities ──
        static string RefactorAuditCmd(string body)
        {
            var data = ParseReq<TopNRequest>(body);
            int n = data != null && data.topN > 0 ? data.topN : 10;
            // ดึง dataPath บน main thread ก่อน แล้วรัน I/O หนักบน background thread
            string dataPath = ExecuteOnMainThread(() => UnityEngine.Application.dataPath);
            return RefactorAudit.Analyze(n, dataPath);
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace("\\", "/");
            string leaf = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        static bool IsThirdParty(string path)
        {
            string[] ex = { "/Plugins/", "/PlayFabSDK/", "/Photon/", "/CBS/", "/GPUInstancer/",
                            "/TextMesh Pro/", "/ProBuilder", "/Polybrush", "/MeshBaker/", "/NuGet/" };
            foreach (var e in ex) if (path.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        static int _mainThreadId = -1;

        // คิวงานที่ต้องรันบน main thread — drain ทุก frame ผ่าน EditorApplication.update
        // (เร็ว/แน่นอนกว่า delayCall ที่ยิงแค่ frame ถัดไปและบางทีไม่ tick ตอน editor idle)
        static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainQueue
            = new System.Collections.Concurrent.ConcurrentQueue<Action>();

        [InitializeOnLoadMethod]
        static void CaptureMainThread()
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            _allowWritesCache = EditorPrefs.GetBool("DeltaMCP_AllowWrites", false);   // prime cache บน main thread
            EditorApplication.update -= PumpMainQueue;
            EditorApplication.update += PumpMainQueue;
        }

        static void PumpMainQueue()
        {
            while (_mainQueue.TryDequeue(out var job))
            {
                try { job(); } catch { /* แต่ละ job จัดการ exception เองแล้ว */ }
            }
        }

        static string ExecuteOnMainThread(Func<string> action)
        {
            // ถ้าถูกเรียกจาก main thread อยู่แล้ว (เช่น Chat Window) → รันตรงๆ
            // ไม่งั้นจะ block ตัวเอง ทำให้คิวไม่ถูก drain → timeout (deadlock)
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                try { return action(); }
                catch (Exception e) { return $"{{\"error\":\"{EscapeJson(e.Message)}\"}}"; }
            }

            // ถูกเรียกจาก background thread (HTTP server) → enqueue ไป main thread แล้วรอ
            string result = null;
            Exception ex = null;
            var done = new System.Threading.ManualResetEventSlim(false);

            _mainQueue.Enqueue(() =>
            {
                try { result = action(); }
                catch (Exception e) { ex = e; }
                finally { done.Set(); }
            });

            // 20 วิ — เผื่อ scene ใหญ่ (SceneHierarchy serialize ทั้ง scene) / ตอน Unity compile-import
            if (!done.Wait(20000))
                return "{\"error\":\"timeout (20s) — main thread busy (กำลัง compile/import? หรือ scene ใหญ่มาก)\"}";
            if (ex != null) return $"{{\"error\":\"{EscapeJson(ex.Message)}\"}}";
            return result ?? "{\"error\":\"no result\"}";
        }

        static PrimitiveType ParsePrimitive(string s) => s?.ToLower() switch
        {
            "cube"     => PrimitiveType.Cube,
            "sphere"   => PrimitiveType.Sphere,
            "cylinder" => PrimitiveType.Cylinder,
            "plane"    => PrimitiveType.Plane,
            "capsule"  => PrimitiveType.Capsule,
            "quad"     => PrimitiveType.Quad,
            _          => PrimitiveType.Cube
        };

        static string EscapeJson(string s) =>
            s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") ?? "";

        // public alias — ใช้จาก class อื่นใน namespace เดียวกัน (เช่น RefactorAudit)
        public static string EscapeJsonPublic(string s) => EscapeJson(s);

        // parse JSON เป็น request object แบบ thread-safe (ไม่ใช้ JsonUtility ที่ต้อง main thread)
        // รองรับ field: string, int, float, bool, float[] — พอสำหรับ request ทุกตัว
        static T ParseReq<T>(string json) where T : new()
        {
            var t = new T();
            if (string.IsNullOrEmpty(json)) return t;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var f in typeof(T).GetFields())
            {
                string key = System.Text.RegularExpressions.Regex.Escape(f.Name);
                if (f.FieldType == typeof(string))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                    if (m.Success) f.SetValue(t, m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n"));
                }
                else if (f.FieldType == typeof(int))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
                    if (m.Success) f.SetValue(t, int.Parse(m.Groups[1].Value));
                }
                else if (f.FieldType == typeof(float))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+\\.?\\d*)");
                    if (m.Success) f.SetValue(t, float.Parse(m.Groups[1].Value, inv));
                }
                else if (f.FieldType == typeof(bool))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
                    if (m.Success) f.SetValue(t, m.Groups[1].Value == "true");
                }
                else if (f.FieldType == typeof(float[]))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\[([^\\]]*)\\]");
                    if (m.Success)
                    {
                        var list = new System.Collections.Generic.List<float>();
                        foreach (var p in m.Groups[1].Value.Split(','))
                            if (float.TryParse(p.Trim(), System.Globalization.NumberStyles.Float, inv, out var v)) list.Add(v);
                        f.SetValue(t, list.ToArray());
                    }
                }
            }
            return t;
        }

        // ── Request models ────────────────────────────────────────────────────
        [Serializable] class NameRequest         { public string name; }
        [Serializable] class FindAssetRequest   { public string name; public string type; }
        [Serializable] class CreateGORequest    { public string name; public string primitive; public string parent; public float x, y, z; }
        [Serializable] class CreatePrefabRequest{ public string name; public string folder; }
        [Serializable] class PlacePrefabRequest { public string name; public float x, y, z; }
        [Serializable] class CreateUIRequest    { public string name; public string type; public string text; public float x, y, width, height; }
        [Serializable] class TerrainRequest     { public float[] heights; public float scale; }
        [Serializable] class CreateTerrainRequest { public string name; public float width; public float length; public float height; public float scale; public float amplitude; public bool generate; }
        [Serializable] class CreateScriptRequest{ public string name; public string folder; public string code; }
        [Serializable] class CreateMaterialRequest { public string name; public string shader; public string color; public string folder; }
        [Serializable] class CreateAtlasRequest  { public string name; public string folder; }
        [Serializable] class CountRequest        { public string type; }
        [Serializable] class HotReloadRequest    { public string action; }
        [Serializable] class WatchAddRequest     { public string objectName; public string component; public string field; }
        [Serializable] class RunCsharpRequest    { public string code; }
    }
}
