# 夕颜若雪网络工具 —— 发布脚本
#
# 用法：
#   .\publish.ps1                              发布到 publish\，并复制到默认目录（存在才复制）
#   .\publish.ps1 -Target 'D:\out'             指定复制目标
#   .\publish.ps1 -NoDeploy                    只产出到 publish\，不复制到任何目录
#   .\publish.ps1 -SkipNative                  跳过原生 DLL（没装 MSVC 工具链时用）
#   .\publish.ps1 -SkipTest                    跳过原生 DLL 契约测试 + 压力测试
#
# 产出：
#   publish\夕颜若雪网络工具.exe     单文件自包含主程序（WinForms）
#   publish\NetDoctorNative.dll      x86 原生 DLL，给 32 位易语言宿主调用

[CmdletBinding()]
param(
    [string]$Target,
    [switch]$NoDeploy,
    [switch]$SkipNative,
    [switch]$SkipTest
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $root 'publish'

# 默认复制目标：仅当该目录存在时才用（从源码 clone 的人通常没有这个目录）
$DefaultTarget = 'E:\挂\xiyanruoxue'
if (-not $Target) {
    if (Test-Path $DefaultTarget) { $Target = $DefaultTarget }
    else { $NoDeploy = $true }
}

function Section($text) { Write-Host "=== $text ===" -ForegroundColor Cyan }
function Good($text)    { Write-Host "    $text" -ForegroundColor Green }
function Warn($text)    { Write-Host "    $text" -ForegroundColor Yellow }
function Dim($text)     { Write-Host "    $text" -ForegroundColor DarkGray }

Section '1/5 停止正在运行的实例'
Get-Process -Name '夕颜若雪网络工具' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Section '2/5 发布主程序（单文件自包含）'
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

dotnet publish (Join-Path $root 'src') `
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
Good ("夕颜若雪网络工具.exe  {0} MB" -f [math]::Round((Get-Item $exe).Length / 1MB, 1))

# 再复制一份 ASCII 文件名：GitHub Release 对非 ASCII 附件名支持有问题
# （中文名上传后会变成 default.exe），发版时用这一份
$exeAscii = Join-Path $outDir 'NetDoctor-win-x64.exe'
Copy-Item $exe $exeAscii -Force
Dim ("NetDoctor-win-x64.exe  {0} MB  （ASCII 名副本，用于 GitHub Release）" -f [math]::Round((Get-Item $exeAscii).Length / 1MB, 1))

$nativeProj = Join-Path $root 'native\NetDoctorNative.csproj'
if (-not $SkipNative -and -not (Test-Path $nativeProj)) {
    Warn '未找到 native\NetDoctorNative.csproj，自动跳过原生 DLL'
    $SkipNative = $true
}

if (-not $SkipNative) {
    Section '3/5 发布 x86 原生 DLL（NativeAOT）'
    Dim '需要 Visual Studio 的 C++ 生成工具与 Windows SDK'

    dotnet publish $nativeProj -c Release -r win-x86 --nologo
    if ($LASTEXITCODE -ne 0) {
        throw '原生 DLL 发布失败。若本机没有 MSVC 工具链，请改用 -SkipNative 只发布主程序。'
    }

    $dll = Join-Path $root 'native\bin\Release\net10.0\win-x86\publish\NetDoctorNative.dll'
    if (-not (Test-Path $dll)) { throw "未生成 DLL：$dll" }
    Copy-Item $dll $outDir -Force
    Good ("NetDoctorNative.dll  {0} MB" -f [math]::Round((Get-Item $dll).Length / 1MB, 1))

    # 每个测试宿主用独立输出目录：
    # 三个测试项目的默认输出路径完全相同，共用会互相覆盖产物
    # （曾经因此让 CrashProbe.exe 凭空消失，排查了很久）。
    $tdir = Join-Path $root '_t'
    if (Test-Path $tdir) { Remove-Item $tdir -Recurse -Force }

    # ---- 契约测试 ----
    $ntestProj = Join-Path $root 'native-test\NetDoctorNativeTest.csproj'
    if (-not $SkipTest -and (Test-Path $ntestProj)) {
        Dim '正在跑原生 DLL 契约测试（x86 宿主模拟易语言调用）…'
        $tpub = Join-Path $tdir 'contract'
        dotnet publish $ntestProj -c Release -r win-x86 --nologo -o $tpub | Out-Null
        Copy-Item $dll (Join-Path $tpub 'NetDoctorNative.dll') -Force
        Push-Location $tpub
        # 不加 Select-Object 之类的管道：那会把输出缓冲到进程结束才显示，
        # 卡住时完全看不到进度。直接透传，再单独取末尾几行做摘要。
        & '.\NetDoctorNativeTest.exe' --ci
        $code = $LASTEXITCODE
        Pop-Location
        if ($code -ne 0) { throw "原生 DLL 契约测试未通过（退出码 $code）" }
        Good '原生 DLL 契约测试通过'
    }

    # ---- 页边界 / 跨页参数探测 ----
    # 「页尾起始 + 次页不可访问」这类布局曾让宿主进程直接消失，
    # 普通功能测试完全测不出来，所以单独作为一道门槛。
    $probeProj = Join-Path $root 'native-test\CrashProbe.csproj'
    if (-not $SkipTest -and (Test-Path $probeProj)) {
        Dim '正在跑原生 DLL 页边界探测（非法指针 / 页尾 / 跨页）…'
        $ppub = Join-Path $tdir 'probe'
        dotnet publish $probeProj -c Release -r win-x86 --nologo -o $ppub | Out-Null
        Copy-Item $dll (Join-Path $ppub 'NetDoctorNative.dll') -Force
        Push-Location $ppub
        $probeBad = @()
        foreach ($k in 'z', '1', 'm', 's', 'h', 't', 'p', 'q', 'y', 'w') {
            & '.\CrashProbe.exe' $k *> $null
            if ($LASTEXITCODE -ne 0) { $probeBad += "$k(exit=$LASTEXITCODE)" }
        }
        Pop-Location
        if ($probeBad.Count -gt 0) {
            throw "页边界探测失败，以下布局导致宿主异常退出：$($probeBad -join ', ')"
        }
        Good '页边界探测通过（10 种布局均未崩溃）'
    }

    # ---- 压力 / 边界测试 ----
    $stestProj = Join-Path $root 'native-test\StressTest.csproj'
    if (-not $SkipTest -and (Test-Path $stestProj)) {
        Dim '正在跑原生 DLL 压力测试（非法指针 / 并发 / 页边界 / 缓冲区）…'
        $spub = Join-Path $tdir 'stress'
        dotnet publish $stestProj -c Release -r win-x86 --nologo -o $spub | Out-Null
        Copy-Item $dll (Join-Path $spub 'NetDoctorNative.dll') -Force
        Push-Location $spub
        & '.\NetDoctorStressTest.exe'
        $code = $LASTEXITCODE
        Pop-Location
        if ($code -ne 0) { throw "原生 DLL 压力测试未通过（退出码 $code）" }
        Good '原生 DLL 压力测试通过'
    }

    # ---- 核心引擎自测 ----
    # 24 组全是只读的（枚举 / 读取 / 连通性检测），不会改动系统，
    # 所以适合放在发布前的门槛里。此前它只在 CI 跑，本地发布时漏掉了。
    $selfProj = Join-Path $root 'selftest\SelfTest.csproj'
    if (-not $SkipTest -and (Test-Path $selfProj)) {
        Dim '正在跑核心引擎自测（网卡/DNS/优化配置/清理目标/硬件/工具扫描）…'
        $spub2 = Join-Path $tdir 'self'
        dotnet build $selfProj -c Release --nologo -o $spub2 | Out-Null
        $selfExe = Join-Path $spub2 'NetDoctorSelfTest.exe'
        if (Test-Path $selfExe) {
            & $selfExe
            $code = $LASTEXITCODE
            if ($code -ne 0) { throw "核心引擎自测未通过（退出码 $code）" }
            Good '核心引擎自测通过'
        } else {
            Warn '未生成 NetDoctorSelfTest.exe，跳过'
        }
    }

    # ---- 安全中心体检自测 ----
    # 新功能之前只在 CI 之外单独跑，本地发布时不会验证到，这里补进门槛。
    $defProj = Join-Path $root 'defender-test\DefenderTest.csproj'
    if (-not $SkipTest -and (Test-Path $defProj)) {
        Dim '正在跑安全中心体检自测（判定准确性 / 只读性 / 解析健壮性）…'
        $dpub = Join-Path $tdir 'defender'
        dotnet build $defProj -c Release --nologo -o $dpub | Out-Null
        $defExe = Join-Path $dpub 'NetDoctorDefenderTest.exe'
        if (Test-Path $defExe) {
            & $defExe
            $code = $LASTEXITCODE
            if ($code -ne 0) { throw "安全中心体检自测未通过（退出码 $code）" }
            Good '安全中心体检自测通过'
        } else {
            Warn '未生成 NetDoctorDefenderTest.exe，跳过'
        }
    }
} else {
    Section '3/5 跳过原生 DLL'
}

Section '4/5 复制到目标目录'
if ($NoDeploy) {
    Dim '未指定目标目录（-NoDeploy，或默认目录不存在），产物只在 publish\'
} else {
    if (-not (Test-Path $Target)) {
        New-Item -ItemType Directory -Path $Target -Force | Out-Null
    }
    Copy-Item $exe (Join-Path $Target '夕颜若雪网络工具.exe') -Force
    Good '已复制：夕颜若雪网络工具.exe'

    $dllOut = Join-Path $outDir 'NetDoctorNative.dll'
    if (Test-Path $dllOut) {
        Copy-Item $dllOut (Join-Path $Target 'NetDoctorNative.dll') -Force
        Good '已复制：NetDoctorNative.dll'
    }

    $doc = Join-Path $root 'docs\使用说明.md'
    if (Test-Path $doc) {
        Copy-Item $doc (Join-Path $Target '网络工具-使用说明.md') -Force
        Good '已复制：网络工具-使用说明.md'
    }
}

Section '5/5 完成'
Get-ChildItem $outDir | ForEach-Object {
    Dim ("{0,-28} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB))
}
Dim '运行记录：logs\NetDoctor_YYYYMMDD.log'
Dim '改动前快照：backup\backup.ini'
