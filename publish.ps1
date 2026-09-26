# 夕颜若雪网络工具 —— 发布脚本
# 用法： powershell -ExecutionPolicy Bypass -File publish.ps1 [-SkipNative] [-SkipTest]
#
#   产出：
#     publish\夕颜若雪网络工具.exe      单文件自包含主程序（WinForms，无界面依赖）
#     publish\NetDoctorNative.dll       x86 原生 DLL，给 32 位易语言宿主调用
#     并复制到 -Target 指定的目录（默认 E:\挂\xiyanruoxue）

param(
    [string]$Target   = 'E:\挂\xiyanruoxue',
    [switch]$SkipNative,
    [switch]$SkipTest
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$src     = Join-Path $root 'src'
$native  = Join-Path $root 'native'
$ntest   = Join-Path $root 'native-test'
$outDir  = Join-Path $root 'publish'

Write-Host '=== 1/5 停止正在运行的实例 ===' -ForegroundColor Cyan
Get-Process -Name '夕颜若雪网络工具' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host '=== 2/5 发布主程序（单文件自包含）===' -ForegroundColor Cyan
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

if ($LASTEXITCODE -ne 0) { throw '主程序发布失败' }
$exe = Join-Path $outDir '夕颜若雪网络工具.exe'
if (-not (Test-Path $exe)) { throw "未生成 exe：$exe" }
Write-Host ("    生成成功：夕颜若雪网络工具.exe  ({0} MB)" -f [math]::Round((Get-Item $exe).Length / 1MB, 1)) -ForegroundColor Green

if (-not $SkipNative) {
    Write-Host '=== 3/5 发布 x86 原生 DLL（NativeAOT）===' -ForegroundColor Cyan
    Write-Host '    需要 Visual Studio 的 C++ 生成工具与 Windows SDK' -ForegroundColor DarkGray
    dotnet publish $native -c Release -r win-x86 --nologo
    if ($LASTEXITCODE -ne 0) { throw '原生 DLL 发布失败（是否缺少 MSVC 工具链？）' }

    $dll = Join-Path $native 'bin\Release\net10.0\win-x86\publish\NetDoctorNative.dll'
    if (-not (Test-Path $dll)) { throw "未生成 DLL：$dll" }
    Copy-Item $dll $outDir -Force
    Write-Host ("    生成成功：NetDoctorNative.dll  ({0} MB)" -f [math]::Round((Get-Item $dll).Length / 1MB, 1)) -ForegroundColor Green

    if (-not $SkipTest) {
        Write-Host '    正在跑原生 DLL 调用测试（x86 宿主模拟易语言调用）…' -ForegroundColor DarkGray
        dotnet publish $ntest -c Release -r win-x86 --nologo | Out-Null
        $tpub = Join-Path $ntest 'bin\Release\net10.0\win-x86\publish'
        Copy-Item $dll (Join-Path $tpub 'NetDoctorNative.dll') -Force
        Push-Location $tpub
        & '.\NetDoctorNativeTest.exe' | Select-Object -Last 4
        $code = $LASTEXITCODE
        Pop-Location
        if ($code -ne 0) { throw "原生 DLL 测试未通过（退出码 $code）" }
        Write-Host '    原生 DLL 测试通过' -ForegroundColor Green
    }
} else {
    Write-Host '=== 3/5 跳过原生 DLL（-SkipNative）===' -ForegroundColor DarkGray
}

Write-Host '=== 4/5 复制到目标目录 ===' -ForegroundColor Cyan
if (-not (Test-Path $Target)) {
    Write-Host "    目标目录不存在，跳过：$Target" -ForegroundColor Yellow
} else {
    Copy-Item $exe (Join-Path $Target '夕颜若雪网络工具.exe') -Force
    Write-Host "    已复制：夕颜若雪网络工具.exe" -ForegroundColor Green

    $dllOut = Join-Path $outDir 'NetDoctorNative.dll'
    if (Test-Path $dllOut) {
        Copy-Item $dllOut (Join-Path $Target 'NetDoctorNative.dll') -Force
        Write-Host "    已复制：NetDoctorNative.dll" -ForegroundColor Green
    }

    $doc = Join-Path $root 'docs\使用说明.md'
    if (Test-Path $doc) {
        Copy-Item $doc (Join-Path $Target '网络工具-使用说明.md') -Force
        Write-Host "    已复制：网络工具-使用说明.md" -ForegroundColor Green
    }
}

Write-Host '=== 5/5 完成 ===' -ForegroundColor Cyan
Get-ChildItem $outDir | ForEach-Object {
    Write-Host ("    {0,-30} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB))
}
Write-Host '    运行记录：logs\NetDoctor_YYYYMMDD.log'
Write-Host '    改动前快照：backup\backup.ini'
