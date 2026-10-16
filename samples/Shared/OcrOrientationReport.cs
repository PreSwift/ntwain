using System;
using System.Collections.Generic;
using System.Linq;

namespace NTwain.OcrValidation
{
    public sealed class OcrOrientationReport
    {
        public string ImagePath { get; set; }
        public string Model { get; set; }
        public string InputStage { get; set; }
        public int BestAngle { get; set; }
        public int ModelOrientationAngle { get; set; }
        public int? SuggestedAngle { get; set; }
        public int? AppliedAngle { get; set; }
        public bool ImageChanged { get; set; }
        public long ImageProcessingMilliseconds { get; set; }
        public bool IsConfident { get; set; }
        public double RelativeMargin { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long ModelLoadMilliseconds { get; set; }
        public long PreprocessingMilliseconds { get; set; }
        public long ClassificationMilliseconds { get; set; }
        public long QueueMilliseconds { get; set; }
        public string Algorithm { get; set; }
        public string Reason { get; set; }
        public List<OcrAngleScore> Candidates { get; set; }
    }

    public sealed class OcrAngleScore
    {
        public int Angle { get; set; }
        public int ClassAngle { get; set; }
        public double Probability { get; set; }
    }

    public static class OcrOrientationDecision
    {
        // Validation heuristics, not calibrated probability or production thresholds.
        public static void Apply(OcrOrientationReport report)
        {
            var ranked = report.Candidates.OrderByDescending(c => c.Probability).ToArray();
            if (ranked.Length != 4 || !ranked.Select(c => c.Angle).OrderBy(a => a)
                .SequenceEqual(new[] { 0, 90, 180, 270 }))
                throw new ArgumentException("Four different orientation candidates are required.");
            var best = ranked[0];
            report.BestAngle = best.Angle;
            report.ModelOrientationAngle = best.ClassAngle;
            report.RelativeMargin = best.Probability <= 0 ? 0
                : (best.Probability - ranked[1].Probability) / best.Probability;
            report.IsConfident = best.Probability >= 0.90 && report.RelativeMargin >= 0.20;
            report.SuggestedAngle = report.IsConfident ? (int?)best.Angle : null;
            report.Reason = report.IsConfident
                ? "整页四方向分类；建议角度相对于当前图片，验证模式未修改图片。"
                : "方向分类置信度不足或候选接近，保留当前图片；BestAngle 仅供诊断。";
        }
    }
}
