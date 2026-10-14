using System;
using System.IO;

namespace Sample.WPF
{
    /// <summary>
    /// 扫描前可选的纸张尺寸。
    /// </summary>
    public enum ScanPaperSize
    {
        A3,
        A4,
        A4L,
        K8,
        K16,
        K16L,
        Auto
    }

    static class ScanSizePreference
    {
        const string FileName = "last-scan-size.txt";

        public static ScanPaperSize Load()
        {
            try
            {
                var path = GetPath();
                if (!File.Exists(path))
                    return ScanPaperSize.Auto;

                ScanPaperSize size;
                if (Enum.TryParse(File.ReadAllText(path).Trim(), out size) && Enum.IsDefined(typeof(ScanPaperSize), size))
                    return size;
            }
            catch (Exception)
            {
            }

            return ScanPaperSize.Auto;
        }

        public static void Save(ScanPaperSize size)
        {
            try
            {
                var path = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, size.ToString());
            }
            catch (Exception)
            {
            }
        }

        static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sample.WPF",
                FileName);
        }
    }

    static class ScanPaperSizeExtensions
    {
        public static string GetDisplayName(this ScanPaperSize size)
        {
            switch (size)
            {
                case ScanPaperSize.Auto: return "自动检测";
                case ScanPaperSize.A3: return "A3";
                case ScanPaperSize.A4: return "A4";
                case ScanPaperSize.A4L: return "A4L";
                case ScanPaperSize.K8: return "8K";
                case ScanPaperSize.K16: return "16K";
                case ScanPaperSize.K16L: return "16KL";
                default: return size.ToString();
            }
        }

        public static string GetDescription(this ScanPaperSize size)
        {
            if (size == ScanPaperSize.Auto)
                return "由扫描仪自动检测纸张大小";

            var dims = size.GetDimensionsMm();
            return string.Format("{0} × {1} mm", dims.WidthMm, dims.HeightMm);
        }

        public static ScanPaperDimensions GetDimensionsMm(this ScanPaperSize size)
        {
            switch (size)
            {
                case ScanPaperSize.A3:
                    return new ScanPaperDimensions(297f, 420f);
                case ScanPaperSize.A4:
                    return new ScanPaperDimensions(210f, 297f);
                case ScanPaperSize.A4L:
                    return new ScanPaperDimensions(297f, 210f);
                case ScanPaperSize.K8:
                    return new ScanPaperDimensions(260f, 370f);
                case ScanPaperSize.K16:
                    return new ScanPaperDimensions(195f, 270f);
                case ScanPaperSize.K16L:
                    return new ScanPaperDimensions(270f, 195f);
                default:
                    return new ScanPaperDimensions(210f, 297f);
            }
        }

        public static bool IsLandscape(this ScanPaperSize size)
        {
            switch (size)
            {
                case ScanPaperSize.A4L:
                case ScanPaperSize.K16L:
                    return true;
                default:
                    return false;
            }
        }
    }

    struct ScanPaperDimensions
    {
        public ScanPaperDimensions(float widthMm, float heightMm)
        {
            WidthMm = widthMm;
            HeightMm = heightMm;
        }

        public float WidthMm { get; private set; }
        public float HeightMm { get; private set; }
    }
}
