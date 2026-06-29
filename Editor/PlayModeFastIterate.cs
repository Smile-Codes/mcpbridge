using UnityEditor;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// เร่งรอบ iterate: เปิด "Enter Play Mode Options" (ปิด Domain + Scene Reload)
    /// → กด Play แทบไม่ต้องรอ recompile/reload domain (จากหลายวินาที → เกือบทันที).
    ///
    /// ข้อควรรู้: เมื่อปิด Domain Reload, static field / static event จะ "ไม่ reset" ระหว่างรอบเล่น
    /// โค้ดที่พึ่ง static state ต้อง clear เองใน [RuntimeInitializeOnLoadMethod] หรือ OnEnable.
    /// อันนี้ช่วยเรื่องความเร็ว "เข้า Play" — ไม่ใช่การเพิ่ม class ตอนเล่น (อันนั้นดู Live Code / Roslyn).
    /// </summary>
    public static class PlayModeFastIterate
    {
        const string MENU = "MCP Bridge/Fast Play Mode (no domain reload)";

        [MenuItem(MENU, true)]
        static bool Validate()
        {
            Menu.SetChecked(MENU, IsOn());
            return true;
        }

        [MenuItem(MENU)]
        static void Toggle()
        {
            if (IsOn())
            {
                EditorSettings.enterPlayModeOptionsEnabled = false;
                Debug.Log("[MCP] Fast Play Mode OFF — domain+scene reload กลับมาทำงานปกติ");
            }
            else
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions =
                    EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
                Debug.Log("[MCP] Fast Play Mode ON — กด Play เร็วขึ้นมาก " +
                          "(ระวัง: static field ไม่ reset เอง — clear ใน RuntimeInitializeOnLoadMethod ถ้าจำเป็น)");
            }
        }

        static bool IsOn() =>
            EditorSettings.enterPlayModeOptionsEnabled &&
            EditorSettings.enterPlayModeOptions.HasFlag(EnterPlayModeOptions.DisableDomainReload);
    }
}
