# Third-party notices

This project is MIT-licensed. Third-party components retain their own licenses.

Runtime/build dependencies used by version 0.1.0-beta.13 include:

| Component | Pinned version | License/source |
|---|---:|---|
| .NET SDK/runtime | 10.0.301 / 10.0.9 | MIT; <https://github.com/dotnet/runtime> |
| Model Context Protocol C# SDK | 1.4.1 | MIT; <https://github.com/modelcontextprotocol/csharp-sdk> |
| Microsoft.Extensions.Hosting | 10.0.9 | MIT; .NET Foundation |
| System.Management | 10.0.9 | MIT; .NET Foundation |
| Tomlyn | 2.10.1 | BSD-2-Clause; <https://github.com/xoofx/Tomlyn> |
| xUnit.net | 2.9.3 | Apache-2.0; <https://github.com/xunit/xunit> |
| Microsoft.NET.Test.Sdk | 18.7.0 | MIT; Microsoft |
| coverlet.collector | 10.0.0 | MIT; <https://github.com/coverlet-coverage/coverlet> |
| Inno Setup | 7.0.2 | Inno Setup License; <https://jrsoftware.org/isinfo.php> |
| Microsoft SBOM Tool | 4.1.5 | MIT; <https://github.com/microsoft/sbom-tool> |

Ollama is not bundled. If authorized, the helper downloads the current official signed Windows installer and verifies its release checksum and publisher. Ollama retains its own license.

Model weights are not bundled and are not covered by this project's MIT License. See [MODEL_LICENSES.md](MODEL_LICENSES.md).

Release packages contain full license metadata in the generated SBOM. NuGet lock files are the authoritative version inventory for transitive packages.
