param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-Orientation.ps1') -Configuration $Configuration
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$taskOutput = Join-Path $taskRoot 'build\ocr-validation'
$taskAppOutput = Join-Path $taskRoot "samples\Sample.WPF\bin\$Configuration"
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

class AutoOrientationSmoke
{
    static object processor;
    static Type processorType;
    static JavaScriptSerializer json = new JavaScriptSerializer();
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static string Pixels(string path)
    {
        using (var image = new Bitmap(path))
        {
            var data = image.LockBits(new Rectangle(0,0,image.Width,image.Height),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
            try
            {
                var bytes = new byte[data.Stride * image.Height];
                Marshal.Copy(data.Scan0,bytes,0,bytes.Length);
                using (var sha = SHA256.Create()) return image.Width + "x" + image.Height + ":" + Convert.ToBase64String(sha.ComputeHash(bytes));
            }
            finally { image.UnlockBits(data); }
        }
    }
    static string FileHash(string path)
    {
        using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
    }
    static Dictionary<string, object> Run(string path)
    {
        using (var completed = new ManualResetEvent(false))
        {
            Action<string> update = delegate(string status) { Console.WriteLine(status); };
            Action done = delegate { completed.Set(); };
            bool queued = (bool)processorType.GetMethod("TryQueue").Invoke(processor,new object[] { path,update,null,done });
            Require(queued && completed.WaitOne(TimeSpan.FromMinutes(3)),"Processing did not complete.");
            Require(SpinWait.SpinUntil(delegate { return (int)processorType.GetProperty("PendingCount").GetValue(processor,null) == 0; },10000),"Pending count not drained.");
        }
        return json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path + ".ocr.json"));
    }
    static int Main(string[] args)
    {
        try { return Execute(args); }
        catch (Exception ex) { Console.WriteLine(ex.GetType().FullName + ": " + ex.Message); return 1; }
    }
    static int Execute(string[] args)
    {
        string folder = args[0];
        var assembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Sample.WPF.exe"));
        var logger = assembly.GetType("Sample.WPF.AppLog",true);
        logger.GetField("_writer",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,new StreamWriter(Path.Combine(folder,"auto-client.log")));
        processorType = assembly.GetType("Sample.WPF.OcrOrientationValidator",true);
        processor = Activator.CreateInstance(processorType,true);
        try
        {
            string canonical = Pixels(Path.Combine(folder,"测试方向-0.png"));
            foreach (int angle in new[] {0,90,180,270})
            {
                string input = Path.Combine(folder,"测试方向-" + angle + ".png");
                string originalHash = FileHash(input);
                // A/B filenames are deliberately unrelated to the required rotation.
                string output = Path.Combine(folder,"auto-" + angle + (angle % 180 == 0 ? "-B.png" : "-A.png"));
                File.Copy(input,output,true);
                var report = Run(output);
                Require(Convert.ToInt32(report["AppliedAngle"]) == (360-angle)%360,"Wrong applied angle.");
                Require(Pixels(output) == canonical,"Corrected PNG pixels do not match upright page.");
                Require(FileHash(input) == originalHash,"External source changed.");
                string correctedHash = FileHash(output);
                var repeated = Run(output);
                Require(Convert.ToInt32(repeated["AppliedAngle"]) == 0,"Corrected page was rotated again.");
                Require(!(bool)repeated["ImageChanged"] && FileHash(output) == correctedHash,"Repeated correction re-encoded the page.");
                Console.WriteLine("PASS auto rotation " + angle + ", exact upright pixels and idempotence.");
            }
            foreach (string name in new[] {"blank.png","invalid.png"})
            {
                string input = Path.Combine(folder,name);
                string hash = FileHash(input);
                var report = Run(input);
                Require(report["AppliedAngle"] == null && !(bool)report["ImageChanged"],"Invalid or blank page was changed.");
                Require(FileHash(input) == hash,"Abstaining changed input pixels.");
            }
            using (var backlog = new CountdownEvent(40))
            {
                for (int i=0;i<40;i++)
                {
                    Action<string> quiet = delegate(string status) {};
                    Action done = delegate { backlog.Signal(); };
                    bool queued = (bool)processorType.GetMethod("TryQueue").Invoke(processor,
                        new object[] {Path.Combine(folder,"blank.png"),quiet,null,done});
                    Require(queued,"Queued pages were silently skipped.");
                }
                Require(backlog.Wait(TimeSpan.FromMinutes(3)),"Backlog pages were not all processed.");
                Require(SpinWait.SpinUntil(delegate { return (int)processorType.GetProperty("PendingCount").GetValue(processor,null) == 0; },10000),"Backlog did not drain.");
                Console.WriteLine("PASS all 40 queued pages completed without dropping pages.");
            }

            var helper = assembly.GetType("Sample.WPF.ScanImageProcessingHelper",true);
            var paperType = assembly.GetType("Sample.WPF.ScanPaperSize",true);
            string canvas = Path.Combine(folder,"canvas.png");
            using (var image = new Bitmap(100,120))
            using (var graphics = Graphics.FromImage(image))
            {
                image.SetResolution(200,300);
                graphics.Clear(Color.Blue);
                image.Save(canvas,ImageFormat.Png);
            }
            helper.GetMethod("ApplyClassifiedOrientation").Invoke(null,new object[] {canvas,90,null});
            using (var image = new Bitmap(canvas))
            {
                Require(image.Width == 120 && image.Height == 100,"Quarter rotation did not swap dimensions.");
                Require(Math.Abs(image.HorizontalResolution-300)<1 && Math.Abs(image.VerticalResolution-200)<1,"Quarter rotation did not swap DPI.");
            }
            helper.GetMethod("ApplyClassifiedOrientation").Invoke(null,new object[] {canvas,0,Enum.Parse(paperType,"A4")});
            using (var image = new Bitmap(canvas))
            {
                Require(image.Width == 141 && image.Height == 100,"Canvas restoration has wrong dimensions.");
                Require(image.GetPixel(10,10).ToArgb() == Color.Blue.ToArgb(),"Canvas restoration changed content.");
                Require(image.GetPixel(140,50).ToArgb() == Color.White.ToArgb(),"Canvas restoration did not add white margin.");
            }

            string fullArea = Path.Combine(folder,"full-area.jpg");
            using (var image = new Bitmap(600,900))
            using (var graphics = Graphics.FromImage(image))
            {
                image.SetResolution(50,50);
                graphics.Clear(Color.White);
                image.Save(fullArea,ImageFormat.Jpeg);
            }
            helper.GetMethod("CropFullArea",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[] {fullArea,Enum.Parse(paperType,"A4L")});
            using (var image = new Bitmap(fullArea)) Require(image.Width == 413 && image.Height == 585,"Full-area crop unexpectedly rotated its output.");
            Console.WriteLine("PASS blank/error preservation, DPI, canvas restoration and crop without rotation.");
            return 0;
        }
        finally { ((IDisposable)processor).Dispose(); }
    }
}
'@
$taskSourcePath = Join-Path $taskOutput 'AutoOrientationSmoke.cs'
[IO.File]::WriteAllText($taskSourcePath,$taskSource,(New-Object System.Text.UTF8Encoding($false)))
$taskExe = Join-Path $taskAppOutput 'AutoOrientationSmoke.exe'
$taskPlatform = if ($Configuration -eq 'Release') { 'x64' } else { 'x86' }
& "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /codepage:65001 "/platform:$taskPlatform" /r:System.Drawing.dll /r:System.Web.Extensions.dll "/out:$taskExe" $taskSourcePath
if ($LASTEXITCODE -ne 0) { throw 'Auto-orientation harness compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskAppOutput 'Sample.WPF.exe.config') -Destination ($taskExe + '.config') -Force
try {
    & $taskExe $taskOutput
    if ($LASTEXITCODE -ne 0) { throw 'Auto-orientation integration tests failed.' }
} finally { Remove-Item -LiteralPath $taskExe,($taskExe + '.config') -ErrorAction SilentlyContinue }
