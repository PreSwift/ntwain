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
using System.Runtime.ExceptionServices;
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
        ScannerProfile _scanProfile;
        ScanApplyResult _scanApply = new ScanApplyResult();
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
                        var rc = EnableWhileUiPumps(() =>
                            _session.CurrentSource.Enable(SourceEnableMode.ShowUIOnly, false, WindowHandle));
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
                            AppLog.Info("Scan build=20261016-canon-skip-feederloaded, exe={0}, library={1}",
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
                            _scanApply = new ScanApplyResult();
                            _scanProfile = ScannerProfile.Match(src);
                            BeginScanSession(src);
                            var restoredDriverProfile = !_captureUsesDriverUi
                                && _scanProfile.TryRestoreDriverProfile(src, selectedSize);
                            if (!restoredDriverProfile)
                                TryEnsureFileTransfer(src);
                            if (_captureUsesDriverUi)
                            {
                                AppLog.Info("驱动 UI 对照扫描：由驱动选择进纸、单双面和扫描区域；结束后记录实际参数");
                                _capSnapshot = ReadCapCurrents(src);
                                AppLog.Info("已记录界面打开前的能力当前值，共 {0} 项", _capSnapshot.Count);
                            }
                            else
                            {
                                _scanApply = _scanProfile.Apply(src, selectedSize);
                                _scanApply.RestoredDriverProfile = restoredDriverProfile;
                            }
                            AppLog.Info("Begin duplex state read");
                            _isDuplexScan = IsDuplexActive(src);
                            AppLog.Info("End duplex state read: {0}", _isDuplexScan);

                            AppLog.Info("Pre-enable configuration complete, 耗时={0} ms", preparationTimer.ElapsedMilliseconds);
                            var mode = ShowUI ? SourceEnableMode.ShowUI : SourceEnableMode.NoUI;
                            AppLog.Info("调用 Enable, mode={0}, hwnd=0x{1:X}", mode, WindowHandle.ToInt64());
                            preparationTimer.Restart();
                            var rc = ShowUI
                                ? EnableWhileUiPumps(() => src.Enable(mode, true, WindowHandle))
                                : src.Enable(mode, true, WindowHandle);
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
                            ShowScanMessage("扫描没有启动", ex.Message);
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

        /// <summary>
        /// 佳能等驱动会把界面挂到主窗口上，并等待主窗口处理消息。
        /// Enable 若直接堵在界面线程上，驱动窗口出不来，程序看起来就像卡住。
        /// </summary>
        static ReturnCode EnableWhileUiPumps(Func<ReturnCode> enable)
        {
            var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
            if (dispatcher == null || !dispatcher.CheckAccess())
                return enable();

            ReturnCode rc = ReturnCode.Failure;
            Exception error = null;
            using (var done = new ManualResetEvent(false))
            {
                var worker = new Thread(() =>
                {
                    try
                    {
                        rc = enable();
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                });
                worker.IsBackground = true;
                worker.Name = "TWAIN Enable";
                worker.Start();

                var frame = new DispatcherFrame();
                ThreadPool.RegisterWaitForSingleObject(done, (state, timedOut) =>
                {
                    dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
                }, null, Timeout.Infinite, true);
                Dispatcher.PushFrame(frame);
            }

            if (error != null)
                ExceptionDispatchInfo.Capture(error).Throw();
            return rc;
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
                // 佳能 MF750 对 CAP_FEEDERLOADED 的 GetCurrent 会抛 AccessViolation，
                // 之后同一条 DSM 调用不再返回，界面就停在「Begin duplex state read」。
                if (key == (int)CapabilityId.CapFeederLoaded)
                {
                    map[key] = "<跳过，佳能驱动读取此项会访问冲突并卡死>";
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
                            if (_scanProfile != null)
                                _scanProfile.TrySaveDriverProfile(_session.CurrentSource, _lastImageWidth, _lastImageHeight);
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
                if (!_captureUsesDriverUi && !_scanApply.UsedDimensionFallback)
                {
                    ScanImageProcessingHelper.PrepareScanGeometry(
                        savedPath, _selectedScanPaperSize, _scanApply.CropAfterScan, _scanApply.CropMargins);
                }
                LogSavedImageSize(savedPath);
                AppLog.Info("扫描图片已保存: {0}", savedPath);
                ScanPaperSize? restoreCanvas = !_captureUsesDriverUi && !_scanApply.UsedDimensionFallback && _scanApply.RestoreCanvas
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
