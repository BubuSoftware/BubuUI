<#
.SYNOPSIS
    目标机 60fps 复测可复现脚本（B3）。
.DESCRIPTION
    固定顺序：Release 构建 -> CPU 渲染测试 -> RenderSpike bench（GPU 分支，无适配器优雅跳过）
    -> 可选 GalleryCheck 窗口帧测量 -> 可选 WindowSpike 分段耗时。
    采集 Win32_VideoController.CurrentRefreshRate / PresentMode / 帧平均·p95 /
    acquire·render·present 分段，写入 out/bench-60fps-<时间戳>.log，
    并打印 16.7ms 预算判定与「本机 29Hz 虚拟显示不下 60fps 结论」提示。

    默认不运行任何窗口测量（避免弹窗 / 需人工）。
    -RunWindowMeasure 开启 GalleryCheck；-IncludeWindowSpike 额外跑 WindowSpike 采集分段耗时。
    结果回填模板：design/reports/target-machine-60fps-template.md
.EXAMPLE
    powershell -File tools/bench-60fps.ps1
    powershell -File tools/bench-60fps.ps1 -RunWindowMeasure -IncludeWindowSpike -Iterations 20 -Frames 1200 -MaxFps 60
#>
[CmdletBinding()]
param(
    [switch]$RunWindowMeasure,
    [switch]$IncludeWindowSpike,
    [switch]$SkipBuild,
    [switch]$SkipCpuTests,
    [int]$Iterations = 20,
    [int]$Frames = 1200,
    [int]$MaxFps = 60,
    [string]$Configuration = "Release",
    [string]$LogPath = ""
)

$ErrorActionPreference = "Continue"
$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:LogPath = $LogPath
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $script:RepoRoot "out"
if ([string]::IsNullOrWhiteSpace($script:LogPath)) {
    $script:LogPath = Join-Path $outDir ("bench-60fps-" + $stamp + ".log")
}
$logDir = Split-Path -Parent $script:LogPath
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
if (Test-Path $script:LogPath) { Remove-Item $script:LogPath -Force }
New-Item -ItemType File -Path $script:LogPath -Force | Out-Null

function Write-Log {
    param([string]$Message)
    $line = "[" + (Get-Date -Format "HH:mm:ss") + "] " + $Message
    Write-Host $line
    Add-Content -Path $script:LogPath -Value $line
}

function Invoke-Step {
    param([string]$Label, [string[]]$DotnetArgs)
    Write-Log ("=== " + $Label + " : dotnet " + ($DotnetArgs -join " ") + " ===")
    $text = (& dotnet @DotnetArgs 2>&1 | Out-String)
    $code = $LASTEXITCODE
    if ($null -ne $text -and $text.Length -gt 0) {
        Add-Content -Path $script:LogPath -Value $text
        Write-Host $text
    }
    Write-Log ("--- 退出码: " + $code + " ---")
    return @{ Text = [string]$text; Exit = $code }
}

function Get-FirstMatch {
    param([string]$Text, [string]$Pattern, [string]$Default = "n/a")
    $m = [regex]::Match($Text, $Pattern)
    if ($m.Success -and $m.Groups.Count -gt 1) { return $m.Groups[1].Value }
    return $Default
}

function To-Double {
    param([string]$Value)
    try { return [double]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture) }
    catch { return [double]::NaN }
}

Push-Location $script:RepoRoot
try {
    Write-Log "========== EclipseUI 60fps 复测 =========="
    Write-Log ("时间: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
    Write-Log ("机器: " + $env:COMPUTERNAME)
    Write-Log ("参数: Configuration=" + $Configuration + " Iterations=" + $Iterations +
               " Frames=" + $Frames + " MaxFps=" + $MaxFps +
               " RunWindowMeasure=" + $RunWindowMeasure + " IncludeWindowSpike=" + $IncludeWindowSpike +
               " SkipBuild=" + $SkipBuild + " SkipCpuTests=" + $SkipCpuTests)
    Write-Log ("日志: " + $script:LogPath)

    # ---- 显示器刷新率（判定有效性的前置条件）----
    # 逐适配器列出并优先取「非虚拟」适配器：虚拟显示链（GameViewer/DisplayLink 等）自报的
    # 刷新率不代表渲染路径的有效节奏（2026-11-08 报告 §5：本机会采到虚拟适配器的 144Hz）。
    $refreshValue = [double]::NaN
    $refreshText = "n/a"
    $refreshSource = ""
    $virtualPattern = 'Virtual|GameViewer|DisplayLink|Spacedesk|Duet|SuperDisplay|indirect'
    try {
        $controllers = @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop)
        foreach ($c in $controllers) {
            $rate = [double]0
            if ($c.CurrentRefreshRate) { $rate = [double]$c.CurrentRefreshRate }
            $isVirtual = ($c.Name -match $virtualPattern)
            Write-Log ("适配器: " + $c.Name + " @ " + $rate + " Hz" + $(if ($isVirtual) { "（虚拟链路，仅供参考）" } else { "" }))
            if ($rate -le 0) { continue }
            if (-not $isVirtual -and [double]::IsNaN($refreshValue)) {
                $refreshValue = $rate
                $refreshText = [string]$rate
                $refreshSource = $c.Name
            }
        }
        if ([double]::IsNaN($refreshValue) -and $controllers.Count -gt 0) {
            # 全部是虚拟适配器：取第一个 >0 的并明确标注来源供人工甄别。
            foreach ($c in $controllers) {
                if ($c.CurrentRefreshRate -and $c.CurrentRefreshRate -gt 0) {
                    $refreshValue = [double]$c.CurrentRefreshRate
                    $refreshText = [string]$c.CurrentRefreshRate
                    $refreshSource = $c.Name + "（虚拟适配器，需人工甄别）"
                    break
                }
            }
        }
    } catch {
        Write-Log ("Win32_VideoController 查询失败: " + $_.Exception.Message)
    }
    Write-Log ("CurrentRefreshRate: " + $refreshText + " Hz（来源: " + $refreshSource + "；判定要求 >= 60 Hz）")

    $combined = ""

    # ---- ① Release 构建 ----
    if (-not $SkipBuild) {
        $r = Invoke-Step "① Release 构建 (EclipseUI.slnx)" @("build", "EclipseUI.slnx", "-c", $Configuration)
        $combined += $r.Text
        if ($r.Exit -ne 0) { Write-Log ("警告: 构建退出码 " + $r.Exit + "，后续结果可能无效") }
    } else {
        Write-Log "① 跳过构建 (-SkipBuild)"
    }

    # ---- ② CPU 渲染测试 ----
    if (-not $SkipCpuTests) {
        $r = Invoke-Step "② CPU 渲染测试 (tests/EclipseUI.Rendering.Tests)" @("test", "tests/EclipseUI.Rendering.Tests", "-c", $Configuration)
        $combined += $r.Text
        if ($r.Exit -ne 0) { Write-Log ("警告: CPU 渲染测试退出码 " + $r.Exit) }
    } else {
        Write-Log "② 跳过 CPU 渲染测试 (-SkipCpuTests)"
    }

    # ---- ③ RenderSpike bench（GPU 分支优雅跳过）----
    $env:RENDERSPIKE_ITERATIONS = [string]$Iterations
    $r = Invoke-Step "③ RenderSpike bench (GPU 分支)" @("run", "--project", "sandbox/RenderSpike", "-c", $Configuration, "--", "bench")
    $combined += $r.Text
    if ($r.Text -match "NaN|useCpu=False failed|no GPU|adapter") {
        Write-Log "③ GPU 分支：GPU 列为空/NaN 或 stderr 报 failed（适配器本次不可用），已优雅跳过 GPU 判定；仅 CPU 数据有效，不下 60fps 结论。"
    } else {
        Write-Log "③ GPU 分支：bench 已执行；若上表 GPU 列仍为 NaN，则以 CPU 数据为准。"
    }

    # ---- ④ GalleryCheck 窗口帧测量（可开关）----
    $galleryText = ""
    if ($RunWindowMeasure) {
        Write-Log "④ GalleryCheck 窗口帧测量：将打开真实窗口，--frames $Frames 后自动退出（需人工确保窗口前台）。"
        $r = Invoke-Step "④ GalleryCheck" @("run", "--project", "sandbox/GalleryCheck", "-c", $Configuration, "--", "--always-render", "--no-vsync", "--max-fps", [string]$MaxFps, "--frames", [string]$Frames)
        $galleryText = $r.Text
        $combined += $r.Text
        if ($r.Exit -ne 0) { Write-Log ("警告: GalleryCheck 退出码 " + $r.Exit) }
    } else {
        Write-Log "④ 跳过窗口帧测量 (-RunWindowMeasure 未开启；避免弹窗/人工介入)，无法据此判定 16.7ms 预算。"
    }

    # ---- ⑤ WindowSpike 分段耗时（可开关）----
    $spikeText = ""
    if ($IncludeWindowSpike) {
        if (-not $RunWindowMeasure) {
            Write-Log "⑤ 提示: -IncludeWindowSpike 建议配合 -RunWindowMeasure；仍单独执行 WindowSpike。"
        }
        $r = Invoke-Step "⑤ WindowSpike 分段耗时" @("run", "--project", "sandbox/WindowSpike", "-c", $Configuration, "--", "--frames", [string]$Frames, "--no-vsync", "--max-fps", [string]$MaxFps)
        $spikeText = $r.Text
        $combined += $r.Text
    } else {
        Write-Log "⑤ 跳过 WindowSpike 分段采集 (-IncludeWindowSpike 未开启)。"
    }

    # ---- 字段采集 ----
    $presentMode = Get-FirstMatch -Text $galleryText -Pattern "present=([A-Za-z0-9_]+)" -Default "n/a"
    if ($presentMode -eq "n/a" -and $combined -match "present=([A-Za-z0-9_]+)") {
        $presentMode = $Matches[1]
    }
    $frameAvg = Get-FirstMatch -Text $galleryText -Pattern "frame=([0-9]+(?:\.[0-9]+)?)ms" -Default "n/a"
    $frameP95 = Get-FirstMatch -Text $galleryText -Pattern "p95=([0-9]+(?:\.[0-9]+)?)ms" -Default "n/a"
    if ($frameAvg -eq "n/a" -and $spikeText.Length -gt 0) {
        $frameAvg = Get-FirstMatch -Text $spikeText -Pattern "frame=([0-9]+(?:\.[0-9]+)?)ms" -Default "n/a"
        $frameP95 = Get-FirstMatch -Text $spikeText -Pattern "p95=([0-9]+(?:\.[0-9]+)?)ms" -Default "n/a"
    }
    $acquireMs = Get-FirstMatch -Text $spikeText -Pattern "stages:\s*acquire=([0-9]+(?:\.[0-9]+)?)ms" -Default "n/a"
    $renderMs  = Get-FirstMatch -Text $spikeText -Pattern "render=([0-9]+(?:\.[0-9]+)?)ms" -Default "n/a"
    $presentMs = Get-FirstMatch -Text $spikeText -Pattern "present=([0-9]+(?:\.[0-9]+)?)ms" -Default "n/a"

    Write-Log "---- 采集汇总 ----"
    Write-Log ("PresentMode       : " + $presentMode)
    Write-Log ("帧平均 avg        : " + $frameAvg + " ms")
    Write-Log ("帧 p95            : " + $frameP95 + " ms")
    Write-Log ("分段 acquire      : " + $acquireMs + " ms")
    Write-Log ("分段 render       : " + $renderMs + " ms")
    Write-Log ("分段 present      : " + $presentMs + " ms")

    # ---- 16.7ms 预算判定 ----
    $budget = 16.7
    $avg = To-Double $frameAvg
    $p95 = To-Double $frameP95
    Write-Log "---- 16.7ms 预算判定 ----"
    if ([double]::IsNaN($avg) -or [double]::IsNaN($p95)) {
        Write-Log "未判定：未采集到帧平均/p95（需 -RunWindowMeasure；分段耗时另需 -IncludeWindowSpike）。"
    } else {
        Write-Log ("实测: avg=" + $frameAvg + "ms, p95=" + $frameP95 + "ms, 预算=" + $budget + "ms")
        if ($avg -le $budget -and $p95 -le $budget) {
            Write-Log "判定: PASS —— 平均与 p95 均在 16.7ms 预算内。"
        } else {
            Write-Log "判定: FAIL —— 超出 16.7ms 预算（需结合 acquire/render/present 分段定位）。"
        }
        if ($p95 -gt $budget) {
            Write-Log ("p95 超出预算 " + [string]([math]::Round($p95 - $budget, 2)) + " ms")
        }
    }

    # ---- 环境限制提示 ----
    if ([double]::IsNaN($refreshValue)) {
        Write-Log "注意: 未采集到有效刷新率；窗口测量受刷新率/vsync 限制，本次帧时间不能作为 60fps 达标依据。"
    } elseif ($refreshValue -lt 60) {
        Write-Log ("注意: CurrentRefreshRate=" + $refreshText + "Hz < 60Hz；窗口测量受刷新率/vsync 限制，本次帧时间不能作为 60fps 达标依据，结论记「不适用」。")
    } elseif ($refreshSource -match $virtualPattern) {
        Write-Log ("注意: 刷新率来自虚拟适配器（" + $refreshSource + "）；虚拟链的合成/串流节奏可能与自报值不符，上屏侧结论需人工甄别。")
    } else {
        Write-Log ("刷新率 " + $refreshText + " Hz 满足 >=60Hz 前置条件（来源: " + $refreshSource + "）。")
    }
    Write-Log "帧成本判定（avg/p95 <= 16.7ms）只反映渲染管线工作成本；「墙钟稳定 60fps」还需 vsync 模式下墙钟 fps >= 59（见 design/reports/target-machine-60fps-2026-11-08.md §4 的两级口径）。"
    Write-Log "请在 >=60Hz 物理显示器目标机复测（步骤见该报告「物理目标机复测指引」节），并回填 design/reports/target-machine-60fps-template.md。"
    Write-Log ("日志已写入: " + $script:LogPath)
    Write-Log "========== 结束 =========="
}
finally {
    Pop-Location
}
