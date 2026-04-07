#requires -Version 5.1
<#
.SYNOPSIS
    编译 ModConfig-SCAgent 并输出到游戏根目录下的 mods/ModConfig-SCAgent（与 ModConfigSCAgent.csproj 的 CopyMod 一致）。
.DESCRIPTION
    - 默认仅 dotnet build；PostBuild 会复制 sts2.scagent.modconfig.dll、ModConfigSCAgent.json（不复制 mod_manifest.json，避免与主清单同 id 重复）
    - -ExportPck：需 GODOT_MONO_EXE，导出 sts2.scagent.modconfig.pck（与清单 has_pck 一致）
.NOTES
    仓库假定位于：[游戏工程根]/mod_projects/scagent-ModConfig-STS2/
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$ExportPck,
    [string]$GodotExecutable = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$csproj = Join-Path $repoRoot "ModConfigSCAgent.csproj"
if (-not (Test-Path -LiteralPath $csproj)) {
    throw "找不到 ModConfigSCAgent.csproj：$csproj"
}

Write-Host "[ModConfig-SCAgent] dotnet build $Configuration ..." -ForegroundColor Cyan
dotnet build $csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$gameRoot = (Resolve-Path (Join-Path $repoRoot "..\..")).Path
$modsDir = Join-Path $gameRoot "mods\ModConfig-SCAgent"
Write-Host "[ModConfig-SCAgent] 已复制构建产物到：$modsDir" -ForegroundColor Green

if (-not $ExportPck) {
    Write-Host "[ModConfig-SCAgent] 跳过 PCK（需时请加 -ExportPck 并设置 GODOT_MONO_EXE）" -ForegroundColor DarkGray
    exit 0
}

$godot = $GodotExecutable
if ([string]::IsNullOrWhiteSpace($godot)) {
    $godot = [Environment]::GetEnvironmentVariable("GODOT_MONO_EXE", "User")
}
if ([string]::IsNullOrWhiteSpace($godot)) {
    $godot = [Environment]::GetEnvironmentVariable("GODOT_MONO_EXE", "Machine")
}
if ([string]::IsNullOrWhiteSpace($godot)) {
    $godot = $env:GODOT_MONO_EXE
}

if ([string]::IsNullOrWhiteSpace($godot) -or -not (Test-Path -LiteralPath $godot)) {
    throw "未找到 Godot Mono。请设置 GODOT_MONO_EXE 或使用 -GodotExecutable。"
}

$pckOut = Join-Path $modsDir "sts2.scagent.modconfig.pck"
$null = New-Item -ItemType Directory -Force -Path $modsDir

Write-Host "[ModConfig-SCAgent] 导出 PCK -> $pckOut" -ForegroundColor Cyan
& $godot --headless --path $repoRoot --export-pack "Windows Desktop" $pckOut
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "[ModConfig-SCAgent] 完成。" -ForegroundColor Green
