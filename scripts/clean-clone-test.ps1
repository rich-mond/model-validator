$ErrorActionPreference = 'Stop'
$source = Resolve-Path (Join-Path $PSScriptRoot '..')
$cloneRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('model-validator-clean-clone-' + [System.Guid]::NewGuid().ToString('N'))
git clone $source $cloneRoot
dotnet restore (Join-Path $cloneRoot 'ModelValidator.slnx') --locked-mode
dotnet build (Join-Path $cloneRoot 'ModelValidator.slnx') -c Release --no-restore
dotnet test (Join-Path $cloneRoot 'ModelValidator.slnx') -c Release --no-build
Write-Host "Clean clone verified at $cloneRoot"
