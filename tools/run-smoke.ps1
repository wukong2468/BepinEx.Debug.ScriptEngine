param(
    [int]$Port = 0,
    [string]$Configuration = "Debug",
    [switch]$SkipBuild,
    [switch]$Offline
)

$ErrorActionPreference = "Stop"

$tools = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $tools
$tests = Join-Path $tools "McpSmokeTests"
$assetRoot = Join-Path $tests "Assets"
$fake = Join-Path $tests "fakeroot"
$outside = "$fake-outside"
$report = Join-Path $tests "smoke-report.json"
$nugetCache = Join-Path $env:USERPROFILE ".nuget\packages"

function Invoke-Build([string]$project) {
    if ($SkipBuild) { return }

    $arguments = @("build", $project, "-c", $Configuration, "-v", "quiet", "--nologo")
    if ($Offline) {
        # 离线时用本地缓存还原，并关掉需要联网的漏洞审计（否则会刷 NU1900 警告）
        $arguments += "-p:RestoreSources=$nugetCache"
        $arguments += "-p:NuGetAudit=false"
    }

    & dotnet @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "构建失败: $project" }
}

Write-Host "=== 1/4 构建被测工程、测试宿主与测试资产 ==="
Invoke-Build (Join-Path $repo "ScriptDebugEngine\ScriptDebugEngine.csproj")
foreach ($name in @("Helper", "MissingRef", "Good", "GoodV2", "Weird", "Evil")) {
    Invoke-Build (Join-Path $assetRoot "$name\$name.csproj")
}
Invoke-Build (Join-Path $tests "McpSmokeTests.csproj")

Write-Host "=== 2/4 准备 fakeroot 测试目录 ==="
Remove-Item $fake -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $outside -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$fake`2" -Recurse -Force -ErrorAction SilentlyContinue   # D-06 用例造的"同前缀兄弟目录"
Remove-Item $report -Force -ErrorAction SilentlyContinue

$scripts = Join-Path $fake "scripts"
New-Item -ItemType Directory -Force $scripts | Out-Null

foreach ($name in @("Good", "GoodV2", "Weird", "MissingRef", "Evil")) {
    $dll = Join-Path $assetRoot "$name\bin\$Configuration\net35\$name.dll"
    if (-not (Test-Path $dll)) { throw "缺少测试资产: $dll" }
    Copy-Item $dll $scripts -Force
}

# 坏 DLL：零字节 / 原生 / 文本
[System.IO.File]::WriteAllBytes((Join-Path $scripts "zero.dll"), (New-Object byte[] 0))
Copy-Item (Join-Path $env:WINDIR "System32\advapi32.dll") (Join-Path $scripts "native.dll") -Force
Set-Content -Path (Join-Path $scripts "text.dll") -Value "not a dll" -Encoding ASCII

# 白名单之外的同名 DLL（符号链接 / junction 用例的目标）
New-Item -ItemType Directory -Force $outside | Out-Null
Copy-Item (Join-Path $scripts "Good.dll") $outside -Force

Write-Host "=== 3/4 选择端口 ==="
if ($Port -le 0) {
    $listener = New-Object System.Net.Sockets.TcpListener -ArgumentList ([System.Net.IPAddress]::Loopback), 0
    $listener.Start()
    $Port = $listener.LocalEndpoint.Port
    $listener.Stop()
}
Write-Host "使用端口 $Port"

Write-Host "=== 4/4 运行冒烟测试 ==="
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
& (Join-Path $tests "bin\$Configuration\net48\McpSmokeTests.exe") $fake $scripts $Port $report
$exitCode = $LASTEXITCODE
$stopwatch.Stop()

if (-not (Test-Path $report)) {
    Write-Host ""
    Write-Host "CRASHED：测试进程没有生成报告（可能被 StackOverflow 之类不可捕获的错误杀死）" -ForegroundColor Red
    exit 99
}

$data = Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host ""
Write-Host ("耗时 {0:N1}s   PASS={1} FAIL={2} SKIP={3}" -f $stopwatch.Elapsed.TotalSeconds, $data.summary.pass, $data.summary.fail, $data.summary.skip)

if ($data.summary.fail -gt 0) {
    Write-Host ""
    Write-Host "失败用例：" -ForegroundColor Red
    $data.cases | Where-Object { $_.result -eq "FAIL" } | ForEach-Object {
        Write-Host ("  {0}  {1}" -f $_.id, $_.name) -ForegroundColor Red
        Write-Host ("      {0}" -f $_.evidence) -ForegroundColor DarkRed
    }
}

Write-Host ""
Write-Host "报告：$report"
exit $exitCode
