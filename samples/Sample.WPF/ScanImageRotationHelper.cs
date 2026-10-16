using System;
using System.Drawing;
using System.IO;
using System.Linq;
using NTwain;

namespace Sample.WPF
{
    /// <summary>
    /// 对华高等进纸方向相反的设备，在 A4/16K 扫描后将横图纠正为竖向。
    /// A 面顺时针 270°，B 面顺时针 90°。
    /// </summary>
    static class ScanImageRotationHelper
    {
        public static bool RequiresPostRotation(ScanPaperSize size)
        {
            return size == ScanPaperSize.A4 || size == ScanPaperSize.K16;
        }

        static bool NeedsFrontBackRotation(ScanPaperSize size, DataSource src)
        {
            return RequiresPostRotation(size)
                && ScannerOrientationHelper.ShouldInvertOrientation(src)
                && !ScannerOrientationHelper.IsHuagaoG300(src);
        }

        public static bool TryRotateSavedImage(string path, ScanPaperSize selectedSize, DataSource src)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            if (ScannerOrientationHelper.IsHuagaoSeries(src) && selectedSize != ScanPaperSize.Auto)
            {
                AppLog.Info("华高 {0} 不在这里旋转，A3/8K 由方向校正处理", Path.GetFileName(path));
                return false;
            }

            if ((selectedSize == ScanPaperSize.A4L || selectedSize == ScanPaperSize.K16L) && src != null
                && ((src.Name.IndexOf("WF-C21000", StringComparison.OrdinalIgnoreCase) >= 0
                    && src.Manufacturer.IndexOf("EPSON", StringComparison.OrdinalIgnoreCase) >= 0)
                    || ScannerOrientationHelper.IsKonica(src)))
                return TryCropEpsonA4LFullArea(path, selectedSize);

            RotateFlipType flip;
            string reason;
            if (selectedSize == ScanPaperSize.Auto)
            {
                string matchDetail;
                if (!NeedsAutoFrontBackRotation(path, ScannerOrientationHelper.IsHuagaoSeries(src), out matchDetail))
                {
                    AppLog.Info("自动检测未旋转 {0}：{1}", Path.GetFileName(path), matchDetail);
                    return false;
                }
                var side = GetSideFromFileName(path);
                flip = side == "B"
                    ? RotateFlipType.Rotate90FlipNone
                    : RotateFlipType.Rotate270FlipNone;
                reason = string.Format("{0}，{1}", matchDetail, side == "B" ? "B 面顺时针 90" : "A 面顺时针 270");
            }
            else if (ScannerOrientationHelper.UsesEpsonA4LandscapeAcquisition(src, selectedSize))
            {
                var side = GetSideFromFileName(path);
                flip = side == "B"
                    ? RotateFlipType.Rotate90FlipNone
                    : RotateFlipType.Rotate270FlipNone;
                reason = side == "B"
                    ? "WF-C21000 B 面顺时针 90"
                    : "WF-C21000 A 面顺时针 270";
            }
            else if (NeedsFrontBackRotation(selectedSize, src))
            {
                var side = GetSideFromFileName(path);
                flip = side == "B"
                    ? RotateFlipType.Rotate90FlipNone
                    : RotateFlipType.Rotate270FlipNone;
                reason = side == "B" ? "B 面顺时针 90" : "A 面顺时针 270";
            }
            else
            {
                return false;
            }

            try
            {
                var epsonA4 = ScannerOrientationHelper.UsesEpsonA4LandscapeAcquisition(src, selectedSize);
                string tempPath = null;
                using (var bmp = new Bitmap(path))
                {
                    bmp.RotateFlip(flip);
                    if (epsonA4)
                    {
                        // This driver transfers all printed content but omits the blank tail.
                        // Restore the paper canvas without stretching any scanned pixels.
                        var dims = selectedSize.GetDimensionsMm();
                        var longMm = Math.Max(dims.WidthMm, dims.HeightMm);
                        var shortMm = Math.Min(dims.WidthMm, dims.HeightMm);
                        var pageHeight = (int)Math.Round(bmp.Width * longMm / shortMm);
                        if (bmp.Height < pageHeight)
                        {
                            using (var page = new Bitmap(bmp.Width, pageHeight))
                            using (var graphics = Graphics.FromImage(page))
                            {
                                page.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);
                                graphics.Clear(Color.White);
                                graphics.DrawImageUnscaled(bmp, 0, 0);
                                tempPath = path + ".tmp";
                                SaveJpeg(page, tempPath);
                            }
                            AppLog.Info("WF-C21000 {0}: restored blank page tail, {1}x{2} -> {1}x{3}",
                                selectedSize.GetDisplayName(), bmp.Width, bmp.Height, pageHeight);
                        }
                    }
                    if (tempPath == null)
                    {
                        tempPath = path + ".tmp";
                        SaveJpeg(bmp, tempPath);
                    }
                }
                File.Replace(tempPath, path, null);

                AppLog.Info("已旋转扫描图 {0}：{1}", Path.GetFileName(path), reason);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Info("旋转扫描图失败: {0}, {1}", path, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 指定纸张时，横竖与纸张定义不一致就转正。正面 270°，背面 90°。
        /// </summary>
        public static bool TryAlignOrientationToPaperSize(string path, ScanPaperSize size, DataSource src)
        {
            if (size == ScanPaperSize.Auto || string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;
            if (ScannerOrientationHelper.IsHuagaoSeries(src)
                && size != ScanPaperSize.A3 && size != ScanPaperSize.K8)
                return false;

            var dims = size.GetDimensionsMm();
            var expectLandscape = dims.WidthMm > dims.HeightMm
                || size == ScanPaperSize.A3
                || size == ScanPaperSize.K8;
            try
            {
                using (var bmp = new Bitmap(path))
                {
                    var imageLandscape = bmp.Width > bmp.Height;
                    if (imageLandscape == expectLandscape)
                        return false;
                    var originalWidth = bmp.Width;
                    var originalHeight = bmp.Height;
                    var side = GetSideFromFileName(path);
                    var flip = side == "B"
                        ? RotateFlipType.Rotate90FlipNone
                        : RotateFlipType.Rotate270FlipNone;
                    var angle = side == "B" ? "B 面顺时针 90" : "A 面顺时针 270";
                    var tempPath = path + ".tmp";
                    bmp.RotateFlip(flip);
                    SaveJpeg(bmp, tempPath);
                    File.Replace(tempPath, path, null);
                    AppLog.Info("指定尺寸方向不符，已旋转 {0}：期望{1}，原图{2}x{3}，{4}",
                        Path.GetFileName(path),
                        expectLandscape ? "横向" : "纵向",
                        originalWidth,
                        originalHeight,
                        angle);
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Info("按纸张方向旋转失败: {0}, {1}", path, ex.Message);
                return false;
            }
        }

        static bool TryCropEpsonA4LFullArea(string path, ScanPaperSize size)
        {
            try
            {
                string tempPath = path + ".tmp";
                var dims = size.GetDimensionsMm();
                var longInches = Math.Max(dims.WidthMm, dims.HeightMm) / 25.4;
                var shortInches = Math.Min(dims.WidthMm, dims.HeightMm) / 25.4;
                using (var bmp = new Bitmap(path))
                {
                    // The WF-C21000 NoUI transfer starts at the opposite edge from its UI
                    // transfer. Acquiring the full A3-height area preserves the sheet.
                    // Crop the centered short x long window, then orient it like the UI.
                    int longPixels = (int)Math.Round(longInches * bmp.HorizontalResolution);
                    int shortPixels = (int)Math.Round(shortInches * bmp.VerticalResolution);
                    if (longPixels <= 0 || shortPixels <= 0
                        || bmp.Width < shortPixels || bmp.Height < longPixels)
                    {
                        AppLog.Info("WF-C21000 {0}: full-area image too small to crop: {1}x{2}, need {3}x{4}",
                            size.GetDisplayName(), bmp.Width, bmp.Height, shortPixels, longPixels);
                        return false;
                    }
                    int left = (bmp.Width - shortPixels) / 2;
                    var area = new Rectangle(left, 0, shortPixels, longPixels);
                    using (var page = bmp.Clone(area, bmp.PixelFormat))
                    {
                        var back = GetSideFromFileName(path) == "B";
                        page.RotateFlip(back
                            ? RotateFlipType.Rotate90FlipNone
                            : RotateFlipType.Rotate270FlipNone);
                        page.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);
                        SaveJpeg(page, tempPath);
                        AppLog.Info("WF-C21000 {0}: full-area {1}x{2}, crop={3}, rotate {4} to {5}x{6}",
                            size.GetDisplayName(), bmp.Width, bmp.Height, area,
                            back ? 90 : 270, longPixels, shortPixels);
                    }
                }
                File.Replace(tempPath, path, null);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Info("WF-C21000 {0} crop failed: {1}", size, ex);
                return false;
            }
        }

        public static string EnsureJpeg(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return path;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg")
                return path;

            var jpgPath = Path.ChangeExtension(path, ".jpg");
            using (var bmp = new Bitmap(path))
            {
                SaveJpeg(bmp, jpgPath);
            }
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLog.Info("删除原扫描图失败: {0}, {1}", path, ex.Message);
            }
            return jpgPath;
        }

        static void SaveJpeg(Bitmap bmp, string path)
        {
            var encoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            var quality = new System.Drawing.Imaging.EncoderParameters(1);
            quality.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality, 100L);
            bmp.Save(path, encoder, quality);
        }

        /// <summary>
        /// 所有扫描仪与爱普生相同：A4/16K 横竖都转，竖版 A3/8K 转，横版 A3/8K 不转。
        /// </summary>
        static bool NeedsAutoFrontBackRotation(string path, bool huagao, out string detail)
        {
            using (var bmp = new Bitmap(path))
            {
                string matched;
                bool rotate;
                float dpiX;
                float dpiY;
                if (!TryClassifyAutoSize(bmp.Width, bmp.Height, bmp.HorizontalResolution, bmp.VerticalResolution,
                    out matched, out rotate, out dpiX, out dpiY))
                {
                    detail = string.Format("{0}x{1}px @{2:0.#}x{3:0.#}dpi 对不上 A3/8K/A4/16K",
                        bmp.Width, bmp.Height, bmp.HorizontalResolution, bmp.VerticalResolution);
                    return false;
                }

                if (huagao)
                    rotate = matched == "A3" || matched == "8K";
                else if (matched == "A4" || matched == "A4L" || matched == "16K" || matched == "16KL")
                    rotate = true;

                var widthMm = bmp.Width / dpiX * 25.4f;
                var heightMm = bmp.Height / dpiY * 25.4f;
                detail = string.Format("判定为{0} {1:0}x{2:0}mm", matched, widthMm, heightMm);
                return rotate;
            }
        }

        static bool TryClassifyAutoSize(int widthPx, int heightPx, float reportedDpiX, float reportedDpiY,
            out string matched, out bool rotate, out float usedDpiX, out float usedDpiY)
        {
            matched = null;
            rotate = false;
            usedDpiX = reportedDpiX;
            usedDpiY = reportedDpiY;

            float[] candidateX;
            float[] candidateY;
            if (reportedDpiX > 1f && reportedDpiY > 1f && !LooksLikeDefaultScreenDpi(widthPx, heightPx, reportedDpiX, reportedDpiY))
            {
                candidateX = new[] { reportedDpiX };
                candidateY = new[] { reportedDpiY };
            }
            else
            {
                candidateX = new[] { 300f, 200f, 150f, 600f };
                candidateY = candidateX;
            }

            var bestError = float.MaxValue;
            for (var i = 0; i < candidateX.Length; i++)
            {
                var dpiX = candidateX[i];
                var dpiY = candidateY[i];
                var widthMm = widthPx / dpiX * 25.4f;
                var heightMm = heightPx / dpiY * 25.4f;
                string name;
                bool shouldRotate;
                float error;
                if (!TryMatchPaperMm(widthMm, heightMm, out name, out shouldRotate, out error))
                    continue;
                if (error >= bestError)
                    continue;
                bestError = error;
                matched = name;
                rotate = shouldRotate;
                usedDpiX = dpiX;
                usedDpiY = dpiY;
            }

            return matched != null;
        }

        static bool LooksLikeDefaultScreenDpi(int widthPx, int heightPx, float dpiX, float dpiY)
        {
            var longPx = Math.Max(widthPx, heightPx);
            return Math.Abs(dpiX - 96f) < 1f && Math.Abs(dpiY - 96f) < 1f && longPx > 1600;
        }

        static bool TryMatchPaperMm(float widthMm, float heightMm, out string name, out bool rotate, out float error)
        {
            // 柯尼卡竖放 A4/16K 传回的已是竖图，再转会把文字放横。
            // 爱普生自动检测会把同一张纸以 A4L/16KL 横向尺寸传回，那种才要转。
            var templates = new[]
            {
                new PaperTemplate("A3", 297f, 420f, true),
                new PaperTemplate("8K", 260f, 370f, true),
                new PaperTemplate("A4", 210f, 297f, false),
                new PaperTemplate("16K", 195f, 270f, false),
                new PaperTemplate("A3横", 420f, 297f, false),
                new PaperTemplate("8K横", 370f, 260f, false),
                new PaperTemplate("A4L", 297f, 210f, true),
                new PaperTemplate("16KL", 270f, 195f, true)
            };

            name = null;
            rotate = false;
            error = float.MaxValue;
            foreach (var template in templates)
            {
                var widthError = Math.Abs(widthMm - template.WidthMm) / template.WidthMm;
                var heightError = Math.Abs(heightMm - template.HeightMm) / template.HeightMm;
                if (widthError > 0.12f || heightError > 0.12f)
                    continue;
                var total = widthError + heightError;
                if (total >= error)
                    continue;
                error = total;
                name = template.Name;
                rotate = template.Rotate;
            }

            return name != null;
        }

        struct PaperTemplate
        {
            public PaperTemplate(string name, float widthMm, float heightMm, bool rotate)
            {
                Name = name;
                WidthMm = widthMm;
                HeightMm = heightMm;
                Rotate = rotate;
            }

            public string Name;
            public float WidthMm;
            public float HeightMm;
            public bool Rotate;
        }

        /// <summary>
        /// G100 扫不到 16K/8K 的标准尺寸时会按更大的幅面出图，四周是白边。裁到纸张内容。
        /// </summary>
        public static bool TryCropNearWhiteMargins(string path, ScanPaperSize size)
        {
            if (size != ScanPaperSize.A3 && size != ScanPaperSize.K8
                && size != ScanPaperSize.K16 && size != ScanPaperSize.K16L)
                return false;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            try
            {
                var tempPath = path + ".tmp";
                using (var loaded = new Bitmap(path))
                using (var source = new Bitmap(loaded.Width, loaded.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    source.SetResolution(loaded.HorizontalResolution, loaded.VerticalResolution);
                    using (var graphics = Graphics.FromImage(source))
                        graphics.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);
                    int left, top, right, bottom;
                    if (!TryFindContentBounds(source, out left, out top, out right, out bottom))
                        return false;
                    var width = right - left + 1;
                    var height = bottom - top + 1;
                    if (width >= source.Width - 4 && height >= source.Height - 4)
                        return false;
                    var area = new Rectangle(left, top, width, height);
                    using (var page = source.Clone(area, source.PixelFormat))
                    {
                        page.SetResolution(source.HorizontalResolution, source.VerticalResolution);
                        SaveJpeg(page, tempPath);
                    }
                    AppLog.Info("华高裁掉四周黑边或白边 {0}: {1}x{2} -> {3}x{4}", Path.GetFileName(path), source.Width, source.Height, width, height);
                }
                File.Replace(tempPath, path, null);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Info("华高裁边失败: {0}", ex.Message);
                return false;
            }
        }

        static bool TryFindContentBounds(Bitmap bmp, out int left, out int top, out int right, out int bottom)
        {
            left = bmp.Width;
            top = bmp.Height;
            right = 0;
            bottom = 0;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                var stride = data.Stride;
                var bytes = new byte[stride * bmp.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                for (var y = 0; y < bmp.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < bmp.Width; x++)
                    {
                        var i = row + x * 3;
                        var dark = bytes[i] < 20 && bytes[i + 1] < 20 && bytes[i + 2] < 20;
                        var light = bytes[i] > 245 && bytes[i + 1] > 245 && bytes[i + 2] > 245;
                        if (dark || light)
                            continue;
                        if (x < left) left = x;
                        if (y < top) top = y;
                        if (x > right) right = x;
                        if (y > bottom) bottom = y;
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return right > left && bottom > top;
        }

        static string GetSideFromFileName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith("-B", StringComparison.OrdinalIgnoreCase))
                return "B";
            return "A";
        }
    }
}
