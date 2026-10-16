using NTwain.OcrValidation;
using OpenCvSharp;
using Sdcb.PaddleInference;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace OcrOrientationWorker
{
    internal static class Program
    {
        const string ModelName = "PP-LCNet_x1_0_doc_ori";
        public const string ResultPrefix = "NTWAIN_OCR_RESULT:";

        private static int Main(string[] args)
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            var output = Console.Out;
            Console.SetOut(Console.Error);
            var json = new JavaScriptSerializer();
            PaddlePredictor predictor = null;
            try
            {
                if (args.Length == 1 && args[0] == "--self-test")
                {
                    RunDecisionTests();
                    output.WriteLine("Orientation decision tests passed.");
                    return 0;
                }
                if (args.Length > 0 && (args.Length != 2 || args[0] != "--analyze"))
                    throw new ArgumentException("Usage: OcrOrientationWorker.exe [--analyze image-path | --self-test]");
                string request;
                while ((request = args.Length == 2 ? json.Serialize(args[1]) : Console.ReadLine()) != null)
                {
                    // .NET Framework's redirected stdin may emit a UTF-8 preamble
                    // before our UTF-8 writer takes over the pipe.
                    request = request.TrimStart('\uFEFF');
                    if (string.IsNullOrWhiteSpace(request)) continue;
                    string path = null;
                    try
                    {
                        path = Path.GetFullPath(json.Deserialize<string>(request));
                        var timer = Stopwatch.StartNew();
                        long modelLoad = 0;
                        // Validate input before loading a native model.
                        using (var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color | ImreadModes.IgnoreOrientation))
                        {
                            if (image.Empty()) throw new InvalidDataException("Unable to decode image.");
                            if (predictor == null)
                            {
                                var modelTimer = Stopwatch.StartNew();
                                string modelDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", ModelName);
                                using (var config = PaddleConfig.FromModelDir(modelDir))
                                {
                                    PaddleDevice.OneDnn(cpuMathThreadCount: Math.Min(4, Environment.ProcessorCount))(config);
                                    predictor = config.CreatePredictor();
                                }
                                modelLoad = modelTimer.ElapsedMilliseconds;
                            }
                            var report = Analyze(predictor, image, path);
                            report.ModelLoadMilliseconds = modelLoad;
                            report.ElapsedMilliseconds = timer.ElapsedMilliseconds;
                            output.WriteLine(ResultPrefix + json.Serialize(report));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex);
                        output.WriteLine(ResultPrefix + json.Serialize(new OcrOrientationReport
                        {
                            ImagePath = path, Model = ModelName,
                            Reason = "方向分类失败，保留图片：" + ex.Message
                        }));
                        if (args.Length == 2) return 1;
                    }
                    output.Flush();
                    if (args.Length == 2) break;
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { if (predictor != null) predictor.Dispose(); }
        }

        private static OcrOrientationReport Analyze(PaddlePredictor predictor, Mat image, string path)
        {
            var report = new OcrOrientationReport
            {
                ImagePath = path, Model = ModelName,
                InputStage = "Existing image; suggested clockwise correction is relative to this file",
                Algorithm = "Document orientation classification only; no det/rec",
                Candidates = new List<OcrAngleScore>()
            };
            var preprocessTimer = Stopwatch.StartNew();
            // Official inference.yml: resize short edge to 256, center crop 224,
            // RGB, scale 1/255, ImageNet mean/std, NCHW float32.
            double scale = 256.0 / Math.Min(image.Width, image.Height);
            using (var resized = new Mat())
            using (var rgb = new Mat())
            using (var gray = new Mat())
            {
                Cv2.Resize(image, resized, new Size(
                    Math.Max(256, (int)(image.Width * scale)),
                    Math.Max(256, (int)(image.Height * scale))), 0, 0, InterpolationFlags.Linear);
                Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.MeanStdDev(gray, out Scalar mean, out Scalar deviation);
                if (deviation.Val0 < 3)
                {
                    foreach (int label in new[] { 0, 90, 180, 270 })
                        report.Candidates.Add(new OcrAngleScore { ClassAngle = label, Angle = (360 - label) % 360 });
                    OcrOrientationDecision.Apply(report);
                    report.Reason = "图像近乎纯色，跳过方向分类并保留图片。";
                    report.PreprocessingMilliseconds = preprocessTimer.ElapsedMilliseconds;
                    return report;
                }
                Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);
                using (var center = new Mat(rgb, new Rect((rgb.Width - 224) / 2, (rgb.Height - 224) / 2, 224, 224)))
                using (var crop = center.Clone())
                {
                    const int pixels = 224 * 224;
                    var bytes = new byte[pixels * 3];
                    Marshal.Copy(crop.Data, bytes, 0, bytes.Length);
                    var values = new float[bytes.Length];
                    var means = new[] { 0.485f, 0.456f, 0.406f };
                    var stds = new[] { 0.229f, 0.224f, 0.225f };
                    for (int channel = 0; channel < 3; channel++)
                        for (int pixel = 0; pixel < pixels; pixel++)
                            values[channel * pixels + pixel] = (bytes[pixel * 3 + channel] / 255f - means[channel]) / stds[channel];
                    using (var input = predictor.GetInputTensor(predictor.InputNames[0]))
                    {
                        input.Shape = new[] { 1, 3, 224, 224 };
                        input.SetData(values);
                    }
                }
            }
            report.PreprocessingMilliseconds = preprocessTimer.ElapsedMilliseconds;
            var classifyTimer = Stopwatch.StartNew();
            predictor.Run();
            using (var output = predictor.GetOutputTensor(predictor.OutputNames[0]))
            {
                var probabilities = output.GetData<float>();
                if (probabilities.Length != 4 || probabilities.Any(p => float.IsNaN(p) || p < 0 || p > 1)
                    || Math.Abs(probabilities.Sum() - 1) > 0.01)
                    throw new InvalidDataException("Unexpected document classifier output.");
                for (int i = 0; i < 4; i++)
                    report.Candidates.Add(new OcrAngleScore
                    {
                        ClassAngle = i * 90,
                        Angle = (360 - i * 90) % 360,
                        Probability = probabilities[i]
                    });
            }
            report.ClassificationMilliseconds = classifyTimer.ElapsedMilliseconds;
            OcrOrientationDecision.Apply(report);
            return report;
        }

        private static void RunDecisionTests()
        {
            foreach (int winner in new[] { 0, 90, 180, 270 })
            {
                var report = TestReport(winner, 0.97, 0.01);
                OcrOrientationDecision.Apply(report);
                if (!report.IsConfident || report.SuggestedAngle != winner)
                    throw new Exception("Decisive correction was not selected: " + winner);
            }
            foreach (var report in new[] { TestReport(90, 0.40, 0.20), TestReport(180, 0.89, 0.11 / 3), TestReport(0, 0, 0) })
            {
                OcrOrientationDecision.Apply(report);
                if (report.SuggestedAngle.HasValue) throw new Exception("Low-confidence result must abstain.");
            }
        }

        private static OcrOrientationReport TestReport(int winner, double best, double other)
        {
            return new OcrOrientationReport
            {
                Candidates = new[] { 0, 90, 180, 270 }.Select(angle => new OcrAngleScore
                {
                    Angle = angle, ClassAngle = (360 - angle) % 360,
                    Probability = angle == winner ? best : other
                }).ToList()
            };
        }
    }
}
