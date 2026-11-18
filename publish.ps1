<#
.SYNOPSIS
    测试并发布“双网卡路由配置工具”（C# 版），生成可直接分发的 exe。

.DESCRIPTION
    SelfContained       自包含单文件：exe 内置 .NET 运行时，目标电脑不需要安装任何东西（默认）。
    FrameworkDependent  依赖框架单文件：exe 很小，目标电脑需要先安装 .NET 10 桌面运行时。
    All                 两种都生成。

    输出目录：
      publish\self-contained\DualNicRouteTool.exe
      publish\framework-dependent\DualNicRouteTool.exe

.EXAMPLE
    .\publish.ps1
    运行单元测试，然后生成自包含单文件 exe。

.EXAMPLE
    .\publish.ps1 -Mode All -SkipTests
    跳过测试，两种 exe 都生成。
#>
[CmdletBinding()]
param(
    [ValidateSet('SelfContained', 'FrameworkDependent', 'All')]
    [string]$Mode = 'SelfContained',

    # 跳过单元测试（测试只使用内存中的假路由表，不会改动本机路由）
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "命令失败（退出码 $LASTEXITCODE）：dotnet $($Arguments -join ' ')"
    }
}

function Format-Size {
    param([long]$Bytes)

    if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
    return '{0:N0} KB' -f ($Bytes / 1KB)
}

Push-Location -LiteralPath $PSScriptRoot
try {
    # ---------- 检查 SDK ----------
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '未找到 dotnet 命令。请先安装 .NET SDK 10：https://dotnet.microsoft.com/download/dotnet/10.0'
    }

    $sdkVersion = (& dotnet --version).Trim()
    if ([int]($sdkVersion.Split('.')[0]) -lt 10) {
        throw "需要 .NET SDK 10 或更高版本，当前为 $sdkVersion。"
    }
    Write-Host "使用 .NET SDK $sdkVersion" -ForegroundColor DarkGray

    # ---------- 单元测试 ----------
    if (-not $SkipTests) {
        Write-Host ''
        Write-Host '==> 运行单元测试' -ForegroundColor Cyan
        Invoke-DotNet @('test', 'DualNicRouteTool.Tests\DualNicRouteTool.Tests.csproj', '-c', 'Release', '--nologo', '-v', 'q')
    }

    # ---------- 发布 ----------
    $targets = [ordered]@{
        SelfContained      = 'publish\self-contained'
        FrameworkDependent = 'publish\framework-dependent'
    }
    $selected = if ($Mode -eq 'All') { @($targets.Keys) } else { @($Mode) }

    $results = foreach ($profile in $selected) {
        Write-Host ''
        Write-Host "==> 发布 $profile" -ForegroundColor Cyan

        $exe = Join-Path $PSScriptRoot (Join-Path $targets[$profile] 'DualNicRouteTool.exe')
        try {
            Invoke-DotNet @('publish', 'DualNicRouteTool\DualNicRouteTool.csproj', '-c', 'Release', "-p:PublishProfile=$profile", '--nologo', '-v', 'q')
        }
        catch {
            if (Get-Process -Name DualNicRouteTool -ErrorAction SilentlyContinue) {
                Write-Warning '检测到 DualNicRouteTool.exe 正在运行。如果它正是上次发布出来的那个 exe，文件会被占用，请先关闭后重试。'
            }
            throw
        }

        if (-not (Test-Path -LiteralPath $exe)) {
            throw "发布完成，但没有找到 $exe"
        }

        '  {0,-19}{1,9}   {2}' -f $profile, (Format-Size (Get-Item -LiteralPath $exe).Length), $exe
    }

    Write-Host ''
    Write-Host '发布完成：' -ForegroundColor Green
    $results | ForEach-Object { Write-Host $_ }
    Write-Host ''

    if ($selected -contains 'FrameworkDependent') {
        Write-Host '提示：framework-dependent 版本需要目标电脑安装 .NET 10 桌面运行时（x64）。' -ForegroundColor DarkYellow
    }
}
finally {
    Pop-Location
}
