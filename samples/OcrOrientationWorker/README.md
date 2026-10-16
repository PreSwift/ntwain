# 整页方向分类与自动转正（不运行 det/rec）

当前只运行 PaddleOCR 的 `PP-LCNet_x1_0_doc_ori` 整页四方向分类模型。通过 `Sdcb.PaddleInference` 执行一次推理，返回 0°/90°/180°/270°的概率，不加载文字检测或文字识别模型，不输出 OCR 内容。

保留历史项目名 `OcrOrientationWorker`、报告后缀 `.ocr.json` 及内部 DTO 名。WPF 示例已从验证模式改为直接按分类角度旋转。

## 使用

1. 本机模型已准备好。新检出仓库时，先运行 `powershell -File samples/OcrOrientationWorker/Get-OrientationModel.ps1`，下载约 7MB 的官方模型；脚本校验固定 SHA256，仅解压指定文件。模型文件及下载缓存被 Git 忽略。
2. 还原 NuGet 并构建 `NTwain.sln`。
3. 运行 Sample.WPF，默认开启「自动转正」，扫描页按高置信度分类结果自动旋转，随后显示最终缩略图。关闭该选项后，不执行软件旋转，也不恢复任何旧旋转规则。
4. 点击「转正图片（副本）」可选择已有图片。输出 JPEG 副本保存在「我的文档/NTwain Scans/自动转正/批次目录」，外部原文件保留；「打开文件夹」可查看本批次副本。扫描过程中不能导入图片。
5. 缩略图下方显示已旋转角度或「无法确定」；日志记录耗时与四个类别概率；图片旁生成 `.ocr.json` 报告。

处理顺序为：扫描落盘及必要机型取景裁切 → 整页方向分类 → 按分类角度旋转一次并按需补白 → 原子替换输出文件 → 刷新缩略图。高置信度才旋转，低置信度、空白或分类失败时保留裁切后的图像方向，不回退到机型或正背面角度猜测。

已删除旧的 A/B 面固定 90°/270°、按纸型强制横竖、自动尺寸匹配后旋转等软件规则；G300 NoUI 的驱动固定旋转由 180°改为 0°。扫描前的纸张取景、方向/Frame 能力配置保留，它们决定是否扫全内容，不决定最终文字方向。驱动 UI 模式也使用分类自动转正，但不额外套用 NoUI 的裁切。

保留 Epson/柯尼卡横向纸型的全幅裁切和华高边缘裁切，裁切本身不再旋转。Epson A4/16K 缺失的空白画布在高置信度旋转后按当前图像横竖补足，内容不拉伸。转正图片采用 JPEG 质量 100；非零角度会重新编码，0°且无需补白时不重写图片。导入多页 TIFF 时仍只处理第一帧。

首次安装需要下载依赖和模型，之后随 Worker 复制部署，识别时不联网、不需要 API Key。独立 x64 .NET Framework 4.8 Worker 可被 32 位扫描程序调用。

## 分类与校正角度

| 模型方向标签 | 建议顺时针校正 |
|---|---|
| 0° | 0° |
| 90° | 270° |
| 180° | 180° |
| 270° | 90° |

`ModelOrientationAngle` / 候选的 `ClassAngle` 为模型标签；`SuggestedAngle` / 候选的 `Angle` 为顺时针校正角度。映射已用四方向合成图片验证。

WPF 报告中的 `AppliedAngle` 记录实际处理角度（未处理时为 null），`ImageChanged` 表示是否重写了像素文件，`ImageProcessingMilliseconds` 记录旋转/补白/落盘耗时。报告的分类结果对应旋转前的文件，角度不能再次应用于已经转正的输出。

模型最高概率至少 0.90，且相对次高概率差至少 20% 时输出建议；否则 `SuggestedAngle=null`。近乎纯色图片先通过像素方差检查，跳过分类并放弃建议。阈值是待实际样本校准的策略，模型概率不等于实测正确率。纯图片、混合方向、竖排、少字和手写页面仍可能判断错误。

预处理严格对应固定模型的 `inference.yml`：RGB、短边缩放至 256、中心裁切 224×224、1/255 归一化与 ImageNet mean/std、NCHW float32。中心区域缺少代表性内容时会影响分类，这与不运行文字检测的方案有关。

Worker 常驻、串行复用模型，最多使用 4 个 CPU 数学线程。后台队列只保存文件路径，不缓存整页位图，不再因超过 32 页而跳过处理。界面显示待处理页数；正常关闭窗口会等后台转正完成，避免取消未处理页。每页超时 3 分钟；报告分别记录模型加载、预处理、分类、图片处理及排队时间，`ElapsedMilliseconds` 仅为 Worker 分析时间，不包含排队及图片写入。异常或超时的页保留图片并显示错误，其他页继续处理。

## 构建与测试

```powershell
powershell -File samples/OcrOrientationWorker/Get-OrientationModel.ps1
dotnet restore samples/OcrOrientationWorker/OcrOrientationWorker.csproj
msbuild NTwain.sln /t:Build /p:Configuration=Release
pwsh -File samples/OcrOrientationWorker/Test-Orientation.ps1 -Configuration Release
pwsh -File samples/OcrOrientationWorker/Test-AutoOrientation.ps1 -Configuration Release
```

单图分析（仅输出 JSON，不写报告文件）：

```powershell
samples/OcrOrientationWorker/bin/Release/net48/OcrOrientationWorker.exe --analyze "C:\scans\page.jpg"
```

以 `NTWAIN_OCR_RESULT:` 开头的行是 JSON 结果。命令行 Worker 仍只做分类；实际旋转、补白和报告写入由 WPF 客户端负责。

2026-10-16 本机测试：四个方向均返回正确校正角度；空白页放弃建议；无效图片错误后继续处理；中文路径、进程复用、正常退出、输入文件哈希不变均通过。合成图后续处理约 26～33ms/页，首次有效图推理预热约 109ms，模型加载另计。旧方案需 1.8～2 秒/页；本次替换后不再调用 det/rec。测试结果位于已忽略的 `build/ocr-validation`，未创建 TestResults 目录。尚未测定真实试卷和各机型的准确率。

自动转正集成测试直接调用 WPF 中的处理器：四方向 PNG 转正后与原正向图片的像素哈希一致；A/B 文件名不影响角度；再次处理已转正图片返回 0°且文件哈希不变；空白/异常文件不改动；90°旋转正确交换像素尺寸及 DPI；补白不拉伸内容；全幅裁切不旋转；40 个排队任务均完成。

## 模型来源

- 官方说明：https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/module_usage/doc_img_orientation_classification.en.md
- 下载地址：https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/PP-LCNet_x1_0_doc_ori_infer.tar
- SHA256：`282337DF5C41F7CDF8DACD5ACF71FDDFDC10218399F4B318463C17F4EAE96C97`
- 官方模型 README 声明 Apache License 2.0，随模型复制部署；预处理和类别顺序来自同一下载包中的 inference.yml。
