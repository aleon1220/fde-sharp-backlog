# Workspace Directory Mapping

Default input/output directories and persisted settings across Windows and WSL2.

## Workspace Directory Mapping (Windows & WSL2)

* **Windows Default Root:** `c:\workspace\smart-backlog-assistant\{in,out}`
* **WSL2 Default Root:** `/mnt/c/workspace/smart-backlog-assistant/{in,out}`
* **Settings Persistence:** Overrides (`backlog config --in ... --out ...`) persist in `smart-backlog-assistant/settings.json`. Plaintext API keys and tokens are never written to `settings.json`.

See also: [cli.md](../usage/cli.md) · [logging-and-secrets.md](logging-and-secrets.md) · [environment-variables.md](environment-variables.md)
