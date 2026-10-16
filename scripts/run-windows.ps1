# Blazma Animation - Windows helper script (PowerShell 5.1 compatible)
#
# Usage (from the repo root):
#   powershell -ExecutionPolicy Bypass -File scripts\run-windows.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\run-windows.ps1 -GodotPath "C:\Tools\Godot"
#   powershell -ExecutionPolicy Bypass -File scripts\run-windows.ps1 -Editor
#   powershell -ExecutionPolicy Bypass -File scripts\run-windows.ps1 -Test

param(
    [string]$GodotPath = "",
    [switch]$Editor,
    [switch]$Test
)

$ErrorActionPreference = "Stop"

$DotnetDownloadUrl = "https://dotnet.microsoft.com/download"
$GodotDownloadUrl  = "https://godotengine.org/download/archive/4.7.2-stable/"
$GodotFilePattern  = "Godot_v4.7*mono_win64.exe"

function Write-Bilingual {
    param(
        [string]$En,
        [string]$Ar,
        [string]$Color = "Cyan"
    )
    Write-Host "$En / $Ar" -ForegroundColor $Color
}

function Stop-WithMessage {
    param(
        [string]$En,
        [string]$Ar
    )
    Write-Bilingual -En $En -Ar $Ar -Color Red
    exit 1
}

# Searches a folder for the Godot .NET executable, going down at most $Depth levels.
# Get-ChildItem -Depth is not available in Windows PowerShell 5.1, so depth is tracked manually.
function Find-GodotExe {
    param(
        [string]$Folder,
        [int]$Depth
    )

    if ($Depth -lt 0 -or -not (Test-Path -LiteralPath $Folder -PathType Container)) {
        return $null
    }

    $hits = Get-ChildItem -LiteralPath $Folder -File -Filter $GodotFilePattern -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike "*console*" }
    if ($hits) {
        return ($hits | Select-Object -First 1).FullName
    }

    $subFolders = Get-ChildItem -LiteralPath $Folder -Directory -ErrorAction SilentlyContinue
    foreach ($sub in $subFolders) {
        $found = Find-GodotExe -Folder $sub.FullName -Depth ($Depth - 1)
        if ($found) {
            return $found
        }
    }

    return $null
}

# ---------------------------------------------------------------
# Paths
# ---------------------------------------------------------------
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$ProjectDir  = $RepoRoot + "\app\godot"
$CsprojPath  = $ProjectDir + "\Blazma.UI.csproj"
$SolutionPath = $RepoRoot + "\Blazma.slnx"

Write-Bilingual -En "Repo folder" -Ar "مجلد المشروع" -Color Gray
Write-Host "  $RepoRoot"

# ---------------------------------------------------------------
# 1. Check .NET SDK 8 (needed by Godot .NET)
# ---------------------------------------------------------------
Write-Bilingual -En "Checking .NET SDK..." -Ar "جارٍ التحقق من .NET SDK..."

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Stop-WithMessage `
        -En "'dotnet' was not found. Install .NET SDK 8 from: $DotnetDownloadUrl" `
        -Ar "لم يتم العثور على dotnet. ثبّت .NET SDK 8 من: $DotnetDownloadUrl"
}

$sdkLines = & dotnet --list-sdks 2>$null
$hasSdk8  = @($sdkLines | Where-Object { $_ -match '^8\.' }).Count -gt 0
$hasSdk10 = @($sdkLines | Where-Object { $_ -match '^10\.' }).Count -gt 0

if (-not $hasSdk8) {
    Stop-WithMessage `
        -En ".NET SDK 8 was not found. Install it from: $DotnetDownloadUrl" `
        -Ar "لم يتم العثور على .NET SDK 8. ثبّته من: $DotnetDownloadUrl"
}

Write-Bilingual -En ".NET SDK 8 found" -Ar "تم العثور على .NET SDK 8" -Color Green

# ---------------------------------------------------------------
# 2. Tests only
# ---------------------------------------------------------------
if ($Test) {
    if (-not $hasSdk10) {
        Write-Bilingual -En "Warning: .NET SDK 10 is needed to run the tests. Install it from: $DotnetDownloadUrl" `
                        -Ar "تحذير: تحتاج .NET SDK 10 لتشغيل الاختبارات. ثبّته من: $DotnetDownloadUrl" -Color Yellow
    }

    Write-Bilingual -En "Running tests..." -Ar "جارٍ تشغيل الاختبارات..."
    & dotnet test "$SolutionPath"
    $testExit = $LASTEXITCODE

    if ($testExit -eq 0) {
        Write-Bilingual -En "Tests passed" -Ar "نجحت الاختبارات" -Color Green
    } else {
        Write-Bilingual -En "Tests failed (exit code $testExit)" -Ar "فشلت الاختبارات (رمز الخروج $testExit)" -Color Red
    }
    exit $testExit
}

# ---------------------------------------------------------------
# 3. Find Godot 4.7.2 .NET
# ---------------------------------------------------------------
Write-Bilingual -En "Looking for Godot 4.7.2 (.NET)..." -Ar "جارٍ البحث عن Godot 4.7.2 (.NET)..."

$godotExe = $null

if ($GodotPath -ne "") {
    if (Test-Path -LiteralPath $GodotPath -PathType Leaf) {
        $godotExe = (Resolve-Path -LiteralPath $GodotPath).Path
    } elseif (Test-Path -LiteralPath $GodotPath -PathType Container) {
        $godotExe = Find-GodotExe -Folder (Resolve-Path -LiteralPath $GodotPath).Path -Depth 2
    } else {
        Stop-WithMessage `
            -En "The -GodotPath you gave does not exist: $GodotPath" `
            -Ar "المسار الذي أعطيته في -GodotPath غير موجود: $GodotPath"
    }
} else {
    $searchFolders = @()
    if ($env:GODOT_PATH) { $searchFolders += $env:GODOT_PATH }
    $searchFolders += "C:\Tools\Godot"
    $searchFolders += "$env:LOCALAPPDATA\Programs\Godot"
    $searchFolders += "$env:USERPROFILE\Downloads"

    foreach ($folder in $searchFolders) {
        if (-not $folder) { continue }

        if (Test-Path -LiteralPath $folder -PathType Leaf) {
            $godotExe = (Resolve-Path -LiteralPath $folder).Path
        } else {
            $godotExe = Find-GodotExe -Folder $folder -Depth 3
        }

        if ($godotExe) { break }
    }
}

if (-not $godotExe) {
    Stop-WithMessage `
        -En "Godot 4.7.2 .NET was not found. Download the .NET (mono) version from: $GodotDownloadUrl , unzip it, then run again with -GodotPath `"C:\path\to\Godot_v4.7.2-stable_mono_win64.exe`"" `
        -Ar "لم يتم العثور على Godot 4.7.2 .NET. حمّل نسخة .NET (mono) من: $GodotDownloadUrl ثم فك الضغط وشغّل السكربت مرة أخرى مع -GodotPath"
}

Write-Bilingual -En "Godot found: $godotExe" -Ar "تم العثور على Godot: $godotExe" -Color Green

# ---------------------------------------------------------------
# 4. Build
# ---------------------------------------------------------------
Write-Bilingual -En "Building..." -Ar "جارٍ البناء..."
& dotnet build "$CsprojPath" -c Debug
if ($LASTEXITCODE -ne 0) {
    Stop-WithMessage `
        -En "Build failed. Read the errors above. If it is the first build, check your internet connection (NuGet)." `
        -Ar "فشل البناء. اقرأ الأخطاء أعلاه. إذا كان هذا أول بناء، تحقق من اتصال الإنترنت (NuGet)."
}
Write-Bilingual -En "Build succeeded" -Ar "نجح البناء" -Color Green

# ---------------------------------------------------------------
# 5. First-run import (Godot must import assets once before it runs)
# ---------------------------------------------------------------
Write-Bilingual -En "Importing project assets (first run may take a while)..." -Ar "جارٍ استيراد ملفات المشروع (قد يستغرق ذلك وقتاً في أول مرة)..."
$importProc = Start-Process -FilePath $godotExe `
    -ArgumentList "--headless --path `"$ProjectDir`" --import" `
    -Wait -NoNewWindow -PassThru
if ($importProc.ExitCode -ne 0) {
    Write-Bilingual -En "Warning: import finished with exit code $($importProc.ExitCode). Continuing." `
                    -Ar "تحذير: انتهى الاستيراد برمز $($importProc.ExitCode). سنكمل." -Color Yellow
} else {
    Write-Bilingual -En "Import finished" -Ar "انتهى الاستيراد" -Color Green
}

# ---------------------------------------------------------------
# 6. Launch
# ---------------------------------------------------------------
if ($Editor) {
    Write-Bilingual -En "Opening Godot editor..." -Ar "جارٍ فتح محرر Godot..."
    $launchArgs = "--editor --path `"$ProjectDir`""
} else {
    Write-Bilingual -En "Starting the app..." -Ar "جارٍ تشغيل التطبيق..."
    $launchArgs = "--path `"$ProjectDir`""
}

Start-Process -FilePath $godotExe -ArgumentList $launchArgs
Write-Bilingual -En "Done. Godot is starting." -Ar "تم. Godot يبدأ الآن." -Color Green
exit 0
