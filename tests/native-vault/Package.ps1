# Public, fixed-schema package provenance; never paths, process state or credentials.
function Read-PackageManifest([string]$Path) {
    $text=[IO.File]::ReadAllText($Path)
    if(-not $text.TrimStart().StartsWith('[',[StringComparison]::Ordinal)){throw 'ManifestRejected'}
    $decoded=Read-BoundedJson $Path 16777216
    $items=@()
    if($null -ne $decoded){$items=@($decoded)}
    Assert-Manifest $items
    # Unary comma keeps the strict, validated array as one return value;
    # callers do not wrap the command output with @( ... ) again.
    return ,$items
}
function Project-Provenance($p) {
    $keys=@('schema','sourceSha','archiveSha256','sourceBeforeSha256','sourceAfterSha256','runtimeManifestSha256','installerSha256','version','sourceFiles','runtimeFiles','builderExit')
    Assert-Keys $p $keys
    if(-not (Test-Integer $p.schema) -or $p.schema -ne 1 -or $p.sourceSha -isnot [string] -or $p.sourceSha -cnotmatch '^[0-9a-f]{40}$' -or $p.version -isnot [string] -or $p.version -cnotmatch '^\d+\.\d+\.\d+$') {throw 'ManifestRejected'}
    foreach($name in @('archiveSha256','sourceBeforeSha256','sourceAfterSha256','runtimeManifestSha256','installerSha256')) {
        if($p.$name -isnot [string] -or $p.$name -cnotmatch '^[0-9a-f]{64}$'){throw 'ManifestRejected'}
    }
    foreach($name in @('sourceFiles','runtimeFiles')) {if(-not (Test-Integer $p.$name) -or $p.$name -lt 1 -or $p.$name -gt 1000000){throw 'ManifestRejected'}}
    if(-not (Test-Integer $p.builderExit) -or $p.builderExit -ne 0 -or $p.sourceBeforeSha256 -cne $p.sourceAfterSha256){throw 'BindingMismatch'}
    $safe=[ordered]@{};foreach($name in $keys){$safe[$name]=$p.$name};return $safe
}
function Read-Package([string]$Root,[string]$SourceSha) {
    Assert-NoReparse $Root
    $names=@('provenance.json','source-before.json','source-after.json','runtime.json','source.zip','Installer.exe')
    $files=@(Get-ChildItem -LiteralPath $Root -File -Force)
    if(@(Get-ChildItem -LiteralPath $Root -Directory -Force).Count -ne 0 -or @(Compare-Object $names @($files.Name) -CaseSensitive).Count -ne 0){throw 'ManifestRejected'}
    $p=Project-Provenance (Read-BoundedJson (Join-Path $Root 'provenance.json'))
    if($SourceSha -cnotmatch '^[0-9a-f]{40}$' -or $p.sourceSha -cne $SourceSha){throw 'BindingMismatch'}
    foreach($pair in @(@('source.zip','archiveSha256'),@('source-before.json','sourceBeforeSha256'),@('source-after.json','sourceAfterSha256'),@('runtime.json','runtimeManifestSha256'),@('Installer.exe','installerSha256'))) {
        if((Get-FileDigest (Join-Path $Root $pair[0])) -cne $p.($pair[1])){throw 'BindingMismatch'}
    }
    # WinPS 5.1 ConvertFrom-Json may return the top-level JSON array as a
    # *single* Object[] pipeline value. Decode first, then enumerate that value
    # into records; never accept a top-level JSON object as a manifest.
    $source=Read-PackageManifest (Join-Path $Root 'source-before.json')
    $after=Read-PackageManifest (Join-Path $Root 'source-after.json')
    $runtime=Read-PackageManifest (Join-Path $Root 'runtime.json')
    if($source.Count -ne $p.sourceFiles -or $after.Count -ne $source.Count -or $runtime.Count -ne $p.runtimeFiles){throw 'BindingMismatch'}
    foreach($required in @('LaunchFlow.exe','LaunchFlow.dll','coreclr.dll','hostpolicy.dll','Microsoft.Playwright.dll','.playwright/package/cli.js')) {
        if(@($runtime|Where-Object {$_.path -ceq $required}).Count -ne 1){throw 'ManifestRejected'}
    }
    return [pscustomobject]@{binding=$p;runtime=$runtime}
}
