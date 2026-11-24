# Syntax only on Linux; actual Windows PowerShell 5.1 is a separate workflow gate.
$ErrorActionPreference='Stop'
$count=0
foreach($file in (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File)) {
    $tokens=$null;$errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    if($errors.Count -ne 0){throw ('AstRejected:'+ $file.Name)}
    $count++
}
Write-Host ('PASS PowerShell AST parse: '+$count+' scripts (host '+$PSVersionTable.PSVersion+')')
