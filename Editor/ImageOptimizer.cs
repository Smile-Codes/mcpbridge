using System.IO;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// ย่อรูปก่อนส่งเข้า Claude API เพื่อประหยัด token
    /// โดยรักษาความคมชัดให้ Profiler / UI screenshot ยังอ่านออกได้
    /// </summary>
    public static class ImageOptimizer
    {
        /// <summary>
        /// ย่อรูปให้ด้านที่ยาวสุดไม่เกิน maxEdge px แล้วคืนค่าเป็น byte[] พร้อม mime type
        /// ถ้ารูปเล็กกว่า maxEdge อยู่แล้ว จะส่งไฟล์เดิมไม่แตะต้อง
        /// </summary>
        public static byte[] ResizeForApi(string path, int maxEdge, out string mimeType)
        {
            byte[] original = File.ReadAllBytes(path);

            var src = new Texture2D(2, 2);
            if (!src.LoadImage(original))
            {
                mimeType = "image/png";
                return original;
            }

            int w = src.width;
            int h = src.height;
            int longEdge = Mathf.Max(w, h);

            // รูปเล็กพออยู่แล้ว → ไม่ต้องย่อ ส่งไฟล์เดิม (คุณภาพเต็ม)
            if (longEdge <= maxEdge)
            {
                Object.DestroyImmediate(src);
                mimeType = GetMime(path);
                return original;
            }

            // คำนวณขนาดใหม่ คงสัดส่วนเดิม
            float scale = (float)maxEdge / longEdge;
            int newW = Mathf.RoundToInt(w * scale);
            int newH = Mathf.RoundToInt(h * scale);

            // ย่อด้วย bilinear ผ่าน RenderTexture (คมกว่า GetPixels ธรรมดา)
            var rt = RenderTexture.GetTemporary(newW, newH, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            var prev = RenderTexture.active;

            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            var dst = new Texture2D(newW, newH, TextureFormat.RGB24, false);
            dst.ReadPixels(new Rect(0, 0, newW, newH), 0, 0);
            dst.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            // encode เป็น PNG (lossless — ตัวเลข Profiler ไม่เบลอ)
            byte[] result = dst.EncodeToPNG();
            mimeType = "image/png";

            Object.DestroyImmediate(src);
            Object.DestroyImmediate(dst);

            return result;
        }

        static string GetMime(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            return ext == ".jpg" || ext == ".jpeg" ? "image/jpeg"
                 : ext == ".webp" ? "image/webp"
                 : "image/png";
        }
    }
}
