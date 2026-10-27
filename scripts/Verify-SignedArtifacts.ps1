param(
    [Parameter(Mandatory = $true)][string]$ExpectedThumbprint,
    [Parameter(Mandatory = $true)][string[]]$Paths
)

$expected = ($ExpectedThumbprint -replace '\s', '').ToUpperInvariant()
if ($expected -notmatch '\A[0-9A-F]{40}\z') {
    throw 'Configure the approved SHA-1 certificate thumbprint before creating a draft release.'
}

foreach ($path in $Paths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'A release artifact is missing.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw 'Release artifact does not have a valid trusted Authenticode signature.'
    }
    $actual = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
    if ($actual -cne $expected) {
        throw 'Release artifact is signed by an unexpected publisher.'
    }
    $usageExtensions = @($signature.SignerCertificate.Extensions |
        Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] })
    $eku = @($usageExtensions | ForEach-Object { $_.EnhancedKeyUsages } |
        Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })
    if ($eku.Count -eq 0) {
        throw 'Release signing certificate is missing the code-signing purpose.'
    }
    Write-Host ('Verified signed release artifact: ' + (Split-Path $path -Leaf))
}
