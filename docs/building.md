# Build from source

## Requirements

- Windows x64.
- Git.
- .NET 10 SDK 10.0.301 (selected by `global.json`).
- Inno Setup 7.0.2 for setup packaging.
- Microsoft SBOM Tool 4.1.5 for release SBOM generation.

End-user packages are self-contained; these tools are needed only by developers.

## Build and test

```powershell
git clone https://github.com/rennavationstudios-bit/codex-gpu-thalen-helper.git
Set-Location .\codex-gpu-thalen-helper
dotnet restore --locked-mode
.\eng\test.ps1 -Configuration Release -LockedMode -Coverage
```

Tests force a temporary `CODEX_HOME` and product-state directory. Routine tests use a mock loopback Ollama server and never invoke a real model. A real GPU test is separately opt-in and must not run while other important GPU workloads are active.

## Package

Install the exact pinned Inno/SBOM tools locally, or pass their executable paths:

```powershell
.\eng\package.ps1 `
  -InnoCompiler 'C:\path\to\ISCC.exe' `
  -SbomTool 'C:\path\to\sbom-tool.exe'
```

Outputs are written only under `.artifacts\release`. The script performs locked restore, Release build/tests, self-contained single-file publishes, Inno compilation, SPDX SBOM generation, signing-status disclosure, and SHA-256 generation.

## Silent package lifecycle test

Installer lifecycle execution is reserved for the release workflow on a disposable GitHub-hosted Windows runner. `eng/installer-lifecycle-test.ps1` rejects a local machine even when its explicit opt-in flag is supplied. The workflow tests an isolated configured install, the `/NOCONFIGURE=1` package-only path, and a SHA-pinned beta.5-to-current in-place upgrade with a private dry-run, four-hash migration apply, idempotent repair, and surgical uninstall. Every path stays beneath `RUNNER_TEMP`; none targets the runner's real Codex home or model storage, downloads a model, or runs inference.

Real silent configuration requires exactly one explicit `/CODEXHOME`, `/MODELSDIR`, `/MODEL` (or `auto`), `/AUTOSTART`, `/PULLANDVALIDATE`, and `/RELIABILITYBASELINE=false` choice. `/STATEDIR` is available for isolation. Duplicate, malformed, missing, or mutually conflicting switches are rejected before configuration begins. `/RELIABILITYBASELINE=true` is rejected because silent setup cannot show the required preview; the interactive wizard keeps both the reliability baseline and model download/validation unchecked by default and displays the baseline's before/after diff before installation.
