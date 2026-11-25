#requires -Version 5.1
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Package.ps1')
$Cases=@('valid_keyboard','valid_unicode','short_correction','owned_wrong','recovery_cancel','valid_clipboard','preflight')
$Phases=@('Preflight','InstallBinding','FreshUser','Recovery','Setup','ShortCorrection','FirstMain','FirstExit','ColdUnlock','WrongRefusal','ColdMain','FinalExit','Cleanup','Complete')
$Reasons=@('None','OperationFailed','Deadline','ExistingUserData','PathRejected','ManifestRejected','BindingMismatch','UnexpectedWindow','ApplicationExited','ApplicationError','ShortLabelIncorrect','ShortAccepted','DatabaseCreatedForShort','WrongAccepted','RecoveryChanged','ExitFailed','WorkerFailed','CleanupIncomplete','ClipboardPayloadMismatch','ClipboardUnavailable','ClipboardClearFailed','ControlObscured','PasswordFocusNotConfirmed','KeyboardCharacterNotMappable','KeyboardLayoutChanged','CapsLockActive','SendInputNotAccepted','ModifierKeyHeld','WindowIdentityLost','ButtonNotUnique','ControlNotInteractable','ClickablePointUnavailable','ClickablePointOutsideDesktop','PasswordControlNotFound','EntryModeInvalid','HostRejected','DesktopUnavailable')
function Get-FileDigest([string]$Path) {
    $stream=[IO.File]::OpenRead($Path); $hash=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $hash.Dispose(); $stream.Dispose() }
}
function Assert-NoReparse([string]$Path) {
    $p=[IO.Path]::GetFullPath($Path)
    while ($p) {
        if ((Test-Path -LiteralPath $p) -and (([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'PathRejected' }
        $p=[IO.Path]::GetDirectoryName($p)
    }
}
function Test-Integer($Value) {
    return $Value -is [int] -or $Value -is [long] -or $Value -is [uint32] -or $Value -is [uint64]
}
function Assert-Keys($Value,[string[]]$Keys) {
    if ($null -eq $Value) { throw 'ManifestRejected' }
    $actual=if ($Value -is [Collections.IDictionary]) {@($Value.Keys)} else {@($Value.PSObject.Properties.Name)}
    if (@(Compare-Object $Keys $actual -CaseSensitive).Count -ne 0) { throw 'ManifestRejected' }
}
function Assert-Manifest([object[]]$Entries) {
    if ($Entries.Count -lt 1) { throw 'ManifestRejected' }
    $set=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($e in $Entries) {
        Assert-Keys $e @('path','sha256','bytes')
        if ($e.path -match '[<>"|?*\x00-\x1f]|[. ](/|$)|(^|/)(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|/|$)') { throw 'ManifestRejected' }
        if ($e.path -isnot [string] -or $e.path -cmatch '(^/|\\|:|//|(^|/)\.{1,2}(/|$)|/$)' -or -not $e.path -or -not $set.Add($e.path) -or $e.sha256 -isnot [string] -or $e.sha256 -cnotmatch '^[0-9a-f]{64}$' -or -not (Test-Integer $e.bytes) -or $e.bytes -lt 0) { throw 'ManifestRejected' }
    }
}
function New-Manifest([string]$Root) {
    Assert-NoReparse $Root
    foreach($directory in (Get-ChildItem -LiteralPath $Root -Directory -Force -Recurse)){Assert-NoReparse $directory.FullName}
    $prefix=[IO.Path]::GetFullPath($Root).TrimEnd([char[]]'\/')+[IO.Path]::DirectorySeparatorChar
    $entries=@(Get-ChildItem -LiteralPath $Root -File -Force -Recurse | Sort-Object FullName | ForEach-Object {
        Assert-NoReparse $_.FullName
        [pscustomobject]@{path=$_.FullName.Substring($prefix.Length).Replace('\','/');sha256=(Get-FileDigest $_.FullName);bytes=$_.Length}
    })
    Assert-Manifest $entries
    return $entries
}
function Assert-Files([string]$Root,[object[]]$Entries,[switch]$Exact,[switch]$Installed) {
    Assert-Manifest $Entries
    foreach ($e in $Entries) {
        $p=Join-Path $Root $e.path; Assert-NoReparse $p
        if (-not [IO.File]::Exists($p) -or ([IO.FileInfo]::new($p)).Length -ne $e.bytes -or (Get-FileDigest $p) -cne $e.sha256) { throw 'BindingMismatch' }
    }
    if ($Exact -or $Installed) {
        $set=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($e in $Entries) { [void]$set.Add($e.path) }
        foreach ($e in (New-Manifest $Root)) {
            if (-not $set.Contains($e.path) -and -not ($Installed -and $e.path -cmatch '^unins[0-9]{3}\.(exe|dat|msg)$')) { throw 'BindingMismatch' }
        }
    }
}
function Required-Milestones([string]$Case) {
    if ($Case -cnotin $Cases) { throw 'HostRejected' }
    if ($Case -ceq 'preflight') { return @('Host','ClipboardPreflight','NestedHost') }
    $m=@('Host','Payload','FreshUser','BinaryUnchanged')
    if ($Case -ceq 'recovery_cancel') { return $m+@('RecoverySeen','RecoveryCancel','RecoveryExited','RecoveryPreserved') }
    $m+=@('SetupSeen','SetupInput','ConfirmInput','SetupSubmitted','FirstMain','FirstExit','UnlockSeen','OwnerInput','OwnerSubmitted','ColdMain','FinalExit')
    if ($Case -ceq 'short_correction') { $m+=@('TooShort','ShortSubmitted','ShortWarningAcknowledged','SetupRetained','NoDatabaseAfterShort') }
    if ($Case -ceq 'owned_wrong') { $m+=@('WrongSubmitted','WrongRefused','WrongAcknowledged','UnlockRetained') }
    if ($Case -ceq 'valid_unicode') { $m+=@('UnicodeExercised') }
    if ($Case -ceq 'valid_clipboard') { $m+=@('ClipboardPreflight') }
    return $m
}
function New-Report([string]$Case) {
    $m=[ordered]@{}; foreach($name in (Required-Milestones $Case)) { $m[$name]=$false }
    return [ordered]@{schema=1;case=$Case;phase='Preflight';category='Harness';reason='OperationFailed';completed=$false;remaining=-1;binding=$null;milestones=$m}
}
function Project-Report($Record) {
    $keys=@('schema','case','phase','category','reason','completed','remaining','binding','milestones')
    $actual=if ($Record -is [Collections.IDictionary]) {@($Record.Keys)} else {@($Record.PSObject.Properties.Name)}
    if (@(Compare-Object $keys $actual -CaseSensitive).Count -ne 0 -or -not (Test-Integer $Record.schema) -or $Record.schema -ne 1 -or $Record.case -isnot [string] -or $Record.case -cnotin $Cases -or $Record.phase -isnot [string] -or $Record.phase -cnotin $Phases -or $Record.category -isnot [string] -or $Record.category -cnotin @('None','Harness','Application','Timeout') -or $Record.reason -isnot [string] -or $Record.reason -cnotin $Reasons -or $Record.completed -isnot [bool] -or -not (Test-Integer $Record.remaining) -or $Record.remaining -lt -1 -or $Record.remaining -gt 32) { throw 'ManifestRejected' }
    $binding=$null
    if ($null -ne $Record.binding) { $binding=Project-Provenance $Record.binding }
    $required=@(Required-Milestones $Record.case)
    $actual=if ($Record.milestones -is [Collections.IDictionary]) {@($Record.milestones.Keys)} else {@($Record.milestones.PSObject.Properties.Name)}
    if (@(Compare-Object $required $actual -CaseSensitive).Count -ne 0) { throw 'ManifestRejected' }
    $projected=[ordered]@{}; $all=$true
    foreach ($name in $required) {
        $v=$Record.milestones.$name
        if ($v -isnot [bool]) { throw 'ManifestRejected' }
        $projected[$name]=$v; $all=$all -and $v
    }
    return [ordered]@{schema=1;case=[string]$Record.case;phase=[string]$Record.phase;category=[string]$Record.category;reason=[string]$Record.reason;completed=[bool]$Record.completed;remaining=[int]$Record.remaining;binding=$binding;milestones=$projected;accepted=(($Record.case -ceq 'preflight' -or $null -ne $binding) -and $all -and $Record.completed -and $Record.phase -ceq 'Complete' -and $Record.category -ceq 'None' -and $Record.reason -ceq 'None' -and $Record.remaining -eq 0)}
}
function Write-Json([string]$Path,$Value) {
    $pending=$Path+'.pending'
    [IO.File]::WriteAllText($pending,(ConvertTo-Json -InputObject $Value -Depth 8 -Compress),[Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($Path)) { [IO.File]::Replace($pending,$Path,[NullString]::Value) } else { [IO.File]::Move($pending,$Path) }
}
function Read-BoundedJson([string]$Path,[int]$Limit=32768) {
    Assert-NoReparse $Path
    if (([IO.FileInfo]::new($Path)).Length -gt $Limit) { throw 'ManifestRejected' }
    if ($null -eq ('NativeUiDriver.JsonGuard' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'JsonGuard.cs') | Out-Null }
    $text=[IO.File]::ReadAllText($Path)
    [NativeUiDriver.JsonGuard]::Validate($text)
    # Preserve nested arrays while normalizing the outer conversion boundary.
    $converter=Get-Command ConvertFrom-Json -ErrorAction Stop
    if($converter.Parameters.ContainsKey('NoEnumerate')){
        $value=$text|ConvertFrom-Json -NoEnumerate
    }else{
        $value=$text|ConvertFrom-Json
    }
    if($null -eq $value){throw 'ManifestRejected'}
    if($value -is [array]){
        foreach($entry in $value){,$entry}
    }else{
        ,$value
    }
}
function Import-Native {
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase,PresentationCore,PresentationFramework,System.Xaml | Out-Null
    $references=@([Windows.Automation.AutomationElement].Assembly.Location,[Windows.Automation.ControlType].Assembly.Location,[Windows.Point].Assembly.Location,[Windows.Controls.PasswordBox].Assembly.Location,[Windows.Media.Visual].Assembly.Location,[System.Xaml.XamlReader].Assembly.Location)
    Add-Type -Path @('NativeInput.cs','NativeExtra.cs','InputPlan.cs','ClipboardContract.cs','ClipboardPreflight.cs'|ForEach-Object {Join-Path $PSScriptRoot $_}) -ReferencedAssemblies $references | Out-Null
    [NativeUiDriver.Native]::SetDpiAwareness()
}
