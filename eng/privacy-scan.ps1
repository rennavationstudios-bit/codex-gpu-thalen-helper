[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')

function Read-RepositoryTextFile {
    param([Parameter(Mandatory)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) {
        return [pscustomobject]@{ IsText = $true; Content = '' }
    }

    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        return [pscustomobject]@{
            IsText = $true
            Content = [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3)
        }
    }
    if ($bytes.Length -ge 4 -and $bytes[0] -eq 0x00 -and $bytes[1] -eq 0x00 -and $bytes[2] -eq 0xFE -and $bytes[3] -eq 0xFF) {
        $utf32BigEndian = [System.Text.UTF32Encoding]::new($true, $true, $false)
        return [pscustomobject]@{
            IsText = $true
            Content = $utf32BigEndian.GetString($bytes, 4, $bytes.Length - 4)
        }
    }
    if ($bytes.Length -ge 4 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and $bytes[2] -eq 0x00 -and $bytes[3] -eq 0x00) {
        return [pscustomobject]@{
            IsText = $true
            Content = [System.Text.Encoding]::UTF32.GetString($bytes, 4, $bytes.Length - 4)
        }
    }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        return [pscustomobject]@{
            IsText = $true
            Content = [System.Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
        }
    }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        return [pscustomobject]@{
            IsText = $true
            Content = [System.Text.Encoding]::BigEndianUnicode.GetString($bytes, 2, $bytes.Length - 2)
        }
    }

    if ([Array]::IndexOf($bytes, [byte]0) -ge 0) {
        return [pscustomobject]@{ IsText = $false; Content = $null }
    }

    return [pscustomobject]@{
        IsText = $true
        Content = [System.Text.UTF8Encoding]::new($false, $false).GetString($bytes)
    }
}

Push-Location $RepositoryRoot
try {
    $files = if (Test-Path -LiteralPath '.git') {
        @(git ls-files --cached --others --exclude-standard | Where-Object {
            Test-Path -LiteralPath $_ -PathType Leaf
        })
    }
    else {
        @(rg --files --hidden -g '!.git/**' -g '!.tools/**' -g '!.artifacts/**' -g '!.test-results/**' -g '!.scan-work/**' -g '!**/bin/**' -g '!**/obj/**')
    }
    if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate repository files.' }

    $violations = [System.Collections.Generic.List[string]]::new()
    $protectedPersonalUsername = [string]::Concat([char]100, [char]112, [char]114, [char]56, [char]57)
    $githubFineGrainedPrefix = [string]::Concat([char]103, [char]105, [char]116, [char]104, [char]117, [char]98, [char]95, [char]112, [char]97, [char]116, [char]95)
    $patterns = [ordered]@{
        protected_personal_username = '(?i)' + [regex]::Escape($protectedPersonalUsername)
        personal_user_path = '(?i)[A-Z]:[\\/]+Users[\\/]+[^<\\/\s]+[\\/]'
        prohibited_drive = '(?i)K:[\\/]+'
        private_key = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
        github_token = '(?i)(?:' + [regex]::Escape($githubFineGrainedPrefix) + '[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9_]{20,})'
        aws_access_key = '(?:AKIA|ASIA)[A-Z0-9]{16}'
        openai_key = '(?i)sk-(?:proj-)?[A-Za-z0-9_-]{20,}'
        bearer_token = '(?i)Authorization\s*[:=]\s*Bearer\s+[A-Za-z0-9._-]{12,}'
        cookie_header = '(?i)(?:Set-Cookie|Cookie)\s*[:=]\s*[^;\r\n]{12,}'
    }

    foreach ($file in $files) {
        foreach ($entry in $patterns.GetEnumerator()) {
            if ([regex]::IsMatch($file, $entry.Value)) {
                $violations.Add("$file [$($entry.Key)]")
            }
        }
        $result = Read-RepositoryTextFile -Path (Join-Path $RepositoryRoot $file)
        if (-not $result.IsText) {
            continue
        }
        $content = $result.Content
        foreach ($entry in $patterns.GetEnumerator()) {
            if ([regex]::IsMatch($content, $entry.Value)) {
                $violations.Add("$file [$($entry.Key)]")
            }
        }
    }

    if ($violations.Count -gt 0) {
        $violations | Sort-Object -Unique | ForEach-Object { Write-Error "Privacy scan violation: $_" }
        throw 'Privacy and secret scan failed.'
    }

    Write-Host "Privacy and secret scan passed for $($files.Count) repository files."
}
finally {
    Pop-Location
}
