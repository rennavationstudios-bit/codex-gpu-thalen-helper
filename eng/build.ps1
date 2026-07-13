[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$LockedMode
)

. (Join-Path $PSScriptRoot 'common.ps1')
Push-Location $RepositoryRoot
try {
    $restoreArguments = @('restore', 'CodexGpuThalenHelper.slnx')
    if ($LockedMode) { $restoreArguments += '--locked-mode' }
    Invoke-Dotnet @restoreArguments
    Invoke-Dotnet build CodexGpuThalenHelper.slnx --no-restore --configuration $Configuration
}
finally {
    Pop-Location
}
