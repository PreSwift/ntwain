using NTwain.Data;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace NTwain.Internals
{
    /// <summary>
    /// 在进入 DSM_Entry 之前把当前三元组写到磁盘。
    /// 驱动访问冲突会直接杀掉进程，最后一条 ENTER 就是崩溃点。
    /// </summary>
    static class DsmCallTrace
    {
        static readonly object Gate = new object();
        static FileStream _stream;

        public static void Enter(DataGroups dg, DataArgumentType dat, Message msg, string detail)
        {
            var line = Format("ENTER", dg, dat, msg, null, detail);
            WriteBreadcrumb(line);
            if (msg != Message.ProcessEvent)
                SafeDebug(line);
        }

        public static void Leave(DataGroups dg, DataArgumentType dat, Message msg, ReturnCode rc, string detail)
        {
            var line = Format("LEAVE", dg, dat, msg, "rc=" + rc, detail);
            WriteBreadcrumb(line);
            if (msg != Message.ProcessEvent)
                SafeDebug(line);
        }

        public static void Fault(DataGroups dg, DataArgumentType dat, Message msg, string detail, Exception ex)
        {
            var line = Format("FAULT", dg, dat, msg, ex == null ? null : ex.GetType().Name, detail);
            WriteBreadcrumb(line);
            try
            {
                PlatformInfo.Current.Log.Error(line, ex);
            }
            catch
            {
                // 日志失败不能掩盖原来的异常
            }
        }

        static string Format(string phase, DataGroups dg, DataArgumentType dat, Message msg, string extra, string detail)
        {
            var text = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} tid={1} {2} dg={3} dat={4} msg={5}",
                DateTime.Now,
                Thread.CurrentThread.ManagedThreadId,
                phase,
                dg,
                dat,
                msg);
            if (!string.IsNullOrEmpty(extra))
                text += " " + extra;
            if (!string.IsNullOrEmpty(detail))
                text += " " + detail.Replace('\r', ' ').Replace('\n', ' ');
            return text;
        }

        static void SafeDebug(string line)
        {
            try
            {
                var log = PlatformInfo.Current.Log;
                if (log != null && log.IsDebugEnabled)
                    log.Debug(line);
            }
            catch
            {
            }
        }

        static void WriteBreadcrumb(string line)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                lock (Gate)
                {
                    if (_stream == null)
                    {
                        Directory.CreateDirectory(TwainDiagnostics.LogDirectory);
                        _stream = new FileStream(
                            TwainDiagnostics.LastDsmCallPath,
                            FileMode.OpenOrCreate,
                            FileAccess.Write,
                            FileShare.ReadWrite,
                            256,
                            FileOptions.WriteThrough);
                    }

                    _stream.Position = 0;
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.SetLength(bytes.Length);
                    _stream.Flush(true);
                }
            }
            catch
            {
            }
        }
    }
}
