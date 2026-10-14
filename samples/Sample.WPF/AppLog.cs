using NTwain;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Sample.WPF
{
    /// <summary>
    /// 立即落盘的扫描日志。托管异常和原生崩溃都会尽量把现场写进同一个目录。
    /// </summary>
    sealed class AppLog : ILog
    {
        public static readonly AppLog Instance = new AppLog();

        static readonly object Gate = new object();
        static FileStream _stream;
        static StreamWriter _writer;
        static bool _started;
        static IntPtr _previousNativeFilter;
        static readonly NativeCrashFilter _nativeFilter = OnNativeCrash;

        public static string DirectoryPath { get; private set; }
        public static string CurrentLogPath { get; private set; }

        public bool IsInfoEnabled { get; set; }
        public bool IsDebugEnabled { get; set; }
        public bool IsErrorEnabled { get; set; }

        public static void Start()
        {
            if (_started)
                return;
            _started = true;

            IsFlags(true);
            OpenLog();
            PlatformInfo.Current.Log = Instance;

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            InstallNativeCrashFilter();

            Info("======== 会话开始 pid={0} bit={1} clr={2} ========",
                Process.GetCurrentProcess().Id,
                IntPtr.Size == 8 ? "64" : "32",
                Environment.Version);
            Info("日志目录: {0}", DirectoryPath);
            Info("DSM: path={0} new={1} supported={2}",
                PlatformInfo.Current.ExpectedDsmPath,
                PlatformInfo.Current.UseNewWinDSM,
                PlatformInfo.Current.IsSupported);
            Info("最近一次驱动调用: {0}", TwainDiagnostics.LastDsmCallPath);
        }

        static void IsFlags(bool enabled)
        {
            Instance.IsInfoEnabled = enabled;
            Instance.IsDebugEnabled = false;
            Instance.IsErrorEnabled = enabled;
        }

        /// <summary>
        /// 关闭当前日志句柄并删除日志目录中的扫描日志和最后一次驱动调用记录，然后重新打开当天日志。
        /// </summary>
        public static int Clear()
        {
            var removed = 0;
            lock (Gate)
            {
                CloseWriter();
                if (!string.IsNullOrEmpty(DirectoryPath) && Directory.Exists(DirectoryPath))
                {
                    foreach (var file in Directory.GetFiles(DirectoryPath, "scan-*.log"))
                    {
                        if (TryDelete(file))
                            removed++;
                    }

                    if (TryDelete(TwainDiagnostics.LastDsmCallPath))
                        removed++;
                }

                OpenLog();
            }

            Info("已清理日志，删除 {0} 个文件", removed);
            return removed;
        }

        static void CloseWriter()
        {
            try
            {
                if (_writer != null)
                    _writer.Dispose();
            }
            catch
            {
            }

            _writer = null;
            _stream = null;
        }

        static bool TryDelete(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void OpenLog()
        {
            try
            {
                DirectoryPath = TwainDiagnostics.LogDirectory;
                Directory.CreateDirectory(DirectoryPath);
                CurrentLogPath = Path.Combine(DirectoryPath, "scan-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                _stream = new FileStream(
                    CurrentLogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.None);
                _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
            }
            catch (Exception ex)
            {
                Trace.WriteLine("打开扫描日志失败: " + ex);
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Info(string messageFormat, params object[] args)
        {
            Write("INFO", Format(messageFormat, args));
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void Error(string message, Exception exception)
        {
            if (exception == null)
                Error(message);
            else
                Write("ERROR", message + Environment.NewLine + exception);
        }

        public static void Error(string messageFormat, params object[] args)
        {
            Write("ERROR", Format(messageFormat, args));
        }

        public static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Error("UI 线程未处理异常", e.Exception);
            try
            {
                MessageBox.Show(
                    "程序发生未处理异常，日志已写入:\n" + CurrentLogPath
                    + "\n\n若是驱动直接把进程打崩，请同时查看:\n" + TwainDiagnostics.LastDsmCallPath,
                    "扫描日志",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
            }
        }

        static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            if (ex != null)
                Error("AppDomain 未处理异常 terminating=" + e.IsTerminating, ex);
            else
                Error("AppDomain 未处理异常 terminating={0} object={1}", e.IsTerminating, e.ExceptionObject);
        }

        static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        }

        static void InstallNativeCrashFilter()
        {
            try
            {
                _previousNativeFilter = SetUnhandledExceptionFilter(
                    Marshal.GetFunctionPointerForDelegate(_nativeFilter));
            }
            catch (Exception ex)
            {
                Error("安装原生崩溃过滤器失败", ex);
            }
        }

        [HandleProcessCorruptedStateExceptions]
        static int OnNativeCrash(IntPtr exceptionPointers)
        {
            try
            {
                uint code;
                IntPtr address;
                string modulePath;
                ReadNativeException(exceptionPointers, out code, out address, out modulePath);
                Error(
                    "原生崩溃 code={0} ({1}) address=0x{2:X} module={3}",
                    FormatExceptionCode(code),
                    "0x" + code.ToString("X8"),
                    address.ToInt64(),
                    string.IsNullOrEmpty(modulePath) ? "未知" : modulePath);
                WriteMiniDump(exceptionPointers);
            }
            catch
            {
            }

            if (_previousNativeFilter == IntPtr.Zero)
                return 0;

            try
            {
                var previous = (NativeCrashFilter)Marshal.GetDelegateForFunctionPointer(
                    _previousNativeFilter,
                    typeof(NativeCrashFilter));
                return previous(exceptionPointers);
            }
            catch
            {
                return 0;
            }
        }

        static void ReadNativeException(IntPtr exceptionPointers, out uint code, out IntPtr address, out string modulePath)
        {
            code = 0;
            address = IntPtr.Zero;
            modulePath = null;
            if (exceptionPointers == IntPtr.Zero)
                return;

            var record = Marshal.ReadIntPtr(exceptionPointers);
            if (record == IntPtr.Zero)
                return;

            code = (uint)Marshal.ReadInt32(record);
            var addressOffset = IntPtr.Size == 8 ? 16 : 12;
            address = Marshal.ReadIntPtr(record, addressOffset);

            IntPtr module;
            if (!GetModuleHandleEx(
                GetModuleHandleExFlagFromAddress | GetModuleHandleExFlagUnchangedRefCount,
                address,
                out module) || module == IntPtr.Zero)
                return;

            var buffer = new StringBuilder(1024);
            if (GetModuleFileName(module, buffer, buffer.Capacity) > 0)
                modulePath = buffer.ToString();
        }

        static string FormatExceptionCode(uint code)
        {
            switch (code)
            {
                case 0xC0000005: return "ACCESS_VIOLATION";
                case 0xC0000094: return "INTEGER_DIVIDE_BY_ZERO";
                case 0xC00000FD: return "STACK_OVERFLOW";
                case 0xC0000374: return "HEAP_CORRUPTION";
                case 0xC0000409: return "STACK_BUFFER_OVERRUN";
                case 0xE06D7363: return "CPP_EXCEPTION";
                default: return "UNKNOWN";
            }
        }

        static void WriteMiniDump(IntPtr exceptionPointers)
        {
            if (string.IsNullOrEmpty(DirectoryPath))
                return;

            var dumpPath = Path.Combine(
                DirectoryPath,
                "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".dmp");
            using (var dump = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var info = new MiniDumpExceptionInformation
                {
                    ThreadId = GetCurrentThreadId(),
                    ExceptionPointers = exceptionPointers,
                    ClientPointers = 1
                };
                var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MiniDumpExceptionInformation)));
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    var ok = MiniDumpWriteDump(
                        Process.GetCurrentProcess().Handle,
                        (uint)Process.GetCurrentProcess().Id,
                        dump.SafeFileHandle.DangerousGetHandle(),
                        MiniDumpNormal,
                        infoPtr,
                        IntPtr.Zero,
                        IntPtr.Zero);
                    if (ok)
                        Error("已写入转储: {0}", dumpPath);
                    else
                        Error("写入转储失败 win32={0} path={1}", Marshal.GetLastWin32Error(), dumpPath);
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }
            }
        }

        static string Format(string messageFormat, object[] args)
        {
            if (string.IsNullOrEmpty(messageFormat) || args == null || args.Length == 0)
                return messageFormat;
            try
            {
                return string.Format(CultureInfo.CurrentCulture, messageFormat, args);
            }
            catch (Exception ex)
            {
                return messageFormat + " (格式化失败: " + ex.Message + ")";
            }
        }

        static void Write(string level, string message)
        {
            if (message == null)
                return;

            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2} {3}",
                DateTime.Now,
                Thread.CurrentThread.ManagedThreadId,
                level,
                message);
            lock (Gate)
            {
                try
                {
                    if (_writer == null)
                        return;
                    _writer.WriteLine(line);
                    _writer.Flush();
                }
                catch
                {
                }
            }
        }

        void ILog.Info(string message)
        {
            if (IsInfoEnabled)
                Info(message);
        }

        void ILog.Info(string messageFormat, params object[] args)
        {
            if (IsInfoEnabled)
                Info(messageFormat, args);
        }

        void ILog.Debug(string message)
        {
            if (IsDebugEnabled)
                Write("DEBUG", message);
        }

        void ILog.Debug(string messageFormat, params object[] args)
        {
            if (IsDebugEnabled)
                Write("DEBUG", Format(messageFormat, args));
        }

        void ILog.Error(string message)
        {
            if (IsErrorEnabled)
                Error(message);
        }

        void ILog.Error(string message, Exception exception)
        {
            if (IsErrorEnabled)
                Error(message, exception);
        }

        void ILog.Error(string messageFormat, Exception exception, params object[] args)
        {
            if (!IsErrorEnabled)
                return;
            Error(Format(messageFormat, args), exception);
        }

        const uint GetModuleHandleExFlagFromAddress = 0x00000004;
        const uint GetModuleHandleExFlagUnchangedRefCount = 0x00000002;
        const int MiniDumpNormal = 0;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int NativeCrashFilter(IntPtr exceptionPointers);

        [DllImport("kernel32.dll")]
        static extern IntPtr SetUnhandledExceptionFilter(IntPtr lpTopLevelExceptionFilter);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetModuleHandleEx(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("dbghelp.dll", SetLastError = true)]
        static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            uint processId,
            IntPtr hFile,
            int dumpType,
            IntPtr exceptionParam,
            IntPtr userStreamParam,
            IntPtr callbackParam);

        [StructLayout(LayoutKind.Sequential)]
        struct MiniDumpExceptionInformation
        {
            public uint ThreadId;
            public IntPtr ExceptionPointers;
            public int ClientPointers;
        }
    }
}
