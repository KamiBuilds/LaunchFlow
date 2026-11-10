function Get-LaunchFlowWorkflowScripts {
    $workflow = Join-Path $PSScriptRoot '../../.github/workflows/windows.yml'
    $blocks = [Collections.Generic.List[string]]::new()
    $lines = [Collections.Generic.List[string]]::new()
    $inside = $false
    foreach ($line in (Get-Content -LiteralPath $workflow)) {
        if ($inside -and $line.StartsWith('          ')) { $lines.Add($line.Substring(10)); continue }
        if ($inside -and [string]::IsNullOrWhiteSpace($line)) { $lines.Add(''); continue }
        if ($inside) { $blocks.Add(($lines -join "`n")); $lines.Clear(); $inside = $false }
        if ($line -ceq '        run: |') { $inside = $true }
    }
    if ($inside) { $blocks.Add(($lines -join "`n")) }
    if ($blocks.Count -eq 0) { throw 'No workflow scripts found.' }
    return $blocks.ToArray()
}

# Execute the shipped draft-publishing script against an inert REST double, never GitHub.
function Test-LaunchFlowDraftWorkflow {
    param([string]$TestRoot)
    $scripts = @(Get-LaunchFlowWorkflowScripts)
    $publish = [scriptblock]::Create($scripts[-1])
    $fixture = Join-Path $TestRoot 'draft-workflow'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'dist') -Force | Out-Null
    $names = @('LaunchFlow-Setup-v0.7.19-x64.exe', 'LaunchFlow-Universal-Installer.exe', 'SHA256SUMS.txt')
    foreach ($name in $names) { Set-Content -LiteralPath (Join-Path $fixture "dist/$name") -Value 'synthetic non-executable artifact' }
    $oldLocation = Get-Location
    $keys = @('GH_TOKEN', 'LF_VERSION', 'GITHUB_REPOSITORY', 'GITHUB_SHA', 'WINDOWS_TEST_EVIDENCE')
    $oldEnvironment = @{}
    foreach ($key in $keys) { $oldEnvironment[$key] = [Environment]::GetEnvironmentVariable($key) }
    function Invoke-RestMethod {
        param([string]$Method = 'Get', [string]$Uri, $Headers, [string]$ContentType, [string]$Body, [string]$InFile)
        $script:workflowCalls.Add("$Method $Uri")
        $api = 'https://api.github.com/repos/Owner/LaunchFlow/releases'
        if ($Method -ceq 'Post' -and $Uri -ceq $api) {
            $request = $Body | ConvertFrom-Json
            if ($request.draft -ne $true -or $request.prerelease -ne $false -or $request.make_latest -cne 'false' -or
                $request.target_commitish -cne ('a' * 40) -or $request.tag_name -cne 'v0.7.19') { throw 'Unsafe draft request.' }
            return [pscustomobject]@{ id = 1234; draft = ($script:workflowMode -ne 'bad-create'); prerelease = $false; tag_name = 'v0.7.19'; target_commitish = ('a' * 40) }
        }
        if ($Method -ceq 'Post' -and $Uri.StartsWith('https://uploads.github.com/repos/Owner/LaunchFlow/releases/1234/assets?name=')) {
            if ($script:workflowMode -eq 'upload-failure') { throw 'Synthetic upload failure.' }
            $name = [uri]::UnescapeDataString(($Uri -split '\?name=', 2)[1])
            if ($name -cne (Split-Path $InFile -Leaf) -or $ContentType -cne 'application/octet-stream') { throw 'Wrong upload target.' }
            $script:workflowAssets.Add([pscustomobject]@{ name = $name; state = 'uploaded'; size = (Get-Item -LiteralPath $InFile).Length; digest = 'sha256:' + (Get-FileHash -LiteralPath $InFile -Algorithm SHA256).Hash.ToLowerInvariant() })
            return
        }
        if ($Method -ceq 'Get' -and $Uri -ceq "$api/1234") {
            if ($script:workflowMode -eq 'bad-digest') { $script:workflowAssets[0].digest = 'sha256:' + ('0' * 64) }
            return [pscustomobject]@{ id = 1234; draft = ($script:workflowMode -ne 'public-readback'); prerelease = $false; tag_name = 'v0.7.19'; target_commitish = ('a' * 40); assets = $script:workflowAssets.ToArray() }
        }
        throw "Unexpected REST request (no live fallback): $Method $Uri"
    }
    try {
        Set-Location -LiteralPath $fixture
        $env:GH_TOKEN = 'synthetic-offline-token'
        $env:LF_VERSION = '0.7.19'
        $env:GITHUB_REPOSITORY = 'Owner/LaunchFlow'
        $env:GITHUB_SHA = 'a' * 40
        $env:WINDOWS_TEST_EVIDENCE = 'synthetic-offline-attestation'
        foreach ($mode in @('success', 'bad-create', 'upload-failure', 'bad-digest', 'public-readback')) {
            Test-Case "draft workflow $mode uses create-only API and verifies read-back" {
                $script:workflowMode = $mode
                $script:workflowCalls = [Collections.Generic.List[string]]::new()
                $script:workflowAssets = [Collections.Generic.List[object]]::new()
                $message = $null
                try { & $publish } catch { $message = $_.Exception.Message }
                $expected = switch ($mode) {
                    'success' { $null }
                    'bad-create' { 'Created draft metadata did not match*' }
                    'upload-failure' { 'Synthetic upload failure.' }
                    'bad-digest' { 'Draft asset verification failed:*' }
                    'public-readback' { 'Draft read-back failed.*' }
                }
                if (($mode -eq 'success' -and $message) -or ($mode -ne 'success' -and $message -notlike $expected)) { throw "Unexpected result: $message" }
                if ($mode -eq 'success' -and $script:workflowCalls.Count -ne 5) { throw 'Missing draft creation, upload or verification request.' }
                if ($mode -eq 'bad-create' -and $script:workflowCalls.Count -ne 1) { throw 'Uploaded after unsafe draft creation.' }
            }
        }
    }
    finally {
        foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $oldEnvironment[$key]) }
        Set-Location -LiteralPath $oldLocation.Path
        Remove-Variable workflowMode, workflowCalls, workflowAssets -Scope Script -ErrorAction SilentlyContinue
    }
}
