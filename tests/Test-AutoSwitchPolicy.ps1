param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\bin\Release\net10.0-windows\win-x64\CodexUsageTray.dll')
)

$dotnet = Get-Command dotnet -ErrorAction Stop
$projectPath = Join-Path $PSScriptRoot '..\CodexUsageTray.csproj'
if (-not (Test-Path -LiteralPath $AssemblyPath)) {
    & $dotnet.Source build (Resolve-Path -LiteralPath $projectPath).Path -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "The tray app build failed with exit code $LASTEXITCODE."
    }
}
$resolvedAssembly = Resolve-Path -LiteralPath $AssemblyPath -ErrorAction Stop
& $dotnet.Source $resolvedAssembly.Path --self-test-auto-switch
$testExitCode = $LASTEXITCODE
if ($testExitCode -ne 0) {
    throw "Auto-switch policy checks failed with exit code $testExitCode."
}
