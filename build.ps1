param(
    [string]$AcadDir = "",
    [string]$OutDir  = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# 自动识别 AutoCAD 安装目录：环境变量 → 注册表 → 标准路径
function Find-AcadDir {
    if ($env:ACAD_DIR -and (Test-Path (Join-Path $env:ACAD_DIR "acmgd.dll"))) {
        return $env:ACAD_DIR
    }

    $found = @()
    foreach ($r in @('HKLM:\SOFTWARE\Autodesk\AutoCAD', 'HKLM:\SOFTWARE\WOW6432Node\Autodesk\AutoCAD')) {
        if (-not (Test-Path $r)) { continue }
        foreach ($ver in Get-ChildItem $r) {
            foreach ($prod in Get-ChildItem $ver.PSPath) {
                $loc = (Get-ItemProperty $prod.PSPath -Name AcadLocation -ErrorAction SilentlyContinue).AcadLocation
                if ($loc -and (Test-Path (Join-Path $loc "acmgd.dll"))) {
                    $found += [pscustomobject]@{ Ver = $ver.PSChildName; Path = $loc.TrimEnd('\') }
                }
            }
        }
    }
    if ($found.Count -gt 0) {
        return ($found | Sort-Object Ver -Descending | Select-Object -First 1).Path
    }

    foreach ($guess in @('C:\Program Files\Autodesk\AutoCAD 2027', 'C:\Program Files\Autodesk\AutoCAD 2026',
                         'C:\Program Files\Autodesk\AutoCAD 2025', 'C:\Program Files\Autodesk\AutoCAD 2024')) {
        if (Test-Path (Join-Path $guess "acmgd.dll")) { return $guess }
    }
    return $null
}

if ([string]::IsNullOrWhiteSpace($AcadDir)) {
    $AcadDir = Find-AcadDir
    if (-not $AcadDir) {
        Write-Host "未找到 AutoCAD 安装目录。请指定：" -ForegroundColor Red
        Write-Host '  build.cmd "D:\CAD2017\AutoCAD 2027"' -ForegroundColor Yellow
        exit 1
    }
    Write-Host "自动识别 AutoCAD: $AcadDir" -ForegroundColor DarkGray
}

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $stamp  = Get-Date -Format "yyyyMMdd_HHmmss"
    $OutDir = Join-Path $root "build\$stamp"
}

Write-Host "编译到: $OutDir" -ForegroundColor Cyan

dotnet build (Join-Path $root "src\Nesting.AutoCAD\Nesting.AutoCAD.csproj") `
    -c Release -p:OutDir="$OutDir\" -p:AcadDir="$AcadDir" -v m

if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 只留运行时需要的文件，去掉调试符号和依赖清单
Get-ChildItem $OutDir -File |
    Where-Object { $_.Extension -eq ".pdb" -or $_.Name -like "*.deps.json" -or $_.Extension -eq ".xml" } |
    Remove-Item -Force -ErrorAction SilentlyContinue

# 同步到插件包，保证 bundle 里始终是最新产物
$bundleWin = Join-Path $root "CadNesting.bundle\Contents\Windows"
New-Item -ItemType Directory -Force -Path $bundleWin | Out-Null
Copy-Item (Join-Path $OutDir "Nesting.AutoCAD.dll") $bundleWin -Force
Copy-Item (Join-Path $OutDir "Nesting.Core.dll")  $bundleWin -Force

Set-Content -Path (Join-Path $root "build\latest.txt") -Value $OutDir -Encoding utf8

Write-Host ""
Write-Host "产物:" -ForegroundColor Green
Get-ChildItem $OutDir -File | ForEach-Object {
    "  {0,-24} {1,10:N1} KB" -f $_.Name, ($_.Length / 1KB)
}
Write-Host ""
Write-Host "在 AutoCAD 里 NETLOAD 这个文件:" -ForegroundColor Yellow
Write-Host "  $(Join-Path $OutDir 'Nesting.AutoCAD.dll')" -ForegroundColor Yellow
Write-Host "也可把 CadNesting.bundle 整个复制到 %AppData%\Autodesk\ApplicationPlugins\ 实现自动加载" -ForegroundColor DarkGray
