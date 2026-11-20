using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace Installer
{
    /// <summary>
    /// MOD 封面图。三层取图，先本地，都没有才返回 null（界面画占位）：
    ///   1. vpk 包里自带的 addonimage.jpg/png/gif —— 游戏自己的 addons 列表显示的就是这张
    ///   2. 工坊订阅的 MOD：图和 vpk 分开存放，同目录下同名不同扩展（&lt;publishedfileid&gt;.jpg/.png/.gif）
    ///   3. 都没有 → null
    ///
    /// 解出来的图统一按 16:9 裁切缩放成 320×180 缓存到 <程序目录>\_装机记录\thumbs\，第二次就不用再解包。
    /// 缓存键带上文件大小 + 修改时间：同名包被换掉会自动失效，不会一直拿旧图。
    /// </summary>
    internal static class Cover
    {
        // 2026-11-21：320×180 在右侧详情里是放大显示的，糊；放到 640×360（16:9 不变）
        public const int ThumbW = 640;
        public const int ThumbH = 360;

        /// <summary>缓存最多留多久。封面只是加速用的，过期了重新解一次就行。</summary>
        private const int KeepDays = 30;

        private static string dir;
        // 路径 → 缩略图路径（null 表示「查过了，确实没有」）。
        // 扫库时会反复问同一批文件，没有这一层就会每次去开 vpk。
        private static readonly Dictionary<string, string> memo =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object gate = new object();

        public static string Dir
        {
            get
            {
                if (dir != null) return dir;
                string root = Core.LogDir;
                if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
                string d = Path.Combine(root, "thumbs");
                try { Directory.CreateDirectory(d); } catch { }
                dir = d;
                return dir;
            }
        }

        /// <summary>
        /// 拿这个 vpk 的缩略图文件路径；没有封面返回 null。
        /// 第一次会解包 + 写缓存，之后直接命中缓存文件，开销就是一次 File.Exists。
        /// </summary>
        public static string For(string vpkPath)
        {
            if (string.IsNullOrEmpty(vpkPath)) return null;

            string key;
            try
            {
                FileInfo fi = new FileInfo(vpkPath);
                if (!fi.Exists) return null;
                key = KeyOf(vpkPath, fi.Length, fi.LastWriteTimeUtc.Ticks);
            }
            catch { return null; }

            lock (gate)
            {
                string cached;
                if (memo.TryGetValue(key, out cached)) return cached;
            }

            string hit = Path.Combine(Dir, key + ".jpg");
            if (File.Exists(hit))
            {
                Remember(key, hit);
                return hit;
            }

            byte[] raw = LoadRaw(vpkPath);
            if (raw == null)
            {
                Remember(key, null);
                return null;
            }
            if (!WriteThumb(raw, hit))
            {
                Remember(key, null);
                return null;
            }
            Remember(key, hit);
            return hit;
        }

        /// <summary>
        /// 预览图优先用**工坊页那张**（下载时存成同名图片），包内自带的 addonimage 只当备用。
        /// 原因：作者打包进 vpk 的图常是旧图/低分辨率/没更新，工坊页那张才是最新最准的（用户 2026-11-21 明确）。
        /// </summary>
        private static byte[] LoadRaw(string vpkPath)
        {
            try
            {
                string folder = Path.GetDirectoryName(vpkPath);
                string stem = Path.GetFileNameWithoutExtension(vpkPath);
                if (folder != null && stem != null)
                {
                    // 工坊的图和 vpk 同名不同扩展，就放在同一层
                    string[] exts = new string[] { ".jpg", ".jpeg", ".png", ".gif" };
                    foreach (string ext in exts)
                    {
                        string p = Path.Combine(folder, stem + ext);
                        if (!File.Exists(p)) continue;
                        FileInfo fi = new FileInfo(p);
                        if (fi.Length <= 0 || fi.Length > 16 * 1024 * 1024) continue;
                        return File.ReadAllBytes(p);
                    }
                }
            }
            catch { }

            // 备用：vpk 里打包的 addonimage
            try
            {
                byte[] b = Vpk.ReadAddonImage(vpkPath);
                if (b != null) return b;
            }
            catch { }
            return null;
        }

        private static void Remember(string key, string value)
        {
            lock (gate) { memo[key] = value; }
        }

        private static string KeyOf(string path, long size, long ticks)
        {
            // 路径 + 大小 + 修改时间 一起进哈希：同名包被替换会换 key
            // v2：缩略图尺寸变了，键里带版本，别让旧的 320×180 缓存继续被用（会一直是糊的）
            string s = "v3|" + ThumbW + "x" + ThumbH + "|" + path.ToLowerInvariant() + "|" + size.ToString() + "|" + ticks.ToString();
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool WriteThumb(byte[] raw, string outPath)
        {
            try
            {
                // Image.FromStream 是延迟读的，流必须活到画完为止，所以 using 要把绘制包在里面
                using (MemoryStream ms = new MemoryStream(raw))
                using (Image src = Image.FromStream(ms))
                using (Bitmap bmp = Fit(src, ThumbW, ThumbH))
                {
                    string tmp = outPath + ".part";
                    // 默认 JPEG 质量约 75，放到 640 宽再用会发糊；这里明确写 88
                    System.Drawing.Imaging.ImageCodecInfo codec = null;
                    foreach (System.Drawing.Imaging.ImageCodecInfo c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                        if (c.FormatID == ImageFormat.Jpeg.Guid) { codec = c; break; }
                    if (codec != null)
                    {
                        using (System.Drawing.Imaging.EncoderParameters ep = new System.Drawing.Imaging.EncoderParameters(1))
                        {
                            ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                            bmp.Save(tmp, codec, ep);
                        }
                    }
                    else bmp.Save(tmp, ImageFormat.Jpeg);
                    if (File.Exists(outPath)) File.Delete(outPath);
                    File.Move(tmp, outPath);
                    return true;
                }
            }
            catch
            {
                try { if (File.Exists(outPath + ".part")) File.Delete(outPath + ".part"); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 按 cover 的方式塞进 16:9：等比放大到铺满，多出来的两边裁掉。
        /// 作者上传的封面从 1:1 到 2:1 都有，还有 7680×4320 的巨图，统一处理才不会有的变形有的留白。
        /// </summary>
        private static Bitmap Fit(Image src, int w, int h)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.Clear(Color.FromArgb(232, 236, 242));

                float sw = (float)w / src.Width;
                float sh = (float)h / src.Height;
                float s = Math.Max(sw, sh);
                int dw = (int)Math.Ceiling(src.Width * s);
                int dh = (int)Math.Ceiling(src.Height * s);
                g.DrawImage(src, new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh));
            }
            return bmp;
        }
    }
}
