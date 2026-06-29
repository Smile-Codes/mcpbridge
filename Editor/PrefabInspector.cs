using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// A3: อ่านเนื้อใน prefab — โครงสร้าง GameObject + component สำคัญ
    /// (mesh/collider/material/rigidbody/particle/light/script/Fusion)
    /// ⚠️ ใช้ AssetDatabase → ต้องเรียกบน main thread เท่านั้น (chat: Enqueue / handler: ExecuteOnMainThread)
    /// cap จำนวน node กัน prefab ใหญ่ทำ output บานปลาย
    /// </summary>
    public static class PrefabInspector
    {
        public static string Inspect(string prefabPath, int maxNodes = 150)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (go == null) return null;

            var sb = new StringBuilder();
            sb.Append($"Prefab: {prefabPath}");
            int budget = maxNodes;
            Walk(go.transform, sb, 0, ref budget);
            if (budget <= 0) sb.Append($"\n... (ตัด — prefab ใหญ่เกิน {maxNodes} node)");
            return sb.ToString();
        }

        static void Walk(Transform t, StringBuilder sb, int depth, ref int budget)
        {
            if (budget-- <= 0) return;
            var go = t.gameObject;
            string indent = new string(' ', depth * 2);
            sb.Append($"\n{indent}- {go.name}{(go.activeSelf ? "" : " [inactive]")}  (layer={LayerMask.LayerToName(go.layer)}, tag={go.tag})");

            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) { sb.Append($"\n{indent}    • <missing script!>"); continue; }
                string d = Describe(c);
                if (d != null) sb.Append($"\n{indent}    • {d}");
            }

            for (int i = 0; i < t.childCount; i++)
            {
                if (budget <= 0) break;
                Walk(t.GetChild(i), sb, depth + 1, ref budget);
            }
        }

        static string Describe(Component c)
        {
            switch (c)
            {
                case Transform _:
                    return null;   // ข้าม — มีทุก node เป็น noise
                case MeshFilter mf:
                    var m = mf.sharedMesh;
                    if (m == null) return "MeshFilter (no mesh)";
                    string tris = m.isReadable ? $"{m.triangles.Length / 3} tris, " : "";
                    return $"MeshFilter: {m.name} ({tris}{m.vertexCount} verts, {m.subMeshCount} submesh)";
                case SkinnedMeshRenderer smr:
                    return $"SkinnedMeshRenderer: mesh={(smr.sharedMesh ? smr.sharedMesh.name : "?")}, mats={smr.sharedMaterials.Length} [{Mats(smr.sharedMaterials)}], shadow={smr.shadowCastingMode}";
                case MeshRenderer mr:
                    return $"MeshRenderer: mats={mr.sharedMaterials.Length} [{Mats(mr.sharedMaterials)}], shadow={mr.shadowCastingMode}, gpuInstance?={AnyInstanced(mr.sharedMaterials)}";
                case MeshCollider mc:
                    return $"MeshCollider: convex={mc.convex}, trigger={mc.isTrigger}{(mc.sharedMesh ? ", mesh=" + mc.sharedMesh.name : "")}  {(mc.convex ? "" : "⚠️ non-convex แพง")}";
                case Collider col:
                    return $"{col.GetType().Name}: trigger={col.isTrigger}";
                case Rigidbody rb:
                    return $"Rigidbody: mass={rb.mass}, kinematic={rb.isKinematic}, interpolation={rb.interpolation}, collisionDetection={rb.collisionDetectionMode}";
                case ParticleSystem ps:
                    return $"ParticleSystem: maxParticles={ps.main.maxParticles}, startLifetime~{ps.main.startLifetime.constant}";
                case Light lt:
                    return $"Light: {lt.type}, bake={lt.lightmapBakeType}, shadows={lt.shadows}";
                case Canvas cv:
                    return $"Canvas: renderMode={cv.renderMode}";
                case Animator an:
                    return $"Animator: controller={(an.runtimeAnimatorController ? an.runtimeAnimatorController.name : "none")}";
                case MonoBehaviour mb:
                    // NetworkObject / NetworkBehaviour (Fusion) ก็เป็น MonoBehaviour — ชื่อ type บอกเอง
                    return $"Script: {mb.GetType().Name}";
                default:
                    return c.GetType().Name;
            }
        }

        static string Mats(Material[] mats)
        {
            var names = new List<string>();
            foreach (var mm in mats) names.Add(mm ? mm.name : "null");
            return string.Join(", ", names);
        }

        static bool AnyInstanced(Material[] mats)
        {
            foreach (var mm in mats) if (mm && mm.enableInstancing) return true;
            return false;
        }
    }
}
