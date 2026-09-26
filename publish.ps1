# 夕颜若雪网络工具 —— 发布脚本
# 用法： powershell -ExecutionPolicy Bypass -File publish.ps1

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$src     = Join-Path $root 'src'
$outDir  = Join-Path $root 'publish'
$target  = 'E:\挂\xiyanruoxue'

Write-Host '=== 1/4 停止正在运行的实例 ===' -ForegroundColor Cyan
Get-Process -Name '夕颜若雪网络工具' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host '=== 2/4 生成单文件自包含版本 ===' -ForegroundColor Cyan
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

dotnet publish $src `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $outDir

if ($LASTEXITCODE -ne 0) { throw '发布失败' }

$exe = Join-Path $outDir '夕颜若雪网络工具.exe'
if (-not (Test-Path $exe)) { throw "未生成 exe：$exe" }
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ("    生成成功：{0}  ({1} MB)" -f $exe, $size) -ForegroundColor Green

Write-Host '=== 3/4 复制到工具箱目录 ===' -ForegroundColor Cyan
if (-not (Test-Path $target)) {
    Write-Host "    目标目录不存在，跳过：$target" -ForegroundColor Yellow
} else {
    Copy-Item $exe (Join-Path $target '夕颜若雪网络工具.exe') -Force
    Write-Host ("    已复制：{0}" -f (Join-Path $target '夕颜若雪网络工具.exe')) -ForegroundColor Green
    $doc = Join-Path $root '使用说明.md'
    if (Test-Path $doc) {
        Copy-Item $doc (Join-Path $target '网络工具-使用说明.md') -Force
        Write-Host ("    已复制：{0}" -f (Join-Path $target '网络工具-使用说明.md')) -ForegroundColor Green
    }
}

Write-Host '=== 4/4 完成 ===' -ForegroundColor Cyan
Write-Host '    运行记录：logs\NetDoctor_YYYYMMDD.log'
Write-Host '    改动前快照：backup\backup.ini'
