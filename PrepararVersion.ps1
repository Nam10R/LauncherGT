param([Parameter(Mandatory=$true)][string]$Clave)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
& (Join-Path $PSScriptRoot 'Build.ps1')
$exe = Join-Path $PSScriptRoot 'dist/Lanzador GTA VI.exe'
$version = [Reflection.AssemblyName]::GetAssemblyName($exe).Version.ToString(3)
$outputDir = Join-Path $PSScriptRoot 'release'
$null = New-Item -ItemType Directory -Force -Path $outputDir
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(
    [Security.Cryptography.CspParameters]@{ ProviderType=24; Flags='CreateEphemeralKey' })
try {
    $protected = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Clave).Path)
    $secret = [Security.Cryptography.ProtectedData]::Unprotect($protected, $null, 'CurrentUser')
    try { $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($secret)) }
    finally { [Array]::Clear($secret,0,$secret.Length) }
    if ($rsa.ToXmlString($false) -cne [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Publisher.xml')).Trim()) {
        throw 'La clave no corresponde a este launcher.'
    }
    $bytes = [IO.File]::ReadAllBytes($exe)
    $signature = $rsa.SignData($bytes, 'SHA256')
    [IO.File]::WriteAllBytes((Join-Path $outputDir 'LauncherGT.exe'), $bytes)
    [IO.File]::WriteAllBytes((Join-Path $outputDir 'LauncherGT.exe.sig'), $signature)
    [IO.File]::WriteAllText((Join-Path $outputDir 'version.txt'), $version, [Text.Encoding]::ASCII)
} finally { $rsa.Dispose() }
Write-Output "Preparada v$version en $outputDir"
