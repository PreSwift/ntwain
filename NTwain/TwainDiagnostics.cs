using System;
using System.IO;

namespace NTwain
{
    /// <summary>
    /// 扫描诊断文件的固定目录。进程被驱动打崩后，仍可从这里查看最后一条记录。
    /// </summary>
    public static class TwainDiagnostics
    {
        public static string LogDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NTwain",
                    "logs");
            }
        }

        public static string LastDsmCallPath
        {
            get { return Path.Combine(LogDirectory, "last-dsm.txt"); }
        }
    }
}
