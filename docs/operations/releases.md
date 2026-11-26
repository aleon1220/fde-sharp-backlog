# Releases & Packaging

Maintainer workflow for publishing CI-gated, checksummed, and attested standalone release binaries.

## Publishing a Release (Maintainers)

* **Green-Gated GitHub Release Pipeline ([`.github/workflows/release-binaries.yml`](../../.github/workflows/release-binaries.yml)):** Push an annotated semver tag (`git tag -a v1.1.0 -m "Release v1.1.0" && git push origin v1.1.0`) or trigger `workflow_dispatch` on `.github/workflows/release-binaries.yml`.
* **Verification Gate:** The `verify` job runs hardened `--locked-mode` NuGet restores, Release `-warnaserror` builds, and the full test suite before `package` builds self-contained single-file archives for `linux-x64`, `win-x64`, and `osx-arm64`.
* **Checksums & Attestation:** Publishes `SHA256SUMS.txt` alongside SLSA build provenance (`actions/attest-build-provenance@v2`) and out-of-repo standalone binary smoke checks.
* **Local Packaging Dry-Run:** Run `./scripts/package-release.sh` (Linux/macOS) or `.\scripts\package-release.ps1` (Windows PowerShell) locally to build archives, `.sha256` files, and run standalone smoke checks before tagging.

See also: [install-binary.md](../getting-started/install-binary.md) · [containers.md](containers.md) · [Issue #59](https://github.com/aleon1220/fde-sharp-backlog/issues/59)
