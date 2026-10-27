# Repository working agreement

## Read before making changes

1. Read `README.md` and the selected delivery step in the owner's local `../delivery-plan.md`, when available.
2. Read `docs/assessment/brief.md`, the relevant architecture/decision documents, and the owner's local `../requirements.md`, when available.
3. Inspect the working tree and existing implementation. Do not overwrite unrelated changes.
4. Confirm that prerequisite steps are merged. Discuss unresolved decisions and the requested step's scope with the owner before implementation.

The assessment's original source is `docs/assessment/original.md`. Preserve it byte-for-byte. The formatted brief is editorial; interpretations belong in the architecture and decision records, not in the employer's requirements.

The delivery plan and requirements checklist are private working documents in the repository's parent folder. They are not part of the Git repository or a fresh clone. Keep them there and do not re-add them to the submission. If unavailable, use the in-repository assessment, architecture, API contract, and current user instructions; ordinary build/test work must not depend on those external files. Keep evaluator-facing documentation self-contained and free of links to the private planning files.

## Approval and Git workflow

- The owner reviews implementation changes in the PR. Standing authorization covers committing, pushing, and creating a PR for each requested implementation step; do not stop for a separate pre-commit approval unless the owner explicitly requests it for that work.
- Validate the change, commit it on the planned branch, push it, and open a PR targeting `main`. Include the concrete behavior, verification evidence, and remaining limitations in the PR description and review handoff.
- Address requested or confirmed review findings on the same branch, validate the fixes, and commit/push them to the existing PR. Report material changes for continued PR review.
- Step 01 was the initial approved commit on `main` without a PR. Subsequent steps use PRs; do not create an empty bootstrap commit.
- Subsequent work starts from the latest approved `main`, on the branch named in the delivery plan. Never use a `codex/` branch prefix in this repository.
- Each step is a separate reviewable change. Do not begin the next step before the previous one is approved and merged.
- Merge still requires authorization; standing permission to commit, push, and open PRs does not authorize merging.
- Use focused, descriptive commits. Keep each commit buildable and include tests and documentation with the behavior they cover.
- Do not fabricate reviews, approvals, passing checks, performance results, or implementation status.

## Implementation boundaries

- Keep each class, record, enum, and DTO in its own file, including test support types. Add meaningful XML summaries to types and methods; test summaries must state the scenario and expected behavior. Document fields/properties/constants only when a particular constraint or behavior warrants explanation; avoid comments that merely restate their names.
- Format every XML summary with its opening tag, descriptive text, and closing tag on separate `///` lines. Do not use single-line summaries.
- Document method and constructor inputs with `<param>` tags, returned values with `<returns>` tags, and generic inputs with `<typeparam>` tags. For void methods, describe side effects instead of inventing a return value. Add `<exception>` tags for meaningful errors that can reach the caller; do not document exceptions handled within the method as propagated errors.
- Keep exactly three project directories directly beside `CmsEventProcessor.slnx`: `CmsEventProcessor`, `CmsEventProcessor.UnitTests`, and `CmsEventProcessor.IntegrationTests`. The production project contains `Api`, `Application`, `Domain`, and `Infrastructure` folders, with `Program.cs` at its root. Do not introduce `src`/`tests` wrappers or separate production projects without agreement.
- Target .NET 10, EF Core 10, and SQL Server. Keep the application portable across Windows and macOS with an external database where required.
- Preserve Domain / Application / Infrastructure / API responsibilities and namespace dependency direction described in `docs/architecture.md`. These are logical boundaries within one production assembly, maintained through review; do not claim compile-time project isolation. Contract-signature tests cover the shared identity surface, not every implementation dependency.
- Share identity contracts and safe results through `Application/Identity`; keep stored account definitions, hashing, file provisioning, and validation in `Infrastructure/Identity`. The API owns HTTP authentication/options, error handling, and command presentation. Only its composition root references Infrastructure registration; handlers/commands depend on Application interfaces. Keep the two purpose-specific identity interfaces; do not add an interface or generic framework for every class.
- Process CMS batches within the HTTP request and commit before returning success. Use asynchronous database I/O, but do not introduce a hosted worker, durable inbox, scheduled retries, or a status-polling endpoint. Transient failures are retried by the caller using the same optional idempotency key and body.
- Prioritize event correctness, durability, and restricted data access. Do not add a broker, cache, frontend, or separate service without an agreed change to the plan.
- Treat all CMS payloads as confidential. Never commit credentials, log payloads or authorization headers, or enable EF sensitive-data logging in shared environments.
- Follow the README's explicit credential-provisioning lifecycle: preconfigure CMS/reader/admin accounts, persist only password hashes in local/deployment secrets, display newly generated passwords once during setup, and load accounts at startup without silent regeneration. Account changes and rotation require explicit configuration changes and a restart.
- Keep identifiers, versions, publication state, and administrator overrides semantically distinct. Update the event decision table and tests when a rule changes.
- Do not describe an unimplemented endpoint, script, test, or deployment as available.

## Verification and handoff

- For documentation changes, check local links, JSON examples, requirement coverage, Mermaid syntax where tooling is available, and whitespace. The foundation now has unit and HTTP integration tests; SQL integration tests arrive with persistence.
- Once the solution exists, use the checked-in SDK/tool configuration and the commands documented in the README and CI. Verify formatting, Release build, relevant unit tests, and SQL-backed integration tests for affected behavior.
- Use real SQL Server for SQL-specific behavior; in-memory tests do not prove transactions, locking, collation, migrations, or database recovery.
- Keep all checks appropriate to the change. Report unavailable checks rather than marking them passed.
- Update the external delivery-plan handoff entry with the actual outcome and evidence when it is available. Record merged PR links only after they exist.
- Review the full diff against the selected step's acceptance criteria. Report confirmed defects and missing requirements; do not manufacture findings.
