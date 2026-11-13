param([switch]$Hold)
$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$arguments = @('run', '--project', (Join-Path $workspacePath 'work/tests/McResourceManager.DesktopTests/McResourceManager.DesktopTests.csproj'), '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '--', $workspacePath)
if ($Hold) { $arguments += '--hold' }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "WPF smoke test failed with exit code $LASTEXITCODE" }
