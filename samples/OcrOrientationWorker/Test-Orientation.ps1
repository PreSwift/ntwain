param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$taskOutput = Join-Path $taskRoot 'build\ocr-validation'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
# Test the deployment copied beside the WPF sample, including all native DLLs.
$taskExe = Join-Path $taskRoot "samples\Sample.WPF\bin\$Configuration\OcrWorker\OcrOrientationWorker.exe"
if (-not (Test-Path -LiteralPath $taskExe)) { throw 'Build NTwain.sln before running this test.' }
& $taskExe --self-test
if ($LASTEXITCODE -ne 0) { throw 'Decision tests failed.' }

$taskBitmap = New-Object System.Drawing.Bitmap(1240,1754)
$taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
$taskFont = New-Object System.Drawing.Font('Microsoft YaHei',28)
try {
    $taskGraphics.Clear([System.Drawing.Color]::White)
    $taskLines = @(
        '文字方向识别验证 学生作业扫描',
        'This page should be upright and readable.',
        '学校 班级 姓名 日期 二零二六年',
        'Mathematics and Chinese reading practice.',
        '请认真阅读题目，然后回答下面的问题。',
        'The quick brown fox jumps over the lazy dog.',
        '保持正面和背面的文字方向一致。',
        'Orientation test: correct page top is here.',
        '第一题：计算结果并写出完整过程。',
        'Second question: explain your answer clearly.')
    for ($taskI = 0; $taskI -lt $taskLines.Count; $taskI++) {
        $taskGraphics.DrawString($taskLines[$taskI],$taskFont,[System.Drawing.Brushes]::Black,80,120+$taskI*120)
    }
    foreach ($taskAngle in @(0,90,180,270)) {
        $taskCopy = $taskBitmap.Clone()
        try {
            if ($taskAngle -ne 0) { $taskCopy.RotateFlip([System.Drawing.RotateFlipType]("Rotate${taskAngle}FlipNone")) }
            $taskCopy.Save((Join-Path $taskOutput "测试方向-$taskAngle.png"),[System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $taskCopy.Dispose() }
    }
    $taskGraphics.Clear([System.Drawing.Color]::White)
    $taskBitmap.Save((Join-Path $taskOutput 'blank.png'),[System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $taskFont.Dispose()
    $taskGraphics.Dispose()
    $taskBitmap.Dispose()
}
$taskInvalid = Join-Path $taskOutput 'invalid.png'
[IO.File]::WriteAllText($taskInvalid,'This is not an image.')

$taskInfo = New-Object System.Diagnostics.ProcessStartInfo
$taskInfo.FileName = $taskExe
$taskInfo.WorkingDirectory = Split-Path $taskExe
$taskInfo.UseShellExecute = $false
$taskInfo.CreateNoWindow = $true
$taskInfo.RedirectStandardInput = $true
$taskInfo.RedirectStandardOutput = $true
$taskInfo.RedirectStandardError = $true
$taskInfo.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
$taskInfo.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
$taskProcess = New-Object System.Diagnostics.Process
$taskProcess.StartInfo = $taskInfo
$taskReports = @()
try {
    [void]$taskProcess.Start()
    $taskInput = New-Object System.IO.StreamWriter($taskProcess.StandardInput.BaseStream, (New-Object System.Text.UTF8Encoding($false)))
    $taskInput.AutoFlush = $true
    $taskErrors = $taskProcess.StandardError.ReadToEndAsync()
    # Invalid input followed by a good page verifies that the persistent process recovers.
    $taskCases = @(@{Name='invalid.png';Expected=$null}, @{Name='blank.png';Expected=$null})
    foreach ($taskAngle in @(0,90,180,270)) {
        $taskCases += @{Name="测试方向-$taskAngle.png";Expected=(360-$taskAngle)%360}
    }
    foreach ($taskCase in $taskCases) {
        $taskPath = Join-Path $taskOutput $taskCase.Name
        $taskHash = (Get-FileHash -LiteralPath $taskPath).Hash
        $taskInput.WriteLine((ConvertTo-Json -InputObject $taskPath -Compress))
        $taskDeadline = [DateTime]::UtcNow.AddMinutes(3)
        do {
            $taskRead = $taskProcess.StandardOutput.ReadLineAsync()
            $taskRemaining = [int][Math]::Max(1,($taskDeadline-[DateTime]::UtcNow).TotalMilliseconds)
            if (-not $taskRead.Wait($taskRemaining)) { throw 'OCR timed out.' }
            $taskLine = $taskRead.Result
            if ($null -eq $taskLine) { throw 'Worker exited unexpectedly.' }
        } until ($taskLine.StartsWith('NTWAIN_OCR_RESULT:'))
        $taskReport = $taskLine.Substring('NTWAIN_OCR_RESULT:'.Length) | ConvertFrom-Json
        if ($taskCase.Name -ne 'invalid.png' -and $taskReport.Algorithm -ne 'Document orientation classification only; no det/rec') {
            throw 'Unexpected inference pipeline; classification-only is required.'
        }
        if ($taskReport.ImagePath -ne $taskPath) { throw 'Unicode path did not round-trip.' }
        if ($taskReport.SuggestedAngle -ne $taskCase.Expected) {
            throw "Unexpected correction for $($taskCase.Name): $($taskReport.SuggestedAngle), expected $($taskCase.Expected)"
        }
        if ((Get-FileHash -LiteralPath $taskPath).Hash -ne $taskHash) { throw 'Input image changed.' }
        $taskReports += $taskReport
        Write-Output "$($taskCase.Name): suggested=$($taskReport.SuggestedAngle), confident=$($taskReport.IsConfident), elapsed=$($taskReport.ElapsedMilliseconds)ms"
    }
    $taskInput.Close()
    if (-not $taskProcess.WaitForExit(10000)) { throw 'Worker did not exit on EOF.' }
    [IO.File]::WriteAllText((Join-Path $taskOutput 'native.log'),$taskErrors.Result)
    if ($taskProcess.ExitCode -ne 0) { throw 'Worker failed on shutdown.' }
    $taskReports | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskOutput 'results.json') -Encoding UTF8
    Write-Output "Passed: four directions, blank/invalid input, Unicode paths, process reuse, unchanged input, clean shutdown. Results: $taskOutput"
} finally {
    if ($taskProcess.Id -and -not $taskProcess.HasExited) { $taskProcess.Kill() }
    $taskProcess.Dispose()
}
