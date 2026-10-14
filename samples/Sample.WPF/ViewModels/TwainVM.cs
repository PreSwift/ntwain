using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using ModernWpf.Messages;
using NTwain;
using NTwain.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Drawing;

namespace Sample.WPF
{
    /// <summary>
    /// Wraps the twain session as a view model for databinding.
    /// </summary>
    class TwainVM : ViewModelBase
    {
        readonly string _scanRootFolder;
        string _currentScanFolder;
        int _transferCount;
        bool _isDuplexScan;
        ScanPaperSize _selectedScanPaperSize;

        public TwainVM()
        {
            DataSources = new ObservableCollection<DataSourceVM>();
            CapturedImages = new ObservableCollection<CapturedScanItem>();
            _scanRootFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NTwain Scans");
            Directory.CreateDirectory(_scanRootFolder);

            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image | DataGroups.Audio, Assembly.GetEntryAssembly());
            _session = new TwainSession(appId);
            _session.TransferError += _session_TransferError;
            _session.TransferReady += _session_TransferReady;
            _session.DataTransferred += _session_DataTransferred;
            _session.SourceDisabled += _session_SourceDisabled;
            _session.StateChanged += _session_StateChanged;
            // 传输事件在 TWAIN 线程处理，避免 Send 阻塞 UI 导致大批量扫描丢页
            _session.SynchronizationContext = null;
        }

        TwainSession _session;
        DispatcherTimer _hideScanStatusTimer;

        #region properties

        public string AppTitle
        {
            get
            {
                if (NTwain.PlatformInfo.Current.IsApp64Bit)
                {
                    return "TWAIN Data Source Tester (64bit)";
                }
                else
                {
                    return "TWAIN Data Source Tester (32bit)";
                }
            }
        }
        public ObservableCollection<DataSourceVM> DataSources { get; private set; }
        private DataSourceVM _selectedSource;

        public DataSourceVM SelectedSource
        {
            get { return _selectedSource; }
            set
            {
                if (_session.State == 4)
                {
                    _session.CurrentSource.Close();
                }
                _selectedSource = value;
                RaisePropertyChanged(() => SelectedSource);
                if (_selectedSource != null)
                {
                    AppLog.Info("选择数据源: {0}", _selectedSource.Name);
                    _selectedSource.Open();
                }
            }
        }

        public int State { get { return _session.State; } }

        private IntPtr _winHandle;
        public IntPtr WindowHandle
        {
            get { return _winHandle; }
            set
            {
                _winHandle = value;
                if (value == IntPtr.Zero)
                {

                }
                else
                {
                    // use this for internal msg loop
                    AppLog.Info("打开 DSM, hwnd=0x{0:X}", value.ToInt64());
                    var rc = _session.Open();
                    AppLog.Info("打开 DSM 结果: {0}", rc);

                    // use this to hook into current app loop
                    //var rc = _session.Open(new WpfMessageLoopHook(value));

                    if (rc == ReturnCode.Success)
                    {
                        ReloadSourcesCommand.Execute(null);
                    }
                }
            }
        }

        public bool ShowUI
        {
            get; set;
        }



        private ICommand _showDriverCommand;
        public ICommand ShowDriverCommand
        {
            get
            {
                return _showDriverCommand ?? (_showDriverCommand = new RelayCommand(() =>
                {
                    if (_session.State == 4)
                    {
                        var rc = _session.CurrentSource.Enable(SourceEnableMode.ShowUIOnly, false, WindowHandle);
                        CommandManager.InvalidateRequerySuggested();
                    }
                }, () =>
                {
                    return _session.State == 4 && _session.CurrentSource.Capabilities.CapEnableDSUIOnly.GetCurrent() == BoolType.True;
                }));
            }
        }

        private ICommand _captureCommand;
        public ICommand CaptureCommand
        {
            get
            {
                return _captureCommand ?? (_captureCommand = new RelayCommand(() =>
                {
                    if (_session.State == 4)
                    {
                        try
                        {
                            AppLog.Info("============================== 开始扫描 ==============================");
                            ScanPaperSize selectedSize;
                            if (!TryPromptScanSize(out selectedSize))
                            {
                                AppLog.Info("已取消本次扫描");
                                return;
                            }

                            var src = _session.CurrentSource;
                            AppLog.Info("开始扫描 source={0} manufacturer={1} size={2} showUI={3} state={4} newDsm={5}",
                                src.Name, src.Manufacturer, selectedSize, ShowUI, _session.State, NTwain.PlatformInfo.Current.UseNewWinDSM);
                            _needCancel = false;
                            _selectedScanPaperSize = selectedSize;
                            BeginScanSession(src);
                            TryEnsureFileTransfer(src);
                            TryForceScanSize(src, selectedSize);
                            TryForceFeederScan(src);
                            TryForceDuplexScan(src);
                            _isDuplexScan = IsDuplexActive(src);

                            if (!ShowUI)
                            {
                                var capIndicators = src.Capabilities.CapIndicators;
                                if (capIndicators.IsSupported && capIndicators.CanSet)
                                {
                                    capIndicators.SetValue(BoolType.False);
                                }
                            }

                            var mode = ShowUI ? SourceEnableMode.ShowUI : SourceEnableMode.NoUI;
                            AppLog.Info("调用 Enable, mode={0}, hwnd=0x{1:X}", mode, WindowHandle.ToInt64());
                            var rc = src.Enable(mode, true, WindowHandle);
                            AppLog.Info("Enable 返回 {0}, state={1}", rc, _session.State);
                            if (rc != ReturnCode.Success)
                            {
                                ShowScanMessage("扫描没有启动", "Enable 返回 " + rc + "。请查看日志确认卡在哪一项能力设置。");
                            }
                            CommandManager.InvalidateRequerySuggested();
                        }
                        catch (AccessViolationException ex)
                        {
                            AppLog.Error("扫描过程发生驱动访问冲突，已中止，不再继续调用驱动", ex);
                            ShowScanMessage("扫描失败", "驱动访问冲突，本次扫描已中止。\n" + ex.Message);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("扫描过程异常", ex);
                            throw;
                        }
                    }
                }, () =>
                {
                    return _session.State == 4;
                }));
            }
        }

        private bool _needCancel = false;

        private ICommand _cancelCommand;
        public ICommand CancelCommand
        {
            get
            {
                return _cancelCommand ?? (_cancelCommand = new RelayCommand(() =>
                {
                    _needCancel = true;
                }, () =>
                {
                    return _session.State > 4;
                }));
            }
        }


        //private ICommand _saveCommand;
        //public ICommand SaveCommand
        //{
        //    get
        //    {
        //        return _saveCommand ?? (_saveCommand = new RelayCommand(() =>
        //        {
        //            Messenger.Default.Send(new ChooseFileMessage(this, files =>
        //            {
        //                var tiffPath = files.FirstOrDefault();

        //                var srcFiles = CapturedImages.Select(ci=>ci.)
        //            })
        //            {
        //                Caption = "Save to File",
        //                Filters = "Tiff files|*.tif,*.tiff"
        //            });
        //        }, () =>
        //        {
        //            return CapturedImages.Count > 0;
        //        }));
        //    }
        //}

        private ICommand _clearCommand;
        public ICommand ClearCommand
        {
            get
            {
                return _clearCommand ?? (_clearCommand = new RelayCommand(() =>
                {
                    CapturedImages.Clear();
                }, () =>
                {
                    return CapturedImages.Count > 0;
                }));
            }
        }

        private ICommand _openSaveFolderCommand;
        public ICommand OpenSaveFolderCommand
        {
            get
            {
                return _openSaveFolderCommand ?? (_openSaveFolderCommand = new RelayCommand(() =>
                {
                    var folder = GetActiveScanFolder();
                    Directory.CreateDirectory(folder);
                    Process.Start("explorer.exe", folder);
                }));
            }
        }

        private ICommand _openLogCommand;
        public ICommand OpenLogCommand
        {
            get
            {
                return _openLogCommand ?? (_openLogCommand = new RelayCommand(() =>
                {
                    var path = AppLog.CurrentLogPath;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        var folder = AppLog.DirectoryPath;
                        if (string.IsNullOrEmpty(folder))
                            return;
                        Directory.CreateDirectory(folder);
                        Process.Start("explorer.exe", folder);
                        return;
                    }

                    Process.Start(path);
                }));
            }
        }

        private ICommand _clearLogCommand;
        public ICommand ClearLogCommand
        {
            get
            {
                return _clearLogCommand ?? (_clearLogCommand = new RelayCommand(() =>
                {
                    var answer = MessageBox.Show(
                        "将删除已有扫描日志。当前日志会重新开始记录。",
                        "清理日志",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.OK)
                        return;

                    var removed = AppLog.Clear();
                    MessageBox.Show(
                        "已清理 " + removed + " 个日志文件。",
                        "清理日志",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }));
            }
        }

        private ICommand _reloadSrc;
        public ICommand ReloadSourcesCommand
        {
            get
            {
                return _reloadSrc ?? (_reloadSrc = new RelayCommand(() =>
                {
                    DataSources.Clear();
                    foreach (var s in _session.Select(s => new DataSourceVM { DS = s }))
                    {
                        DataSources.Add(s);
                    }
                }, () =>
                {
                    return _session.State > 2;
                }));
            }
        }

        /// <summary>
        /// Gets the captured images.
        /// </summary>
        /// <value>
        /// The captured images.
        /// </value>
        public ObservableCollection<CapturedScanItem> CapturedImages { get; private set; }

        /// <summary>
        /// 当前扫描会话的保存目录。
        /// </summary>
        public string ScanSaveFolderPath { get { return GetActiveScanFolder(); } }

        public double MinThumbnailSize { get { return 50; } }
        public double MaxThumbnailSize { get { return 300; } }

        private double _thumbSize = 150;
        public double ThumbnailSize
        {
            get { return _thumbSize; }
            set
            {
                if (value > MaxThumbnailSize) { value = MaxThumbnailSize; }
                else if (value < MinThumbnailSize) { value = MinThumbnailSize; }
                _thumbSize = value;
                RaisePropertyChanged(() => ThumbnailSize);
            }
        }


        #endregion

        void StartHideScanStatusTimer()
        {
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_hideScanStatusTimer != null)
                    return;
                _hideScanStatusTimer = new DispatcherTimer(DispatcherPriority.Background, App.Current.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                _hideScanStatusTimer.Tick += (s, args) => NativeWindowHelper.HideScanStatusWindows();
                _hideScanStatusTimer.Start();
            }));
        }

        void StopHideScanStatusTimer()
        {
            if (_hideScanStatusTimer == null)
                return;
            _hideScanStatusTimer.Stop();
            _hideScanStatusTimer = null;
        }

        static void ShowScanMessage(string caption, string message)
        {
            Messenger.Default.Send(new MessageBoxMessage(message, null)
            {
                Caption = caption,
                Icon = MessageBoxImage.Warning,
                Button = MessageBoxButton.OK
            });
        }

        bool TryPromptScanSize(out ScanPaperSize selectedSize)
        {
            if (ShowUI)
            {
                selectedSize = ScanPaperSize.Auto;
                return true;
            }

            selectedSize = ScanSizePreference.Load();
            var owner = Application.Current == null
                ? null
                : Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current.MainWindow;
            if (owner == null)
                return false;

            var dialog = new ScanSizeSelectionWindow
            {
                Owner = owner
            };

            if (dialog.ShowDialog() != true)
                return false;

            selectedSize = dialog.SelectedSize;
            return true;
        }

        /// <summary>
        /// 在 Enable 之前强制扫描尺寸：优先 ICAP_SUPPORTEDSIZES，失败则用 ImageLayout 指定物理区域。
        /// </summary>
        static void TryForceScanSize(DataSource src, ScanPaperSize size)
        {
            if (src == null || size == ScanPaperSize.Auto)
                return;

            try
            {
                var caps = src.Capabilities;
                var label = size.GetDisplayName();
                var applySize = ScannerOrientationHelper.GetEffectiveSize(src, size);
                if (applySize != size)
                {
                    AppLog.Info("检测到进纸方向相反的驱动 ({0})，{1} 按 {2} 设置方向与扫描区域",
                        src.Name, label, applySize.GetDisplayName());
                }

                // 关掉会覆盖固定纸张尺寸的自动检测/裁边
                TrySetCap(caps.ICapUndefinedImageSize, BoolType.False);
                TrySetCap(caps.ICapAutomaticBorderDetection, BoolType.False);
                TrySetCap(caps.ICapAutoSize, AutoSize.None);

                if (!IsEpson(src))
                    TryApplyScanOrientation(src, applySize);

                if (IsEpson(src))
                {
                    TryApplyEpsonScanArea(src, applySize, label);
                    return;
                }

                SupportedSize? twainSize = GetTwainSupportedSize(applySize);
                if (twainSize.HasValue)
                {
                    var sizes = caps.ICapSupportedSizes;
                    if (sizes.IsSupported && sizes.CanSet)
                    {
                        var available = SafeGetValues(sizes);
                        var canUse = available.Count == 0 || available.Contains(twainSize.Value);
                        if (canUse)
                        {
                            var rc = sizes.SetValue(twainSize.Value);
                            AppLog.Info("强制扫描尺寸 {0} (ICapSupportedSizes): {1}, 当前={2}", label, rc, sizes.GetCurrent());
                        }
                        else
                        {
                            AppLog.Info("当前驱动未列出 {0}，可用尺寸: {1}", label, string.Join(", ", available));
                        }
                    }
                }

                if (TrySetImageLayout(src, applySize))
                    TryApplyScanOrientation(src, applySize);
            }
            catch (Exception ex)
            {
                AppLog.Info("强制 {0} 扫描尺寸失败: {1}", size.GetDisplayName(), ex.Message);
            }
        }

        /// <summary>
        /// 在 Enable 之前强制使用进纸器（ADF）扫描。
        /// </summary>
        static void TryForceFeederScan(DataSource src)
        {
            if (src == null)
                return;

            try
            {
                var caps = src.Capabilities;

                if (caps.CapFeederEnabled.IsSupported && caps.CapFeederEnabled.CanSet)
                {
                    var rc = caps.CapFeederEnabled.SetValue(BoolType.True);
                    AppLog.Info("强制进纸器 (CapFeederEnabled): {0}", rc);
                }
                else
                {
                    AppLog.Info("当前驱动不支持设置 CapFeederEnabled");
                }

                // Canon MF 在旧版 twain_32 上 MSG_SET CAP_AUTOFEED 会访问冲突，且冲突后不能再调用驱动。
                // 连续进纸改由 CapFeederEnabled + CapXferCount=-1 完成。
                AppLog.Info("跳过 CapAutoFeed，避免佳能驱动在设置该项时崩溃");

                if (caps.CapXferCount.IsSupported && caps.CapXferCount.CanSet)
                {
                    var countRc = caps.CapXferCount.SetValue(-1);
                    AppLog.Info("传输张数 (CapXferCount=-1): {0}", countRc);
                }
            }
            catch (AccessViolationException ex)
            {
                AppLog.Error("进纸器设置触发驱动访问冲突，已中止本次扫描", ex);
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Info("强制进纸器扫描失败: {0}", ex.Message);
            }
        }

        /// <summary>
        /// 在 Enable 之前强制双面扫描：打开 CapDuplexEnabled。
        /// </summary>
        static void TryForceDuplexScan(DataSource src)
        {
            if (src == null)
                return;

            try
            {
                var caps = src.Capabilities;

                if (caps.CapDuplex.IsSupported)
                {
                    var duplexMode = caps.CapDuplex.GetCurrent();
                    AppLog.Info("扫描仪双面能力 (CapDuplex): {0}", duplexMode);
                    if (duplexMode == Duplex.None)
                    {
                        AppLog.Info("当前扫描仪不支持双面扫描");
                        return;
                    }
                }

                if (caps.CapDuplexEnabled.IsSupported && caps.CapDuplexEnabled.CanSet)
                {
                    var rc = caps.CapDuplexEnabled.SetValue(BoolType.True);
                    AppLog.Info("强制双面扫描 (CapDuplexEnabled): {0}, 当前={1}", rc, caps.CapDuplexEnabled.GetCurrent());
                }
                else
                {
                    AppLog.Info("当前驱动不支持设置 CapDuplexEnabled");
                }
            }
            catch (Exception ex)
            {
                AppLog.Info("强制双面扫描失败: {0}", ex.Message);
            }
        }

        static void TryApplyScanOrientation(DataSource src, ScanPaperSize size)
        {
            var orientation = src.Capabilities.ICapOrientation;
            if (!orientation.IsSupported || !orientation.CanSet)
                return;

            var landscape = size.IsLandscape();
            var available = SafeGetValues(orientation);
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
            else
            {
                if (available.Count == 0 || available.Contains(OrientationType.Portrait))
                    target = OrientationType.Portrait;
                else if (available.Contains(OrientationType.Rot0))
                    target = OrientationType.Rot0;
            }

            if (!target.HasValue)
            {
                AppLog.Info("当前驱动未支持 {0} 所需方向，可用: {1}",
                    landscape ? "横向" : "纵向",
                    string.Join(", ", available));
                return;
            }

            var rc = orientation.SetValue(target.Value);
            AppLog.Info("强制扫描方向 {0} (ICapOrientation): {1}, 当前={2}",
                target.Value, rc, orientation.GetCurrent());
        }

        /// <summary>
        /// 爱普生不接受把单位改成厘米后再写 ImageLayout，失败后 Enable 也会失败。
        /// 标准尺寸本身又不会改实际扫描范围，所以按驱动当前单位写 ICAP_FRAMES。
        /// </summary>
        static void TryApplyEpsonScanArea(DataSource src, ScanPaperSize size, string label)
        {
            TrySetSupportedPaperSize(src, size, label);

            if (!TrySetFrame(src, size, false))
                AppLog.Info("爱普生按当前单位设置扫描区域失败");

            if (size.IsLandscape())
                TryApplyScanOrientation(src, size);
        }

        static void TrySetSupportedPaperSize(DataSource src, ScanPaperSize size, string label)
        {
            SupportedSize? twainSize = GetTwainSupportedSize(size);
            if (!twainSize.HasValue)
                return;

            var sizes = src.Capabilities.ICapSupportedSizes;
            if (!sizes.IsSupported || !sizes.CanSet)
                return;

            var available = SafeGetValues(sizes);
            if (available.Count > 0 && !available.Contains(twainSize.Value))
            {
                AppLog.Info("当前驱动未列出 {0}，可用尺寸: {1}", label, string.Join(", ", available));
                return;
            }

            var rc = sizes.SetValue(twainSize.Value);
            AppLog.Info("强制扫描尺寸 {0} (ICapSupportedSizes): {1}, 当前={2}", label, rc, sizes.GetCurrent());
        }

        static bool TrySetFrame(DataSource src, ScanPaperSize size, bool changeUnit)
        {
            var dims = size.GetDimensionsMm();
            float widthMm = dims.WidthMm;
            float heightMm = dims.HeightMm;
            if (IsEpson(src) && !size.IsLandscape())
            {
                // 竖放必须高大于宽。Landscape 会把图像转横，高度反而比宽度小。
                widthMm = Math.Min(dims.WidthMm, dims.HeightMm);
                heightMm = Math.Max(dims.WidthMm, dims.HeightMm);
            }
            var units = src.Capabilities.ICapUnits;
            var unit = changeUnit ? ResolveLayoutUnit(units) : GetCurrentUnit(units);
            float width;
            float height;
            switch (unit)
            {
                case Unit.Millimeters:
                    width = widthMm;
                    height = heightMm;
                    break;
                case Unit.Centimeters:
                    width = widthMm / 10f;
                    height = heightMm / 10f;
                    break;
                default:
                    width = widthMm / 25.4f;
                    height = heightMm / 25.4f;
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
            width = Math.Min(width, maxWidth);
            height = Math.Min(height, maxHeight);
            var left = GetFrameLeft(src, maxWidth, width);
            AppLog.Info("扫描区域上限 {0}: {1} x {2}，左边距 {3}", unit, maxWidth, maxHeight, left);

            var frame = new TWFrame
            {
                Left = left,
                Top = 0,
                Right = left + width,
                Bottom = height
            };
            var frames = src.Capabilities.ICapFrames;
            if (!frames.IsSupported || !frames.CanSet)
            {
                AppLog.Info("当前驱动不能设置 ICapFrames");
                return false;
            }

            var rc = frames.SetValue(frame);
            AppLog.Info("强制扫描尺寸 {0} (ICapFrames {1}): {2}, Frame={3}", size.GetDisplayName(), unit, rc, frame);
            return rc == ReturnCode.Success;
        }

        static float GetFrameLeft(DataSource src, float maxWidth, float width)
        {
            var alignCap = src.Capabilities.CapFeederAlignment;
            FeederAlignment align = FeederAlignment.None;
            if (alignCap.IsSupported && alignCap.CanGetCurrent)
            {
                align = alignCap.GetCurrent();
                AppLog.Info("进纸对齐: {0}", align);
            }
            else if (IsEpson(src))
            {
                align = FeederAlignment.Center;
                AppLog.Info("爱普生未报告进纸对齐，按居中计算扫描区域");
            }

            float left = 0;
            if (align == FeederAlignment.Center)
                left = (maxWidth - width) / 2f;
            else if (align == FeederAlignment.Right)
                left = maxWidth - width;
            if (left < 0)
                left = 0;
            return left;
        }

        static Unit GetCurrentUnit(ICapWrapper<Unit> units)
        {
            if (units != null && units.IsSupported && units.CanGetCurrent)
                return units.GetCurrent();
            return Unit.Inches;
        }

        static bool IsEpson(DataSource src)
        {
            var info = string.Format("{0} {1}", src.Manufacturer, src.Name);
            return info.IndexOf("EPSON", StringComparison.OrdinalIgnoreCase) >= 0
                || info.IndexOf("爱普生", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static SupportedSize? GetTwainSupportedSize(ScanPaperSize size)
        {
            switch (size)
            {
                case ScanPaperSize.A3:
                    return SupportedSize.A3;
                case ScanPaperSize.A4:
                case ScanPaperSize.A4L:
                    return SupportedSize.A4;
                default:
                    return null;
            }
        }

        static bool TrySetImageLayout(DataSource src, ScanPaperSize size)
        {
            var dims = size.GetDimensionsMm();
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
            if (physicalWidth.IsSupported)
                width = Math.Min(width, physicalWidth.GetCurrent());
            if (physicalHeight.IsSupported)
                height = Math.Min(height, physicalHeight.GetCurrent());

            TWImageLayout layout;
            src.DGImage.ImageLayout.Get(out layout);
            layout.Frame = new TWFrame
            {
                Left = 0,
                Top = 0,
                Right = width,
                Bottom = height
            };

            var rc = src.DGImage.ImageLayout.Set(layout);
            AppLog.Info("强制扫描尺寸 {0} (ImageLayout {1}): {2}, Frame={3}", size.GetDisplayName(), unit, rc, layout.Frame);
            if (rc != ReturnCode.Success)
                return false;

            var frames = src.Capabilities.ICapFrames;
            if (frames.IsSupported && frames.CanSet)
                frames.SetValue(layout.Frame);
            return true;
        }

        static Unit ResolveLayoutUnit(ICapWrapper<Unit> units)
        {
            if (units == null || !units.IsSupported)
                return Unit.Inches;

            var available = SafeGetValues(units);
            if (units.CanSet)
            {
                if (available.Contains(Unit.Millimeters) && units.SetValue(Unit.Millimeters) == ReturnCode.Success)
                    return Unit.Millimeters;
                if (available.Contains(Unit.Centimeters) && units.SetValue(Unit.Centimeters) == ReturnCode.Success)
                    return Unit.Centimeters;
            }

            return units.CanGetCurrent ? units.GetCurrent() : Unit.Inches;
        }

        static void TrySetCap<T>(ICapWrapper<T> cap, T value)
        {
            if (cap != null && cap.IsSupported && cap.CanSet)
            {
                cap.SetValue(value);
            }
        }

        static IList<T> SafeGetValues<T>(IReadOnlyCapWrapper<T> cap)
        {
            try
            {
                return cap.GetValues().ToList();
            }
            catch
            {
                return new List<T>();
            }
        }

        void _session_SourceDisabled(object sender, EventArgs e)
        {
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    StopHideScanStatusTimer();
                    AppLog.Info("扫描结束，共保存 {0} 张，目录: {1}", _transferCount, _currentScanFolder);
                    //_session.CurrentSource.Close();
                    
                    // 触发状态更新
                    RaisePropertyChanged(() => State);
                    
                    // 使用 CommandManager 来触发所有命令的状态更新
                    CommandManager.InvalidateRequerySuggested();
                    
                    // 手动触发每个命令的状态更新
                    if (_showDriverCommand != null)
                        ((RelayCommand)_showDriverCommand).RaiseCanExecuteChanged();
                    if (_captureCommand != null)
                        ((RelayCommand)_captureCommand).RaiseCanExecuteChanged();
                    if (_cancelCommand != null)
                        ((RelayCommand)_cancelCommand).RaiseCanExecuteChanged();
                    if (_reloadSrc != null)
                        ((RelayCommand)_reloadSrc).RaiseCanExecuteChanged();
                }
                catch (Exception ex)
                {
                    AppLog.Info($"关闭源时出错: {ex.Message}");
                }
            }));
        }

        void _session_TransferError(object sender, TransferErrorEventArgs e)
        {
            if (e.Exception != null)
                AppLog.Error("传输错误", e.Exception);
            else
                AppLog.Error("传输错误 rc={0} cc={1}", e.ReturnCode, e.SourceStatus.ConditionCode);
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Exception != null)
                {
                    Messenger.Default.Send(new MessageBoxMessage(e.Exception.Message, null)
                    {
                        Caption = "Transfer Error Exception",
                        Icon = System.Windows.MessageBoxImage.Error,
                        Button = System.Windows.MessageBoxButton.OK
                    });
                }
                else
                {
                    Messenger.Default.Send(new MessageBoxMessage(string.Format("Return Code: {0}\nCondition Code: {1}", e.ReturnCode, e.SourceStatus.ConditionCode), null)
                    {
                        Caption = "Transfer Error",
                        Icon = System.Windows.MessageBoxImage.Error,
                        Button = System.Windows.MessageBoxButton.OK
                    });
                }
            }));
        }

        void _session_TransferReady(object sender, TransferReadyEventArgs e)
        {
            if (_needCancel == true) {
                e.CancelAll = true;
                return;
            }
            StartHideScanStatusTimer();
            AppLog.Info("准备接受图片数据");
            var mech = _session.CurrentSource.Capabilities.ICapXferMech.GetCurrent();
            AppLog.Info("当前传输模式: {0}", mech);
            if (mech == XferMech.File)
            {
                var formats = _session.CurrentSource.Capabilities.ICapImageFileFormat.GetValues();
                var wantFormat = formats.Contains(FileFormat.Jfif)
                    ? FileFormat.Jfif
                    : formats.Contains(FileFormat.Tiff) ? FileFormat.Tiff : FileFormat.Bmp;
                var ext = wantFormat == FileFormat.Jfif ? ".jpg" : wantFormat == FileFormat.Tiff ? ".tif" : ".bmp";
                var fileSetup = new TWSetupFileXfer
                {
                    Format = wantFormat,
                    FileName = GetNextScanFilePath(ext)
                };
                var rc = _session.CurrentSource.DGControl.SetupFileXfer.Set(fileSetup);
                AppLog.Info("扫描图片将保存到: {0}", fileSetup.FileName);
            }
            else if (mech == XferMech.Native)
            {
                AppLog.Info("下一张将保存为: {0}", PeekNextScanFilePath(".jpg"));
            }
            else if (mech == XferMech.Memory)
            {
                // ?
            }
        }

        string GetActiveScanFolder()
        {
            return _currentScanFolder ?? _scanRootFolder;
        }

        void BeginScanSession(DataSource src)
        {
            _currentScanFolder = Path.Combine(_scanRootFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(_currentScanFolder);
            _transferCount = 0;
            _isDuplexScan = false;
            CapturedImages.Clear();
            AppLog.Info("本次扫描保存目录: {0}", _currentScanFolder);
            RaisePropertyChanged(() => ScanSaveFolderPath);
        }

        static bool IsDuplexActive(DataSource src)
        {
            if (src == null)
                return false;

            var caps = src.Capabilities;
            if (caps.CapDuplex.IsSupported && caps.CapDuplex.GetCurrent() == Duplex.None)
                return false;

            return caps.CapDuplexEnabled.IsSupported
                && caps.CapDuplexEnabled.GetCurrent() == BoolType.True;
        }

        string BuildScanFileName(int transferIndex)
        {
            string fileName;
            if (_isDuplexScan)
            {
                var sheet = (transferIndex + 1) / 2;
                var side = (transferIndex % 2 == 1) ? "A" : "B";
                fileName = string.Format("{0}-{1}", sheet, side);
            }
            else
            {
                fileName = string.Format("{0}-A", transferIndex);
            }

            return fileName;
        }

        string PeekNextScanFilePath(string extension)
        {
            return Path.Combine(GetActiveScanFolder(), BuildScanFileName(_transferCount + 1) + extension);
        }

        string GetNextScanFilePath(string extension)
        {
            _transferCount++;
            return Path.Combine(GetActiveScanFolder(), BuildScanFileName(_transferCount) + extension);
        }

        static void TryEnsureFileTransfer(DataSource src)
        {
            if (src == null)
                return;

            var xferMech = src.Capabilities.ICapXferMech;
            if (!xferMech.IsSupported || !xferMech.CanSet)
            {
                AppLog.Info("当前驱动不支持设置 File 传输模式，将使用驱动默认模式");
                return;
            }

            var available = SafeGetValues(xferMech);
            if (available.Count > 0 && !available.Contains(XferMech.File))
            {
                AppLog.Info("当前驱动不支持 File 传输，可用模式: {0}", string.Join(", ", available));
                return;
            }

            var rc = xferMech.SetValue(XferMech.File);
            AppLog.Info("设置传输模式 File: {0}, 当前={1}", rc, xferMech.GetCurrent());

            var jpegQuality = src.Capabilities.ICapJpegQuality;
            if (jpegQuality.IsSupported && jpegQuality.CanSet)
            {
                var qualityRc = jpegQuality.SetValue(JpegQuality.High);
                AppLog.Info("设置 JPEG 质量 High: {0}", qualityRc);
            }
        }

        void LogReceivedImageInfo(DataTransferredEventArgs e)
        {
            var transferIndex = _transferCount + 1;
            try
            {
                var infos = e.GetExtImageInfo(
                    ExtendedImageInfo.PageNumber,
                    ExtendedImageInfo.PageSide,
                    ExtendedImageInfo.DocumentNumber).ToList();

                AppLog.Info(
                    "收到图像: 第 {0} 次传输, 进纸={1}, 页面={2}, 文档号={3}",
                    transferIndex,
                    FormatExtInfo(infos, ExtendedImageInfo.PageNumber, null),
                    FormatExtInfo(infos, ExtendedImageInfo.PageSide, FormatPageSide),
                    FormatExtInfo(infos, ExtendedImageInfo.DocumentNumber, null));
            }
            catch (Exception ex)
            {
                AppLog.Info("收到图像: 第 {0} 次传输, 读取进纸信息失败: {1}", transferIndex, ex.Message);
            }
        }

        static string FormatExtInfo(IList<TWInfo> infos, ExtendedImageInfo id, Func<object, string> formatValue)
        {
            var info = infos.FirstOrDefault(it => it.InfoID == id);
            if (info.InfoID != id || info.ReturnCode != ReturnCode.Success)
                return "驱动未提供";

            var values = info.ReadValues();
            if (values == null || values.Count == 0 || values[0] == null)
                return "驱动未提供";

            return formatValue == null ? Convert.ToString(values[0]) : formatValue(values[0]);
        }

        static string FormatPageSide(object value)
        {
            ushort side;
            try
            {
                side = Convert.ToUInt16(value);
            }
            catch (Exception)
            {
                return Convert.ToString(value);
            }

            switch ((CameraSide)side)
            {
                case CameraSide.Top:
                    return "正面";
                case CameraSide.Bottom:
                    return "背面";
                case CameraSide.Both:
                    return "双面";
                default:
                    return string.Format("未知({0})", side);
            }
        }

        void _session_DataTransferred(object sender, DataTransferredEventArgs e)
        {
            try
            {
                LogReceivedImageInfo(e);
                AppLog.Info("收到图片数据，传输模式: {0}", e.TransferType);
                string savedPath;
                if (!TrySaveTransferredImage(e, out savedPath))
                {
                    AppLog.Info("保存扫描图片失败，序号={0}", _transferCount + 1);
                    return;
                }

                savedPath = ScanImageRotationHelper.EnsureJpeg(savedPath);
                ScanImageRotationHelper.TryRotateSavedImage(savedPath, _selectedScanPaperSize, _session.CurrentSource);
                LogSavedImageSize(savedPath);
                AppLog.Info("扫描图片已保存: {0}", savedPath);
                QueueScanItem(savedPath);
            }
            catch (Exception ex)
            {
                AppLog.Info("处理扫描图片失败: {0}", ex);
                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Messenger.Default.Send(new MessageBoxMessage(ex.Message, null)
                    {
                        Caption = "处理扫描图片失败",
                        Icon = System.Windows.MessageBoxImage.Error,
                        Button = System.Windows.MessageBoxButton.OK
                    });
                }));
            }
        }

        static void LogSavedImageSize(string path)
        {
            try
            {
                using (var bmp = new Bitmap(path))
                {
                    AppLog.Info("图像尺寸: {0} x {1} 像素", bmp.Width, bmp.Height);
                }
            }
            catch (Exception ex)
            {
                AppLog.Info("读取图像尺寸失败: {0}", ex.Message);
            }
        }

        bool TrySaveTransferredImage(DataTransferredEventArgs e, out string savedPath)
        {
            savedPath = null;

            switch (e.TransferType)
            {
                case XferMech.Native:
                    savedPath = NativeImageSaveHelper.SaveDibToBmpFile(e.NativeData, GetNextScanFilePath(".bmp"));
                    return savedPath != null;
                case XferMech.File:
                    savedPath = e.FileDataPath;
                    return !string.IsNullOrEmpty(savedPath) && File.Exists(savedPath);
                case XferMech.Memory:
                    return false;
                default:
                    return false;
            }
        }

        void QueueScanItem(string savedPath)
        {
            var item = new CapturedScanItem { FilePath = savedPath };
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                CapturedImages.Add(item);
            }));

            Task.Run(() =>
            {
                var thumb = LoadThumbnailFromFile(savedPath, (int)MaxThumbnailSize);
                if (thumb == null)
                    return;

                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    item.Thumbnail = thumb;
                }));
            });
        }

        static BitmapSource LoadThumbnailFromFile(string path, int maxWidth)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(path, UriKind.Absolute);
                if (maxWidth > 0)
                {
                    img.DecodePixelWidth = maxWidth;
                }
                img.EndInit();
                if (img.CanFreeze)
                {
                    img.Freeze();
                }
                return img;
            }
            catch (Exception ex)
            {
                AppLog.Info("加载缩略图失败: {0}, {1}", path, ex.Message);
                return null;
            }
        }

        internal void CloseDown()
        {
            AppLog.Info("关闭会话, state={0}", _session.State);
            if (_session.State == 4)
            {
                _session.CurrentSource.Close();
            }
            _session.Close();
            AppLog.Info("会话已关闭, state={0}", _session.State);
        }

        void _session_StateChanged(object sender, EventArgs e)
        {
            AppLog.Info("TWAIN 状态变为 {0}", _session.State);
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                RaisePropertyChanged(() => State);
                
                // 使用 CommandManager 来触发所有命令的状态更新
                CommandManager.InvalidateRequerySuggested();
                
                // 手动触发每个命令的状态更新
                if (_showDriverCommand != null)
                    ((RelayCommand)_showDriverCommand).RaiseCanExecuteChanged();
                if (_captureCommand != null)
                    ((RelayCommand)_captureCommand).RaiseCanExecuteChanged();
                if (_cancelCommand != null)
                    ((RelayCommand)_cancelCommand).RaiseCanExecuteChanged();
                if (_reloadSrc != null)
                    ((RelayCommand)_reloadSrc).RaiseCanExecuteChanged();
            }));
        }
    }
}
