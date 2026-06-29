using System;
using System.Reflection;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// คุม Hot Reload (SingularityGroup) ผ่าน reflection — ไม่ต้องผูก asmdef กับ package
    /// ใช้สั่งเปิด + เช็คสถานะจาก AI ได้ (เช่น "เล่นอยู่แล้วขอแก้โค้ด → เปิด Hot Reload ให้มั้ย")
    /// </summary>
    public static class HotReloadControl
    {
        // ServerHealthCheck.I.IsServerHealthy = Hot Reload server กำลังรันอยู่หรือไม่
        public static bool IsRunning()
        {
            try
            {
                var t = FindType("SingularityGroup.HotReload.Editor.ServerHealthCheck");
                if (t == null) return false;
                object inst = t.GetProperty("I", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                           ?? t.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (inst == null) return false;
                object healthy = inst.GetType().GetProperty("IsServerHealthy")?.GetValue(inst);
                return healthy is bool b && b;
            }
            catch { return false; }
        }

        // HotReloadCli.StartAsync() — เริ่ม Hot Reload server (ต้องอยู่ main thread)
        public static bool Start(out string msg)
        {
            try
            {
                if (IsRunning()) { msg = "Hot Reload กำลังรันอยู่แล้ว"; return true; }
                var t = FindType("SingularityGroup.HotReload.Editor.Cli.HotReloadCli");
                if (t == null) { msg = "ไม่พบ Hot Reload package (com.singularitygroup.hotreload)"; return false; }
                var m = t.GetMethod("StartAsync", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null) { msg = "ไม่พบ HotReloadCli.StartAsync()"; return false; }
                m.Invoke(null, null);   // เป็น async — fire-and-forget, server จะ start ใน background
                msg = "สั่งเปิด Hot Reload แล้ว (server กำลัง start — รอ ~2-5 วิ)";
                return true;
            }
            catch (Exception e) { msg = "เปิด Hot Reload ไม่สำเร็จ: " + e.Message; return false; }
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullName); if (t != null) return t; } catch { }
            }
            return null;
        }
    }
}
