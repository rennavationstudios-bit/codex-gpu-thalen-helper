[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
Push-Location $RepositoryRoot
try {
    $json = & $Dotnet list CodexGpuThalenHelper.slnx package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability audit command failed.' }
    $audit = $json | ConvertFrom-Json
    $vulnerabilities = @(
        foreach ($project in @($audit.projects)) {
            foreach ($framework in @($project.frameworks)) {
                foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                    foreach ($vulnerability in @($package.vulnerabilities)) {
                        if ($null -ne $vulnerability) { $vulnerability }
                    }
                }
            }
        }
    )
    if ($vulnerabilities.Count -gt 0) {
        throw "NuGet reported $($vulnerabilities.Count) vulnerable package entries."
    }
    Write-Host 'NuGet vulnerability audit passed.'
}
finally {
    Pop-Location
}
