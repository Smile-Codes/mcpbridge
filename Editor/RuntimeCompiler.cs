using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DeltaUnity.MCP
{
    /// <summary>
    /// Live Code — compile C# ตอน runtime ด้วย Roslyn (csc ที่มากับ Unity) แล้วโหลด assembly ใหม่
    /// เข้า AppDomain ปัจจุบัน → รันโค้ดใหม่ "ระหว่างเกมกำลังเล่น" ได้ โดยไม่ stop / ไม่ domain reload.
    ///
    /// ทำไมได้: เราไม่ได้แก้ assembly เดิม (ทำไม่ได้) — แต่ "เพิ่ม assembly ใหม่" เข้าไป
    /// → class/method ใหม่ใช้งานได้ทันที วิ่งกับ scene + state ที่กำลังเล่นอยู่.
    ///
    /// ข้อจำกัด: ใช้ใน Editor (Mono) เท่านั้น (dev tool); assembly ใหม่อยู่จน domain reload ครั้งถัดไป;
    /// แก้โค้ดในไฟล์เกมจริงยังต้อง recompile ตามปกติ — อันนี้คือ sandbox สำหรับลองโค้ดสด.
    /// </summary>
    public static class RuntimeCompiler
    {
        /// <summary>compile โค้ด (ไฟล์ C# เต็ม) แล้วรัน. คืน log ผ่าน out. true = สำเร็จ.</summary>
        public static bool CompileAndRun(string code, out string log)
        {
            var sb = new StringBuilder();
            try
            {
                string tmpDir = Path.Combine(Path.GetTempPath(), "DeltaLiveCode");
                Directory.CreateDirectory(tmpDir);
                string srcFile = Path.Combine(tmpDir, "LiveCode.cs");
                string outDll  = Path.Combine(tmpDir, $"LiveCode_{DateTime.Now.Ticks}.dll");
                File.WriteAllText(srcFile, code, new UTF8Encoding(false));

                // references = assembly ที่โหลดอยู่ทั้งหมด (UnityEngine, game asm, ฯลฯ)
                var refs = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .Select(a => { try { return a.Location; } catch { return null; } })
                    .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                    .Distinct()
                    .ToList();

                // response file (กัน command line ยาวเกิน)
                string rsp = Path.Combine(tmpDir, "args.rsp");
                var args = new StringBuilder();
                args.AppendLine("-target:library");
                args.AppendLine("-langversion:latest");
                args.AppendLine("-nologo");
                args.AppendLine($"-out:\"{outDll}\"");
                foreach (var r in refs) args.AppendLine($"-r:\"{r}\"");
                args.AppendLine($"\"{srcFile}\"");
                File.WriteAllText(rsp, args.ToString());

                if (!FindCompiler(out string exe, out string preArg))
                {
                    log = "หา Roslyn csc ไม่เจอใน Unity install — บอก path ของ Editor มาได้ ผมจะปรับ detection";
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = (string.IsNullOrEmpty(preArg) ? "" : $"\"{preArg}\" ") + $"@\"{rsp}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(30000);

                if (!File.Exists(outDll))
                {
                    log = "Compile ไม่ผ่าน:\n" + stdout + "\n" + stderr;
                    return false;
                }

                // โหลด assembly ใหม่ + รัน
                byte[] bytes = File.ReadAllBytes(outDll);   // โหลดเป็น bytes กัน lock ไฟล์
                var asm = Assembly.Load(bytes);
                int ran = RunAssembly(asm, sb);

                if (ran == 0) sb.AppendLine("⚠ โหลดสำเร็จแต่ไม่มีอะไรรัน — ใส่ class ที่เป็น MonoBehaviour หรือมี `public static void Run()`");
                else sb.Insert(0, $"✅ compile + load สำเร็จ — รัน {ran} entrypoint\n");
                log = sb.ToString();
                return true;
            }
            catch (Exception e)
            {
                log = "error: " + e;
                return false;
            }
        }

        // หา entrypoint แล้วรัน: MonoBehaviour → attach กับ GameObject ใหม่ในซีนสด; class ที่มี static Run() → เรียก
        static int RunAssembly(Assembly asm, StringBuilder sb)
        {
            int ran = 0;
            foreach (var type in asm.GetTypes())
            {
                if (typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    var go = new GameObject("LiveCode_" + type.Name);
                    go.AddComponent(type);
                    sb.AppendLine($"  • attached MonoBehaviour '{type.Name}' → GameObject ในซีนสด");
                    ran++;
                }
                else
                {
                    var run = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                    if (run != null && run.GetParameters().Length == 0)
                    {
                        object result = run.Invoke(null, null);
                        sb.AppendLine($"  • {type.Name}.Run() → {result ?? "void"}");
                        ran++;
                    }
                }
            }
            return ran;
        }

        // หา csc ของ Unity — รองรับทั้ง Unity 6 (DotNetSdkRoslyn/csc.dll ผ่าน dotnet) และ 2022 (Tools/Roslyn/csc.exe)
        static bool FindCompiler(out string exe, out string preArg)
        {
            exe = null; preArg = null;
            string root = EditorApplication.applicationContentsPath;   // .../Editor/Data

            // 1) Unity 2022 style: csc.exe ตรงๆ
            string cscExe = Path.Combine(root, "Tools", "Roslyn", "csc.exe");
            if (File.Exists(cscExe)) { exe = cscExe; preArg = null; return true; }

            // 2) Unity 6 style: csc.dll รันผ่าน dotnet
            string cscDll = Path.Combine(root, "DotNetSdkRoslyn", "csc.dll");
            if (File.Exists(cscDll))
            {
                foreach (var dn in new[]
                {
                    Path.Combine(root, "NetCoreRuntime", "dotnet.exe"),
                    Path.Combine(root, "Tools", "netcorerun", "netcorerun.exe"),
                })
                {
                    if (File.Exists(dn)) { exe = dn; preArg = cscDll; return true; }
                }
                // dotnet ในเครื่อง (PATH)
                exe = "dotnet"; preArg = cscDll; return true;
            }
            return false;
        }
    }

    /// <summary>หน้าต่าง Live Code — วางโค้ด แล้ว Run ระหว่างเล่นได้เลย</summary>
    public class RuntimeCompilerWindow : EditorWindow
    {
        string _code = DEFAULT;
        string _log = "";
        Vector2 _scroll, _logScroll;

        const string DEFAULT =
@"using UnityEngine;

// ตัวอย่าง: class ใหม่ที่รันได้ตอนเล่น (MonoBehaviour จะถูก attach อัตโนมัติ)
public class LiveTest : MonoBehaviour
{
    void Start() => Debug.Log(""LiveTest start — เพิ่ม class ใหม่ตอนเล่นได้!"");
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
            Debug.Log(""space! object ในซีน: "" + FindObjectsOfType<Transform>().Length);
    }
}

// หรือ class ที่มี static Run() — จะถูกเรียกครั้งเดียวตอนกด Run
public static class Probe
{
    public static string Run() => ""scene objects = "" + Object.FindObjectsOfType<Transform>().Length;
}";

        [MenuItem("Radius/MCP/Live Code (Roslyn)")]
        public static void Open()
        {
            var w = GetWindow<RuntimeCompilerWindow>("Live Code");
            w.minSize = new Vector2(420, 360);
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "compile C# ตอนรัน แล้วโหลดเข้าเกมที่กำลังเล่น (ไม่ stop / ไม่ recompile โปรเจกต์). " +
                "MonoBehaviour จะถูก attach กับ GameObject ใหม่ในซีนสด; class ที่มี public static Run() จะถูกเรียก.",
                MessageType.Info);

            EditorGUILayout.LabelField("Code", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(160));
            _code = EditorGUILayout.TextArea(_code, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(0.5f, 0.85f, 0.55f);
            if (GUILayout.Button(Application.isPlaying ? "▶ Compile & Run (live)" : "▶ Compile & Run", GUILayout.Height(28)))
            {
                bool ok = RuntimeCompiler.CompileAndRun(_code, out _log);
                if (!ok) Debug.LogWarning("[LiveCode] " + _log);
            }
            GUI.backgroundColor = Color.white;
            if (GUILayout.Button("Clear log", GUILayout.Height(28), GUILayout.Width(90))) _log = "";
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_log))
            {
                EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(90));
                EditorGUILayout.TextArea(_log, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }
    }
}
