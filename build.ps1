param(
    [switch]$Run,
    [string]$OutputName = 'CodexProxyMask.exe'
)

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDirectory = Join-Path $projectRoot 'out'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkDirectory 'csc.exe'

if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw "找不到 C# 编译器: $compilerPath"
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$referenceNames = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
$compilerArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    "/out:out\$OutputName",
    '/win32manifest:app.manifest'
)

foreach ($referenceName in $referenceNames) {
    $referencePath = Join-Path $frameworkDirectory $referenceName
    $compilerArguments += "/reference:$referencePath"
}

$sourceNames = Get-ChildItem -LiteralPath $projectRoot -Filter '*.cs' -File |
    Sort-Object Name |
    Select-Object -ExpandProperty Name
$compilerArguments += $sourceNames

Push-Location $projectRoot
try {
    & $compilerPath @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}

Write-Host ("Built: " + (Join-Path $outputDirectory $OutputName))
if ($Run) {
    Start-Process -FilePath (Join-Path $outputDirectory $OutputName)
}
