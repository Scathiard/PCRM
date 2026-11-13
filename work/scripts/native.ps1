param(
    [ValidateSet('build', 'test', 'core-test', 'ui-test', 'qa')][string]$Action = 'build'
)
$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$cachePath = Join-Path $workspacePath 'work\cache'
$taskTempRoot = Join-Path $workspacePath 'work\tmp\mcrm-build'
$taskTempPath = Join-Path $taskTempRoot ([guid]::NewGuid().ToString('N'))
$priorEnvironment = @{}
$settings = @{
    DOTNET_CLI_HOME = (Join-Path $cachePath 'dotnet\cli-home')
    NUGET_PACKAGES = (Join-Path $cachePath 'nuget')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    TEMP = $taskTempPath
    TMP = $taskTempPath
}
New-Item -ItemType Directory -Path $taskTempPath -Force | Out-Null
foreach ($key in $settings.Keys) {
    $priorEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
}
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Push-Location $workspacePath
try {
    if ($Action -in @('core-test', 'test')) {
        Invoke-Dotnet @('run', '--project', 'work/tests/McResourceManager.Tests/McResourceManager.Tests.csproj', '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '--', $workspacePath)
    }
    if ($Action -in @('build', 'qa')) {
        Invoke-Dotnet @('publish', 'work/src/McResourceManager.Desktop/McResourceManager.Desktop.csproj', '-c', 'Release', '-o', 'releases/dist/native', '--disable-build-servers', '-p:UseSharedCompilation=false', '--no-self-contained')
    }
    if ($Action -in @('ui-test', 'test', 'qa')) {
        $uiTest = Join-Path $PSScriptRoot 'ui-smoke.ps1'
        if (-not (Test-Path -LiteralPath $uiTest)) { throw 'WPF smoke test is not implemented yet.' }
        & $uiTest -Hold:($Action -eq 'qa')
    }
    $oversized = @(Get-ChildItem -LiteralPath 'work/src','work/tests' -Filter '*.cs' -Recurse | Where-Object { @(Get-Content -LiteralPath $_.FullName).Count -gt 250 })
    if ($oversized.Count -gt 0) { throw ('C# file exceeds 250 lines: ' + ($oversized.FullName -join ', ')) }
}
finally {
    Pop-Location
    foreach ($key in $priorEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $priorEnvironment[$key], 'Process') }
    $resolved = [IO.Path]::GetFullPath($taskTempPath)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($taskTempRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid cleanup path.' }
    $links = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($links.Count -gt 0) { throw "Temporary directory contains links; preserved: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
