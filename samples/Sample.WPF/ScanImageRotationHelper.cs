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

        public static bool TryRotateSavedImage(string path, ScanPaperSize selectedSize, DataSource src)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            if (selectedSize == ScanPaperSize.A4L && src != null
                && src.Name.IndexOf("WF-C21000", StringComparison.OrdinalIgnoreCase) >= 0
                && src.Manufacturer.IndexOf("EPSON", StringComparison.OrdinalIgnoreCase) >= 0)
                return TryCropEpsonA4LFullArea(path);

            RotateFlipType flip;
            string reason;
            if (ScannerOrientationHelper.UsesEpsonA4LandscapeAcquisition(src, selectedSize))
            {
                flip = RotateFlipType.Rotate270FlipNone;
                reason = "WF-C21000: Rot0 A4L capture to portrait";
            }
            else if (RequiresPostRotation(selectedSize) && ScannerOrientationHelper.ShouldInvertOrientation(src))
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
                        // This driver transfers all printed content but omits the blank tail of A4.
                        // Restore the paper canvas without stretching any scanned pixels.
                        var a4Height = (int)Math.Round(bmp.Width * 297.0 / 210.0);
                        if (bmp.Height < a4Height)
                        {
                            using (var page = new Bitmap(bmp.Width, a4Height))
                            using (var graphics = Graphics.FromImage(page))
                            {
                                page.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);
                                graphics.Clear(Color.White);
                                graphics.DrawImageUnscaled(bmp, 0, 0);
                                tempPath = path + ".tmp";
                                SaveJpeg(page, tempPath);
                            }
                            AppLog.Info("WF-C21000 A4: restored blank page tail, {0}x{1} -> {0}x{2}",
                                bmp.Width, bmp.Height, a4Height);
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

        static bool TryCropEpsonA4LFullArea(string path)
        {
            try
            {
                string tempPath = path + ".tmp";
                using (var bmp = new Bitmap(path))
                {
                    // The WF-C21000 NoUI transfer starts at the opposite edge from its UI
                    // transfer. Acquiring the full A3-height area preserves the entire A4
                    // sheet. Crop the centered 210 x 297 mm sheet, then orient it like the UI.
                    int longPixels = (int)Math.Round(11.69 * bmp.HorizontalResolution);
                    int shortPixels = (int)Math.Round(8.27 * bmp.VerticalResolution);
                    if (longPixels <= 0 || shortPixels <= 0
                        || bmp.Width < shortPixels || bmp.Height < longPixels)
                    {
                        AppLog.Info("WF-C21000 A4L: full-area image too small to crop: {0}x{1}, need {2}x{3}",
                            bmp.Width, bmp.Height, shortPixels, longPixels);
                        return false;
                    }
                    int left = (bmp.Width - shortPixels) / 2;
                    var area = new Rectangle(left, 0, shortPixels, longPixels);
                    using (var page = bmp.Clone(area, bmp.PixelFormat))
                    {
                        page.RotateFlip(RotateFlipType.Rotate270FlipNone);
                        page.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);
                        SaveJpeg(page, tempPath);
                    }
                    AppLog.Info("WF-C21000 A4L: full-area {0}x{1}, crop={2}, rotate 270 to {3}x{4}",
                        bmp.Width, bmp.Height, area, longPixels, shortPixels);
                }
                File.Replace(tempPath, path, null);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Info("WF-C21000 A4L crop failed: {0}", ex);
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

        static string GetSideFromFileName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith("-B", StringComparison.OrdinalIgnoreCase))
                return "B";
            return "A";
        }
    }
}
