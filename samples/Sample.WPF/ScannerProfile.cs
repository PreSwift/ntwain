using NTwain;
using NTwain.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Sample.WPF
{
    sealed class ScanApplyResult
    {
        public bool UsedDimensionFallback;
        public bool CropAfterScan;
        public bool CropMargins;
        public bool RestoreCanvas;
        public bool RestoredDriverProfile;
    }

    /// <summary>
    /// 按机型 JSON 设置扫描。数值在配置里，写入方式只有少数几种策略。
    /// </summary>
    sealed class ScannerProfile
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public int Priority { get; private set; }
        public bool MatchAny { get; private set; }
        public bool FallbackSwapDimensions { get; private set; }
        public bool SwapPortraitLandscape { get; private set; }
        public bool CropMargins { get; private set; }
        public string[] ManufacturerContains { get; private set; }
        public string[] NameContains { get; private set; }
        public List<CapSetting> CommonBefore { get; private set; }
        public List<CapSetting> CommonAfter { get; private set; }
        public Dictionary<string, PaperConfig> Papers { get; private set; }
        public DriverProfileConfig DriverProfile { get; private set; }
        public bool IsDefault { get; private set; }

        public static ScannerProfile Match(DataSource src)
        {
            var profiles = ScannerProfileCatalog.Load();
            var manufacturer = src == null ? "" : src.Manufacturer ?? "";
            var name = src == null ? "" : src.Name ?? "";
            ScannerProfile best = null;
            var bestScore = int.MinValue;
            foreach (var profile in profiles)
            {
                if (profile.IsDefault)
                    continue;
                var score = profile.MatchScore(manufacturer, name);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = profile;
                }
            }
            if (best == null)
                best = profiles.First(item => item.IsDefault);
            AppLog.Info("扫描配置 {0} ({1})", best.Name, best.Id);
            return best;
        }

        public bool TryRestoreDriverProfile(DataSource src, ScanPaperSize size)
        {
            PaperConfig paper;
            if (!TryGetPaper(size, out paper) || !paper.restoreDriverProfile || DriverProfile == null)
                return false;
            var path = DriverProfilePath;
            if (!File.Exists(path))
            {
                AppLog.Info("驱动配置不存在，跳过恢复: {0}", path);
                return false;
            }
            try
            {
                var custom = src.Capabilities.CapCustomDSData;
                if (!custom.IsSupported || !custom.CanGetCurrent || custom.GetCurrent() != BoolType.True)
                {
                    AppLog.Info("驱动不支持 CustomDSData，无法恢复配置");
                    return false;
                }
                var settings = File.ReadAllBytes(path);
                if (settings.Length == 0 || settings.Length > 4 * 1024 * 1024)
                    return false;
                src.Settings = settings;
                AppLog.Info("已恢复驱动配置: {0}, bytes={1}", path, settings.Length);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Info("恢复驱动配置失败: {0}", ex.Message);
                return false;
            }
        }

        public void TrySaveDriverProfile(DataSource src, int imageWidth, int imageHeight)
        {
            var profile = DriverProfile;
            if (src == null || profile == null || string.IsNullOrEmpty(profile.file))
                return;
            if (profile.requireWidthGreaterThanHeight && (imageWidth <= imageHeight || imageWidth == 0))
                return;
            try
            {
                var frames = src.Capabilities.ICapFrames;
                var sizes = src.Capabilities.ICapSupportedSizes;
                if (!frames.IsSupported || !frames.CanGetCurrent || !sizes.IsSupported || !sizes.CanGetCurrent)
                    return;
                SupportedSize expected;
                if (!Enum.TryParse(profile.supportedSize, out expected) || sizes.GetCurrent() != expected)
                    return;
                var frame = frames.GetCurrent();
                if (Math.Abs((float)frame.Left) > 0.01f || Math.Abs((float)frame.Top) > 0.01f
                    || Math.Abs((float)frame.Right - profile.frameRight) > 0.01f
                    || Math.Abs((float)frame.Bottom - profile.frameBottom) > 0.01f)
                    return;
                var custom = src.Capabilities.CapCustomDSData;
                if (!custom.IsSupported || !custom.CanGetCurrent || custom.GetCurrent() != BoolType.True)
                    return;
                var settings = src.Settings;
                if (settings == null || settings.Length == 0 || settings.Length > 4 * 1024 * 1024)
                    return;
                var path = DriverProfilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, settings);
                AppLog.Info("已保存驱动配置: {0}, bytes={1}", path, settings.Length);
            }
            catch (Exception ex)
            {
                AppLog.Info("保存驱动配置失败: {0}", ex.Message);
            }
        }

        public ScanApplyResult Apply(DataSource src, ScanPaperSize size)
        {
            var attempted = SwapPortraitLandscape ? SwapPaper(size) : size;
            if (attempted != size)
                AppLog.Info("{0} 按 {1} 的纸张配置设置", size.GetDisplayName(), attempted.GetDisplayName());
            try
            {
                var result = ApplyOnce(src, attempted);
                if (result != null)
                    return result;
                if (!FallbackSwapDimensions || size == ScanPaperSize.Auto)
                    throw new InvalidOperationException(size.GetDisplayName() + " 配置与请求不符，已中止扫描。");
                AppLog.Info("{0} 原方向配置与请求不符，交换宽高与方向重试", size.GetDisplayName());
                ApplySwappedFallback(src, attempted);
                AppLog.Info("{0} 另一方向扫描区域回退成功", size.GetDisplayName());
                return new ScanApplyResult { UsedDimensionFallback = true };
            }
            catch (AccessViolationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!FallbackSwapDimensions || size == ScanPaperSize.Auto)
                    throw;
                AppLog.Info("{0} 原方向配置失败: {1}，交换宽高与方向重试", size.GetDisplayName(), ex.Message);
                try
                {
                    ApplySwappedFallback(src, attempted);
                    AppLog.Info("{0} 另一方向扫描区域回退成功", size.GetDisplayName());
                    return new ScanApplyResult { UsedDimensionFallback = true };
                }
                catch (AccessViolationException)
                {
                    throw;
                }
                catch (Exception inner)
                {
                    throw new InvalidOperationException(size.GetDisplayName() + " 两个方向均无法配置，已中止扫描。", inner);
                }
            }
        }

        ScanApplyResult ApplyOnce(DataSource src, ScanPaperSize size)
        {
            ApplySettings(src, CommonBefore, false);
            var paper = paperOf(size);
            if (paper != null)
            {
                if (!ApplyPaper(src, size, paper))
                    return null;
            }
            else
                ApplyGenericSwap(src, size);
            ApplySettings(src, CommonAfter, paper != null && paper.skipIndicators);
            return new ScanApplyResult
            {
                CropAfterScan = paper != null && paper.cropAfterScan,
                CropMargins = paper != null && paper.cropMargins.HasValue ? paper.cropMargins.Value : CropMargins,
                RestoreCanvas = paper != null && paper.restoreCanvas
            };
        }

        PaperConfig paperOf(ScanPaperSize size)
        {
            PaperConfig paper;
            return TryGetPaper(size, out paper) ? paper : null;
        }

        static IList<CapSetting> CustomStringSettings(IList<CapSetting> settings)
        {
            if (settings == null)
                return null;
            return settings.Where(setting =>
            {
                CapabilityId id;
                return setting != null && setting.value is string
                    && TryParseCap(setting.cap, out id) && IsCustom(id);
            }).ToList();
        }

        void ApplySwappedFallback(DataSource src, ScanPaperSize attempted)
        {
            var other = SwapPaper(attempted);
            PaperConfig otherPaper;
            if (other != attempted && TryGetPaper(other, out otherPaper)
                && string.Equals(otherPaper.area, "settingsOnly", StringComparison.OrdinalIgnoreCase))
            {
                ApplySettings(src, CommonBefore, false);
                if (!ApplyPaper(src, other, otherPaper))
                    throw new InvalidOperationException(other.GetDisplayName() + " 读回与请求不符");
                ApplySettings(src, CommonAfter, otherPaper.skipIndicators);
                return;
            }
            ApplySettings(src, CommonBefore, false);
            ApplyGenericSwap(src, attempted);
            ApplySettings(src, CommonAfter, false);
        }

        bool ApplyPaper(DataSource src, ScanPaperSize size, PaperConfig paper)
        {
            var dimensionSize = size;
            if (!string.IsNullOrEmpty(paper.usePaper))
                dimensionSize = ParsePaper(paper.usePaper);
            var area = string.IsNullOrEmpty(paper.area) ? "supportedSizeAndFrame" : paper.area;
            switch (area)
            {
                case "settingsOnly":
                    ApplySettings(src, paper.settings, false);
                    ApplySettings(src, CustomStringSettings(paper.settings), false);
                    if (paper.verify && !VerifySettings(src, paper.settings))
                        return false;
                    break;
                case "supportedSizeAndFrame":
                    ApplySupportedSizeAndFrame(src, dimensionSize, paper);
                    break;
                case "frameIgnorePhysicalLimit":
                    ApplyFrameIgnorePhysicalLimit(src, dimensionSize, paper);
                    break;
                case "fullAreaThenCrop":
                    ApplyFullArea(src, paper);
                    break;
                default:
                    throw new InvalidOperationException("未知扫描区域策略: " + area);
            }
            return true;
        }

        void ApplySupportedSizeAndFrame(DataSource src, ScanPaperSize size, PaperConfig paper)
        {
            var caps = src.Capabilities;
            TrySetCap(caps.ICapUndefinedImageSize, BoolType.False);
            TrySetCap(caps.ICapAutomaticBorderDetection, BoolType.False);
            TrySetCap(caps.ICapAutoSize, AutoSize.None);
            if (!string.IsNullOrEmpty(paper.forceOrientation))
                SetOrientation(src, paper.forceOrientation);
            else
                ApplyOrientation(src, size, false);
            SetSupportedSizeIfListed(src, size);
            ApplySettings(src, paper.settings, false);
            var ok = string.Equals(paper.frameMode, "currentUnitFrames", StringComparison.OrdinalIgnoreCase)
                ? TrySetFrame(src, size, false)
                : TrySetImageLayout(src, size, false);
            if (!ok)
                throw new InvalidOperationException(size.GetDisplayName() + " 扫描区域设置失败。");
        }

        void ApplyFrameIgnorePhysicalLimit(DataSource src, ScanPaperSize size, PaperConfig paper)
        {
            EnsureInches(src);
            var caps = src.Capabilities;
            TrySetCap(caps.ICapUndefinedImageSize, BoolType.False);
            TrySetCap(caps.ICapAutomaticBorderDetection, BoolType.False);
            TrySetCap(caps.ICapAutoSize, AutoSize.None);
            ApplyOrientation(src, size, false);
            var sizes = caps.ICapSupportedSizes;
            if (sizes.IsSupported && sizes.CanSet)
            {
                var twainSize = TwainSize(size);
                var value = twainSize.HasValue ? twainSize.Value : SupportedSize.None;
                AppLog.Info("{0} SupportedSizes={1}: {2}", size.GetDisplayName(), value, sizes.SetValue(value));
            }
            EnsureInches(src);
            var dims = size.GetDimensionsMm();
            var frame = new TWFrame
            {
                Left = 0,
                Top = 0,
                Right = dims.WidthMm / 25.4f,
                Bottom = dims.HeightMm / 25.4f
            };
            TWImageLayout layout;
            src.DGImage.ImageLayout.Get(out layout);
            layout.Frame = frame;
            var rc = src.DGImage.ImageLayout.Set(layout);
            AppLog.Info("{0} ImageLayout Inches: {1}, Frame={2}", size.GetDisplayName(), rc, frame);
            var frames = caps.ICapFrames;
            if (frames.IsSupported && frames.CanSet)
            {
                rc = frames.SetValue(frame);
                AppLog.Info("{0} ICapFrames: {1}", size.GetDisplayName(), rc);
            }
            if (rc != ReturnCode.Success)
                throw new InvalidOperationException(size.GetDisplayName() + " 扫描区域设置失败。");
            ApplySettings(src, paper.settings, false);
        }

        void ApplyFullArea(DataSource src, PaperConfig paper)
        {
            EnsureInches(src);
            ApplySettings(src, paper.settings, false);
            if (paper.frame == null)
                throw new InvalidOperationException("整幅扫描缺少 frame。");
            SetConfiguredFrame(src, paper.frame);
        }

        void ApplyGenericSwap(DataSource src, ScanPaperSize size)
        {
            var caps = src.Capabilities;
            TrySetCap(caps.ICapUndefinedImageSize, BoolType.False);
            TrySetCap(caps.ICapAutomaticBorderDetection, BoolType.False);
            TrySetCap(caps.ICapAutoSize, AutoSize.None);
            TrySetCap(caps.ICapSupportedSizes, SupportedSize.None);
            ApplyOrientation(src, size, true);
            if (!TrySetImageLayout(src, size, true))
                throw new InvalidOperationException("交换区域失败");
        }

        void ApplySettings(DataSource src, IList<CapSetting> settings, bool skipIndicators)
        {
            if (settings == null)
                return;
            foreach (var setting in settings)
            {
                if (setting == null || string.IsNullOrEmpty(setting.cap))
                    continue;
                if (skipIndicators && setting.cap == "CapIndicators")
                    continue;
                try
                {
                    ApplySetting(src, setting);
                }
                catch (AccessViolationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Info("设置 {0} 失败: {1}", setting.cap, ex.Message);
                    if (setting.required)
                        throw;
                }
            }
        }

        void ApplySetting(DataSource src, CapSetting setting)
        {
            CapabilityId id;
            if (!TryParseCap(setting.cap, out id))
                throw new InvalidOperationException("未知能力 " + setting.cap);
            if (id == CapabilityId.CapDuplexEnabled && AsBool(setting.value))
            {
                var duplex = src.Capabilities.CapDuplex;
                if (duplex.IsSupported && duplex.CanGetCurrent && duplex.GetCurrent() == Duplex.None)
                {
                    AppLog.Info("当前扫描仪不支持双面扫描");
                    return;
                }
            }
            if ((id == CapabilityId.ICapXResolution || id == CapabilityId.ICapYResolution)
                && !ResolutionUnitsAreInches(src))
                return;
            if (IsCustom(id))
            {
                SetCustom(src, id, setting);
                return;
            }
            SetStandard(src, id, setting);
        }

        static bool ResolutionUnitsAreInches(DataSource src)
        {
            var units = src.Capabilities.ICapUnits;
            if (units.IsSupported && units.CanGetCurrent && units.GetCurrent() == Unit.Inches)
                return true;
            AppLog.Info("驱动未使用英寸，跳过 DPI 设置");
            return false;
        }

        void SetStandard(DataSource src, CapabilityId id, CapSetting setting)
        {
            var caps = src.Capabilities;
            switch (id)
            {
                case CapabilityId.CapFeederEnabled:
                    SetBoolCap(caps.CapFeederEnabled, setting, "CapFeederEnabled");
                    break;
                case CapabilityId.CapDuplexEnabled:
                    SetBoolCap(caps.CapDuplexEnabled, setting, "CapDuplexEnabled");
                    break;
                case CapabilityId.CapIndicators:
                    SetBoolCap(caps.CapIndicators, setting, "CapIndicators");
                    break;
                case CapabilityId.CapXferCount:
                    SetValueCap(caps.CapXferCount, (int)AsNumber(setting.value), setting, "CapXferCount");
                    break;
                case CapabilityId.ICapUnits:
                    SetValueCap(caps.ICapUnits, ParseEnum<Unit>(setting.value), setting, "ICapUnits");
                    break;
                case CapabilityId.ICapXResolution:
                    SetValueCap(caps.ICapXResolution, (TWFix32)(float)AsNumber(setting.value), setting, "ICapXResolution");
                    break;
                case CapabilityId.ICapYResolution:
                    SetValueCap(caps.ICapYResolution, (TWFix32)(float)AsNumber(setting.value), setting, "ICapYResolution");
                    break;
                case CapabilityId.ICapOrientation:
                    SetValueCap(caps.ICapOrientation, ParseEnum<OrientationType>(setting.value), setting, "ICapOrientation");
                    break;
                case CapabilityId.ICapRotation:
                    SetValueCap(caps.ICapRotation, (TWFix32)(float)AsNumber(setting.value), setting, "ICapRotation");
                    break;
                case CapabilityId.ICapSupportedSizes:
                    SetValueCap(caps.ICapSupportedSizes, ParseEnum<SupportedSize>(setting.value), setting, "ICapSupportedSizes");
                    break;
                case CapabilityId.ICapUndefinedImageSize:
                    SetBoolCap(caps.ICapUndefinedImageSize, setting, "ICapUndefinedImageSize");
                    break;
                case CapabilityId.ICapAutomaticBorderDetection:
                    SetBoolCap(caps.ICapAutomaticBorderDetection, setting, "ICapAutomaticBorderDetection");
                    break;
                case CapabilityId.ICapAutomaticRotate:
                    SetBoolCap(caps.ICapAutomaticRotate, setting, "ICapAutomaticRotate");
                    break;
                case CapabilityId.ICapAutoSize:
                    SetValueCap(caps.ICapAutoSize, ParseEnum<AutoSize>(setting.value), setting, "ICapAutoSize");
                    break;
                default:
                    throw new InvalidOperationException("没有标准设置方法: " + setting.cap);
            }
        }

        static void SetBoolCap(ICapWrapper<BoolType> cap, CapSetting setting, string name)
        {
            if (!cap.IsSupported)
            {
                AppLog.Info("{0} 不支持，跳过", name);
                return;
            }
            if (cap.CanSet)
            {
                var value = AsBool(setting.value) ? BoolType.True : BoolType.False;
                var rc = cap.SetValue(value);
                AppLog.Info("{0}={1}: {2}", name, value, rc);
                if (rc != ReturnCode.Success && setting.required)
                    throw new InvalidOperationException(name + " 设置失败");
            }
            else if (setting.required && !cap.CanGetCurrent)
            {
                throw new InvalidOperationException(name + " 不能设置");
            }
            if (setting.required && cap.CanGetCurrent)
            {
                var current = cap.GetCurrent();
                var expected = AsBool(setting.value) ? BoolType.True : BoolType.False;
                AppLog.Info("{0} 读回={1}", name, current);
                if (current != expected)
                    throw new InvalidOperationException(name + " 读回不符");
            }
        }

        static void SetValueCap<T>(ICapWrapper<T> cap, T value, CapSetting setting, string name)
        {
            if (!cap.IsSupported)
            {
                AppLog.Info("{0} 不支持，跳过", name);
                return;
            }
            if (cap.CanSet)
            {
                var rc = cap.SetValue(value);
                AppLog.Info("{0}={1}: {2}", name, value, rc);
                if (rc != ReturnCode.Success && setting.required)
                    throw new InvalidOperationException(name + " 设置失败");
            }
            else if (setting.required)
            {
                throw new InvalidOperationException(name + " 不能设置");
            }
            if (setting.required && cap.CanGetCurrent && !ValuesMatch(setting, cap.GetCurrent()))
            {
                AppLog.Info("{0} 读回={1}", name, cap.GetCurrent());
                throw new InvalidOperationException(name + " 读回不符");
            }
        }

        void SetCustom(DataSource src, CapabilityId id, CapSetting setting)
        {
            if (setting.value is string)
            {
                SetCustomString(src, id, (string)setting.value);
                return;
            }
            if (string.Equals(setting.unit, "mm", StringComparison.OrdinalIgnoreCase))
            {
                SetCustomFix32(src, id, (float)AsNumber(setting.value));
                return;
            }
            var raw = src.Capabilities.GetValuesRaw(id);
            ReturnCode rc;
            using (var cap = new TWCapability(id, new TWOneValue
            {
                ItemType = raw.ItemType,
                Item = (uint)AsNumber(setting.value)
            }))
                rc = src.DGControl.Capability.Set(cap);
            AppLog.Info("{0}={1}: {2}, 当前={3}", setting.cap, setting.value, rc, src.Capabilities.GetCurrent(id));
            if (rc != ReturnCode.Success && setting.required)
                throw new InvalidOperationException(setting.cap + " 设置失败");
        }

        static void SetCustomString(DataSource src, CapabilityId id, string value)
        {
            var raw = src.Capabilities.GetValuesRaw(id);
            var values = raw.CollectionValues == null
                ? new List<object>()
                : raw.CollectionValues.Cast<object>().ToList();
            var index = values.FindIndex(item => NameEquals(item, value));
            ReturnCode rc = ReturnCode.Failure;
            if (index >= 0)
            {
                using (var cap = new TWCapability(id))
                {
                    if (src.DGControl.Capability.Get(cap) == ReturnCode.Success && cap.TrySelectEnumIndex(index))
                        rc = src.DGControl.Capability.Set(cap);
                }
            }
            if (!NameEquals(src.Capabilities.GetCurrent(id), value))
            {
                var type = IsStringItem(raw.ItemType) ? raw.ItemType : ItemType.String255;
                using (var cap = new TWCapability(id, value, type))
                    rc = src.DGControl.Capability.Set(cap);
            }
            if (index >= 0 && !NameEquals(src.Capabilities.GetCurrent(id), value))
            {
                using (var cap = new TWCapability(id, new TWOneValue
                {
                    ItemType = ItemType.UInt16,
                    Item = (uint)index
                }))
                    rc = src.DGControl.Capability.Set(cap);
            }
            if (index < 0)
                AppLog.Info("0x{0:X4} 可选值中没有 {1}: {2}", (int)id, value,
                    string.Join(" | ", values.Select(item => Convert.ToString(item))));
            AppLog.Info("0x{0:X4}={1}: {2}, 当前={3}", (int)id, value, rc,
                src.Capabilities.GetCurrent(id));
        }

        static bool TryIntegral(object value, out int number)
        {
            number = 0;
            if (value is Enum || value is string || value is bool || value is TWFix32)
                return false;
            try
            {
                number = Convert.ToInt32(value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool NameEquals(object current, string value)
        {
            var text = Convert.ToString(current);
            if (text == null)
                return false;
            return string.Equals(text.Trim().TrimEnd('\0'), value.Trim(), StringComparison.Ordinal);
        }

        static void SetCustomFix32(DataSource src, CapabilityId id, float value)
        {
            var one = ((TWFix32)value).ToOneValue();
            ReturnCode rc;
            using (var cap = new TWCapability(id, one))
                rc = src.DGControl.Capability.Set(cap);
            AppLog.Info("0x{0:X4}={1}: {2}, 当前={3}", (int)id, value, rc, src.Capabilities.GetCurrent(id));
        }

        bool VerifySettings(DataSource src, IList<CapSetting> settings)
        {
            if (settings == null)
                return true;
            foreach (var setting in settings)
            {
                CapabilityId id;
                if (setting == null || !TryParseCap(setting.cap, out id))
                    continue;
                var current = src.Capabilities.GetCurrent(id);
                if (!ValuesMatch(setting, current))
                {
                    AppLog.Info("{0} 读回与请求不符。当前={1}", setting.cap, current);
                    return false;
                }
            }
            return true;
        }

        static bool ValuesMatch(CapSetting setting, object current)
        {
            if (current == null)
                return false;
            if (setting.value is bool)
            {
                if (current is BoolType)
                    return ((BoolType)current == BoolType.True) == (bool)setting.value;
                return Convert.ToBoolean(current) == (bool)setting.value;
            }
            if (setting.value is string)
            {
                int number;
                OrientationType orientation;
                if (TryIntegral(current, out number)
                    && Enum.TryParse((string)setting.value, true, out orientation)
                    && number == (int)orientation)
                    return true;
                if (current is Enum)
                {
                    try
                    {
                        var parsed = Enum.Parse(current.GetType(), (string)setting.value);
                        return current.Equals(parsed);
                    }
                    catch (ArgumentException)
                    {
                        return string.Equals(current.ToString(), (string)setting.value, StringComparison.Ordinal);
                    }
                }
                return string.Equals(Convert.ToString(current).Trim().TrimEnd('\0'), ((string)setting.value).Trim(), StringComparison.Ordinal);
            }
            if (IsNumber(setting.value))
            {
                double actual;
                if (current is TWFix32)
                    actual = (float)(TWFix32)current;
                else if (!TryDouble(current, out actual))
                    return false;
                return Math.Abs(actual - AsNumber(setting.value)) <= 0.1;
            }
            return string.Equals(Convert.ToString(current), Convert.ToString(setting.value), StringComparison.Ordinal);
        }

        void SetConfiguredFrame(DataSource src, FrameConfig frame)
        {
            var twainFrame = new TWFrame
            {
                Left = (float)frame.left,
                Top = (float)frame.top,
                Right = (float)frame.right,
                Bottom = (float)frame.bottom
            };
            if (string.Equals(frame.target, "ICapFrames", StringComparison.OrdinalIgnoreCase))
            {
                var frames = src.Capabilities.ICapFrames;
                if (!frames.IsSupported || !frames.CanSet)
                    throw new InvalidOperationException("无法设置扫描框");
                var rc = frames.SetValue(twainFrame);
                AppLog.Info("整幅 ICapFrames={0}: {1}", twainFrame, rc);
                if (rc != ReturnCode.Success)
                    throw new InvalidOperationException("整幅扫描框设置失败");
                return;
            }
            TWImageLayout layout;
            src.DGImage.ImageLayout.Get(out layout);
            layout.Frame = twainFrame;
            var layoutRc = src.DGImage.ImageLayout.Set(layout);
            AppLog.Info("整幅 ImageLayout={0}: {1}", twainFrame, layoutRc);
            var framesCap = src.Capabilities.ICapFrames;
            if (framesCap.IsSupported && framesCap.CanSet)
            {
                layoutRc = framesCap.SetValue(twainFrame);
                AppLog.Info("整幅 ICapFrames={0}: {1}", twainFrame, layoutRc);
            }
            if (layoutRc != ReturnCode.Success)
                throw new InvalidOperationException("整幅扫描区域设置失败");
        }

        static void SetSupportedSizeIfListed(DataSource src, ScanPaperSize size)
        {
            var twainSize = TwainSize(size);
            if (!twainSize.HasValue)
                return;
            var sizes = src.Capabilities.ICapSupportedSizes;
            if (!sizes.IsSupported || !sizes.CanSet)
                return;
            var available = SafeGet(sizes);
            if (available.Count > 0 && !available.Contains(twainSize.Value))
            {
                AppLog.Info("当前驱动未列出 {0}", twainSize.Value);
                return;
            }
            var rc = sizes.SetValue(twainSize.Value);
            AppLog.Info("强制扫描尺寸 {0} (ICapSupportedSizes): {1}", size.GetDisplayName(), rc);
        }

        static void ApplyOrientation(DataSource src, ScanPaperSize size, bool swapDimensions)
        {
            var orientation = src.Capabilities.ICapOrientation;
            if (!orientation.IsSupported || !orientation.CanSet)
                return;
            var landscape = size.IsLandscape() != swapDimensions;
            var available = SafeGet(orientation);
            OrientationType? target = null;
            if (landscape)
            {
                if (available.Count == 0 || available.Contains(OrientationType.Landscape))
                    target = OrientationType.Landscape;
                else if (available.Contains(OrientationType.Rot270))
                    target = OrientationType.Rot270;
                else if (available.Contains(OrientationType.Rot90))
                    target = OrientationType.Rot90;
            }
            else if (available.Count == 0 || available.Contains(OrientationType.Portrait))
                target = OrientationType.Portrait;
            else if (available.Contains(OrientationType.Rot0))
                target = OrientationType.Rot0;
            if (!target.HasValue)
            {
                AppLog.Info("当前驱动未支持 {0} 方向", landscape ? "横向" : "纵向");
                return;
            }
            AppLog.Info("扫描方向 {0}: {1}", target.Value, orientation.SetValue(target.Value));
        }

        static void SetOrientation(DataSource src, string name)
        {
            var orientation = src.Capabilities.ICapOrientation;
            if (!orientation.IsSupported || !orientation.CanSet)
                return;
            var value = ParseEnum<OrientationType>(name);
            AppLog.Info("扫描方向 {0}: {1}", value, orientation.SetValue(value));
        }

        static bool TrySetFrame(DataSource src, ScanPaperSize size, bool swapDimensions)
        {
            var dims = size.GetDimensionsMm();
            if (swapDimensions)
                dims = new ScanPaperDimensions(dims.HeightMm, dims.WidthMm);
            var unit = CurrentUnit(src.Capabilities.ICapUnits);
            float width;
            float height;
            switch (unit)
            {
                case Unit.Millimeters:
                    width = dims.WidthMm;
                    height = dims.HeightMm;
                    break;
                case Unit.Centimeters:
                    width = dims.WidthMm / 10f;
                    height = dims.HeightMm / 10f;
                    break;
                default:
                    width = dims.WidthMm / 25.4f;
                    height = dims.HeightMm / 25.4f;
                    unit = Unit.Inches;
                    break;
            }
            var physicalWidth = src.Capabilities.ICapPhysicalWidth;
            var physicalHeight = src.Capabilities.ICapPhysicalHeight;
            float maxWidth = width;
            float maxHeight = height;
            if (physicalWidth.IsSupported)
                maxWidth = physicalWidth.GetCurrent();
            if (physicalHeight.IsSupported)
                maxHeight = physicalHeight.GetCurrent();
            if (width > maxWidth + 0.001f || height > maxHeight + 0.001f)
            {
                AppLog.Info("请求区域超过物理范围，不截短纸张");
                return false;
            }
            var left = FrameLeft(src, maxWidth, width);
            var frame = new TWFrame { Left = left, Top = 0, Right = left + width, Bottom = height };
            var frames = src.Capabilities.ICapFrames;
            if (!frames.IsSupported || !frames.CanSet)
            {
                AppLog.Info("当前驱动不能设置 ICapFrames");
                return false;
            }
            var rc = frames.SetValue(frame);
            AppLog.Info("扫描框 {0} {1}: {2}, Frame={3}", size.GetDisplayName(), unit, rc, frame);
            return rc == ReturnCode.Success;
        }

        static bool TrySetImageLayout(DataSource src, ScanPaperSize size, bool swapDimensions)
        {
            var dims = size.GetDimensionsMm();
            if (swapDimensions)
                dims = new ScanPaperDimensions(dims.HeightMm, dims.WidthMm);
            var unit = ResolveLayoutUnit(src.Capabilities.ICapUnits);
            float width;
            float height;
            switch (unit)
            {
                case Unit.Millimeters:
                    width = dims.WidthMm;
                    height = dims.HeightMm;
                    break;
                case Unit.Centimeters:
                    width = dims.WidthMm / 10f;
                    height = dims.HeightMm / 10f;
                    break;
                default:
                    width = dims.WidthMm / 25.4f;
                    height = dims.HeightMm / 25.4f;
                    break;
            }
            var physicalWidth = src.Capabilities.ICapPhysicalWidth;
            var physicalHeight = src.Capabilities.ICapPhysicalHeight;
            if ((physicalWidth.IsSupported && width > physicalWidth.GetCurrent() + 0.001f)
                || (physicalHeight.IsSupported && height > physicalHeight.GetCurrent() + 0.001f))
            {
                AppLog.Info("{0} 扫描区域超过物理范围，交换宽高={1}", size.GetDisplayName(), swapDimensions);
                return false;
            }
            TWImageLayout layout;
            src.DGImage.ImageLayout.Get(out layout);
            layout.Frame = new TWFrame { Left = 0, Top = 0, Right = width, Bottom = height };
            var rc = src.DGImage.ImageLayout.Set(layout);
            AppLog.Info("扫描区域 {0} (ImageLayout {1}): {2}, Frame={3}", size.GetDisplayName(), unit, rc, layout.Frame);
            if (rc != ReturnCode.Success)
                return false;
            var frames = src.Capabilities.ICapFrames;
            if (frames.IsSupported && frames.CanSet)
                return frames.SetValue(layout.Frame) == ReturnCode.Success;
            return true;
        }

        static void EnsureInches(DataSource src)
        {
            var units = src.Capabilities.ICapUnits;
            if (units.CanGetCurrent && units.GetCurrent() == Unit.Inches)
                return;
            if (units.CanSet)
                AppLog.Info("设置扫描区域单位 Inches: {0}", units.SetValue(Unit.Inches));
            if (!units.CanGetCurrent || units.GetCurrent() != Unit.Inches)
                throw new InvalidOperationException("无法确认扫描区域单位为英寸");
        }

        static Unit ResolveLayoutUnit(ICapWrapper<Unit> units)
        {
            if (units == null || !units.IsSupported)
                return Unit.Inches;
            var available = SafeGet(units);
            if (units.CanSet)
            {
                if (available.Contains(Unit.Millimeters) && units.SetValue(Unit.Millimeters) == ReturnCode.Success)
                    return Unit.Millimeters;
                if (available.Contains(Unit.Centimeters) && units.SetValue(Unit.Centimeters) == ReturnCode.Success)
                    return Unit.Centimeters;
            }
            return units.CanGetCurrent ? units.GetCurrent() : Unit.Inches;
        }

        static Unit CurrentUnit(ICapWrapper<Unit> units)
        {
            if (units != null && units.IsSupported && units.CanGetCurrent)
                return units.GetCurrent();
            return Unit.Inches;
        }

        static float FrameLeft(DataSource src, float maxWidth, float width)
        {
            var feeder = src.Capabilities.CapFeederEnabled;
            if (!feeder.IsSupported || !feeder.CanGetCurrent || feeder.GetCurrent() != BoolType.True)
                return 0;
            var alignCap = src.Capabilities.CapFeederAlignment;
            var align = FeederAlignment.None;
            if (alignCap.IsSupported && alignCap.CanGetCurrent)
                align = alignCap.GetCurrent();
            float left = 0;
            if (align == FeederAlignment.Center)
                left = (maxWidth - width) / 2f;
            else if (align == FeederAlignment.Right)
                left = maxWidth - width;
            return left < 0 ? 0 : left;
        }

        int MatchScore(string manufacturer, string name)
        {
            var info = manufacturer + " " + name;
            var groups = 0;
            var hits = 0;
            if (ManufacturerContains != null && ManufacturerContains.Length > 0)
            {
                groups++;
                if (ContainsAny(info, ManufacturerContains))
                    hits++;
            }
            if (NameContains != null && NameContains.Length > 0)
            {
                groups++;
                if (ContainsAny(name, NameContains))
                    hits++;
            }
            if (groups == 0 || hits == 0)
                return int.MinValue;
            if (!MatchAny && hits != groups)
                return int.MinValue;
            return hits + Priority * 10;
        }

        bool TryGetPaper(ScanPaperSize size, out PaperConfig paper)
        {
            paper = null;
            return Papers != null && Papers.TryGetValue(size.ToString(), out paper);
        }

        string DriverProfilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NTwain", "profiles", DriverProfile.file);
            }
        }

        static ScanPaperSize SwapPaper(ScanPaperSize size)
        {
            switch (size)
            {
                case ScanPaperSize.A4: return ScanPaperSize.A4L;
                case ScanPaperSize.A4L: return ScanPaperSize.A4;
                case ScanPaperSize.K16: return ScanPaperSize.K16L;
                case ScanPaperSize.K16L: return ScanPaperSize.K16;
                default: return size;
            }
        }

        static ScanPaperSize ParsePaper(string name)
        {
            ScanPaperSize size;
            if (!Enum.TryParse(name, out size))
                throw new InvalidOperationException("未知纸张 " + name);
            return size;
        }

        static SupportedSize? TwainSize(ScanPaperSize size)
        {
            switch (size)
            {
                case ScanPaperSize.A3: return SupportedSize.A3;
                case ScanPaperSize.A4:
                case ScanPaperSize.A4L: return SupportedSize.A4;
                default: return null;
            }
        }

        static bool TryParseCap(string name, out CapabilityId id)
        {
            const string prefix = "CustomBase+";
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                int offset;
                if (!int.TryParse(name.Substring(prefix.Length), out offset))
                {
                    id = 0;
                    return false;
                }
                id = (CapabilityId)((int)CapabilityId.CustomBase + offset);
                return true;
            }
            return Enum.TryParse(name, out id);
        }

        static bool IsCustom(CapabilityId id)
        {
            return (int)id >= (int)CapabilityId.CustomBase;
        }

        static bool IsStringItem(ItemType type)
        {
            return type == ItemType.String32 || type == ItemType.String64
                || type == ItemType.String128 || type == ItemType.String255;
        }

        static T ParseEnum<T>(object value)
        {
            return (T)Enum.Parse(typeof(T), Convert.ToString(value));
        }

        static bool AsBool(object value)
        {
            if (value is bool)
                return (bool)value;
            return AsNumber(value) != 0;
        }

        static bool IsNumber(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long || value is ulong
                || value is float || value is double || value is decimal;
        }

        static double AsNumber(object value)
        {
            return Convert.ToDouble(value);
        }

        static bool TryDouble(object value, out double number)
        {
            try
            {
                number = Convert.ToDouble(value);
                return true;
            }
            catch (Exception)
            {
                number = 0;
                return false;
            }
        }

        static void TrySetCap<T>(ICapWrapper<T> cap, T value)
        {
            if (cap != null && cap.IsSupported && cap.CanSet)
                cap.SetValue(value);
        }

        static List<T> SafeGet<T>(IReadOnlyCapWrapper<T> cap)
        {
            try { return cap.GetValues().ToList(); }
            catch { return new List<T>(); }
        }

        static bool ContainsAny(string text, IEnumerable<string> tokens)
        {
            if (string.IsNullOrEmpty(text) || tokens == null)
                return false;
            foreach (var token in tokens)
            {
                if (!string.IsNullOrEmpty(token) && text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        internal static ScannerProfile FromFile(ProfileFile file, ScannerProfile basis)
        {
            var profile = basis == null ? new ScannerProfile() : basis.Copy();
            profile.Id = file.id;
            profile.Name = string.IsNullOrEmpty(file.name) ? file.id : file.name;
            profile.IsDefault = string.Equals(file.id, "default", StringComparison.OrdinalIgnoreCase);
            profile.Priority = file.priority;
            profile.MatchAny = file.matchAny;
            if (file.fallbackSwapDimensions.HasValue)
                profile.FallbackSwapDimensions = file.fallbackSwapDimensions.Value;
            else if (basis == null)
                profile.FallbackSwapDimensions = true;
            if (file.swapPortraitLandscape.HasValue)
                profile.SwapPortraitLandscape = file.swapPortraitLandscape.Value;
            if (file.cropMargins.HasValue)
                profile.CropMargins = file.cropMargins.Value;
            if (file.match != null)
            {
                profile.ManufacturerContains = file.match.manufacturerContains;
                profile.NameContains = file.match.nameContains;
            }
            if (basis == null)
            {
                profile.CommonBefore = file.commonBefore ?? new List<CapSetting>();
                profile.CommonAfter = file.commonAfter ?? new List<CapSetting>();
                profile.Papers = file.papers ?? new Dictionary<string, PaperConfig>();
            }
            else
            {
                profile.CommonBefore = Concat(basis.CommonBefore, file.commonBefore);
                profile.CommonAfter = Concat(file.commonAfter, basis.CommonAfter);
                profile.Papers = new Dictionary<string, PaperConfig>(basis.Papers ?? new Dictionary<string, PaperConfig>());
                if (file.papers != null)
                {
                    foreach (var pair in file.papers)
                        profile.Papers[pair.Key] = pair.Value;
                }
            }
            if (file.driverProfile != null)
                profile.DriverProfile = file.driverProfile;
            return profile;
        }

        ScannerProfile Copy()
        {
            return new ScannerProfile
            {
                Id = Id,
                Name = Name,
                Priority = Priority,
                MatchAny = MatchAny,
                FallbackSwapDimensions = FallbackSwapDimensions,
                SwapPortraitLandscape = SwapPortraitLandscape,
                CropMargins = CropMargins,
                ManufacturerContains = ManufacturerContains,
                NameContains = NameContains,
                CommonBefore = CommonBefore,
                CommonAfter = CommonAfter,
                Papers = Papers,
                DriverProfile = DriverProfile,
                IsDefault = IsDefault
            };
        }

        static List<CapSetting> Concat(IList<CapSetting> first, IList<CapSetting> second)
        {
            var list = new List<CapSetting>();
            if (first != null)
                list.AddRange(first);
            if (second != null)
                list.AddRange(second);
            return list;
        }
    }

    sealed class ScannerProfileCatalog
    {
        static List<ScannerProfile> _profiles;

        static string ProfileDirectory()
        {
            var besideApp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scanners");
            if (Directory.Exists(besideApp))
                return besideApp;
            return Path.Combine(Path.GetDirectoryName(typeof(ScannerProfile).Assembly.Location), "Scanners");
        }

        public static IList<ScannerProfile> Load()
        {
            if (_profiles != null)
                return _profiles;
            var directory = ProfileDirectory();
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("找不到扫描配置目录: " + directory);
            var serializer = new JavaScriptSerializer();
            var files = new Dictionary<string, ProfileFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                var file = serializer.Deserialize<ProfileFile>(File.ReadAllText(path));
                if (file == null || string.IsNullOrEmpty(file.id))
                    throw new InvalidDataException("扫描配置缺少 id: " + path);
                files[file.id] = file;
            }
            if (!files.ContainsKey("default"))
                throw new InvalidDataException("缺少 default.json");
            var resolved = new Dictionary<string, ScannerProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in files.Keys)
                Resolve(id, files, resolved, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            _profiles = resolved.Values.ToList();
            return _profiles;
        }

        static ScannerProfile Resolve(string id, Dictionary<string, ProfileFile> files,
            Dictionary<string, ScannerProfile> resolved, HashSet<string> stack)
        {
            ScannerProfile existing;
            if (resolved.TryGetValue(id, out existing))
                return existing;
            if (!stack.Add(id))
                throw new InvalidDataException("扫描配置循环继承: " + id);
            ProfileFile file;
            if (!files.TryGetValue(id, out file))
                throw new InvalidDataException("找不到扫描配置 " + id);
            ScannerProfile basis = null;
            if (!string.IsNullOrEmpty(file.extends))
                basis = Resolve(file.extends, files, resolved, stack);
            var profile = ScannerProfile.FromFile(file, basis);
            resolved[id] = profile;
            return profile;
        }
    }

    sealed class ProfileFile
    {
        public string id { get; set; }
        public string name { get; set; }
        public string note { get; set; }
        public string extends { get; set; }
        public int priority { get; set; }
        public bool matchAny { get; set; }
        public bool? fallbackSwapDimensions { get; set; }
        public bool? swapPortraitLandscape { get; set; }
        public bool? cropMargins { get; set; }
        public MatchRule match { get; set; }
        public List<CapSetting> commonBefore { get; set; }
        public List<CapSetting> commonAfter { get; set; }
        public Dictionary<string, PaperConfig> papers { get; set; }
        public DriverProfileConfig driverProfile { get; set; }
    }

    sealed class MatchRule
    {
        public string[] manufacturerContains { get; set; }
        public string[] nameContains { get; set; }
    }

    sealed class PaperConfig
    {
        public string note { get; set; }
        public string area { get; set; }
        public bool verify { get; set; }
        public bool cropAfterScan { get; set; }
        public bool? cropMargins { get; set; }
        public bool restoreCanvas { get; set; }
        public bool restoreDriverProfile { get; set; }
        public bool skipIndicators { get; set; }
        public string usePaper { get; set; }
        public string forceOrientation { get; set; }
        public string frameMode { get; set; }
        public List<CapSetting> settings { get; set; }
        public FrameConfig frame { get; set; }
    }

    sealed class CapSetting
    {
        public string cap { get; set; }
        public object value { get; set; }
        public string unit { get; set; }
        public bool required { get; set; }
        public string note { get; set; }
    }

    sealed class FrameConfig
    {
        public double left { get; set; }
        public double top { get; set; }
        public double right { get; set; }
        public double bottom { get; set; }
        public string unit { get; set; }
        public string target { get; set; }
    }

    sealed class DriverProfileConfig
    {
        public string file { get; set; }
        public string supportedSize { get; set; }
        public double frameRight { get; set; }
        public double frameBottom { get; set; }
        public bool requireWidthGreaterThanHeight { get; set; }
    }
}
