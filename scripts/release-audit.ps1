$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')

dotnet build (Join-Path $repo 'ModelValidator.slnx') -c Release
dotnet test (Join-Path $repo 'ModelValidator.slnx') -c Release --no-build

$forbidden = @('.sln', '.slnx', '.csproj', '.cs', '.py', '.js', '.java', '.rs')
$allowedRoots = @('src/', 'tests/')
$files = git -C $repo ls-files
foreach ($file in $files) {
  $normalized = $file.Replace('\', '/')
  $allowed = $false
  foreach ($root in $allowedRoots) {
    if ($normalized.StartsWith($root)) { $allowed = $true }
  }
  foreach ($ext in $forbidden) {
    if ($normalized.EndsWith($ext) -and -not $allowed -and $normalized -ne 'ModelValidator.slnx') {
      throw "Forbidden target-like source artifact outside framework code/tests: $normalized"
    }
  }
}

$artifacts = Join-Path $repo 'artifacts/release-audit'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
dotnet list (Join-Path $repo 'ModelValidator.slnx') package --include-transitive | Tee-Object -FilePath (Join-Path $artifacts 'dependency-inventory.txt')
dotnet list (Join-Path $repo 'ModelValidator.slnx') package --vulnerable --include-transitive
Write-Host 'Release audit passed.'
