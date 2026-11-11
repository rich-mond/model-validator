param([Parameter(Mandatory)][string]$ChallengesPath)
$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet).Source
$repo = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $repo 'src/ModelValidator.Cli/bin/Release/net10.0/ModelValidator.Cli.dll'
$pack = Join-Path (Resolve-Path $ChallengesPath) 'python/order-normalization'
$root = Join-Path ([IO.Path]::GetTempPath()) ('model-validator-e2e-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
Write-Host "Regression evidence: $root"
function Check($condition, [string]$message) { if (!$condition) { throw $message } }
function Invoke-Cli([string[]]$Arguments, [int]$Expected = 0) {
    $text = & $dotnet $cli @Arguments 2>&1 | Out-String
    $actual = $LASTEXITCODE
    if ($actual -ne $Expected) { throw "CLI exit $actual (expected $Expected): $text" }
    return $text
}
$agent = Join-Path $root 'agent.ps1'
@'
$ErrorActionPreference = 'Stop'
$args | ConvertTo-Json -AsArray | Set-Content -LiteralPath (Join-Path $args[10] 'received.json')
if ($args[13] -in @('valid', 'target-failed', 'target-timeout')) {
    & git apply --whitespace=nowarn $args[12]
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($args[13] -eq 'target-failed') { exit 7 }
if ($args[13] -eq 'target-timeout') { Start-Sleep -Seconds 20 }
'@ | Set-Content $agent
$oracle = Join-Path $pack 'oracle/solution.patch'
function Run-Quick([string]$Mode, [string]$Name, [string]$Challenge = $pack, [string[]]$Extra = @()) {
    $output = Join-Path $root $Name
    $arguments = @('benchmark', '--challenge', $Challenge, '--agent', 'recorder', '--model', 'example-model', '--provider', 'custom', '--output', $output) + $Extra + @(
        '--', 'pwsh', '-NoProfile', '-File', $agent, '--model', '{model}', '--sandbox', 'workspace-write', '--ask-for-approval', 'never',
        'two words', '', '{workspace}', '{promptPath}', '{output}', '{prompt}', $oracle, $Mode)
    $console = Invoke-Cli $arguments
    $console | Set-Content (Join-Path $root "$Name.console.log")
    return $output
}
function Read-Attempt([string]$Output) { return Get-Content (Join-Path $Output 'targets/recorder-example-model/attempt-001/result.json') -Raw | ConvertFrom-Json }
$valid = Run-Quick valid valid
$incorrect = Run-Quick incorrect incorrect
$good = Read-Attempt $valid
$bad = Read-Attempt $incorrect
Check $good.coverage.resolved 'Oracle candidate must resolve'
Check (!$bad.coverage.resolved) 'Starter must not resolve'
Check ($good.coverage.requirementsPassed -gt $bad.coverage.requirementsPassed) 'Correctness must distinguish candidates'
Check ($good.terminationReason -eq 'completed' -and $bad.terminationReason -eq 'assertion-failed') 'Termination classification'
Check ($good.comparisonFingerprint -eq $bad.comparisonFingerprint) 'Same verified inputs must be comparable'
$attemptRoot = Join-Path $valid 'targets/recorder-example-model/attempt-001'
Check (!(Test-Path (Join-Path $attemptRoot 'workspace'))) 'Quick run must remove disposable workspace'
foreach ($file in @('candidate.patch','changed-files.json','target.stdout.log','target.stderr.log','result.json','validators/duplicate-lines/stdout.log')) {
    Check (Test-Path (Join-Path $attemptRoot $file)) "Missing evidence: $file"
}
$received = Get-Content (Join-Path $attemptRoot 'adapter-output/received.json') -Raw | ConvertFrom-Json
Check (($received[0..7] | ConvertTo-Json -Compress) -eq (@('--model','example-model','--sandbox','workspace-write','--ask-for-approval','never','two words','') | ConvertTo-Json -Compress)) 'Argument order and boundaries'
Check ($received[8] -eq (Join-Path $attemptRoot 'workspace')) 'Workspace placeholder'
Check ($received[9] -eq (Join-Path $attemptRoot 'workspace/MODEL_VALIDATOR_TASK.md')) 'Prompt path placeholder'
Check ($received[10] -eq (Join-Path $attemptRoot 'adapter-output')) 'Output placeholder'
Check ($received[11].Contains('normalize_order')) 'Prompt content placeholder'
$target = Get-Content (Join-Path $valid '_generated/recorder-example-model.target.json') -Raw | ConvertFrom-Json
Check ($target.model.version -eq 'unknown') 'Unknown model version must stay unknown'
$hashBefore = (Get-FileHash (Join-Path $valid 'comparison.json')).Hash
$null = Invoke-Cli @('benchmark','--challenge',$pack,'--output',$valid,'--','git','--version') 1
Check ($hashBefore -eq (Get-FileHash (Join-Path $valid 'comparison.json')).Hash) 'Second invocation corrupted evidence'
$null = Invoke-Cli @('benchmark','--challenge',$pack,'--output',(Join-Path $root 'empty'),'--') 1
Check (!(Test-Path (Join-Path $root 'empty'))) 'Empty command created output'
$null = Invoke-Cli @('compare','--runs',$valid,$incorrect)
Check ((Get-Content (Join-Path $valid 'comparison.md') -Raw).Contains('| True |')) 'Markdown resolution disagrees'
Check ((Get-Content (Join-Path $root 'incorrect.console.log') -Raw).Contains('UNRESOLVED')) 'Console resolution disagrees'

# Reuse the real generated plan to verify retention and referenced-input validation.
$plan = Get-Content (Join-Path $valid '_generated/benchmark-plan.json') -Raw | ConvertFrom-Json
$plan.targets = @((Join-Path $valid '_generated/recorder-example-model.target.json'))
$plan.outputPath = Join-Path $root 'retained'
$plan.execution.retainWorkspaces = $true
$planPath = Join-Path $root 'retained-plan.json'
$plan | ConvertTo-Json -Depth 50 | Set-Content $planPath
$null = Invoke-Cli @('plan','validate','--path',$planPath)
$null = Invoke-Cli @('run','--plan',$planPath)
Check (Test-Path (Join-Path $plan.outputPath 'targets/recorder-example-model/attempt-001/workspace')) 'RetainWorkspaces=true was ignored'
$plan.execution.maximumParallelTargets = 2
$plan | ConvertTo-Json -Depth 50 | Set-Content $planPath
$null = Invoke-Cli @('plan','validate','--path',$planPath) 1
$plan.execution.maximumParallelTargets = 1
$plan.targets += $plan.targets[0]
$plan | ConvertTo-Json -Depth 50 | Set-Content $planPath
$null = Invoke-Cli @('plan','validate','--path',$planPath) 1
$plan.targets = @((Join-Path $root 'missing-target.json'))
$plan | ConvertTo-Json -Depth 50 | Set-Content $planPath
$null = Invoke-Cli @('plan','validate','--path',$planPath) 1

foreach ($inputFile in @('prompt.md','workspace/starter.bundle','validator/run','base-commit')) {
    $copy = Join-Path $root ('corrupt-' + $inputFile.Replace('/','-'))
    Copy-Item $pack $copy -Recurse
    if ($inputFile -eq 'base-commit') {
        $manifest = Get-Content (Join-Path $copy 'challenge.json') -Raw | ConvertFrom-Json
        $manifest.workspace.baseCommit = 'a' * 40
        $manifest | ConvertTo-Json -Depth 50 | Set-Content (Join-Path $copy 'challenge.json')
    } else { Add-Content (Join-Path $copy $inputFile) 'corrupted' }
    $output = Join-Path $root ('rejected-' + $inputFile.Replace('/','-'))
    $null = Invoke-Cli @('benchmark','--challenge',$copy,'--output',$output,'--','git','--version') 1
    Check (!(Test-Path $output)) 'Corrupt input created benchmark output'
    $null = Invoke-Cli @('benchmark','--challenge',$copy,'--output',$output) 1
    $plan.targets = @((Join-Path $valid '_generated/recorder-example-model.target.json'))
    $plan.challengePath = $copy
    $plan.outputPath = $output
    $plan | ConvertTo-Json -Depth 50 | Set-Content $planPath
    $null = Invoke-Cli @('plan','validate','--path',$planPath) 1
    $null = Invoke-Cli @('run','--plan',$planPath) 1
    Check (!(Test-Path $output)) 'Invalid plan created benchmark output'
}
$failed = Run-Quick target-failed target-failed
$failure = Read-Attempt $failed
Check (!$failure.coverage.resolved -and $failure.terminationReason -eq 'target-failed') 'Failed target classified as resolved'
Check ($failure.coverage.requirementCoveragePercent -eq 100) 'Target outcome must remain separate from objective assertion coverage'

foreach ($kind in @('target-timeout','validator-timeout','infrastructure')) {
    $copy = Join-Path $root "$kind-pack"
    Copy-Item $pack $copy -Recurse
    $manifest = Get-Content (Join-Path $copy 'challenge.json') -Raw | ConvertFrom-Json
    if ($kind -eq 'target-timeout') { $manifest.limits.targetTimeoutSeconds = 3 }
    elseif ($kind -eq 'validator-timeout') {
        $manifest.validation.assertions[0].command = @('/bin/sleep','10')
        $manifest.validation.assertions[0].timeoutSeconds = 1
    } else { $manifest.validation.assertions[0].command = @('/missing-validator-command') }
    $manifest | ConvertTo-Json -Depth 50 | Set-Content (Join-Path $copy 'challenge.json')
    $mode = if ($kind -eq 'target-timeout') { 'target-timeout' } else { 'valid' }
    $output = Run-Quick $mode $kind $copy @('--model-version','reported-version')
    $result = Read-Attempt $output
    $expected = if ($kind -eq 'infrastructure') { 'validation-infrastructure-error' } else { $kind }
    Check (!$result.coverage.resolved -and $result.terminationReason -eq $expected) "Incorrect $kind result"
    if ($kind -ne 'target-timeout') { Check ($result.coverage.requirementsPassed -eq 1) 'Incomplete validator increased passed coverage' }
    $config = Get-Content (Join-Path $output '_generated/recorder-example-model.target.json') -Raw | ConvertFrom-Json
    Check ($config.model.version -eq 'reported-version') 'Explicit model version lost'
    Check (($result.finishedUtc -as [datetimeoffset]) -ge ($result.startedUtc -as [datetimeoffset])) 'Invalid attempt timestamps'
    $total = [timespan]::Parse($result.totalDuration)
    Check ($total -ge ([timespan]::Parse($result.targetDuration) + [timespan]::Parse($result.validationDuration) + [timespan]::Parse($result.candidateCaptureDuration))) 'Total duration omitted attempt phases'
}
# Exercise interactive timing with delayed metadata entry and an unchanged starter.
$interactiveOutput = Join-Path $root 'interactive'
$info = [Diagnostics.ProcessStartInfo]::new($dotnet)
$info.UseShellExecute = $false
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($argument in @($cli,'benchmark','--challenge',$pack,'--model','example-model','--output',$interactiveOutput)) { $info.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($info)
$stderr = $process.StandardError.ReadToEndAsync()
$transcript = [Text.StringBuilder]::new()
while (($line = $process.StandardOutput.ReadLine()) -ne $null) {
    $null = $transcript.AppendLine($line)
    if ($line.StartsWith('4. Return here')) { break }
}
$process.StandardInput.WriteLine('')
# The target has finished. Metadata entry is deliberately slower than the target.
Start-Sleep -Milliseconds 1500
$process.StandardInput.WriteLine('example-model')
$process.StandardInput.WriteLine('')
$process.StandardInput.Close()
$remaining = $process.StandardOutput.ReadToEndAsync()
Check ($process.WaitForExit(60000)) 'Interactive benchmark did not finish'
$null = $transcript.Append($remaining.GetAwaiter().GetResult())
$transcript.ToString() | Set-Content (Join-Path $root 'interactive.console.log')
Check ($process.ExitCode -eq 0) "Interactive execution failed: $($stderr.GetAwaiter().GetResult())"
$interactive = Get-Content (Join-Path $interactiveOutput 'targets/interactive-example-model/attempt-001/result.json') -Raw | ConvertFrom-Json
Check ([timespan]::Parse($interactive.targetDuration).TotalSeconds -lt 1) 'Metadata entry leaked into target duration'
Check ([timespan]::Parse($interactive.totalDuration).TotalSeconds -ge 1.5) 'Metadata omitted from total duration'
Check (!$interactive.coverage.resolved) 'Interactive starter must fail'
Check (Test-Path (Join-Path $interactiveOutput 'targets/interactive-example-model/attempt-001/workspace')) 'Interactive workspace not retained'
$snapshot = Get-Content (Join-Path $interactiveOutput 'targets/interactive-example-model/attempt-001/target.snapshot.json') -Raw | ConvertFrom-Json
Check ($snapshot.model.version -eq 'unknown') 'Interactive unknown model version fabricated'
$process.Dispose()
$interruptedOutput = Join-Path $root 'interrupted'
$info.ArgumentList.Clear()
foreach ($argument in @($cli,'benchmark','--challenge',$pack,'--output',$interruptedOutput)) { $info.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($info)
$process.StandardInput.Close()
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
Check ($process.WaitForExit(60000)) 'Interrupted benchmark did not finish'
Check ($process.ExitCode -eq 1 -and $stderr.GetAwaiter().GetResult().Contains('interrupted')) 'EOF was treated as target completion'
Check (!(Test-Path (Join-Path $interruptedOutput 'comparison.json'))) 'Interrupted validation created a completed report'
$stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $root 'interrupted.console.log')
$process.Dispose()
$null = Invoke-Cli @('doctor')
$savedPath = $env:PATH
try {
    $env:PATH = ''
    $diagnostic = Invoke-Cli @('doctor') 1
    Check ($diagnostic.Contains('Git: FAILED') -and $diagnostic.Contains('Docker CLI: FAILED')) 'Doctor concealed missing prerequisites'
} finally { $env:PATH = $savedPath }
Write-Host 'Deterministic benchmark regression passed.'
