$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework WPF
$stage = Join-Path $projectDir 'dist'
$null = New-Item -ItemType Directory -Force -Path $stage
$destination = Join-Path $stage 'Lanzador GTA VI.exe'
$options = @('/nologo','/target:winexe','/platform:x64','/optimize+','/debug-','/codepage:65001',"/out:$destination")
foreach ($lib in @('WindowsBase','PresentationCore','PresentationFramework')) {
    $options += '/r:' + (Join-Path $wpf "$lib.dll")
}
foreach ($lib in @('System.Xaml','System.Web.Extensions','System.Windows.Forms','System.Xml','System.Xml.Linq','System.IO.Compression','System.IO.Compression.FileSystem','System.Net.Http')) {
    $options += "/r:$lib.dll"
}
$options += '/win32icon:' + (Join-Path $projectDir 'arte\Lanzador.ico')
foreach ($pair in @('Window.xaml','Card.xaml','arte\Cover.png')) {
    $options += '/resource:' + (Join-Path $projectDir $pair) + ',' + [IO.Path]::GetFileName($pair)
}
foreach ($logo in @('Xcl.png','Victory.png')) {
    $options += '/resource:' + (Join-Path $projectDir ('arte\' + $logo)) + ',' + $logo
}
$options += '/resource:' + (Join-Path $projectDir 'Publisher.xml') + ',Publisher.xml'
foreach ($source in @('Lanzador.cs','PublicLibrary.cs','PublicInstall.cs','BuildTools.cs','Updates.cs','SelfUpdate.cs','CgbjStatus.cs','PersonalProtection.cs')) {
    $options += Join-Path $projectDir $source
}
& (Join-Path $framework 'csc.exe') @options
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el lanzador.' }
Copy-Item -LiteralPath (Join-Path $projectDir 'LEEME.txt') -Destination $stage
