# 核心的两个发布版本（用户裁定 2026-10-23）：
#   win-x64 / linux-x64，**单文件**、自包含（目标机不需要装 .NET）。
#
# ⚠️ **本文件必须带 UTF-8 BOM**（文件开头那三个字节）：Windows PowerShell 5.1 会把**无 BOM** 的
#   UTF-8 当 ANSI 读，中文注释被拆成乱字节 ⇒ 解析直接失败，报的是一串「表达式或语句中包含意外的
#   标记」（2026-11-24 实测，真因只是编码）。改这个文件时别让 BOM 丢掉。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1 [-Version 1.1.0]
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
    [string]$Version = "1.1.0",
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

# ⚠️ **必须是 GNU tar**。Windows 自带的 `C:\Windows\System32\tar.exe` 是 bsdtar/libarchive：
#   它**不认** `--mode=755`（实测：`Option --mode=755 is not supported`），而不带它压出来的条目
#   记的是 `-rw-rw-rw-`（Windows 没有可执行位）⇒ 解到 Linux 上 Permission denied。
#   两者都正好是「解压就能跑」的反面，所以这里先把它找出来；找不到就当场说清楚，不静默降级。
#   ⚠️ 这条不是假想的：2026-11-24 在 PowerShell 里跑本脚本，`tar` 落到的就是 system32 那个 bsdtar，
#   于是 linux 包根本没生成（而 win 包已经出来、SHA256SUMS 也没写）——症状是「好像出了点问题」。
function Resolve-GnuTar {
    $candidates = @()
    $candidates += @(Get-Command tar -All -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
    $candidates += @(
        (Join-Path ${env:ProgramFiles} "Git\usr\bin\tar.exe")
        (Join-Path ${env:ProgramFiles(x86)} "Git\usr\bin\tar.exe")
        (Join-Path ${env:LOCALAPPDATA} "Programs\Git\usr\bin\tar.exe")
        "C:\msys64\usr\bin\tar.exe"
        "C:\msys2\usr\bin\tar.exe"
    )
    foreach ($c in $candidates) {
        if (-not $c -or -not (Test-Path $c)) { continue }
        # ⚠️ 先把整段输出收进数组再取第一条：`& cmd | Select-Object -First 1` 会在拿到第一条后
        # **关掉上游管道**，MSYS 的 tar.exe 于是带着非零退出码退出来（`$LASTEXITCODE` 被弄脏，
        # 而这一步本身根本不是判据）。实测踩过：自检那一步因此误报「tar -t 失败」。
        $vLines = @(& $c --version 2>$null)
        if ($vLines.Count -gt 0 -and ($vLines[0] -match "GNU tar")) { return $c }
    }
    throw ("找不到 GNU tar（PATH 上的 tar 是 bsdtar，不支持 --mode=755，压不出可执行的 linux 包）。" +
        " 装 Git for Windows / MSYS2 后重跑，或把它的 usr\bin 加到 PATH——CI 里由 release.yml 的" +
        "「GNU tar 进 PATH」那一步负责。")
}
$gnuTar = Resolve-GnuTar

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
            # ⚠️ 用**相对路径**：GNU tar 会把 `E:\…` 里的冒号当成「远端主机」
            # （`Cannot connect to E: resolve failed`）。相对路径没有冒号，两边都认。
            # ⚠️ 再显式给 `--mode=755`：产物是从 Windows 盘取的，没有它 tar 记下来的是
            # `-rw-rw-rw-` ⇒ 解到 Linux 上**跑不起来**（Permission denied），
            # 而那正是单文件包最不该有的意外。
            # ⚠️ 用 **$gnuTar 的绝对路径**而不是裸 `tar`：裸的那一个在 PowerShell 里会落到
            # C:\Windows\System32\tar.exe（bsdtar），它不认 --mode 也不记可执行位。
            $rel = "..\lilara-$Version-$rid.tar.gz"
            & $gnuTar --mode=755 -czf $rel "lilara"
            if ($LASTEXITCODE -ne 0) { throw "tar 失败：$rid" }

            # 自检：包里那一条**必须**是可执行位。没有这一句，「解压就能跑」只是注释里的一句话——
            # 而 bsdtar 的产物（-rw-rw-rw-）照样能生成一个看着完全正常的 .tar.gz。
            # ⚠️ 这一句也**必须用相对路径**（2026-11-24 实测）：传 `$tgz` 那个绝对路径时，
            # GNU tar 报 `Cannot connect to E: resolve failed` ⇒ 检查没跑成，
            # 而**包其实是好的**——一句话的失误会让下一双眼睛去怀疑压包那一步。
            $listing = @(& $gnuTar -tvzf $rel)
            if ($LASTEXITCODE -ne 0) { throw "tar -t 失败：$rid" }
            $entry = if ($listing.Count -gt 0) { $listing[0] } else { "" }
            if ($entry -notmatch "^-rwx") { throw "linux 包里的权限位不是可执行（$entry）：重跑前先确认用的是 GNU tar" }
        }
        finally { Pop-Location }
        Write-Host ("  {0} — {1:N1} MB" -f (Split-Path $tgz -Leaf), ((Get-Item $tgz).Length / 1MB))
    }
}
# 校验和：跟着发布包一起发出去。
# ⚠️ 用 `*` 前缀（sha256sum 的二进制模式写法）：下载下来可以直接 `sha256sum -c SHA256SUMS`。
$sums = Join-Path $out "SHA256SUMS"
$rows = foreach ($file in (Get-ChildItem $out -File -Filter "lilara-$Version-*" | Where-Object { $_.Extension -in ".zip", ".gz" } | Sort-Object Name)) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$($file.Name)"
}
# ⚠️ **必须**只数这一次发布的产物（2026-11-24 实测踩到）：`publish/` 是所有版本共用的目录，
# 上一版的 `.zip` 还在那里 ⇒ 不限定版本时校验和会把它、乃至手工调试留下的残包一起写进去，
# 而 `release.yml` 是把目录里**所有** `.zip`/`.gz` 挂上 Release 的 ⇒ 一次发布可能带上半年前的包。
if (@($rows).Count -ne @($Rids).Count) {
    throw ("校验和覆盖了 {0} 个产物，但这次只发布 {1} 个 RID——publish 里是不是有别的版本或残包？" -f @($rows).Count, @($Rids).Count)
}
# ⚠️ 用 WriteAllText 写 LF，不用 Set-Content：Set-Content 在 Windows PowerShell 上写 CRLF，
#    而 `sha256sum -c` 在 Linux 上会把行尾那个 \r 当成文件名的一部分（实测：No such file or directory）。
[System.IO.File]::WriteAllText($sums, (($rows -join "`n") + "`n"))
Write-Host ("SHA256SUMS — {0} 行" -f @($rows).Count)

Write-Host "产物在 $out"

