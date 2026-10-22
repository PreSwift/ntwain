using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Sample.WPF
{
    /// <summary>保留机型取景几何；最终旋转角度仅由整页分类器提供。</summary>
    static class ScanImageProcessingHelper
    {
        public static void PrepareScanGeometry(string path, ScanPaperSize size, bool cropFullArea, bool cropMargins, bool cropLandscape = false, float cropWidthMm = 0, float cropHeightMm = 0)
        {
            if (cropFullArea)
            {
                try { CropFullArea(path, size, cropLandscape, cropWidthMm, cropHeightMm); }
                catch (Exception ex) { AppLog.Info("全幅裁切失败，保留完整图像继续方向分类: {0}", ex.Message); }
            }
            if (cropMargins)
                TryCropNearWhiteMargins(path, size);
        }

        static void CropFullArea(string path, ScanPaperSize size, bool landscape, float cropWidthMm, float cropHeightMm)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var image = new Bitmap(path))
                {
                    var dims = size.GetDimensionsMm();
                    if (cropWidthMm > 0 && cropHeightMm > 0)
                        dims = new ScanPaperDimensions(cropWidthMm, cropHeightMm);
                    int width = (int)Math.Round((landscape ? Math.Max(dims.WidthMm, dims.HeightMm) : Math.Min(dims.WidthMm, dims.HeightMm)) / 25.4 * image.HorizontalResolution);
                    int height = (int)Math.Round((landscape ? Math.Min(dims.WidthMm, dims.HeightMm) : Math.Max(dims.WidthMm, dims.HeightMm)) / 25.4 * image.VerticalResolution);
                    if (width <= 0 || height <= 0 || image.Width < width || image.Height < height)
                        throw new InvalidDataException("全幅图不足以裁切指定纸张。");
                    using (var page = image.Clone(new Rectangle((image.Width - width) / 2, 0, width, height), image.PixelFormat))
                        SaveJpeg(page, temp);
                    AppLog.Info("全幅裁切 {0}: {1}x{2} -> {3}x{4}，未旋转", size, image.Width, image.Height, width, height);
                }
                File.Replace(temp, path, null);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static string EnsureJpeg(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg") return path;
            string output = Path.ChangeExtension(path, ".jpg");
            using (var image = new Bitmap(path)) SaveJpeg(image, output);
            try { File.Delete(path); }
            catch (Exception ex) { AppLog.Info("删除原扫描图失败: {0}", ex.Message); }
            return output;
        }

        public static void CreateJpegCopy(string input, string output)
        {
            // Use stored pixels, strip orientation metadata, and keep external originals untouched.
            using (var source = new Bitmap(input))
            using (var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(copy))
            {
                copy.SetResolution(source.HorizontalResolution, source.VerticalResolution);
                graphics.DrawImageUnscaled(source, 0, 0);
                SaveJpeg(copy, output);
            }
        }

        /// <summary>一次落盘完成分类角度旋转与可选纸张画布补白，不根据文件名或机型猜角度。</summary>
        public static bool ApplyClassifiedOrientation(string path, int angle, ScanPaperSize? restoreCanvas = null)
        {
            if (angle != 0 && angle != 90 && angle != 180 && angle != 270)
                throw new ArgumentOutOfRangeException(nameof(angle));
            if (angle == 0 && !restoreCanvas.HasValue) return false;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var image = new Bitmap(path))
                {
                    float dpiX = image.HorizontalResolution;
                    float dpiY = image.VerticalResolution;
                    if (angle != 0)
                    {
                        image.RotateFlip(angle == 90 ? RotateFlipType.Rotate90FlipNone
                            : angle == 180 ? RotateFlipType.Rotate180FlipNone : RotateFlipType.Rotate270FlipNone);
                        if (angle == 90 || angle == 270) image.SetResolution(dpiY, dpiX);
                    }
                    int width = image.Width, height = image.Height;
                    if (restoreCanvas.HasValue)
                    {
                        var dims = restoreCanvas.Value.GetDimensionsMm();
                        double ratio = Math.Max(dims.WidthMm, dims.HeightMm) / Math.Min(dims.WidthMm, dims.HeightMm);
                        if (width >= height) width = Math.Max(width, (int)Math.Round(height * ratio));
                        else height = Math.Max(height, (int)Math.Round(width * ratio));
                    }
                    if (width != image.Width || height != image.Height)
                    {
                        using (var page = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                        using (var graphics = Graphics.FromImage(page))
                        {
                            page.SetResolution(image.HorizontalResolution, image.VerticalResolution);
                            graphics.Clear(Color.White);
                            graphics.DrawImageUnscaled(image, 0, 0);
                            SaveImage(page, temp, path);
                        }
                        AppLog.Info("恢复纸张空白画布: {0}x{1} -> {2}x{3}，不拉伸内容", image.Width, image.Height, width, height);
                    }
                    else
                    {
                        if (angle == 0) return false;
                        SaveImage(image, temp, path);
                    }
                }
                File.Replace(temp, path, null);
                return true;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        static void SaveImage(Bitmap image, string temp, string target)
        {
            string ext = Path.GetExtension(target).ToLowerInvariant();
            if (ext == ".png") image.Save(temp, ImageFormat.Png);
            else if (ext == ".bmp") image.Save(temp, ImageFormat.Bmp);
            else SaveJpeg(image, temp);
        }

        static void SaveJpeg(Bitmap image, string path)
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using (var parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 100L);
                image.Save(path, encoder, parameters);
            }
        }

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

    }
}
