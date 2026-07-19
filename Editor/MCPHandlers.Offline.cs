using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace MCPBridge
{
    // Offline pack — เครื่องมือสำหรับเกม single-player/offline:
    //   ScriptableObject (อ่าน/แก้ config/balance/data), scene spatial query (raycast/overlap/navmesh),
    //   console_alert (ดัก log ตาม pattern)
    public static partial class MCPHandlers
    {
        // ── ScriptableObject: อ่านค่า ──────────────────────────────────────
        static string ReadScriptableObject(string body)
        {
            var data = ParseReq<SoRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var so = ResolveScriptableObject(data.path, data.name, out string path);
                if (so == null) return $"{{\"error\":\"ไม่พบ ScriptableObject: {EscapeJson(data.path ?? data.name)}\"}}";

                var sob = new SerializedObject(so);
                var prop = sob.GetIterator();
                var sb = new StringBuilder($"{{\"path\":\"{EscapeJson(path)}\",\"type\":\"{EscapeJson(so.GetType().Name)}\",\"properties\":{{");
                bool first = true; int n = 0;
                prop.Next(true);
                while (prop.NextVisible(false) && n < 60)
                {
                    if (prop.name == "m_Script") continue;
                    string val = PropValue(prop);
                    if (val == null) continue;
                    if (!first) sb.Append(",");
                    sb.Append($"\"{EscapeJson(prop.displayName)}\":{val}");
                    first = false; n++;
                }
                sb.Append("}}");
                return sb.ToString();
            });
        }

        // ── ScriptableObject: แก้ค่า (config/balance/data) ─────────────────
        static string EditScriptableObject(string body)
        {
            var data = ParseReq<SoEditRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                if (string.IsNullOrEmpty(data.property)) return "{\"error\":\"property required\"}";
                var so = ResolveScriptableObject(data.path, data.name, out string path);
                if (so == null) return $"{{\"error\":\"ไม่พบ ScriptableObject: {EscapeJson(data.path ?? data.name)}\"}}";

                var sob = new SerializedObject(so);
                var prop = sob.FindProperty(data.property) ?? FindByDisplayName(sob, data.property);
                if (prop == null) return $"{{\"error\":\"property not found: {EscapeJson(data.property)}\"}}";

                Undo.RecordObject(so, "MCP EditScriptableObject");
                if (!ApplyValue(prop, data.value)) return $"{{\"error\":\"unsupported property type for '{EscapeJson(data.property)}'\"}}";
                sob.ApplyModifiedProperties();
                EditorUtility.SetDirty(so);
                AssetDatabase.SaveAssets();
                return $"{{\"edited\":\"{EscapeJson(path)}\",\"property\":\"{EscapeJson(data.property)}\",\"value\":\"{EscapeJson(data.value)}\"}}";
            });
        }

        static ScriptableObject ResolveScriptableObject(string path, string name, out string resolvedPath)
        {
            resolvedPath = null;
            if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/"))
            {
                var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (so != null) { resolvedPath = path; return so; }
            }
            string term = !string.IsNullOrEmpty(name) ? name : System.IO.Path.GetFileNameWithoutExtension(path ?? "");
            if (string.IsNullOrEmpty(term)) return null;
            foreach (var g in AssetDatabase.FindAssets($"{term} t:ScriptableObject"))
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(p);
                if (so != null) { resolvedPath = p; return so; }
            }
            return null;
        }

        // ── Raycast — ยิง ray หา collider (debug combat/targeting) ─────────
        static string RaycastQuery(string body)
        {
            var data = ParseReq<RaycastRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var origin = new Vector3(data.ox, data.oy, data.oz);
                var dir = new Vector3(data.dx, data.dy, data.dz);
                if (dir.sqrMagnitude < 1e-6f)
                {
                    var target = new Vector3(data.tx, data.ty, data.tz);
                    dir = target - origin;
                    if (dir.sqrMagnitude < 1e-6f) return "{\"error\":\"ต้องระบุ direction (dx,dy,dz) หรือ target (tx,ty,tz) ที่ต่างจาก origin\"}";
                }
                dir.Normalize();
                float maxDist = data.maxDistance > 0 ? data.maxDistance : 1000f;
                int mask = ParseLayerMask(data.layers);

                if (data.all)
                {
                    var hits = Physics.RaycastAll(origin, dir, maxDist, mask).OrderBy(h => h.distance).ToArray();
                    var sb = new StringBuilder($"{{\"hits\":{hits.Length},\"results\":[");
                    for (int i = 0; i < hits.Length && i < 30; i++)
                    {
                        if (i > 0) sb.Append(",");
                        sb.Append(HitJson(hits[i]));
                    }
                    return sb.Append("]}").ToString();
                }
                if (Physics.Raycast(origin, dir, out var hit, maxDist, mask))
                    return $"{{\"hit\":true,\"result\":{HitJson(hit)}}}";
                return "{\"hit\":false}";
            });
        }

        static string HitJson(RaycastHit h)
        {
            var go = h.collider != null ? h.collider.gameObject : null;
            return $"{{\"object\":\"{EscapeJson(go != null ? go.name : "?")}\"," +
                   $"\"layer\":\"{EscapeJson(go != null ? LayerMask.LayerToName(go.layer) : "")}\"," +
                   $"\"distance\":{h.distance:0.###}," +
                   $"\"point\":[{h.point.x:0.##},{h.point.y:0.##},{h.point.z:0.##}]," +
                   $"\"normal\":[{h.normal.x:0.##},{h.normal.y:0.##},{h.normal.z:0.##}]}}";
        }

        // ── Overlap — หา collider ในรัศมี ─────────────────────────────────
        static string OverlapQuery(string body)
        {
            var data = ParseReq<OverlapRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                if (data.radius <= 0) return "{\"error\":\"radius required (> 0)\"}";
                var center = new Vector3(data.cx, data.cy, data.cz);
                int mask = ParseLayerMask(data.layers);
                var cols = Physics.OverlapSphere(center, data.radius, mask);
                var sb = new StringBuilder($"{{\"center\":[{data.cx},{data.cy},{data.cz}],\"radius\":{data.radius},\"count\":{cols.Length},\"objects\":[");
                for (int i = 0; i < cols.Length && i < 50; i++)
                {
                    if (i > 0) sb.Append(",");
                    var go = cols[i].gameObject;
                    float dist = Vector3.Distance(center, go.transform.position);
                    sb.Append($"{{\"name\":\"{EscapeJson(go.name)}\",\"layer\":\"{EscapeJson(LayerMask.LayerToName(go.layer))}\",\"dist\":{dist:0.##}}}");
                }
                return sb.Append("]}").ToString();
            });
        }

        // ── NavMesh path — คำนวณเส้นทาง 2 จุด (debug AI เดินไม่ถึง) ─────────
        static string NavMeshPath(string body)
        {
            var data = ParseReq<NavPathRequest>(body);
            return ExecuteOnMainThread(() =>
            {
                var from = new Vector3(data.fx, data.fy, data.fz);
                var to = new Vector3(data.tx, data.ty, data.tz);
                var path = new NavMeshPath();
                bool ok = NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path);

                float total = 0f;
                var corners = path.corners;
                for (int i = 1; i < corners.Length; i++) total += Vector3.Distance(corners[i - 1], corners[i]);

                var sb = new StringBuilder("{");
                sb.Append($"\"calculated\":{ok.ToString().ToLower()},");
                sb.Append($"\"status\":\"{path.status}\",");   // Complete / Partial / Invalid
                sb.Append($"\"corners\":{corners.Length},\"distance\":{total:0.##},\"path\":[");
                for (int i = 0; i < corners.Length && i < 40; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append($"[{corners[i].x:0.##},{corners[i].y:0.##},{corners[i].z:0.##}]");
                }
                sb.Append("]");
                if (path.status != NavMeshPathStatus.PathComplete)
                    sb.Append(",\"note\":\"Partial/Invalid = ไปไม่ถึง (จุดอยู่นอก NavMesh, ขาดช่วง, หรือ area ถูกปิด) — เช็ค bake + off-mesh link\"");
                return sb.Append("}").ToString();
            });
        }

        // comma-separated layer names → mask (ว่าง = ทุก layer)
        static int ParseLayerMask(string layers)
        {
            if (string.IsNullOrEmpty(layers)) return ~0;
            int mask = 0;
            foreach (var n in layers.Split(','))
            {
                int l = LayerMask.NameToLayer(n.Trim());
                if (l >= 0) mask |= 1 << l;
            }
            return mask == 0 ? ~0 : mask;
        }

        // ── console_alert — ดัก Debug.Log ตาม pattern ────────────────────
        static string ConsoleAlertAdd(string body)
        {
            var data = ParseReq<ConsoleAlertRequest>(body);
            if (string.IsNullOrEmpty(data.pattern)) return "{\"error\":\"pattern required\"}";
            string err = ConsoleAlert.Add(data.pattern, data.level);
            if (err != null) return $"{{\"error\":\"{EscapeJson(err)}\"}}";
            return $"{{\"watching\":\"{EscapeJson(data.pattern)}\",\"level\":\"{EscapeJson(string.IsNullOrEmpty(data.level) ? "all" : data.level)}\"," +
                   "\"note\":\"จะนับ+เก็บ log ที่ตรง pattern ตอน Play — ดูผล console_alert_get\"}";
        }

        // ── Request models ────────────────────────────────────────────────
        [Serializable] class SoRequest      { public string path; public string name; }
        [Serializable] class SoEditRequest  { public string path; public string name; public string property; public string value; }
        [Serializable] class RaycastRequest { public float ox, oy, oz, dx, dy, dz, tx, ty, tz, maxDistance; public bool all; public string layers; }
        [Serializable] class OverlapRequest { public float cx, cy, cz, radius; public string layers; }
        [Serializable] class NavPathRequest { public float fx, fy, fz, tx, ty, tz; }
        [Serializable] class ConsoleAlertRequest { public string pattern; public string level; }
    }
}
