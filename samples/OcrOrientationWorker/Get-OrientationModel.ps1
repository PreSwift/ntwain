$ErrorActionPreference = 'Stop'
$taskUrl = 'https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/PP-LCNet_x1_0_doc_ori_infer.tar'
$taskHash = '282337DF5C41F7CDF8DACD5ACF71FDDFDC10218399F4B318463C17F4EAE96C97'
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$taskCache = Join-Path $taskRoot 'build\doc-orientation-model'
$taskTarget = Join-Path $PSScriptRoot 'models\PP-LCNet_x1_0_doc_ori'
New-Item -ItemType Directory -Path $taskCache,$taskTarget -Force | Out-Null
$taskArchive = Join-Path $taskCache 'model.tar'
if (-not (Test-Path -LiteralPath $taskArchive) -or (Get-FileHash -LiteralPath $taskArchive).Hash -ne $taskHash) {
    Invoke-WebRequest -Uri $taskUrl -OutFile $taskArchive -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $taskArchive).Hash -ne $taskHash) { throw 'Official model SHA256 mismatch.' }
& tar -xf $taskArchive -C $taskTarget --strip-components=1 `
    PP-LCNet_x1_0_doc_ori_infer/inference.json `
    PP-LCNet_x1_0_doc_ori_infer/inference.pdiparams `
    PP-LCNet_x1_0_doc_ori_infer/inference.yml `
    PP-LCNet_x1_0_doc_ori_infer/README.md
if ($LASTEXITCODE -ne 0) { throw 'Model extraction failed.' }
Write-Output "Official four-direction model ready: $taskTarget"
