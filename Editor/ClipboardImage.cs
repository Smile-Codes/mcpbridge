using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace DeltaUnity.MCP
{
    /// <summary>
    /// ดึงรูปจาก Windows clipboard (กรณีกด Ctrl+V หรือ Print Screen แล้วยังไม่ได้เซฟไฟล์)
    /// Unity ไม่รองรับ image clipboard ในตัว เลยเรียก PowerShell ดึง System.Windows.Forms.Clipboard
    /// </summary>
    public static class ClipboardImage
    {
        /// <summary>
        /// ถ้า clipboard มีรูป → เซฟเป็น PNG ชั่วคราว คืน path
        /// ถ้าไม่มีรูป (เป็น text หรือว่าง) → คืน null
        /// </summary>
        public static string TryGetImagePath()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return null;

            string tmp = Path.Combine(Path.GetTempPath(), $"delta_paste_{Guid.NewGuid():N}.png").Replace("\\", "/");

            string script =
                "Add-Type -AssemblyName System.Windows.Forms;" +
                "Add-Type -AssemblyName System.Drawing;" +
                "$img=[System.Windows.Forms.Clipboard]::GetImage();" +
                $"if($img -ne $null){{$img.Save('{tmp}',[System.Drawing.Imaging.ImageFormat]::Png);Write-Output 'OK'}}else{{Write-Output 'NONE'}}";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -STA -Command \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var proc = Process.Start(psi);
                string outp = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(4000);

                if (outp.Contains("OK") && File.Exists(tmp))
                    return tmp;
            }
            catch { /* ignore */ }
            return null;
        }
    }
}
