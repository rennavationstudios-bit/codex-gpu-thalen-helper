[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
$downloads = Join-Path $RepositoryRoot '.tools\downloads'
$innoDirectory = Join-Path $RepositoryRoot '.tools\inno'
$sbomDirectory = Join-Path $RepositoryRoot '.tools\sbom'
New-Item -ItemType Directory -Path $downloads, $innoDirectory, $sbomDirectory -Force | Out-Null

$innoCompilerHash = '0FF6140D641F84B64204A2C4D52207C6FC437C9F4DB8779C83083D84F7E3D70D'

function Test-VerifiedInnoCompiler {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $innoCompilerHash) {
        return $false
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    return $signature.Status -eq 'Valid' -and
        $null -ne $signature.SignerCertificate -and
        $signature.SignerCertificate.Subject -match '(^|,\s*)O=Pyrsys B\.V\.(,|$)'
}

$innoInstaller = Join-Path $downloads 'innosetup-7.0.2-x64.exe'
$innoUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/innosetup-7.0.2-x64.exe'
$innoHash = '5AD54CA3DEF786F8F4212552E54CC6D8D61329E2D24A1CFEE0571D42C2684FF1'
if (-not (Test-Path -LiteralPath $innoInstaller) -or (Get-FileHash -LiteralPath $innoInstaller -Algorithm SHA256).Hash -ne $innoHash) {
    & curl.exe -L --fail --retry 3 --output $innoInstaller $innoUrl
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup download failed.' }
}
if ((Get-FileHash -LiteralPath $innoInstaller -Algorithm SHA256).Hash -ne $innoHash) {
    throw 'Inno Setup checksum mismatch.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $innoInstaller
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Pyrsys B\.V\.') {
    throw 'Inno Setup publisher validation failed.'
}
$innoCompiler = Join-Path $innoDirectory 'ISCC.exe'
if (-not (Test-VerifiedInnoCompiler -Path $innoCompiler)) {
    Reset-RepositoryDirectory -Path $innoDirectory
    $process = Start-Process -FilePath $innoInstaller -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', "/DIR=$innoDirectory"
    ) -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw 'Inno Setup local installation failed.' }
}
if (-not (Test-VerifiedInnoCompiler -Path $innoCompiler)) {
    throw 'The repository-local Inno Setup compiler failed its pinned checksum or publisher validation.'
}

$sbomTool = Join-Path $sbomDirectory 'sbom-tool.exe'
$sbomUrl = 'https://github.com/microsoft/sbom-tool/releases/download/v4.1.5/sbom-tool-win-x64.exe'
$sbomHash = '625767B371B7FDD58F40F618B8A86DA0247A33C89E419039C86B4EDBA1DAD4B5'
if (-not (Test-Path -LiteralPath $sbomTool) -or (Get-FileHash -LiteralPath $sbomTool -Algorithm SHA256).Hash -ne $sbomHash) {
    & curl.exe -L --fail --retry 3 --output $sbomTool $sbomUrl
    if ($LASTEXITCODE -ne 0) { throw 'Microsoft SBOM Tool download failed.' }
}
if ((Get-FileHash -LiteralPath $sbomTool -Algorithm SHA256).Hash -ne $sbomHash) {
    throw 'Microsoft SBOM Tool checksum mismatch.'
}

Write-Host 'Verified release tools are available in the repository-local .tools directory.'
