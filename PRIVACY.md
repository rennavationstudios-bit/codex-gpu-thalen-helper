# Privacy policy

Codex GPU Thalen Helper has zero telemetry. It includes no analytics, crash upload, tracking pixel, advertising, affiliate link, remote logging, or background usage reporting.

The helper processes explicitly supplied reviewer text through the user's local Ollama endpoint. It does not call OpenAI. Codex may contact OpenAI independently as part of Codex operation; that is outside this helper.

The helper stores operational state required to manage its installation: version, timestamps, managed-file ownership/backups, selected model and digest, model directory, hardware tier, acceleration result, preferences, availability, and last health status. It does not store prompts, responses, credentials, authentication files, username, hostname, serial numbers, Windows product identifiers, network identifiers, or unrelated application inventory.

Hardware profiling is local and non-telemetric. A user can voluntarily export a redacted diagnostic JSON report. The exporter omits user/host identifiers, serials, prompts, responses, tokens, cookies, and authentication.

Network access is limited to documented needs: loopback Ollama requests, official Ollama release/model sources when the user authorizes installation/download, and explicit GitHub release update checks. No update check runs as a hidden background task.

Uninstallation removes product state. Backups of files modified by the helper remain beside their original files for recovery unless the user removes them.
