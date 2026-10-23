# 核心的两个发布版本（用户裁定 2026-10-23）：
#   win-x64 / linux-x64，**单文件**、自包含（目标机不需要装 .NET）。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1 [-Version 0.1.0]
#
# 产物：publish\lilara-<version>-win-x64.zip / lilara-<version>-linux-x64.tar.gz
#       每个压缩包里是**一个可执行文件**（自解压单文件：.NET 运行时打进 exe）。
#
# ⚠️ 两个必须一起给的开关：
#   · PublishSingleFile —— 单文件；.NET 10 默认**不**把原生库内嵌，所以还要下一条
#   · IncludeNativeLibrariesForSelfExtract —— 否则单文件版第一次跑会在临时目录找不到
#     Microsoft.AspNetCore.App 的原生库（核心托管 Kestrel / WS），报的是与真因隔一层的错。
# ⚠️ 单文件版**启用压缩**（`EnableCompressionInSingleFile`，默认关）：实测 .NET 10 的
#   自解压单文件在默认设置下每次启动会把内嵌的原生库解压到临时目录，压缩后包从 ~106 MB 降到 ~45 MB，
#   代价是首次启动多几十毫秒。这里选体积。
param(
    [string]$Version = "0.1.0",
    [string[]]$Rids = @("win-x64", "linux-x64")
)
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$proj = Join-Path $root "src\Lilara.Cli\Lilara.Cli.csproj"
$out = Join-Path $root "publish"
New-Item -ItemType Directory -Force -Path $out | Out-Null

foreach ($rid in $Rids) {
    $dir = Join-Path $out "lilara-$Version-$rid"
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }

    Write-Host "=== publish $rid" -ForegroundColor Cyan
    dotnet publish $proj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:Version=$Version `
        -o $dir
    if ($LASTEXITCODE -ne 0) { throw "publish 失败：$rid" }

    # 单文件的判据：产物目录里应当只剩可执行文件 + 少量附属文件（pdb 除外）。
    $exe = if ($rid -like "win-*") { Join-Path $dir "lilara.exe" } else { Join-Path $dir "lilara" }
    if (-not (Test-Path $exe)) { throw "缺可执行文件：$exe" }
    Remove-Item (Join-Path $dir "*.pdb") -Force -ErrorAction SilentlyContinue

    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    $other = @(Get-ChildItem $dir -File | Where-Object { $_.FullName -ne (Get-Item $exe).FullName }).Count
    Write-Host ("  {0} — {1} MB；目录里其它文件 {2} 个" -f (Split-Path $exe -Leaf), $size, $other)
}

# 压缩包：每个 RID 一个，解压出来就是那**一个**可执行文件。
foreach ($rid in $Rids) {
    $dir = Join-Path $out "lilara-$Version-$rid"
    $exe = if ($rid -like "win-*") { Join-Path $dir "lilara.exe" } else { Join-Path $dir "lilara" }
    if ($rid -like "win-*") {
        $zip = Join-Path $out "lilara-$Version-$rid.zip"
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path $exe -DestinationPath $zip -CompressionLevel Optimal
        Write-Host ("  {0} — {1:N1} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB))
    } else {
        # ⚠️ 用 tar 而不是 Compress-Archive：后者在 Windows 上**存不住 unix 可执行位**，
        # 解到 Linux 上还得手动 chmod +x——而「解压就能跑」正是单文件版的全部意义。
        $tgz = Join-Path $out "lilara-$Version-$rid.tar.gz"
        Remove-Item $tgz -Force -ErrorAction SilentlyContinue
        Push-Location $dir
        try {
            # ⚠️ 用**相对路径**：PATH 上的 tar 是 MSYS 的 GNU tar，它把 `E:\…` 里的冒号当成
            # 「远端主机」（`Cannot connect to E: resolve failed`）。相对路径没有冒号，两边都认。
            # ⚠️ 再显式给 `--mode=755`：产物是从 Windows 盘取的，tar 记下来的是 -rw-r--r--
            # ⇒ 解到 Linux 上**跑不起来**（Permission denied），而那正是单文件包最不该有的意外。
            tar --mode=755 -czf "..\lilara-$Version-$rid.tar.gz" "lilara"
            if ($LASTEXITCODE -ne 0) { throw "tar 失败：$rid" }
        }
        finally { Pop-Location }
        Write-Host ("  {0} — {1:N1} MB" -f (Split-Path $tgz -Leaf), ((Get-Item $tgz).Length / 1MB))
    }
}
Write-Host "产物在 $out"
