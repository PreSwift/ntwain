using NTwain;

namespace Sample.WPF
{
    /// <summary>
    /// 处理部分扫描仪（如华高）进纸方向与 TWAIN 方向定义相反的情况。
    /// </summary>
    static class ScannerOrientationHelper
    {
        public static bool ShouldInvertOrientation(DataSource src)
        {
            if (src == null)
                return false;

            var info = string.Format("{0} {1}", src.Manufacturer, src.Name);
            return info.IndexOf("华高", System.StringComparison.OrdinalIgnoreCase) >= 0
                || info.IndexOf("Huago", System.StringComparison.OrdinalIgnoreCase) >= 0
                || info.IndexOf("HUAGAO", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 对进纸方向相反的驱动，互换竖向/横向尺寸的配置（如 A4 ↔ A4L）。
        /// </summary>
        public static ScanPaperSize GetEffectiveSize(DataSource src, ScanPaperSize size)
        {
            if (!ShouldInvertOrientation(src))
                return size;

            switch (size)
            {
                case ScanPaperSize.A4:
                    return ScanPaperSize.A4L;
                case ScanPaperSize.A4L:
                    return ScanPaperSize.A4;
                case ScanPaperSize.K16:
                    return ScanPaperSize.K16L;
                case ScanPaperSize.K16L:
                    return ScanPaperSize.K16;
                default:
                    return size;
            }
        }
    }
}
