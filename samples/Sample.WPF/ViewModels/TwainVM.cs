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
        bool _captureUsesDriverUi;
        volatile int _lastImageWidth;
        volatile int _lastImageHeight;
        readonly OcrOrientationValidator _ocrValidator = new OcrOrientationValidator();
        volatile bool _isOcrValidationEnabled = true;

        public bool IsOcrValidationEnabled
        {
            get { return _isOcrValidationEnabled; }
            set { _isOcrValidationEnabled = value; RaisePropertyChanged(() => IsOcrValidationEnabled); }
        }

        public bool IsOrientationBusy { get { return _ocrValidator.PendingCount > 0; } }
        public string OrientationProgress
        {
            get { return IsOrientationBusy ? "转正待处理 " + _ocrValidator.PendingCount + " 页" : ""; }
        }

        ICommand _validateOcrFilesCommand;
        public ICommand ValidateOcrFilesCommand
        {
            get
            {
                return _validateOcrFilesCommand ?? (_validateOcrFilesCommand = new RelayCommand(() =>
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "选择图片自动转正（生成副本）",
                        Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff",
                        Multiselect = true
                    };
                    if (dialog.ShowDialog() == true)
                    {
                        var folder = Path.Combine(_scanRootFolder, "自动转正", DateTime.Now.ToString("yyyyMMdd-HHmmss")
                            + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        Directory.CreateDirectory(folder);
                        _currentScanFolder = folder;
                        RaisePropertyChanged(() => ScanSaveFolderPath);
                        foreach (var path in dialog.FileNames)
                        {
                            var copy = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jpg");
                            ScanImageProcessingHelper.CreateJpegCopy(path, copy);
                            QueueScanItem(copy, true);
                        }
                    }
                }, () => State <= 4));
            }
        }

        public TwainVM()
        {
            DataSources = new ObservableCollection<DataSourceVM>();
            CapturedImages = new ObservableCollection<CapturedScanItem>();
            _scanRootFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NTwain Scans");
            Directory.CreateDirectory(_scanRootFolder);
            _ocrValidator.PendingCountChanged += () =>
            {
                if (!App.Current.Dispatcher.HasShutdownStarted)
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        RaisePropertyChanged(() => IsOrientationBusy);
                        RaisePropertyChanged(() => OrientationProgress);
                    }));
            };

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
                if (ReferenceEquals(_selectedSource, value))
                    return;
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
                    return _session.State == 4 && SelectedSource != null && SelectedSource.SupportsDriverUIOnly;
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
                            AppLog.Info("Scan build=20261016-huagao-crop-border, exe={0}, library={1}",
                                Assembly.GetEntryAssembly().Location, typeof(TwainSession).Assembly.Location);
                            ScanPaperSize selectedSize;
                            if (!TryPromptScanSize(out selectedSize))
                            {
                                AppLog.Info("已取消本次扫描");
                                return;
                            }

                            var preparationTimer = Stopwatch.StartNew();
                            var src = _session.CurrentSource;
                            AppLog.Info("开始扫描 source={0} manufacturer={1} size={2} showUI={3} state={4} newDsm={5}",
                                src.Name, src.Manufacturer, selectedSize, ShowUI, _session.State, NTwain.PlatformInfo.Current.UseNewWinDSM);
                            _needCancel = false;
                            _selectedScanPaperSize = selectedSize;
                            _captureUsesDriverUi = ShowUI;
                            BeginScanSession(src);
                            var restoredEpsonProfile = !_captureUsesDriverUi
                                && selectedSize == ScanPaperSize.A4L && IsWfC21000(src)
                                && TryRestoreEpsonA4LSettings(src);
                            if (!restoredEpsonProfile)
                                TryEnsureFileTransfer(src);
                            if (_captureUsesDriverUi)
                            {
                                AppLog.Info("驱动 UI 对照扫描：由驱动选择进纸、单双面和扫描区域；结束后记录实际参数");
                                _capSnapshot = ReadCapCurrents(src);
                                AppLog.Info("已记录界面打开前的能力当前值，共 {0} 项", _capSnapshot.Count);
                            }
                            else if (IsEpsonFullAreaLandscape(src, selectedSize))
                            {
                                if (!restoredEpsonProfile)
                                    TryForceFeederScan(src);
                                TryApplyEpsonA4LFullArea(src);
                                TryForceDuplexScan(src);
                            }
                            else if (IsKonicaFullAreaLandscape(src, selectedSize))
                            {
                                TryForceFeederScan(src);
                                TryForceDuplexScan(src);
                                TryApplyKonicaLandscapeFullArea(src);
                            }
                            else if (!restoredEpsonProfile)
                            {
                                TryForceFeederScan(src);
                                TryForceDuplexScan(src);
                                TryForceScanSize(src, selectedSize);
                                TryApplyHuagaoG300NoUi(src);
                            }
                            AppLog.Info("Begin duplex state read");
                            _isDuplexScan = IsDuplexActive(src);
                            AppLog.Info("End duplex state read: {0}", _isDuplexScan);

                            if (!ShowUI && !restoredEpsonProfile && !IsEpsonFullAreaLandscape(src, selectedSize))
                            {
                                AppLog.Info("Begin CapIndicators configuration");
                                var capIndicators = src.Capabilities.CapIndicators;
                                if (capIndicators.IsSupported && capIndicators.CanSet)
                                {
                                    var indicatorRc = capIndicators.SetValue(BoolType.False);
                                    AppLog.Info("End CapIndicators SET: {0}", indicatorRc);
                                }
                            }

                            AppLog.Info("Pre-enable configuration complete, 耗时={0} ms", preparationTimer.ElapsedMilliseconds);
                            var mode = ShowUI ? SourceEnableMode.ShowUI : SourceEnableMode.NoUI;
                            AppLog.Info("调用 Enable, mode={0}, hwnd=0x{1:X}", mode, WindowHandle.ToInt64());
                            preparationTimer.Restart();
                            var rc = src.Enable(mode, true, WindowHandle);
                            AppLog.Info("驱动 Enable 耗时: {0} ms", preparationTimer.ElapsedMilliseconds);
                            AppLog.Info("Enable 返回 {0}, state={1}", rc, _session.State);
                            if (rc != ReturnCode.Success)
                            {
                                var status = src.GetStatus();
                                AppLog.Info("Enable failed: ReturnCode={0}, ConditionCode={1}", rc, status.ConditionCode);
                                ShowScanMessage("扫描没有启动", "Enable 返回 " + rc + "，驱动错误码=" + status.ConditionCode + "。详情见日志。");
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
        /// 自动检测不能沿用上一次的固定纸张。16KL 会把纸张留在 A3，下一次自动检测就会扫出整张 A3。
        /// </summary>
        static void TryApplyAutoPaperDetection(DataSource src)
        {
            try
            {
                var caps = src.Capabilities;
                var sizes = caps.ICapSupportedSizes;
                if (sizes.IsSupported && sizes.CanSet)
                    AppLog.Info("自动检测 SupportedSizes=None: {0}", sizes.SetValue(SupportedSize.None));

                if (caps.ICapAutoSize.IsSupported && caps.ICapAutoSize.CanSet)
                    AppLog.Info("自动检测 ICapAutoSize=Auto: {0}", caps.ICapAutoSize.SetValue(AutoSize.Auto));

                if (caps.ICapAutomaticBorderDetection.IsSupported && caps.ICapAutomaticBorderDetection.CanSet)
                    AppLog.Info("自动检测消除黑边=True: {0}", caps.ICapAutomaticBorderDetection.SetValue(BoolType.True));

                if (caps.ICapUndefinedImageSize.IsSupported && caps.ICapUndefinedImageSize.CanSet)
                    AppLog.Info("自动检测 UndefinedImageSize=True: {0}", caps.ICapUndefinedImageSize.SetValue(BoolType.True));
            }
            catch (Exception ex)
            {
                AppLog.Info("自动检测纸张设置失败: {0}", ex.Message);
            }
        }

        /// <summary>
        /// 在 Enable 之前强制扫描尺寸：优先 ICAP_SUPPORTEDSIZES，失败则用 ImageLayout 指定物理区域。
        /// </summary>
        static void TryForceScanSize(DataSource src, ScanPaperSize size)
        {
            if (src == null)
                return;

            if (size == ScanPaperSize.Auto)
            {
                TryApplyAutoPaperDetection(src);
                return;
            }

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

                // 关掉会覆盖固定纸张尺寸的自动检测/裁边。
                // G100 的物理宽度只有 10 英寸，横向框写不进去；界面扫描只改方向并保持不定尺寸。
                var g100 = ScannerOrientationHelper.IsHuagaoG100(src);
                if (g100)
                {
                    TrySetCap(caps.ICapUndefinedImageSize, BoolType.True);
                    AppLog.Info("G100 保持不定图像尺寸，由扫描方向决定出图");
                }
                else
                {
                    TrySetCap(caps.ICapUndefinedImageSize, BoolType.False);
                }
                TrySetCap(caps.ICapAutomaticBorderDetection, BoolType.False);
                TrySetCap(caps.ICapAutoSize, AutoSize.None);

                if (!IsEpson(src) && !g100)
                    TryApplyScanOrientation(src, applySize);

                if (IsEpson(src))
                {
                    TryApplyEpsonScanArea(src, applySize, label, ScannerOrientationHelper.UsesEpsonA4LandscapeAcquisition(src, size), size == ScanPaperSize.A4L && IsWfC21000(src));
                    return;
                }

                if (ScannerOrientationHelper.IsKonica(src))
                {
                    TryApplyKonicaPaperFrame(src, applySize, label);
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
                else if (g100)
                {
                    var sizes = caps.ICapSupportedSizes;
                    if (sizes.IsSupported && sizes.CanSet)
                        AppLog.Info("G100 {0} SupportedSizes=None: {1}", label, sizes.SetValue(SupportedSize.None));
                    if (caps.ICapAutomaticBorderDetection.IsSupported && caps.ICapAutomaticBorderDetection.CanSet)
                        AppLog.Info("G100 {0} 消除黑边=True: {1}", label, caps.ICapAutomaticBorderDetection.SetValue(BoolType.True));
                }

                if (g100)
                {
                    TryApplyScanOrientation(src, applySize);
                    var rotation = caps.ICapRotation;
                    if (rotation.IsSupported && rotation.CanSet)
                        AppLog.Info("G100 ICAP_ROTATION=0: {0}", rotation.SetValue((TWFix32)0f));
                    AppLog.Info("G100 不设置 ImageLayout：物理宽度 10 英寸，横向框会被拒绝");
                    return;
                }

                if (TrySetImageLayout(src, applySize))
                    TryApplyScanOrientation(src, applySize);
            }
            catch (Exception ex)
            {
                AppLog.Info("强制 {0} 扫描尺寸失败: {1}", size.GetDisplayName(), ex.Message);
            }
        }

        static void TryApplyHuagaoG300NoUi(DataSource src)
        {
            if (!ScannerOrientationHelper.IsHuagaoG300(src))
                return;

            var caps = src.Capabilities;
            var border = caps.ICapAutomaticBorderDetection;
            if (border.IsSupported && border.CanSet)
                AppLog.Info("G300 消除黑框=True: {0}", border.SetValue(BoolType.True));
            else
                AppLog.Info("G300 不支持设置消除黑框");

            var rotation = caps.ICapRotation;
            if (rotation.IsSupported && rotation.CanSet)
            {
                var rc = rotation.SetValue((TWFix32)0f);
                var current = rotation.CanGetCurrent ? rotation.GetCurrent().ToString() : "?";
                AppLog.Info("G300 ICAP_ROTATION=0: {0}, 当前={1}，最终方向由整页分类处理", rc, current);
            }
            else
                AppLog.Info("G300 不支持设置 ICAP_ROTATION");
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
                    AppLog.Info("强制双面扫描 (CapDuplexEnabled): {0}", rc);
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
        static void TryApplyEpsonScanArea(DataSource src, ScanPaperSize size, string label, bool useDriverPaperArea, bool horizontalPaper)
        {
            if (horizontalPaper)
            {
                TryApplyEpsonA4LUiGeometry(src);
                return;
            }
            var autoRotate = src.Capabilities.ICapAutomaticRotate;
            if (autoRotate.IsSupported && autoRotate.CanSet)
            {
                var rc = autoRotate.SetValue(BoolType.False);
                AppLog.Info("Epson ICapAutomaticRotate=False: {0}", rc);
                if (rc == ReturnCode.Failure)
                    AppLog.Info("ICapAutomaticRotate error: {0}", src.GetStatus().ConditionCode);
            }
            TrySetSupportedPaperSize(src, size, label);
            if (useDriverPaperArea)
            {
                var orientation = src.Capabilities.ICapOrientation;
                if (orientation.IsSupported && orientation.CanSet)
                {
                    var rc = orientation.SetValue(OrientationType.Rot0);
                    AppLog.Info("WF-C21000 physical paper orientation Rot0: {0}, current={1}", rc, orientation.GetCurrent());
                    if (rc == ReturnCode.Failure)
                        AppLog.Info("Orientation error: {0}", src.GetStatus().ConditionCode);
                }
            }
            else
                TryApplyScanOrientation(src, size);

            var rotation = src.Capabilities.ICapRotation;
            if (rotation.IsSupported && rotation.CanSet)
            {
                var rotationRc = rotation.SetValue((TWFix32)0f);
                AppLog.Info("Epson ICapRotation=0: {0}", rotationRc);
                if (rotationRc == ReturnCode.Failure)
                    AppLog.Info("ICapRotation error: {0}", src.GetStatus().ConditionCode);
            }

            if (!TrySetFrame(src, size, false))
                AppLog.Info("爱普生按当前单位设置扫描区域失败");


            if (AppLog.Instance.IsDebugEnabled || size == ScanPaperSize.K16 || size == ScanPaperSize.K16L)
                LogScanGeometry(src);
        }

        static void TryApplyEpsonA4LUiGeometry(DataSource src)
        {
            // Values measured after a successful WF-C21000 A4L scan through its own UI.
            // Avoid rewriting an already-matching capability: Epson's UI also maintains
            // private state which a redundant TWAIN SET may reset.
            var caps = src.Capabilities;
            if (caps.ICapAutomaticRotate.IsSupported && caps.ICapAutomaticRotate.CanSet
                && (!caps.ICapAutomaticRotate.CanGetCurrent || caps.ICapAutomaticRotate.GetCurrent() != BoolType.False))
                AppLog.Info("WF-C21000 A4L AutomaticRotate=False: {0}", caps.ICapAutomaticRotate.SetValue(BoolType.False));

            var sizes = caps.ICapSupportedSizes;
            if (sizes.IsSupported && sizes.CanSet
                && (!sizes.CanGetCurrent || sizes.GetCurrent() != SupportedSize.A4))
                AppLog.Info("WF-C21000 A4L SupportedSizes=A4: {0}", sizes.SetValue(SupportedSize.A4));

            var orientation = caps.ICapOrientation;
            if (orientation.IsSupported && orientation.CanSet
                && (!orientation.CanGetCurrent || orientation.GetCurrent() != OrientationType.Rot0))
                AppLog.Info("WF-C21000 A4L Orientation=Rot0: {0}", orientation.SetValue(OrientationType.Rot0));

            var rotation = caps.ICapRotation;
            if (rotation.IsSupported && rotation.CanSet
                && (!rotation.CanGetCurrent || Math.Abs((float)rotation.GetCurrent()) > 0.01f))
                AppLog.Info("WF-C21000 A4L Rotation=0: {0}", rotation.SetValue((TWFix32)0f));

            var units = caps.ICapUnits;
            if (units.IsSupported && units.CanSet
                && (!units.CanGetCurrent || units.GetCurrent() != Unit.Inches))
                AppLog.Info("WF-C21000 A4L Units=Inches: {0}", units.SetValue(Unit.Inches));

            var frames = caps.ICapFrames;
            if (frames.IsSupported && frames.CanSet)
            {
                var matchesUiFrame = false;
                if (frames.CanGetCurrent)
                {
                    var current = frames.GetCurrent();
                    matchesUiFrame = Math.Abs((float)current.Left) < 0.01f
                        && Math.Abs((float)current.Top) < 0.01f
                        && Math.Abs((float)current.Right - 11.69f) < 0.01f
                        && Math.Abs((float)current.Bottom - 8.27f) < 0.01f;
                    AppLog.Info("WF-C21000 A4L current frame={0}, matches driver UI={1}", current, matchesUiFrame);
                }
                if (!matchesUiFrame)
                {
                    var uiFrame = new TWFrame { Left = 0, Top = 0, Right = 11.69f, Bottom = 8.27f };
                    AppLog.Info("WF-C21000 A4L set driver UI frame {0}: {1}", uiFrame, frames.SetValue(uiFrame));
                }
            }
            LogScanGeometry(src);
        }

        static string EpsonA4LSettingsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NTwain", "profiles", "epson-wf-c21000-a4l.dsdata");
            }
        }

        void TrySaveEpsonA4LSettings(DataSource src)
        {
            if (!IsWfC21000(src) || _lastImageWidth <= _lastImageHeight || _lastImageWidth == 0)
                return;
            try
            {
                var frames = src.Capabilities.ICapFrames;
                var sizes = src.Capabilities.ICapSupportedSizes;
                if (!frames.IsSupported || !frames.CanGetCurrent || !sizes.IsSupported || !sizes.CanGetCurrent
                    || sizes.GetCurrent() != SupportedSize.A4)
                    return;
                var frame = frames.GetCurrent();
                if (Math.Abs((float)frame.Left) > 0.01f || Math.Abs((float)frame.Top) > 0.01f
                    || Math.Abs((float)frame.Right - 11.69f) > 0.01f
                    || Math.Abs((float)frame.Bottom - 8.27f) > 0.01f)
                    return;

                var custom = src.Capabilities.CapCustomDSData;
                if (!custom.IsSupported || !custom.CanGetCurrent || custom.GetCurrent() != BoolType.True)
                {
                    AppLog.Info("WF-C21000 does not expose CustomDSData; driver UI settings cannot be saved");
                    return;
                }
                var settings = src.Settings;
                if (settings == null || settings.Length == 0 || settings.Length > 4 * 1024 * 1024)
                {
                    AppLog.Info("WF-C21000 CustomDSData unavailable or unexpected length: {0}", settings == null ? 0 : settings.Length);
                    return;
                }
                var path = EpsonA4LSettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, settings);
                AppLog.Info("WF-C21000 A4L driver UI settings saved: {0}, bytes={1}", path, settings.Length);
            }
            catch (Exception ex) { AppLog.Info("WF-C21000 A4L driver UI settings capture failed: {0}", ex); }
        }

        static bool TryRestoreEpsonA4LSettings(DataSource src)
        {
            var path = EpsonA4LSettingsPath;
            if (!File.Exists(path))
            {
                AppLog.Info("WF-C21000 A4L driver UI settings profile not present: {0}", path);
                return false;
            }
            try
            {
                var custom = src.Capabilities.CapCustomDSData;
                if (!custom.IsSupported || !custom.CanGetCurrent || custom.GetCurrent() != BoolType.True)
                {
                    AppLog.Info("WF-C21000 CustomDSData is not available for restore");
                    return false;
                }
                var settings = File.ReadAllBytes(path);
                if (settings.Length == 0 || settings.Length > 4 * 1024 * 1024)
                    return false;
                src.Settings = settings;
                AppLog.Info("WF-C21000 A4L driver UI settings restored: bytes={0}", settings.Length);
                var readback = src.Settings;
                AppLog.Info("WF-C21000 A4L private settings readback: bytes={0}, exactMatch={1}",
                    readback == null ? 0 : readback.Length,
                    readback != null && settings.SequenceEqual(readback));
                LogScanGeometry(src);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Info("WF-C21000 A4L driver UI settings restore failed: {0}", ex);
                return false;
            }
        }

        static bool IsKonicaFullAreaLandscape(DataSource src, ScanPaperSize size)
        {
            return ScannerOrientationHelper.IsKonica(src)
                && (size == ScanPaperSize.A4L || size == ScanPaperSize.K16L);
        }

        static void TryApplyKonicaPaperFrame(DataSource src, ScanPaperSize size, string label)
        {
            // 驱动回报的物理幅面停在 8.5 x 11 英寸，按它裁会把 8K 收成 A3。
            // 与爱普生一样按纸张毫米数写扫描区域，不跟这个上限裁。
            var dims = size.GetDimensionsMm();
            float width = dims.WidthMm / 25.4f;
            float height = dims.HeightMm / 25.4f;
            var sizes = src.Capabilities.ICapSupportedSizes;
            if (sizes.IsSupported && sizes.CanSet)
            {
                var twainSize = GetTwainSupportedSize(size);
                if (twainSize.HasValue)
                    AppLog.Info("Konica {0}: SupportedSizes={1}: {2}", label, twainSize.Value, sizes.SetValue(twainSize.Value));
                else
                    AppLog.Info("Konica {0}: SupportedSizes=None: {1}", label, sizes.SetValue(SupportedSize.None));
            }

            var frame = new TWFrame { Left = 0, Top = 0, Right = width, Bottom = height };
            TWImageLayout layout;
            src.DGImage.ImageLayout.Get(out layout);
            layout.Frame = frame;
            AppLog.Info("Konica {0} ImageLayout Inches: {1}, Frame={2}", label, src.DGImage.ImageLayout.Set(layout), frame);
            var frames = src.Capabilities.ICapFrames;
            if (frames.IsSupported && frames.CanSet)
                AppLog.Info("Konica {0} ICapFrames: {1}", label, frames.SetValue(frame));
        }

        static void TryApplyKonicaLandscapeFullArea(DataSource src)
        {
            try
            {
                AppLog.Info("Konica landscape: acquire full area before cropping");
                var sizes = src.Capabilities.ICapSupportedSizes;
                if (sizes.IsSupported && sizes.CanSet)
                    AppLog.Info("Full-area SupportedSizes=A3: {0}", sizes.SetValue(SupportedSize.A3));
                var orientation = src.Capabilities.ICapOrientation;
                if (orientation.IsSupported && orientation.CanSet)
                    AppLog.Info("Full-area Orientation=Rot0: {0}", orientation.SetValue(OrientationType.Rot0));
                TWImageLayout layout;
                src.DGImage.ImageLayout.Get(out layout);
                layout.Frame = new TWFrame { Left = 0, Top = 0, Right = 11.69f, Bottom = 17f };
                AppLog.Info("Full-area ImageLayout={0}: {1}", layout.Frame, src.DGImage.ImageLayout.Set(layout));
            }
            catch (Exception ex) { AppLog.Info("Konica full-area configuration failed: {0}", ex); }
        }

        static void TryApplyEpsonA4LFullArea(DataSource src)
        {
            try
            {
                AppLog.Info("WF-C21000 A4L: acquire full area before cropping to A4L");
                var sizes = src.Capabilities.ICapSupportedSizes;
                if (sizes.IsSupported && sizes.CanSet)
                    AppLog.Info("Full-area SupportedSizes=A3: {0}", sizes.SetValue(SupportedSize.A3));
                var units = src.Capabilities.ICapUnits;
                if (units.IsSupported && units.CanSet && units.GetCurrent() != Unit.Inches)
                    AppLog.Info("Full-area Units=Inches: {0}", units.SetValue(Unit.Inches));
                var orientation = src.Capabilities.ICapOrientation;
                if (orientation.IsSupported && orientation.CanSet)
                    AppLog.Info("Full-area Orientation=Rot0: {0}", orientation.SetValue(OrientationType.Rot0));
                var frames = src.Capabilities.ICapFrames;
                if (frames.IsSupported && frames.CanSet)
                {
                    var frame = new TWFrame { Left = 0, Top = 0, Right = 11.69f, Bottom = 17f };
                    AppLog.Info("Full-area Frames={0}: {1}", frame, frames.SetValue(frame));
                }
                LogScanGeometry(src);
            }
            catch (Exception ex) { AppLog.Info("WF-C21000 A4L full-area configuration failed: {0}", ex); }
        }

        static void TrySetSupportedPaperSize(DataSource src, ScanPaperSize size, string label)
        {
            SupportedSize? twainSize = GetTwainSupportedSize(size);
            if (!twainSize.HasValue)
                return;

            var sizes = src.Capabilities.ICapSupportedSizes;
            if (!sizes.IsSupported || !sizes.CanSet)
                return;

            var rc = sizes.SetValue(twainSize.Value);
            AppLog.Info("强制扫描尺寸 {0} (ICapSupportedSizes): {1}", label, rc);
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
            if (rc == ReturnCode.Failure)
                AppLog.Info("ICAP_FRAMES SET failed: {0}", src.GetStatus().ConditionCode);
            AppLog.Info("强制扫描尺寸 {0} (ICapFrames {1}): {2}, Frame={3}", size.GetDisplayName(), unit, rc, frame);
            return rc == ReturnCode.Success;
        }

        static void LogScanGeometry(DataSource src)
        {
            LogCurrentCapability("Units", src.Capabilities.ICapUnits);
            LogCurrentCapability("Orientation", src.Capabilities.ICapOrientation);
            LogCurrentCapability("AutomaticRotate", src.Capabilities.ICapAutomaticRotate);
            LogCurrentCapability("Rotation", src.Capabilities.ICapRotation);
            LogCurrentCapability("SupportedSizes", src.Capabilities.ICapSupportedSizes);
            LogCurrentCapability("Frames", src.Capabilities.ICapFrames);
            LogCurrentCapability("PhysicalWidth", src.Capabilities.ICapPhysicalWidth);
            LogCurrentCapability("PhysicalHeight", src.Capabilities.ICapPhysicalHeight);
            LogCurrentCapability("FeederEnabled", src.Capabilities.CapFeederEnabled);
            LogCurrentCapability("FeederAlignment", src.Capabilities.CapFeederAlignment);
            LogCurrentCapability("DuplexEnabled", src.Capabilities.CapDuplexEnabled);
            LogCurrentCapability("XferCount", src.Capabilities.CapXferCount);
            LogCurrentCapability("AutoSize", src.Capabilities.ICapAutoSize);
            LogCurrentCapability("UndefinedImageSize", src.Capabilities.ICapUndefinedImageSize);
            LogCurrentCapability("AutomaticBorderDetection", src.Capabilities.ICapAutomaticBorderDetection);
            LogCurrentCapability("XResolution", src.Capabilities.ICapXResolution);
            LogCurrentCapability("YResolution", src.Capabilities.ICapYResolution);
            LogCurrentCapability("XferMech", src.Capabilities.ICapXferMech);
            LogCurrentCapability("CustomDSData", src.Capabilities.CapCustomDSData);
            // Epson: avoid DAT_IMAGELAYOUT/MSG_GET in pre-enable diagnostics. Read ICAP_FRAMES above instead.
        }

        static void LogCurrentCapability<T>(string name, IReadOnlyCapWrapper<T> cap)
        {
            try
            {
                if (cap.IsSupported && cap.CanGetCurrent)
                    AppLog.Info("Scan geometry: {0}={1}", name, cap.GetCurrent());
            }
            catch (Exception ex) { AppLog.Info("{0} GET failed: {1}", name, ex.Message); }
        }

        static float GetFrameLeft(DataSource src, float maxWidth, float width)
        {
            var feeder = src.Capabilities.CapFeederEnabled;
            if (!feeder.IsSupported || !feeder.CanGetCurrent || feeder.GetCurrent() != BoolType.True)
                return 0;
            var alignCap = src.Capabilities.CapFeederAlignment;
            FeederAlignment align = FeederAlignment.None;
            if (alignCap.IsSupported && alignCap.CanGetCurrent)
            {
                align = alignCap.GetCurrent();
                AppLog.Info("进纸对齐: {0}", align);
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

        static bool IsEpsonFullAreaLandscape(DataSource src, ScanPaperSize size)
        {
            return IsWfC21000(src) && (size == ScanPaperSize.A4L || size == ScanPaperSize.K16L);
        }

        static bool IsWfC21000(DataSource src)
        {
            return src != null && src.Name.IndexOf("WF-C21000", StringComparison.OrdinalIgnoreCase) >= 0
                && IsEpson(src);
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

        Dictionary<int, string> _capSnapshot;

        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        static Dictionary<int, string> ReadCapCurrents(DataSource src)
        {
            var map = new Dictionary<int, string>();
            foreach (var id in src.Capabilities.CapSupportedCaps.GetValues())
            {
                var key = (int)id;
                if (key >= 0x8000)
                {
                    map[key] = "<跳过自定义能力，避免驱动卡死>";
                    continue;
                }
                var support = src.Capabilities.QuerySupport(id);
                if ((support & QuerySupports.GetCurrent) == 0)
                {
                    map[key] = "<不支持读取当前值>";
                    continue;
                }
                AppLog.Info("读取能力 0x{0:X4}", key);
                try
                {
                    var value = src.Capabilities.GetCurrent(id);
                    map[key] = value == null ? "<null>" : value.ToString();
                }
                catch (Exception ex)
                {
                    map[key] = "读取失败: " + ex.GetType().Name;
                    AppLog.Info("读取能力 0x{0:X4} 失败并停止后续读取: {1}", key, ex.GetType().Name);
                    break;
                }
            }
            return map;
        }

        void LogCapChanges(DataSource src)
        {
            var after = ReadCapCurrents(src);
            if (_capSnapshot == null)
            {
                AppLog.Info("没有界面打开前的能力快照，无法对比");
                return;
            }
            var changed = 0;
            foreach (var pair in after)
            {
                string before;
                var name = Enum.IsDefined(typeof(CapabilityId), (ushort)pair.Key)
                    ? ((CapabilityId)(ushort)pair.Key).ToString()
                    : "自定义";
                if (!_capSnapshot.TryGetValue(pair.Key, out before))
                {
                    changed++;
                    AppLog.Info("能力新增 0x{0:X4} {1} = {2}", pair.Key, name, pair.Value);
                }
                else if (before != pair.Value)
                {
                    changed++;
                    AppLog.Info("能力变化 0x{0:X4} {1}: {2} -> {3}", pair.Key, name, before, pair.Value);
                }
            }
            AppLog.Info("能力对比结束，变化 {0} 项", changed);
            _capSnapshot = null;
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
                    if (_captureUsesDriverUi)
                    {
                        AppLog.Info("驱动 UI 扫描结束，TWAIN state={0}；以下为驱动界面应用后的参数", _session.State);
                        if (_session.State == 4 && _session.CurrentSource != null)
                        {
                            LogScanGeometry(_session.CurrentSource);
                            LogCapChanges(_session.CurrentSource);
                            TrySaveEpsonA4LSettings(_session.CurrentSource);
                        }
                        else
                            AppLog.Info("驱动 UI 参数未读取：数据源尚未返回 state 4");
                        _captureUsesDriverUi = false;
                    }
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
            var folderName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var folder = Path.Combine(_scanRootFolder, folderName);
            for (var suffix = 2; Directory.Exists(folder); suffix++)
                folder = Path.Combine(_scanRootFolder, folderName + "-" + suffix);
            _currentScanFolder = folder;
            Directory.CreateDirectory(_currentScanFolder);
            _transferCount = 0;
            _isDuplexScan = false;
            _lastImageWidth = 0;
            _lastImageHeight = 0;
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

            var rc = xferMech.SetValue(XferMech.File);
            AppLog.Info("设置传输模式 File: {0}", rc);
        }

        void LogReceivedImageInfo(DataTransferredEventArgs e)
        {
            var transferIndex = _transferCount + 1;
            try
            {
                var image = e.ImageInfo;
                if (image != null)
                {
                    _lastImageWidth = image.ImageWidth;
                    _lastImageHeight = image.ImageLength;
                    AppLog.Info("Raw image: {0} x {1} pixels, resolution={2} x {3}",
                        image.ImageWidth, image.ImageLength, image.XResolution, image.YResolution);
                }
                AppLog.Info("收到图像: 第 {0} 次传输", transferIndex);
            }
            catch (Exception ex)
            {
                AppLog.Info("收到图像: 第 {0} 次传输, 读取图像信息失败: {1}", transferIndex, ex.Message);
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

                savedPath = ScanImageProcessingHelper.EnsureJpeg(savedPath);
                if (!_captureUsesDriverUi)
                {
                    ScanImageProcessingHelper.PrepareScanGeometry(savedPath, _selectedScanPaperSize, _session.CurrentSource);
                }
                LogSavedImageSize(savedPath);
                AppLog.Info("扫描图片已保存: {0}", savedPath);
                ScanPaperSize? restoreCanvas = !_captureUsesDriverUi
                    && ScannerOrientationHelper.UsesEpsonA4LandscapeAcquisition(_session.CurrentSource, _selectedScanPaperSize)
                    ? (ScanPaperSize?)_selectedScanPaperSize : null;
                QueueScanItem(savedPath, false, restoreCanvas);
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

        void QueueScanItem(string savedPath, bool forceOcr = false, ScanPaperSize? restoreCanvas = null)
        {
            var item = new CapturedScanItem { FilePath = savedPath };
            if (forceOcr || IsOcrValidationEnabled)
            {
                item.OcrStatus = "等待自动转正…";
                if (!_ocrValidator.TryQueue(savedPath, status =>
                {
                    if (!App.Current.Dispatcher.HasShutdownStarted)
                        App.Current.Dispatcher.BeginInvoke(new Action(() => item.OcrStatus = status));
                }, restoreCanvas, () => RefreshScanThumbnail(item)))
                {
                    item.OcrStatus = "自动转正未启动（保留图片）";
                    AppLog.Info("自动转正处理器已停止: {0}", savedPath);
                    Task.Run(() => RefreshScanThumbnail(item));
                }
            }
            else Task.Run(() => RefreshScanThumbnail(item));
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                CapturedImages.Add(item);
            }));

        }

        void RefreshScanThumbnail(CapturedScanItem item)
        {
            var thumb = LoadThumbnailFromFile(item.FilePath, (int)MaxThumbnailSize);
            if (thumb == null) return;

            if (!App.Current.Dispatcher.HasShutdownStarted) App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                item.Thumbnail = thumb;
            }));
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
                img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
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
            _ocrValidator.Dispose();
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
                if (_validateOcrFilesCommand != null)
                    ((RelayCommand)_validateOcrFilesCommand).RaiseCanExecuteChanged();
            }));
        }
    }
}
