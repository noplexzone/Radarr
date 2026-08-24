# Radarr Multi-Edition Production Hardening Implementation Plan

> **For Hermes:** Execute task-by-task with TDD, stable commits, independent specification and quality review, and final artifact verification.

**Goal:** Make one Main file plus one independently monitored current file per durable edition slot safe for controlled production use without changing ordinary Radarr behavior for movies with no slots.

**Architecture:** Introduce one explicit acquisition target contract (`Main` or `EditionSlot:<stable ID>`) and one deterministic structured matcher used at every search, decision, grab, history, reconstruction, failure, retry, and import boundary. Add forward-only identity and pending-operation schema, a recoverable staged file-operation coordinator, startup/disk-scan integrity reconciliation, rendered-path uniqueness preflight, server-side grab revalidation, typed diagnostics, and UI safety status. Preserve the existing slot table/IDs, migrations 243–245, target-history propagation, slot-specific quality/custom-format overrides, upgrade preflight, and stable filename suffix behavior.

**Tech stack:** C#/.NET 8, FluentMigrator, NUnit/Moq/FluentAssertions, React/TypeScript, Redux, Yarn/Webpack, Docker, GitHub Actions.

## Baseline and upstream state

- Feature HEAD before hardening: `45b5e0b92d100ca86fae01feafc8c9639dd3b9b2`.
- Upstream merge base: `520bf4215a13223433ef6c77ad7e822cd8359c94`, tagged `v6.2.1.10437`.
- Current reviewed upstream: `origin/develop` `a11778302bc04e9d60b7d1d0c640c423272deb0b`, `v6.4.2.10590-9-ga11778302`.
- Frontend baseline: frozen Yarn install, ESLint, Stylelint, and production Webpack build pass.
- Backend baseline: restore and Release build pass. Full-solution execution has environment-dependent Automation/Integration failures because the test app/ChromeDriver are absent; Common has two malformed Cloudflare-cookie failures; Core has one `DatabaseTargetFixture.write_long_log` failure. Preserve these separately from hardening regressions.
- Upstream changed release parsing, search handling, pending-release/download decisions, tracked-download/history reconstruction, imports, disk scanning, and migrations since divergence. Replacement and rename behavior were not materially changed, but their callers/contracts changed.

## Global constraints

- One normal Main file; zero or more durable named slots; at most one current file per slot.
- Never assign a file to two targets, cross movies/slots, silently guess an ambiguous edition, or treat null/unknown intent as Main.
- Never irreversibly delete/recycle an active file before recoverability is durable.
- Never rewrite migrations 243–245; all schema changes are forward migrations.
- Preserve ordinary upstream behavior when a movie has no edition slots.
- No production recommendation until failure injection, migrations, restart recovery, compatibility, packaging, runtime, and acceptance gates pass.
- No production-container mutation without Caleb’s separate approval. Publish only `noplexzone/radarr:develop` for acceptance after all branch gates pass.

## Verification command pattern

Focused backend tests:

```bash
DOCKER_HOST=tcp://172.18.0.1:2375 docker run --rm \
  -v /mnt/user/appdata/dev/radarr:/src -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test src/NzbDrone.Core.Test/Radarr.Core.Test.csproj \
  -c Release /p:RunAnalyzers=false /p:TreatWarningsAsErrors=false \
  --filter "FullyQualifiedName~<Fixture>"
```

API tests use `src/NzbDrone.Api.Test/Radarr.Api.Test.csproj`. Each task must first run its new test and confirm RED for the intended reason, then GREEN, then the adjacent fixture group, `git diff --check`, and a Release build of the owning project.

---

## Task 1 — Explicit acquisition target contract

**Objective:** Replace nullable-slot intent with an explicit target kind while retaining compatibility adapters only at legacy serialization boundaries.

**Files:** create `MovieAcquisitionTarget.cs`; modify `MovieSearchCriteria`, `RemoteMovie`, `LocalMovie`, `GrabbedReleaseInfo`, `TrackedDownload`, queue/history resources/events, search commands, and fixtures.

**Interfaces:** `MovieAcquisitionTarget.Main`, `ForEditionSlot(int)`, equality/serialization helpers, and `Unknown` only for fail-closed legacy reconstruction. Compatibility `MovieEditionSlotId` derives from the target and is never authoritative.

**Tests:** Main, exact slot, unknown, stale slot, transition clearing stale ID, queue/history serialization, restart reconstruction, same download ID with Main/slot A/slot B.

**Commit:** `feat(editions): introduce explicit acquisition targets`

## Task 2 — Deterministic matcher and Main isolation

**Objective:** Centralize normalized matching and ensure Main rejects configured, unknown, or ambiguous edition evidence.

**Files:** create `EditionMatchResult.cs`, `EditionMatchSource.cs`, `MovieEditionMatcher.cs`; replace matching in `MovieEditionSpecification`, `DownloadDecisionMaker`, RSS/release-push decisions, and search services; extend release resources.

**Result:** `NoEditionEvidence`, `UniqueSlot`, `UnknownEdition`, `Ambiguous`, `Invalid`; collect all canonical/search/alias candidates; parsed metadata wins only when uniquely mapped; longer token sequences win only uniquely; ties remain ambiguous. Safe unknown-edition default is reject/manual review.

**Tests:** Extended, Extended Edition, Director’s Cut, Extended Director’s Cut, Unrated, Unrated Extended Edition, Theatrical, Ultimate Cut, Final Cut, aliases, punctuation/apostrophes, abbreviations, missing metadata, multiple aliases, ordinary title/release-group words, both overlap orderings, all search/RSS/push paths, and no-slot behavior.

**Commit:** `feat(editions): isolate main and slot release matching`

## Task 3 — Exact target through lifecycle and failure paths

**Objective:** Propagate the target through missing/cutoff search, grabs, queue/cache refresh, both history stores, restart, import, failures, and retries.

**Files:** search services/commands, `DownloadService`, both history services, tracked-download services, failed-download services, import aggregation/approval, queue/history API mappers, notifications/events.

**Tests:** malformed history fails closed; every terminal history stamped; cache refresh preserves target/profile/minimum/formats; failure for slot A cannot select slot B/Main; retry exact target; restarted queue exact.

**Commit:** `feat(editions): preserve exact targets through download lifecycle`

## Task 4 — Identity schema and transactional validation

**Objective:** Prevent one movie’s slots from claiming the same normalized canonical/search/alias identity without guessing during migration.

**Files:** forward migration 246 (or next free post-integration version), `MovieEditionIdentities` entity/repository/service/mapping, transaction-scoped slot mutation, API and migration fixtures.

**Schema:** `(MovieId, MovieEditionSlotId, NormalizedTerm, IdentityType, DisplayValue)` with unique `(MovieId, NormalizedTerm)` after collision-free backfill. Record existing collisions for manual repair; do not rewrite ownership.

**Tests:** clean upstream, migrations 243–245/prototype shapes, duplicates/orphans, deterministic large backfill, interrupted startup, concurrent aliases, overlap validation.

**Commit:** `feat(editions): enforce unique edition identities`

## Task 5 — Recoverable file-operation coordinator

**Objective:** Make promote, replace, remove, recycle/delete, reassign, clear, convert, rename, and organizer mutations recoverable.

**Files:** forward migration/model/repository for `MovieEditionFileOperations`; executor, staging policy, compensation/recovery, fault-injection seam; route assignment, upgrade, import, deletion, move, and rename through it.

**Sequence:** validate state/ownership → source/destination/collision preflight → durable pending row → reversible staging → one DB transaction → invariant audit → commit → cleanup/finalize; restore or retain recoverable pending state on failure.

**Tests:** failure before/during movement, after staging Main, after clearing slot, after file update, after Main pointer change, before slot delete, after commit, cleanup failure, and restart at every state. Assert no valid file loss.

**Commit:** `feat(editions): make file operations recoverable`

## Task 6 — Integrity auditor and reconciliation

**Objective:** Detect and classify drift at startup, refresh, rescan, recovery, migration, and manual preview/repair boundaries without filename-based reassignment.

**Files:** finding/status/repair contracts, auditor/reconciler, health check, commands/API, startup registration, scan/refresh hooks; replace `ReconcileForMovie` no-op.

**Findings:** missing Main/slot files, deleted slot, cross-movie, dual Main+slot, duplicate slot file, pending operation, restore/tree mismatch, unassigned edition-looking file. Classes: safe automatic, recoverable pending, manual review, unrecoverable. Destructive repair requires preview.

**Commit:** `feat(editions): audit and reconcile edition integrity`

## Task 7 — Rendered filename uniqueness

**Objective:** Prove final rendered paths are unique before moving or deleting.

**Files:** `FileNameBuilder`, moving/rename/import services, preview API/resources, fixtures.

**Behavior/tests:** render all targets under platform-aware case/path normalization; stable readable slot-ID suffix for blank/non-durable/colliding values; whole-batch preflight; populated/blank tokens, identical names, no token, Main-slot/slot-slot collision, foreign path, display rename, Windows/Unix, punctuation/case/Unicode/path length.

**Commit:** `feat(editions): guarantee rendered filename uniqueness`

## Task 8 — Manual grab/API boundary hardening

**Objective:** Revalidate movie, target, release, monitoring, matching, quality, and formats immediately before download.

**Files:** `ReleaseController`, release resources/cache contract or opaque decision token, API fixtures.

**Behavior/tests:** key by movie+target+indexer+release; client target is a claim only; reject foreign/deleted/unmonitored/mismatched/stale/tampered requests before `DownloadReport`; clone complete resolved context; test Main/slot swaps and cache collisions.

**Commit:** `feat(editions): revalidate targets before manual grab`

## Task 9 — Typed diagnostics and safety UI

**Objective:** Show Main/slots as explicit upgrade tracks with target status, rejection/ambiguity, integrity, operations, and exact destructive outcomes.

**Files:** edition table/modals, interactive releases, queue/history rows, translations, typed API state, CSS/tests.

**UI:** monitoring, file, quality/CF, missing/cutoff, queue/history target, ambiguity reason, integrity/pending operation, filename preview, exact recycle/delete/retain/reassign text, disabled unsafe actions, server errors, and copy: “One Main file plus one current file per edition slot.”

**Checks:** frontend tests, ESLint, Stylelint, Webpack type/build, Impeccable detector, bounded desktop/mobile inspection and console/network check.

**Commit:** `feat(editions): expose target and integrity diagnostics`

## Task 10 — Manual upstream integration

**Objective:** Integrate the intended production upstream after hardening contracts/tests make conflicts diagnosable.

**Procedure:** backup ref; deliberate merge/rebase; manually review parser, searches, release push, decisions, queue/tracking, history/failure, import/replacement, refresh/scan, organizer/naming, API/UI, and migrations. Preserve upstream fixes and renumber only new migrations if needed; never resolve lifecycle/migration conflicts wholesale.

**Commit:** `chore(editions): integrate current upstream radarr`

## Task 11 — Complete test, migration, concurrency, CI, and docs matrix

**Objective:** Close every matrix item and record reproducible CI evidence.

**Gates:** legacy no-slot refresh/rescan/rename/restart/search/download/import/upgrade; Main+Director’s Cut E2E; overlap matrix; every search path; import/upgrade/naming isolation; full recovery and restart matrix; clean/prototype/realistic DB migrations, collisions/orphans/interruption/backup restoration; concurrency races; backend restore/build/unit/integration; frontend install/lint/style/type/tests/build; Docker/package; clean and copied-DB startup; API and search-to-import E2E. CI may not weaken assertions or skip new tests.

**Docs:** semantics, one-file-per-slot, matching/ambiguity, naming, migration/unassigned files, integrity/recovery, backup/rollback, isolated rollout.

**Commits:** `test(editions): complete production acceptance matrix`, `docs(editions): add rollout and recovery guide`, and a separate CI commit if needed.

## Task 12 — Independent review, artifact, and recommendation

**Objective:** Produce a stable reviewed branch and honest recommendation.

**Gates:** independent spec then quality/security/data-loss review; remediate all critical/important findings; fresh exact-HEAD checks/diff/clean state; push child branch and monitor CI; publish only `noplexzone/radarr:develop` through CI; verify digest and fresh test-container startup/API without touching production. Any missing recovery/migration/compatibility gate caps recommendation at isolated beta.

## Requirement-to-task mapping

| Requirement | Tasks |
|---|---|
| Explicit targets/lifecycle | 1, 3 |
| Main isolation/matching/ambiguity | 2 |
| Identity uniqueness/migration | 4, 11 |
| Recoverable destructive operations | 5 |
| Restart/rescan/integrity | 6 |
| Filename safety | 7 |
| Manual grab/API | 8 |
| UI/diagnostics/logging | 2, 5, 6, 9 |
| Upstream/legacy compatibility | 10, 11 |
| CI/failure/concurrency/E2E | 11 |
| Documentation/rollback/final report | 11, 12 |

## Completion rule

Each task follows implementation → independent specification review → independent quality/security review → remediation → Jarvis verification. No task completes from an agent summary. The branch is not production-ready until Tasks 1–12 and every destructive, restart, migration, compatibility, packaging, and runtime gate have fresh passing evidence.
