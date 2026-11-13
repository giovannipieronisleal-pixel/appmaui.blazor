param(
    [string]$PublishDir = "dist/YouLink-Windows",
    [string]$OutputZipPath = "dist-zip/YouLink-Windows-x64.zip",
    [string]$OutputInstallerDir = "dist-installer",
    [string]$Version = "1.0.11",
    [switch]$BuildInstaller,
    [switch]$RequireInstaller
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$resolvedPublishDir = if ([System.IO.Path]::IsPathRooted($PublishDir)) { $PublishDir } else { Join-Path $repoRoot $PublishDir }
$resolvedZipPath = if ([System.IO.Path]::IsPathRooted($OutputZipPath)) { $OutputZipPath } else { Join-Path $repoRoot $OutputZipPath }
$resolvedInstallerDir = if ([System.IO.Path]::IsPathRooted($OutputInstallerDir)) { $OutputInstallerDir } else { Join-Path $repoRoot $OutputInstallerDir }

if (-not (Test-Path (Join-Path $resolvedPublishDir "appmaui.blazor.exe"))) {
    throw "Executável publicado não encontrado em '$resolvedPublishDir'."
}

$stageRoot = Join-Path $repoRoot "obj\package-stage"
$bundleDir = Join-Path $stageRoot "YouLink"
$appDir = Join-Path $bundleDir "App"
$launcherObjDir = Join-Path $stageRoot "launcher-obj"

if (Test-Path $stageRoot) {
    Remove-Item -Path $stageRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $appDir -Force | Out-Null
New-Item -ItemType Directory -Path $launcherObjDir -Force | Out-Null

Write-Host "Copiando binários publicados para pasta organizada App\..."
Copy-Item -Path (Join-Path $resolvedPublishDir "*") -Destination $appDir -Recurse -Force

foreach ($sensitiveDir in @("App_Data")) {
    $candidate = Join-Path $appDir $sensitiveDir
    if (Test-Path $candidate) {
        Remove-Item -Path $candidate -Recurse -Force
    }
}
foreach ($sensitiveFile in @("clothingstore_db.json", "email_outbox.txt", "ClothingStore.AdminApp.exe")) {
    $candidate = Join-Path $appDir $sensitiveFile
    if (Test-Path $candidate) {
        Remove-Item -Path $candidate -Force
    }
}

$iconSource = Join-Path $appDir "appicon.ico"
if (-not (Test-Path $iconSource)) {
    throw "Ícone appicon.ico não encontrado em '$appDir'."
}

function Find-VcVars64 {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $installPaths = @()
    if (Test-Path $vswhere) {
        $found = & $vswhere -all -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
        if ($found) {
            $installPaths += $found
        }
    }

    foreach ($base in $installPaths) {
        $vcvars = Join-Path $base "VC\Auxiliary\Build\vcvars64.bat"
        if (Test-Path $vcvars) {
            return $vcvars
        }
    }

    $fallback = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Microsoft Visual Studio", "${env:ProgramFiles}\Microsoft Visual Studio" -Filter "vcvars64.bat" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($fallback) {
        return $fallback.FullName
    }
    return $null
}

$vcvars64 = Find-VcVars64
if (-not $vcvars64) {
    throw "Toolchain MSVC (vcvars64.bat) não encontrado para compilar YouLink.exe."
}

$verParts = ($Version -split '\.') + @("0", "0", "0", "0")
$verComma = "$($verParts[0]),$($verParts[1]),$($verParts[2]),$($verParts[3])"
$verStr = "$($verParts[0]).$($verParts[1]).$($verParts[2]).$($verParts[3])"

Copy-Item -Path (Join-Path $PSScriptRoot "launcher\YouLinkLauncher.cpp") -Destination $launcherObjDir -Force
Copy-Item -Path (Join-Path $PSScriptRoot "launcher\YouLinkLauncher.rc") -Destination $launcherObjDir -Force
Copy-Item -Path $iconSource -Destination (Join-Path $launcherObjDir "appicon.ico") -Force

$launcherExeOut = Join-Path $bundleDir "YouLink.exe"
$compileBat = Join-Path $launcherObjDir "build-launcher.cmd"
$compileScript = @"
@echo off
call "$vcvars64" >nul
if errorlevel 1 exit /b 1
pushd "$launcherObjDir"
rc.exe /nologo /DYOULINK_VER_COMMA=$verComma /DYOULINK_VER_STR=\"$verStr\" /fo YouLinkLauncher.res YouLinkLauncher.rc
if errorlevel 1 exit /b 1
cl.exe /nologo /O2 /MT /EHsc /DUNICODE /D_UNICODE YouLinkLauncher.cpp YouLinkLauncher.res /link /SUBSYSTEM:WINDOWS /OUT:"$launcherExeOut" kernel32.lib user32.lib
exit /b %errorlevel%
"@
Set-Content -Path $compileBat -Value $compileScript -Encoding ASCII

Write-Host "Compilando launcher nativo YouLink.exe com ícone oficial..."
& cmd.exe /c "`"$compileBat`""
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $launcherExeOut)) {
    throw "Falha ao compilar YouLink.exe launcher (código $LASTEXITCODE)."
}

$readmeTxt = @"
============================================================
  YouLink v$Version — Versão Windows (x64)
============================================================

COMO ABRIR O APLICATIVO:
1. Extraia TODA a pasta "YouLink" deste .zip para o seu computador
   (clique com o botão direito -> "Extrair Tudo...").
2. Entre na pasta extraída e clique duas vezes em:

      YouLink.exe

IMPORTANTE:
- Não apague nem separe a pasta "App" do "YouLink.exe":
  todas as bibliotecas e arquivos visuais ficam organizados lá dentro.
- Para instalação com atalho automático na Área de Trabalho,
  baixe o instalador "YouLink-Setup-x64.exe" na página de Releases.
"@
Set-Content -Path (Join-Path $bundleDir "LEIA-ME.txt") -Value $readmeTxt -Encoding UTF8

$zipParent = Split-Path -Path $resolvedZipPath -Parent
if (-not (Test-Path $zipParent)) {
    New-Item -ItemType Directory -Path $zipParent -Force | Out-Null
}
if (Test-Path $resolvedZipPath) {
    Remove-Item -Path $resolvedZipPath -Force
}

Write-Host "Compactando pacote organizado em $resolvedZipPath..."
Compress-Archive -Path $bundleDir -DestinationPath $resolvedZipPath -CompressionLevel Optimal -Force

if ($BuildInstaller -or $RequireInstaller) {
    $iscc = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if (-not $iscc -and $RequireInstaller) {
        Write-Host "Instalando Inno Setup 6 via Chocolatey..."
        choco install innosetup -y --no-progress | Out-Null
        $iscc = @(
            (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
        ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    }

    if ($iscc) {
        if (-not (Test-Path $resolvedInstallerDir)) {
            New-Item -ItemType Directory -Path $resolvedInstallerDir -Force | Out-Null
        }
        $issPath = Join-Path $PSScriptRoot "YouLink.iss"
        Write-Host "Gerando instalador Windows via Inno Setup ($iscc)..."
        & $iscc "/DMyAppVersion=$Version" "/DPackageDir=$bundleDir" "/DOutputSetupDir=$resolvedInstallerDir" $issPath
        if ($LASTEXITCODE -ne 0) {
            throw "ISCC.exe falhou com código $LASTEXITCODE."
        }
    }
    elseif ($RequireInstaller) {
        throw "ISCC.exe (Inno Setup 6) não encontrado para gerar YouLink-Setup-x64.exe."
    }
    else {
        Write-Warning "ISCC.exe não encontrado localmente; YouLink-Setup-x64.exe não foi gerado nesta máquina."
    }
}

Write-Host "Pacote pronto:"
Get-Item $resolvedZipPath | Select-Object Name, Length
$setupExe = Join-Path $resolvedInstallerDir "YouLink-Setup-x64.exe"
if (Test-Path $setupExe) {
    Get-Item $setupExe | Select-Object Name, Length
}
