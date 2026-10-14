using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using ModernWpf.Messages;
using NTwain;
using NTwain.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;

namespace Sample.WPF
{
    /// <summary>
    /// Wraps a data source as view model.
    /// </summary>
    class DataSourceVM : ViewModelBase
    {
        public DataSource DS { get; set; }

        // WPF command requery must never enter a TWAIN driver (including during native dialogs).
        public bool SupportsDriverUIOnly { get; private set; }

        public string Name { get { return DS.Name; } }
        public string Version { get { return DS.Version.Info; } }
        public string Protocol { get { return DS.ProtocolVersion.ToString(); } }

        ICollectionView _capView;
        public DataSourceVM()
        {
            Caps = new ObservableCollection<CapVM>();
            _capView = CollectionViewSource.GetDefaultView(Caps);
            _capView.SortDescriptions.Add(new System.ComponentModel.SortDescription("Name", System.ComponentModel.ListSortDirection.Ascending));
            _capView.Filter = FilterCapRoutine;
        }


        private bool FilterCapRoutine(object obj)
        {
            if (!string.IsNullOrWhiteSpace(CapFilter))
            {
                var vm = obj as CapVM;
                if (vm != null)
                {
                    return vm.Name.IndexOf(CapFilter, System.StringComparison.OrdinalIgnoreCase) > -1;
                }
            }
            return true;
        }

        public void Open()
        {
            AppLog.Info("打开数据源: {0} / {1}", DS.Manufacturer, DS.Name);
            SupportsDriverUIOnly = false;
            Caps.Clear();
            var timer = Stopwatch.StartNew();
            var rc = DS.Open();
            AppLog.Info("驱动 Open 耗时: {0} ms, 结果={1}", timer.ElapsedMilliseconds, rc);
            timer.Restart();
            //rc = DGControl.Status.Get(dsId, ref stat);
            if (rc == ReturnCode.Success)
            {
                AppLog.Info("开始读取驱动能力列表");
                var supportedCaps = DS.Capabilities.CapSupportedCaps.GetValues().ToList();
                AppLog.Info("读取驱动能力列表耗时: {0} ms, 能力数={1}", timer.ElapsedMilliseconds, supportedCaps.Count);
                foreach (var cap in supportedCaps)
                    Caps.Add(new CapVM(DS, cap));
            }
            if (rc == ReturnCode.Success)
            {
                try
                {
                    var cap = DS.Capabilities.CapEnableDSUIOnly;
                    SupportsDriverUIOnly = cap.IsSupported && cap.CanGetCurrent
                        && cap.GetCurrent() == BoolType.True;
                }
                catch (Exception ex)
                {
                    AppLog.Info("读取驱动界面支持失败: {0}", ex.Message);
                }
            }
            AppLog.Info("能力列表初始化耗时: {0} ms", timer.ElapsedMilliseconds);
            AppLog.Info("打开数据源结果: {0}, 能力数={1}, UIOnly={2}", rc, Caps.Count, SupportsDriverUIOnly);
        }

        private string _capFilter;

        public string CapFilter
        {
            get { return _capFilter; }
            set
            {
                _capFilter = value;
                _capView.Refresh();
            }
        }

        public ObservableCollection<CapVM> Caps { get; private set; }

        public CapVM GetCapVM(ushort key)
        {
            foreach (var item in Caps)
            {
                if ((ushort)item.Cap == key) {
                    return item;
                }
            }
            return null;
        }

        public bool ResetCapVMValue(ushort key)
        {
            CapVM vm = GetCapVM(key);
            if (DS.IsOpen == true && vm != null && vm.Supports.HasFlag(QuerySupports.Reset))
            {
                try
                {
                    vm.Reset();
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Info("ResetCapVMValue Error: {0} => {1} \n {2}", (CapabilityId)key, ex.Message, ex.StackTrace);
                    return false;
                }
            }
            else
            {
                if (DS.IsOpen == false)
                {
                    AppLog.Info("ResetCapVMValue Fail: {0} => DS IS NOT OPEN!", (CapabilityId)key);
                }
                else if (vm == null)
                {
                    AppLog.Info("ResetCapVMValue Fail: {0} => CAP NOT FOUND!", (CapabilityId)key);
                }
                else if (vm.Supports.HasFlag(QuerySupports.Reset) == false)
                {
                    AppLog.Info("ResetCapVMValue Fail: {0} => CAP IS READONLY!", (CapabilityId)key);
                }
                return false;
            }
        }

        public bool SetCapVMValue(ushort key, object value)
        {
            CapVM vm = GetCapVM(key);
            if (DS.IsOpen == true && vm != null && vm.Supports.HasFlag(QuerySupports.Set))
            {
                try
                {
                    vm.Set(value);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Info("SetCapVMValue Error: {0} -> {1} => {2} \n {3}", (CapabilityId)key, value.ToString(), ex.Message, ex.StackTrace);
                    return false;
                }
            }
            else
            {
                if (DS.IsOpen == false)
                {
                    AppLog.Info("SetCapVMValue Fail: {0} -> {1} => DS IS NOT OPEN!", (CapabilityId)key, value.ToString());
                }
                else if (vm == null)
                {
                    AppLog.Info("SetCapVMValue Fail: {0} -> {1} => CAP NOT FOUND!", (CapabilityId)key, value.ToString());
                }
                else if (vm.Supports.HasFlag(QuerySupports.Set) == false)
                {
                    AppLog.Info("SetCapVMValue Fail: {0} -> {1} => CAP IS READONLY!", (CapabilityId)key, value.ToString());
                }
                return false;
            }
        }

        public bool SetCapVMValue(ushort key, Func<TWOneValue> hock)
        {
            CapVM vm = GetCapVM(key);
            if (DS.IsOpen == true && vm != null && vm.Supports.HasFlag(QuerySupports.Set))
            {
                try
                {
                    vm.Set(hock);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Info("SetCapVMValue Error: {0} => {1} \n {2}", (CapabilityId)key, ex.Message, ex.StackTrace);
                    return false;
                }
            }
            else
            {
                if (DS.IsOpen == false)
                {
                    AppLog.Info("SetCapVMValue Fail: {0} => DS IS NOT OPEN!", (CapabilityId)key);
                }
                else if (vm == null)
                {
                    AppLog.Info("SetCapVMValue Fail: {0} => CAP NOT FOUND!", (CapabilityId)key);
                }
                else if (vm.Supports.HasFlag(QuerySupports.Set) == false)
                {
                    AppLog.Info("SetCapVMValue Fail: {0} => CAP IS READONLY!", (CapabilityId)key);
                }
                return false;
            }
        }

        private ICommand _saveCapCommand;

        public ICommand SaveCapValuesCommand
        {
            get
            {
                return _saveCapCommand ?? (_saveCapCommand = new RelayCommand(() =>
                {
                    Messenger.Default.Send(new ChooseFileMessage(files =>
                    {
                        try
                        {
                            StringBuilder report = new StringBuilder();
                            report.Append("Cap values for TWAIN device ").AppendLine(DS.Name);
                            report.Append("Generated on ").AppendLine(DateTime.Now.ToString("yyyy/MM/dd hh:mm tt")).AppendLine();

                            foreach (CapVM cap in _capView)
                            {
                                try
                                {
                                    report.Append(cap.Name).AppendLine(":");
                                    report.Append('\t').Append("Supports: ").Append(cap.Supports).AppendLine();
                                    
                                    if (cap.Supports.HasFlag(QuerySupports.Get))
                                    {
                                        report.Append('\t').Append("Values: ");
                                        var values = cap.Get();
                                        if (values != null)
                                        {
                                            int count = 0;
                                            foreach (var v in values)
                                            {
                                                report.Append(v).Append(',');
                                                count++;
                                                if (count >= 100)
                                                {
                                                    report.Append("...(more)");
                                                    break;
                                                }
                                            }
                                        }
                                        report.AppendLine();
                                    }

                                    if (cap.Supports.HasFlag(QuerySupports.GetCurrent))
                                    {
                                        var currentValue = cap.GetCurrent();
                                        report.Append('\t').Append("Current: ").Append(currentValue).AppendLine();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    report.Append('\t').Append("Failed: ").Append(ex.Message).AppendLine();
                                    AppLog.Error("读取能力失败 {0}: {1}", cap.Name, ex.Message);
                                }
                                report.AppendLine();
                            }

                            File.WriteAllText(files.First(), report.ToString());
                            using (Process.Start(files.First())) { }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("保存能力值失败", ex);
                            Messenger.Default.Send(new NotificationMessage($"保存能力值失败: {ex.Message}"));
                        }
                    })
                    {
                        Caption = "Choose Save File",
                        Filters = "Text files|*.txt",
                        InitialFileName = DS.Name + " capability",
                        Purpose = FilePurpose.Save,
                    });
                }, () => DS.IsOpen));
            }
        }

    }
}
