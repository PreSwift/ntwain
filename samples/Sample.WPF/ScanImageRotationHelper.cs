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

            RotateFlipType flip;
            string reason;
            if (NeedsEpsonUpright(selectedSize, src))
            {
                // 驱动给出的竖图内容相对正方向顺时针偏了 90°，再转 270° 才正。宽高会互换。
                flip = RotateFlipType.Rotate270FlipNone;
                reason = "爱普生竖向顺时针 270";
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
                using (var bmp = new Bitmap(path))
                {
                    bmp.RotateFlip(flip);
                    SaveJpeg(bmp, path);
                }

                AppLog.Info("已旋转扫描图 {0}：{1}", Path.GetFileName(path), reason);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Info("旋转扫描图失败: {0}, {1}", path, ex.Message);
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

        static bool NeedsEpsonUpright(ScanPaperSize size, DataSource src)
        {
            if (src == null || size == ScanPaperSize.Auto || size.IsLandscape())
                return false;
            var info = string.Format("{0} {1}", src.Manufacturer, src.Name);
            return info.IndexOf("EPSON", StringComparison.OrdinalIgnoreCase) >= 0
                || info.IndexOf("爱普生", StringComparison.OrdinalIgnoreCase) >= 0;
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
