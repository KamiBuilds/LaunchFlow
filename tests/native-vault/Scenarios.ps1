# Native installed-product scenarios. No reflection, password ValuePattern, fallback or storage bypass.
function Snapshot-RecoveryRoot([string]$Root) {
    Assert-NoReparse $Root
    if(-not [IO.Directory]::Exists($Root)){throw 'RecoveryChanged'}
    $prefix=[IO.Path]::GetFullPath($Root).TrimEnd([char[]]'\/')+[IO.Path]::DirectorySeparatorChar
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($Root)
    $entries=@(while($pending.Count -gt 0){
        foreach($path in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())){
            Assert-NoReparse $path
            $name=$path.Substring($prefix.Length).Replace('\','/')
            if([IO.Directory]::Exists($path)){
                [pscustomobject]@{path=$name;kind='Directory';bytes=0;sha256=''};$pending.Push($path)
            }else{
                $length=([IO.FileInfo]::new($path)).Length
                [pscustomobject]@{path=$name;kind='File';bytes=$length;sha256=(Get-FileDigest $path)}
            }
        }
    })
    # Only two fixed root-level diagnostic files may be written/rotated by startup.
    foreach($entry in $entries){
        if($entry.path -cin @('startup-diagnostics.log','startup-diagnostics.log.previous') -and ($entry.kind -cne 'File' -or $entry.bytes -gt 262144)){throw 'RecoveryChanged'}
    }
    return $entries
}
function Assert-RecoveryRoot([string]$Root,[object[]]$Before) {
    $after=@(Snapshot-RecoveryRoot $Root)
    $diagnostics=@('startup-diagnostics.log','startup-diagnostics.log.previous')
    # Snapshot all entries, including diagnostics; except only their known bounded writes.
    $expected=@($Before|Where-Object {$_.path -cnotin $diagnostics}|Sort-Object path)
    $actual=@($after|Where-Object {$_.path -cnotin $diagnostics}|Sort-Object path)
    if($expected.Count -ne $actual.Count){throw 'RecoveryChanged'}
    for($i=0;$i -lt $expected.Count;$i++){
        foreach($key in @('path','kind','bytes','sha256')){if($expected[$i].$key -cne $actual[$i].$key){throw 'RecoveryChanged'}}
    }
}
function Save-State { Write-Json (Join-Path $ResultRoot 'private/record.json') $script:Report }
function Phase([string]$Name) {$script:Report.phase=$Name;Save-State}
function Mark([string]$Name) {$script:Report.milestones.$Name=$true;Save-State}
function App-Fail([string]$Reason) {$script:Report.category='Application';$script:Report.reason=$Reason;throw $Reason}
function Safe-WindowKind($Window) {
    # Classifiers are source-defined. Never show raw window text or secret values.
    $known=@('Setup','Unlock','Main','StartupError','DependencyError',
        'StartupRegistrationWarning','WeakInput','SetupInputRejected',
        'AuthenticationRejected','Recovery','VaultUnchanged','Other')
    $kind=if([string]$Window.Kind -cin $known){[string]$Window.Kind}else{'Other'}
    return $kind
}
function Check-App {
    if($script:App.HasExited){App-Fail 'ApplicationExited'}
    $identity=$script:Ledger|Where-Object {$_.pid -eq $script:App.Id}|Select-Object -Last 1
    if($null -eq (Get-Owned $identity)){throw 'WindowIdentityLost'}
}
function Windows {Check-App;return @([NativeUiDriver.Native]::Windows($script:App.Id))}
function Wait-Window([string]$Kind,[string[]]$Allowed=@(),[int]$Seconds=60) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    # Win32/WPF may briefly expose an owned native popup during window
    # transitions. A persistent unknown owned dialog is still an error.
    $unknownOwnedFirstSeen=@{}
    while($timer.Elapsed.TotalSeconds -lt $Seconds) {
        $windows=@(Windows)
        $pendingOwned=$false
        foreach($w in $windows) {
            if($w.Kind -cin @('StartupError','DependencyError')){App-Fail 'ApplicationError'}
            if($w.Kind -ceq 'Main' -and $Kind -cne 'Main'){
                Write-Host ('native-window: phase='+$script:Report.phase+' expected='+$Kind+' actual=Main')
                App-Fail $(if($script:Report.phase -ceq 'ShortCorrection'){'ShortAccepted'}elseif($script:Report.phase -ceq 'WrongRefusal'){'WrongAccepted'}else{'UnexpectedWindow'})
            }
            if($w.Kind -ceq 'StartupRegistrationWarning'){App-Fail 'ApplicationError'}
            if($w.Kind -cne $Kind -and $w.Kind -cnotin $Allowed -and ($w.Kind -cne 'Other' -or $w.Owned)){
                # Project only a fixed classifier into the result's reason; the
                # parent's CI log must never contain raw window titles or secrets.
                if($w.Kind -ceq 'SetupInputRejected'){App-Fail 'UnexpectedSetupDialog'}
                if($w.Kind -ceq 'WeakInput'){App-Fail 'UnexpectedWeakDialog'}
                if($w.Kind -ceq 'Other' -and $w.Owned){
                    # Wait-Main requires a live, enabled and visible tab surface
                    # twice. Ignore unnamed WPF top-level popup surfaces ONLY
                    # while checking for Main, never while admitting a password,
                    # detecting a rejection dialog or restoring encrypted data.
                    if($Kind -ceq 'Main'){continue}
                    $handleKey=[string]$w.Handle
                    if(-not $unknownOwnedFirstSeen.ContainsKey($handleKey)) {
                        $unknownOwnedFirstSeen[$handleKey]=$timer.Elapsed.TotalMilliseconds
                    }
                    if($timer.Elapsed.TotalMilliseconds - $unknownOwnedFirstSeen[$handleKey] -ge 2000) {
                        App-Fail 'UnexpectedOwnedDialog'
                    }
                    $pendingOwned=$true
                    continue
                }
                App-Fail 'UnexpectedWindow'
            }
        }
        $found=@($windows|Where-Object {$_.Kind -ceq $Kind})
        if($found.Count -gt 1){throw 'WindowIdentityLost'}
        if($found.Count -eq 1 -and -not $pendingOwned){return $found[0]}
        Start-Sleep -Milliseconds 150
    }
    throw 'Deadline'
}
function Fill-Field($Window,[string]$Id,[string]$Value) {Check-App;[NativeUiDriver.Native]::Fill($Window.Handle,$script:App.Id,$Id,$Value,$script:Mode)}
function Click-Button($Window,[string]$Name) {Check-App;[NativeUiDriver.Native]::ClickButton($Window.Handle,$script:App.Id,$Name)}
function Dismiss-Dialog($Window) {
    Click-Button $Window 'OK'
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($timer.Elapsed.TotalSeconds -lt 10) {
        $remaining=@(Windows|Where-Object {$_.Handle -eq $Window.Handle})
        if($remaining.Count -eq 0){return}
        Start-Sleep -Milliseconds 100
    }
    throw 'Deadline'
}
function Wait-Main([string]$Milestone,[string]$Previous) {
    $window=Wait-Window 'Main' @($Previous)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        Check-App
        if([NativeUiDriver.Native]::MainReady($window.Handle,$script:App.Id)) {
            Start-Sleep -Milliseconds 400;Check-App
            if([NativeUiDriver.Native]::MainReady($window.Handle,$script:App.Id)){Mark $Milestone;return}
        }
        Start-Sleep -Milliseconds 150
    }while($timer.Elapsed.TotalSeconds -lt 10)
    throw 'Deadline'
}
function Full-Exit([string]$Milestone) {
    Check-App
    $helper=Start-Owned $script:Exe '--exit-for-install'
    if(-not $script:App.WaitForExit(240000) -or -not $helper.WaitForExit(15000)){throw 'Deadline'}
    if($script:App.ExitCode -ne 0 -or $helper.ExitCode -ne 0){App-Fail 'ExitFailed'}
    if((Count-Owned) -ne 0){throw 'ExitFailed'}
    Mark $Milestone
}
function Invoke-Scenario {
    Assert-WindowsHost
    Import-Native
    if([NativeUiDriver.Native]::InputSize -ne 40 -or -not [NativeUiDriver.Native]::InputDesktopAvailable()){throw 'DesktopUnavailable'}
    Mark 'Host'
    if($Case -ceq 'preflight') {
        [NativeUiDriver.Native]::ClipboardPreflight();Mark 'ClipboardPreflight';return
    }
    Phase 'InstallBinding'
    $package=Read-Package $PackageRoot $SourceSha;$script:Report.binding=$package.binding;Save-State
    Phase 'FreshUser';Assert-FreshUser;Mark 'FreshUser'
    # Installer writes only this fresh actor's ordinary product location. It is not a manual wizard test.
    $installer=Start-Owned $script:Installer '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
    Wait-OwnedExit $installer 180
    Assert-Files ([IO.Path]::GetDirectoryName($script:Exe)) $package.runtime -Installed;Mark 'Payload'
    $script:Mode=if($Case -ceq 'valid_keyboard'){'NativeSendInputKeyboard'}elseif($Case -ceq 'valid_clipboard'){'NativeClipboardPaste'}else{'NativeSendInputUnicode'}
    if($Case -ceq 'valid_clipboard'){[NativeUiDriver.Native]::ClipboardPreflight();Mark 'ClipboardPreflight'}
    if($Case -ceq 'recovery_cancel') {
        Phase 'Recovery';[void][IO.Directory]::CreateDirectory($script:DataRoot)
        $db=Join-Path $script:DataRoot 'launchflow.db'
        $stream=[IO.File]::Open($db,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$stream.Dispose()
        $before=Get-FileDigest $db
        $rootBefore=@(Snapshot-RecoveryRoot $script:DataRoot)
        $script:App=Start-Owned $script:Exe
        $dialog=Wait-Window 'Recovery';Mark 'RecoverySeen';Click-Button $dialog ('Cancel '+[char]0x2014+' exit without moving files');Mark 'RecoveryCancel'
        Wait-OwnedExit $script:App 30;Mark 'RecoveryExited'
        if((Get-FileDigest $db) -cne $before -or (Test-Path -LiteralPath ($db+'-wal')) -or (Test-Path -LiteralPath ($db+'-shm'))){App-Fail 'RecoveryChanged'}
        try{Assert-RecoveryRoot $script:DataRoot $rootBefore}catch{App-Fail 'RecoveryChanged'}
        Mark 'RecoveryPreserved'
    } else {
        $script:Owner='Aa9!'+[Guid]::NewGuid().ToString('N')
        if($Case -ceq 'valid_unicode'){$script:Owner+=([char]0x03a9).ToString()+[char]0x4e2d+[char]0x0301;Mark 'UnicodeExercised'}
        Phase 'Setup';$script:App=Start-Owned $script:Exe
        $setup=Wait-Window 'Setup';Mark 'SetupSeen'
        if($Case -ceq 'short_correction') {
            Phase 'ShortCorrection';$short='Aa1!'+[Guid]::NewGuid().ToString('N').Substring(0,6)
            Fill-Field $setup 'PasswordBox' $short;Fill-Field $setup 'ConfirmBox' $short;$short=$null
            if(-not [NativeUiDriver.Native]::TooShortLabelObserved($setup.Handle,$script:App.Id)){App-Fail 'ShortLabelIncorrect'}
            Mark 'TooShort';Click-Button $setup 'Create vault & continue';Mark 'ShortSubmitted'
            $warning=Wait-Window 'WeakInput' @('Setup');Dismiss-Dialog $warning;Mark 'ShortWarningAcknowledged'
            $retry=Wait-Window 'Setup'
            if($retry.Handle -ne $setup.Handle){App-Fail 'UnexpectedWindow'};Mark 'SetupRetained'
            if(Test-Path -LiteralPath (Join-Path $script:DataRoot 'launchflow.db')){App-Fail 'DatabaseCreatedForShort'}
            Mark 'NoDatabaseAfterShort';Phase 'Setup'
        }
        Fill-Field $setup 'PasswordBox' $script:Owner;Mark 'SetupInput'
        Fill-Field $setup 'ConfirmBox' $script:Owner;Mark 'ConfirmInput'
        Click-Button $setup 'Create vault & continue';Mark 'SetupSubmitted'
        Phase 'FirstMain';Wait-Main 'FirstMain' 'Setup'
        Phase 'FirstExit';Full-Exit 'FirstExit'
        Phase 'ColdUnlock';$script:App=Start-Owned $script:Exe
        $unlock=Wait-Window 'Unlock';Mark 'UnlockSeen'
        if($Case -ceq 'owned_wrong') {
            Phase 'WrongRefusal';$wrong='Bb8!'+[Guid]::NewGuid().ToString('N')
            if([string]::Equals($wrong,$script:Owner,[StringComparison]::Ordinal)){throw 'OperationFailed'}
            Fill-Field $unlock 'PasswordBox' $wrong;$wrong=$null
            Click-Button $unlock 'Unlock';Mark 'WrongSubmitted'
            $warning=Wait-Window 'AuthenticationRejected' @('Unlock');Mark 'WrongRefused'
            Dismiss-Dialog $warning;Mark 'WrongAcknowledged'
            # UnlockWindow closes after submitting any nonempty password.
            # On an incorrect password App.InitializeVault starts a NEW
            # UnlockWindow for the next attempt. Keep verifying the UI remains
            # in the unlock flow, but never require the prior HWND to survive.
            $retry=Wait-Window 'Unlock'
            $unlock=$retry
            Mark 'UnlockRetained'
            Phase 'ColdUnlock'
        }
        Fill-Field $unlock 'PasswordBox' $script:Owner;Mark 'OwnerInput'
        Click-Button $unlock 'Unlock';Mark 'OwnerSubmitted'
        Phase 'ColdMain';Wait-Main 'ColdMain' 'Unlock'
        Phase 'FinalExit';Full-Exit 'FinalExit'
    }
    # Full file payload, not merely the EXE, remains bound after both processes exit.
    Assert-Files ([IO.Path]::GetDirectoryName($script:Exe)) $package.runtime -Installed
    Mark 'BinaryUnchanged'
}
