using NTwain.OcrValidation;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Sample.WPF
{
    /// <summary>串行复用 x64 分类进程，按高置信度结果自动转正，不占用 TWAIN 线程。</summary>
    sealed class OcrOrientationValidator : IDisposable
    {
        readonly BlockingCollection<Job> _jobs = new BlockingCollection<Job>();
        readonly CancellationTokenSource _stop = new CancellationTokenSource();
        readonly object _processGate = new object();
        Process _process;
        StreamWriter _input;
        string _lastDiagnostic;
        bool _disposed;
        int _pendingCount;
        public int PendingCount { get { return Volatile.Read(ref _pendingCount); } }
        public event Action PendingCountChanged;

        public OcrOrientationValidator()
        {
            Task.Run((Action)Consume);
        }

        public bool TryQueue(string path, Action<string> update, ScanPaperSize? restoreCanvas = null, Action completed = null)
        {
            if (_disposed) return false;
            Interlocked.Increment(ref _pendingCount);
            try
            {
                _jobs.Add(new Job { Path = path, Update = update, Queued = Stopwatch.StartNew(),
                    RestoreCanvas = restoreCanvas, Completed = completed });
                PendingCountChanged?.Invoke();
                return true;
            }
            catch (InvalidOperationException)
            {
                Interlocked.Decrement(ref _pendingCount);
                return false;
            }
        }

        void Consume()
        {
            try
            {
                foreach (var job in _jobs.GetConsumingEnumerable(_stop.Token))
                {
                    try
                    {
                        long queueMilliseconds = job.Queued.ElapsedMilliseconds;
                        job.Update("方向分类中…");
                        var json = new JavaScriptSerializer();
                        var process = EnsureWorker();
                        _input.WriteLine(json.Serialize(Path.GetFullPath(job.Path)));
                        _input.Flush();
                        var response = ReadResponseAsync(process);
                        var timeout = Task.Delay(TimeSpan.FromMinutes(3), _stop.Token);
                        if (Task.WhenAny(response, timeout).GetAwaiter().GetResult() != response)
                        {
                            response.ContinueWith(t => { var observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                            throw new TimeoutException("OCR 超时或应用正在关闭");
                        }
                        var report = json.Deserialize<OcrOrientationReport>(response.GetAwaiter().GetResult());
                        if (report == null || string.IsNullOrWhiteSpace(report.ImagePath))
                            throw new InvalidDataException("方向分类返回无效路径：" + (report == null ? "空报告" : report.Reason));
                        if (!string.Equals(Path.GetFullPath(report.ImagePath),
                            Path.GetFullPath(job.Path), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("OCR 返回的图片路径不匹配");
                        report.InputStage = "已保存图片（扫描页已完成取景裁切）；按分类结果自动转正";
                        report.QueueMilliseconds = queueMilliseconds;
                        _stop.Token.ThrowIfCancellationRequested();
                        if (report.IsConfident && report.SuggestedAngle.HasValue)
                        {
                            var processingTimer = Stopwatch.StartNew();
                            report.ImageChanged = ScanImageProcessingHelper.ApplyClassifiedOrientation(
                                job.Path, report.SuggestedAngle.Value, job.RestoreCanvas);
                            report.AppliedAngle = report.SuggestedAngle;
                            report.ImageProcessingMilliseconds = processingTimer.ElapsedMilliseconds;
                            report.Reason = "已按整页方向分类结果处理；未应用其他软件旋转规则。";
                        }
                        // A report write failure must not mislabel a successfully rotated page.
                        try { File.WriteAllText(job.Path + ".ocr.json", json.Serialize(report), new UTF8Encoding(false)); }
                        catch (Exception ex) { AppLog.Info("方向报告写入失败 {0}: {1}", job.Path, ex.Message); }
                        string message = report.IsConfident
                            ? (report.AppliedAngle == 0 ? "方向：已正向" : "方向：已顺时针旋转 " + report.AppliedAngle + "°")
                            : "方向：无法确定（保留图片）";
                        if (report.Candidates == null) message = "方向：分类失败（查看日志）";
                        job.Update(message);
                        AppLog.Info("{0}，文件={1}，耗时={2}ms，margin={3:0.000}，{4}，报告={1}.ocr.json",
                            message, job.Path, report.ElapsedMilliseconds, report.RelativeMargin, report.Reason);
                        AppLog.Info("方向分类性能：排队={0}ms，模型加载={1}ms，预处理={2}ms，分类={3}ms，图片处理={4}ms",
                            report.QueueMilliseconds, report.ModelLoadMilliseconds, report.PreprocessingMilliseconds,
                            report.ClassificationMilliseconds, report.ImageProcessingMilliseconds);
                        if (report.Candidates != null)
                            foreach (var candidate in report.Candidates)
                                AppLog.Info("方向标签={0}°，顺时针校正={1}°，概率={2:0.0000}",
                                    candidate.ClassAngle, candidate.Angle, candidate.Probability);
                    }
                    catch (Exception ex)
                    {
                        StopWorker();
                        if (_stop.IsCancellationRequested) break;
                        job.Update("自动转正失败（保留图片）：" + ex.Message);
                        AppLog.Info("自动转正失败 {0}: {1}: {2}; worker={3}",
                            job.Path, ex.GetType().FullName, ex.Message, _lastDiagnostic);
                    }
                    finally
                    {
                        try { job.Completed?.Invoke(); }
                        catch (Exception ex) { AppLog.Info("刷新转正图片失败: {0}", ex.Message); }
                        Interlocked.Decrement(ref _pendingCount);
                        PendingCountChanged?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        Process EnsureWorker()
        {
            lock (_processGate)
            {
                _stop.Token.ThrowIfCancellationRequested();
                if (_process != null && !_process.HasExited) return _process;
                if (_process != null) { _process.Dispose(); _process = null; }
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OcrWorker", "OcrOrientationWorker.exe");
                if (!File.Exists(path)) throw new FileNotFoundException("请先构建 OCR Worker。", path);
                _lastDiagnostic = null;
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo(path)
                    {
                        WorkingDirectory = Path.GetDirectoryName(path),
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                    }
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        _lastDiagnostic = e.Data.Length > 1000 ? e.Data.Substring(0, 1000) : e.Data;
                };
                process.Start();
                _input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                process.BeginErrorReadLine();
                _process = process;
                return process;
            }
        }

        static async Task<string> ReadResponseAsync(Process process)
        {
            const string prefix = "NTWAIN_OCR_RESULT:";
            string line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line.Substring(prefix.Length);
            throw new IOException("OCR 子进程意外退出");
        }

        void StopWorker()
        {
            lock (_processGate)
            {
                if (_process == null) return;
                try { if (!_process.HasExited) _process.Kill(); }
                catch (InvalidOperationException) { }
                finally
                {
                    if (_input != null) { _input.Dispose(); _input = null; }
                    _process.Dispose(); _process = null;
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _stop.Cancel();
            _jobs.CompleteAdding();
            StopWorker();
        }

        sealed class Job
        {
            public string Path;
            public Action<string> Update;
            public Stopwatch Queued;
            public ScanPaperSize? RestoreCanvas;
            public Action Completed;
        }
    }
}
