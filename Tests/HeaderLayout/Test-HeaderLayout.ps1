param([switch]$NoBuild, [switch]$NativeAot, [ValidateSet(25,50,75,100,125,150,175,200)][int]$StartupScale = 100,
    [ValidateSet('Light','Dark')][string]$StartupTheme = 'Light',
    [ValidateSet('en')]
    [string[]]$Languages = @('en'))
$ErrorActionPreference = 'Stop'
if (-not $NoBuild) {
    if ($NativeAot) {
        & dotnet publish (Join-Path $PSScriptRoot 'HeaderLayout.Tests.csproj') -c Release -p:Platform=x64 -p:PublishAot=true
    } else {
        & dotnet build (Join-Path $PSScriptRoot 'HeaderLayout.Tests.csproj') -c Debug -p:Platform=x64 --no-restore
    }
    if ($LASTEXITCODE -ne 0) { throw 'Header layout test host did not build.' }
}
$output = Join-Path $PSScriptRoot 'bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64'
if ($NativeAot) { $output = Join-Path $PSScriptRoot 'bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish' }
$exe = Join-Path $output 'HeaderLayout.Tests.exe'
$report = Join-Path $output 'header-layout-results.txt'
$started = [DateTime]::UtcNow
$testProcess = Start-Process -FilePath $exe -ArgumentList "--startup-scale=$StartupScale", "--startup-theme=$StartupTheme", "--languages=$($Languages -join ',')" -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(180000)) {
    # Only this newly created, backend-free test host may be stopped.
    $testProcess.Kill()
    throw 'Native header layout test timed out after 180 seconds.'
}
if (-not (Test-Path -LiteralPath $report)) { throw 'Native header layout test produced no report.' }
$reportFile = Get-Item -LiteralPath $report
if ($reportFile.LastWriteTimeUtc -lt $started.AddSeconds(-1)) { throw 'Native layout report was stale.' }
$result = Get-Content -LiteralPath $report -Raw
Write-Output $result
if ($testProcess.ExitCode -ne 0 -or $result -match 'FAIL' -or $result -notmatch '(?m)^PASS:') {
    throw 'Native header layout assertions failed; inspect the report above.'
}
