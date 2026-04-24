param(
    [string]$Project = "JustCode.csproj",
    [string]$Configuration = "Debug",
    [string]$TempRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Remove-DirectorySafely {
    param([Parameter(Mandatory = $true)][string]$PathToRemove,
          [Parameter(Mandatory = $true)][string]$ExpectedRoot)

    if (-not (Test-Path -LiteralPath $PathToRemove)) { return }
    $resolved = (Resolve-Path -LiteralPath $PathToRemove).Path
    if (-not $resolved.StartsWith($ExpectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$repoRoot = (Resolve-Path ".").Path
$projectPath = Join-Path $repoRoot $Project
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project not found: $projectPath"
}

$tempRootPath = if ([string]::IsNullOrWhiteSpace($TempRoot)) {
    Join-Path ([System.IO.Path]::GetTempPath()) "justcode-verify-temp"
}
elseif ([System.IO.Path]::IsPathRooted($TempRoot)) {
    [System.IO.Path]::GetFullPath($TempRoot)
}
else {
    Join-Path $repoRoot ($TempRoot -replace '/', '\')
}
$tempRootParent = Split-Path -Parent $tempRootPath
$outDir = Join-Path $tempRootPath "out"
$baseOutDir = Join-Path $tempRootPath "bin"
$baseObjDir = Join-Path $tempRootPath "obj"

$trackedOutputStatusBefore = (& git status --porcelain=v1 -- temp_bin test-build) -join "`n"

try {
    Remove-DirectorySafely -PathToRemove $tempRootPath -ExpectedRoot $tempRootParent
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    New-Item -ItemType Directory -Path $baseOutDir -Force | Out-Null
    New-Item -ItemType Directory -Path $baseObjDir -Force | Out-Null
    $repoObjDir = Join-Path $repoRoot "obj"
    $clearedRepoObj = Test-Path -LiteralPath $repoObjDir
    Remove-DirectorySafely -PathToRemove $repoObjDir -ExpectedRoot $repoRoot

    $buildArgs = @(
        "build", $projectPath,
        "-c", $Configuration,
        "-o", $outDir,
        "-p:BaseOutputPath=$baseOutDir\",
        "-p:BaseIntermediateOutputPath=$baseObjDir\",
        "-p:MSBuildProjectExtensionsPath=$baseObjDir\"
    )
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    $exePath = Join-Path $outDir "JustCode.exe"
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "Built executable not found: $exePath"
    }

    $proc = Start-Process -FilePath $exePath -WorkingDirectory $outDir -PassThru
    Start-Sleep -Seconds 2
    try { $null = $proc.WaitForInputIdle(10000) } catch { }
    Start-Sleep -Seconds 3
    $proc.Refresh()
    if ($proc.HasExited) {
        throw "App exited before verification completed."
    }

    $mainWindowHandle = $proc.MainWindowHandle
    if ($mainWindowHandle -eq 0) {
        throw "App did not create a main window handle."
    }

    $null = $proc.CloseMainWindow()
    Start-Sleep -Seconds 3
    $proc.Refresh()
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force
        Start-Sleep -Seconds 1
        $proc.Refresh()
    }

    if (-not $proc.HasExited) {
        throw "App did not exit after close request."
    }

    Write-Output "APP_STARTED=True"
    Write-Output "MAIN_WINDOW_HANDLE=$mainWindowHandle"
    Write-Output "APP_EXITED=True"
    Write-Output "EXIT_CODE=$($proc.ExitCode)"
    Write-Output "CLEARED_REPO_OBJ=$clearedRepoObj"
}
finally {
    Remove-DirectorySafely -PathToRemove $tempRootPath -ExpectedRoot $tempRootParent
}

$trackedOutputStatusAfter = (& git status --porcelain=v1 -- temp_bin test-build) -join "`n"
if ($trackedOutputStatusBefore -ne $trackedOutputStatusAfter) {
    throw "Verification mutated legacy temp_bin/test-build local outputs."
}

Write-Output "TRACKED_OUTPUTS_UNCHANGED=True"
