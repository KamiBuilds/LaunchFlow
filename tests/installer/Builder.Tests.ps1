# Real builder control flow with synthetic payloads and inert tool/signing doubles.
function Test-LaunchFlowBuilder {
    param([string]$PayloadDirectory, [string]$TestRoot)
    $fixture = Join-Path $TestRoot 'builder'
    $source = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
    foreach ($directory in @('tools', 'installer', 'LaunchFlow', 'LaunchFlow/Services/Platform', 'bootstrapper', 'dist')) {
        New-Item -ItemType Directory -Path (Join-Path $fixture $directory) -Force | Out-Null
    }
    foreach ($file in @('BUILD-INSTALLER.ps1', 'BUILD-UNIVERSAL-INSTALLER.ps1', 'BUILD-PACKAGING-HELPERS.ps1')) {
        Copy-Item -LiteralPath (Join-Path $source $file) -Destination $fixture
    }
    # The synthetic publish tree contains a text placeholder for node.exe.
    # Replace only the fixture's Firefox installer, not the production helper;
    # keep asserting that the real builder invokes the Firefox packaging step.
    Add-Content -LiteralPath (Join-Path $fixture 'BUILD-PACKAGING-HELPERS.ps1') -Value @'
function Install-LaunchFlowFirefoxBrowsers {
    param([string]$PublishDirectory, [string]$DotNetPath)
    if ($env:LF_TEST_COMPILER_MODE -eq 'publish-failure') {
        throw 'Fixture Firefox install must not run after a failed publish.'
    }
    $root = Join-Path $PublishDirectory 'playwright-browsers/firefox-synthetic/firefox'
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'firefox.exe') -Value 'synthetic Firefox artifact'
    Assert-LaunchFlowFirefoxBrowsers -PublishDirectory $PublishDirectory
}
'@
    # The actual builder validates all version surfaces before touching artifacts.
    # Keep this independent synthetic fixture at 0.7.19; do not copy current app metadata.
    Set-Content -LiteralPath (Join-Path $fixture 'LaunchFlow/LaunchFlow.csproj') -Value '<Project><PropertyGroup><Version>0.7.19</Version></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $fixture 'LaunchFlow/Services/Platform/AppInfo.cs') -Value 'return v is null ? "0.7.19" : $"{v.Major}.{v.Minor}.{v.Build}";'
    Set-Content -LiteralPath (Join-Path $fixture 'README.md') -Value @('# LaunchFlow fixture', 'Current build: **v0.7.19**')
    Set-Content -LiteralPath (Join-Path $fixture 'CHANGELOG.md') -Value '# v0.7.19 — synthetic packaging fixture'
    Set-Content -LiteralPath (Join-Path $fixture 'installer/LaunchFlow.iss') -Value @'
#define AppVersion "0.7.19"
[Code]
function InitializeSetup(): Boolean;
var
  Attempt: Integer;
begin
  Result := False;
  for Attempt := 1 to 960 do
  begin
    if not CheckForMutexes('LaunchFlow.Windows.SingleInstance') then
    begin
      Result := True;
      Exit;
    end;
    Sleep(250);
  end;
end;
'@
    Set-Content -LiteralPath (Join-Path $fixture 'tools/Sign-LaunchFlow.ps1') -Value @'
function Get-LaunchFlowSignTool { return $null }
function Test-LaunchFlowSigningConfigured { return $false }
function Get-LaunchFlowInnoSignToolDefinition { return $null }
function Invoke-LaunchFlowSign { param($PathToSign, [switch]$QuietIfUnsigned) }
'@
    Copy-Item -LiteralPath $PayloadDirectory -Destination (Join-Path $fixture 'template') -Recurse
    $dotnetDouble = Join-Path $fixture 'dotnet-double.ps1'
    Set-Content -LiteralPath $dotnetDouble -Value @'
if ($args[0] -eq '--list-sdks') { '8.0.427 [synthetic SDK]'; $global:LASTEXITCODE = 0; return }
if ($args[0] -ne 'publish') { throw 'Unexpected developer tool invocation.' }
if ($env:LF_TEST_COMPILER_MODE -eq 'publish-failure') { $global:LASTEXITCODE = 23; return }
$index = [array]::IndexOf($args, '-o')
if ($index -lt 0) { throw 'Publish output path was not explicit.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'template') -Destination $args[$index + 1] -Recurse
$global:LASTEXITCODE = 0
'@
    $innoDouble = Join-Path $fixture 'inno-double.ps1'
    Set-Content -LiteralPath $innoDouble -Value @'
$target = Join-Path $PSScriptRoot 'dist/LaunchFlow-Setup-v0.7.19-x64.exe'
switch ($env:LF_TEST_COMPILER_MODE) {
    'success' { Set-Content -LiteralPath $target -Value 'synthetic current artifact' }
    'empty' { [IO.File]::WriteAllBytes($target, [byte[]]@()) }
    'failure' { $global:LASTEXITCODE = 31; return }
    'no-output' { }
    default { throw 'Unexpected compiler invocation.' }
}
$global:LASTEXITCODE = 0
'@
    function Get-Command {
        param([string]$Name, $ErrorAction)
        if ($Name -cne 'dotnet.exe') { throw "Unexpected tool discovery: $Name" }
        [pscustomobject]@{ Source = $dotnetDouble }
    }
    # A build produces artifacts; it must not discover or close the user's installed application.
    function Get-Process { throw 'Builder must not inspect running user applications.' }
    function Stop-Process { throw 'Builder must not terminate running user applications.' }
    $oldOS = $env:OS
    $oldPath = $env:PATH
    $oldMode = $env:LF_TEST_COMPILER_MODE
    $oldLocation = Get-Location
    $oldExitCode = $global:LASTEXITCODE
    try {
        # Simulate Windows discovery only; no native installer or package manager is executed.
        $env:OS = 'Windows_NT'
        foreach ($repository in @(' Owner/LaunchFlow', "Owner/LaunchFlow`n", 'Owner/../LaunchFlow', 'SET-YOUR-OWNER/REPO')) {
            Test-Case "universal builder rejects literal invalid repository $repository before writes" {
                $sourceFile = Join-Path $fixture 'bootstrapper/release-source.txt'
                Set-Content -LiteralPath $sourceFile -Value 'Owner/Existing'
                $message = $null
                try { & (Join-Path $fixture 'BUILD-UNIVERSAL-INSTALLER.ps1') -Repository $repository }
                catch { $message = $_.Exception.Message }
                if ($message -notlike 'Invalid repository.*') { throw "Expected repository rejection, got '$message'." }
                if ((Get-Content -LiteralPath $sourceFile -Raw).Trim() -cne 'Owner/Existing') { throw 'Invalid repository changed the configured source.' }
            }
        }
        Test-Case "signed distribution gate rejects missing certificate before changing outputs" {
            $target = Join-Path $fixture 'dist/LaunchFlow-Setup-v0.7.19-x64.exe'
            Set-Content -LiteralPath $target -Value 'preserve existing build'
            $message = $null
            try {
                & (Join-Path $fixture 'BUILD-INSTALLER.ps1') -NoAutoInstall -RequireSignedDistribution -InnoCompiler $innoDouble
            } catch { $message = $_.Exception.Message }
            if ($message -notlike 'A trusted signed distribution was requested*') {
                throw "Expected early certificate gate, got '$message'."
            }
            if ((Get-Content -LiteralPath $target -Raw).Trim() -cne 'preserve existing build') {
                throw 'Signed distribution gate modified an existing artifact before rejecting the request.'
            }
        }
        foreach ($case in @(
            @{ Name = 'missing AppInfo'; File = 'LaunchFlow/Services/Platform/AppInfo.cs'; Replacement = $null; Expected = '*AppInfo.cs*' },
            @{ Name = 'mismatched AppInfo'; File = 'LaunchFlow/Services/Platform/AppInfo.cs'; Replacement = 'return v is null ? "0.7.18"'; Expected = '*AppInfo display-version fallback*' },
            @{ Name = 'mismatched README'; File = 'README.md'; Replacement = 'Current build: **v0.7.18**'; Expected = '*README current-build line*' },
            @{ Name = 'missing CHANGELOG entry'; File = 'CHANGELOG.md'; Replacement = '# v0.7.18'; Expected = '*CHANGELOG is missing*' },
            @{ Name = 'under-budget exit loop'; File = 'installer/LaunchFlow.iss'; Replacement = '#define AppVersion "0.7.19"' + [Environment]::NewLine + 'for Attempt := 1 to 959 do'; Expected = '*below the 960*' },
            @{ Name = 'missing exit loop'; File = 'installer/LaunchFlow.iss'; Replacement = '#define AppVersion "0.7.19"'; Expected = '*missing the mutex wait loop*' }
        )) {
            Test-Case "builder preflight rejects $($case.Name) without changing outputs" {
                $path = Join-Path $fixture $case.File
                $original = [IO.File]::ReadAllText($path)
                $target = Join-Path $fixture 'dist/LaunchFlow-Setup-v0.7.19-x64.exe'
                Set-Content -LiteralPath $target -Value 'preserve preflight artifact'
                try {
                    if ($null -eq $case.Replacement) { Remove-Item -LiteralPath $path }
                    else { Set-Content -LiteralPath $path -Value $case.Replacement }
                    $message = $null
                    try { & (Join-Path $fixture 'BUILD-INSTALLER.ps1') -NoAutoInstall -InnoCompiler $innoDouble }
                    catch { $message = $_.Exception.Message }
                    if (-not $message -or $message -notlike $case.Expected) {
                        throw "Expected $($case.Name) preflight failure, got '$message'."
                    }
                    if ((Get-Content -LiteralPath $target -Raw).Trim() -cne 'preserve preflight artifact') {
                        throw 'Preflight failure changed a prior installer.'
                    }
                }
                finally { [IO.File]::WriteAllText($path, $original) }
            }
        }
        foreach ($mode in @('success', 'no-output', 'empty', 'failure', 'publish-failure')) {
            Test-Case "builder $mode never reuses stale artifacts" {
                $env:LF_TEST_COMPILER_MODE = $mode
                $target = Join-Path $fixture 'dist/LaunchFlow-Setup-v0.7.19-x64.exe'
                $stale = Join-Path $fixture 'dist/LaunchFlow-Setup-v99.0.0-x64.exe'
                Set-Content -LiteralPath $target -Value 'stale expected artifact'
                Set-Content -LiteralPath $stale -Value 'unrelated stale artifact'
                $build = { & (Join-Path $fixture 'BUILD-INSTALLER.ps1') -NoAutoInstall -InnoCompiler $innoDouble }
                if ($mode -eq 'success') {
                    & $build
                    if ((Get-Content -LiteralPath $target -Raw).Trim() -cne 'synthetic current artifact') { throw 'Wrong artifact selected.' }
                    Assert-LaunchFlowFirefoxBrowsers -PublishDirectory (Join-Path $fixture 'publish')
                }
                else {
                    $errorMessage = $null
                    try { & $build } catch { $errorMessage = $_.Exception.Message }
                    $expectedError = switch ($mode) {
                        'publish-failure' { 'dotnet publish failed.*' }
                        'failure' { 'Inno Setup compilation failed.*' }
                        default { 'Inno Setup returned successfully but the expected artifact is missing or empty:*' }
                    }
                    if ($errorMessage -notlike $expectedError) { throw "Expected '$expectedError', got '$errorMessage'." }
                }
                if ((Get-Content -LiteralPath $stale -Raw).Trim() -cne 'unrelated stale artifact') { throw 'Unrelated artifact changed.' }
                if ($mode -eq 'no-output' -and (Test-Path -LiteralPath $target)) { throw 'Expected stale target was retained.' }
            }
        }
    }
    finally {
        $env:OS = $oldOS
        $env:PATH = $oldPath
        $env:LF_TEST_COMPILER_MODE = $oldMode
        Set-Location -LiteralPath $oldLocation.Path
        $global:LASTEXITCODE = $oldExitCode
    }
}
