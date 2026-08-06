$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDirectory = Join-Path $projectRoot 'src\LosslessStitcher'
$distributionDirectory = Join-Path $projectRoot 'dist'
$iconFile = Join-Path $projectRoot 'assets\app.ico'
$outputName = (-join @([char]0x65E0, [char]0x635F, [char]0x62FC, [char]0x56FE)) + '.exe'
$outputFile = Join-Path $distributionDirectory $outputName
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    Write-Host "C# compiler not found: $compiler" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
    Write-Host "Source directory not found: $sourceDirectory" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $iconFile -PathType Leaf)) {
    Write-Host "Application icon not found: $iconFile" -ForegroundColor Red
    exit 1
}

$sourceFiles = @(
    Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File |
        Sort-Object -Property FullName |
        ForEach-Object { $_.FullName }
)

if ($sourceFiles.Count -eq 0) {
    Write-Host "No .cs files found in: $sourceDirectory" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $distributionDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $distributionDirectory | Out-Null
}

$compilerArguments = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    "/win32icon:$iconFile"
    '/codepage:65001'
    '/utf8output'
    "/out:$outputFile"
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.IO.Compression.dll'
    '/reference:System.IO.Compression.FileSystem.dll'
) + $sourceFiles

& $compiler $compilerArguments
$compilerExitCode = $LASTEXITCODE

if ($compilerExitCode -ne 0) {
    Write-Host "Compilation failed with exit code: $compilerExitCode" -ForegroundColor Red
    exit $compilerExitCode
}

Write-Host "Build completed: $outputFile" -ForegroundColor Green
exit 0
