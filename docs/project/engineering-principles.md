# Engineering Principles

How `SmartBacklogAssistant` is built. Read before opening a PR.

## Design philosophy

* Practical, user-centric UX: scannable output, progressive disclosure, sensible defaults, and zero visual clutter.
* One unified workflow per task; avoid duplicate code paths.

## AI-First SDLC contract

| Responsibility | Owner |
| --- | --- |
| File ingestion, secret hygiene, formatting, persistence, validation | Deterministic C# |
| Unstructured requirement synthesis | LLM |

**Zero Hallucination:** Never invent internal owners, ticket IDs, sprint dates, or metrics absent from the source.

## Output formats (`Type01` / `Type02` parity)

| Format | Stage | Required sections |
| --- | --- | --- |
| `Type01` | `complete` | `Title`, `User Story`, verbosity-aware `Description` (`* ` bullets), `Acceptance Criteria (Given / When / Then)`, `Alternatives` |
| `Type02` | `schedule` | `Title`, verbosity-aware `Description` (`* ` bullets), `Alternatives` (no `User Story` or `GWT` clutter) |

**Verbosity budgets (`DescriptionFormatter`):** `Minimalistic` ≤ 25 words, `Balanced` ≤ 57 words, `Detailed` ≤ 95 words.

## Build, lockfile & security discipline

* [`Directory.Build.props`](../../Directory.Build.props) sets `RestorePackagesWithLockFile=true`; `packages.lock.json` is committed for [`SmartBacklogAssistant`](../../src/SmartBacklogAssistant/packages.lock.json) and [`SmartBacklogAssistant.Tests`](../../tests/SmartBacklogAssistant.Tests/packages.lock.json), and `RestoreLockedMode=true` is enforced when `ContinuousIntegrationBuild=true`.
* Restore with `--locked-mode`. Use `--force-evaluate` only when intentionally adding or updating a NuGet package, then commit the updated lockfiles.
* Builds must pass with zero warnings and all tests passing:

```bash
dotnet restore SmartBacklogAssistant.slnx --locked-mode
dotnet build SmartBacklogAssistant.slnx -c Release -p:TreatWarningsAsErrors=true
dotnet test SmartBacklogAssistant.slnx -c Release
```

* **Secrets:** Never write API keys or tokens to `settings.json`, `stdout`, or unredacted `stderr`. Route secrets through [`GeminiSecretProvider`](../../src/SmartBacklogAssistant/Services/GeminiSecretProvider.cs), [`SessionEnvironmentStore`](../../src/SmartBacklogAssistant/Services/SessionEnvironmentStore.cs), [`ApiKeyManagementService`](../../src/SmartBacklogAssistant/Services/ApiKeyManagementService.cs), and [`SecretRedactor`](../../src/SmartBacklogAssistant/Services/SecretRedactor.cs).

See also: [logging-and-secrets.md](../configuration/logging-and-secrets.md) · [build-from-source.md](../getting-started/build-from-source.md) · [BACKLOG.md](BACKLOG.md) · [PLAN.md](PLAN.md)
