# Verification and reviewer walkthrough

The service has one production project, two test projects, and a manual Postman collection. The private owner planning files are unnecessary for checkout, build, test, or operation.

## Prerequisites

Install the exact SDK from [global.json](../global.json), Git, and a compatible SQL Server when exercising storage. Docker with x86-64 Linux containers can supply SQL Server; a dedicated external test server is supported through `CMS_TEST_SQL_CONNECTION`. Only the optional [load exercise](operations.md#reproduce-the-synthetic-load-exercise) needs Python. Python is unnecessary for account provisioning and normal API execution.

## Test commands

Run from the repository root after the README's locked restore, tool restore, and Release build:

| Suite | Command | Requirements |
| --- | --- | --- |
| Unit + HTTP/host integration | `dotnet test CmsEventProcessor.slnx -c Release --no-build --no-restore --filter "Category!=Sql"` | .NET only |
| SQL-specific integration | `dotnet test CmsEventProcessor.IntegrationTests -c Release --no-build --no-restore --filter "Category=Sql"` | Docker or dedicated external SQL |
| All suites | `dotnet test CmsEventProcessor.slnx -c Release --no-build --no-restore` | All prerequisites above |

Append `--logger trx --results-directory artifacts/test-results` for ignored machine-readable reports. Collect coverage with `--collect "XPlat Code Coverage"`.

External-server mode must point to a **dedicated test server** where the configured principal may create/delete isolated `cms_test_...` catalogs. It must never be an owner's demo/production database connection. A SQL recovery test takes only its generated catalog offline and back online. The external server itself is never stopped. Read [SQL execution boundaries](persistence.md#test-execution-and-evidence) before supplying that private environment variable.

## Manual API walkthrough

Provision accounts, configure SQL, apply migrations, and run the API using either [native setup](../README.md#running-locally) or [Docker setup](../README.md#running-with-docker). Import the collection and an empty environment template into the Postman application, fill credentials privately, then run its 29 requests in order.

The walkthrough checks committed ingestion, exact keyed replay/body conflicts, input validation, unseen unpublish revisions, immediate reader/admin differences, republish, local disabling, override persistence, forbidden reader mutation, hard deletion, stale replay, bounded listings, generated OpenAPI, and protected live/ready checks. A successful batch is queried immediately, without processing-status polling or artificial processing delays. The run deletes its synthetic snapshot; content-free deletion/replay metadata remains according to the retention rules. See the [Postman guide](postman/README.md) for credential handling and the temporary HTTPS demo template.

## CI and platform evidence

[CI](../.github/workflows/ci.yml) starts from a fresh GitHub checkout. Windows and Ubuntu verify locked restore, pinned tools, formatting, Release compilation, and the database-free suites. Ubuntu additionally executes actual SQL tests on an isolated instance. The image build follows successful verification. Only .NET test/coverage reports are uploaded; private settings and raw process captures stay out of artifacts.

The existing [operational measurement](performance.md) records Windows native and Linux-container recovery evidence from 2026-10-27. Current verification results and hosted run links belong in the PR; CI is authoritative for the exact reviewed revision. A Linux image build is not proof of every container startup configuration. macOS execution and an Azure deployment have not been performed. The documented Apple Silicon path runs the native app against external SQL; unsupported SQL container emulation is not a prerequisite.

## Requirement-to-evidence map

These are the employer's requirements, paraphrased from the [brief](assessment/brief.md); atomicity, replay, ordering policy, and operational extras remain documented design choices.

| Assessment concern | Implementation and evidence |
| --- | --- |
| .NET 9 or above; platform-independent service | .NET 10 pinned SDK; Windows/Ubuntu builds; external SQL path for native macOS; [setup](../README.md#running-locally) |
| `/cms/events`, Basic auth, compliant generated username/GUID password, batches | [Webhook](webhook.md), identity/authentication tests, provisioning and credential tests |
| Restricted content and distinct CMS/consumer identities | [Access matrix](api.md#access-matrix), cross-scheme/role HTTP cases, private setup, safe logging |
| Validate/sanitize incoming data | Complete envelope/UTF-8/Unicode/duplicate/depth/count/byte checks; [contract](api.md#post-cmsevents); arbitrary valid payloads preserved |
| Latest revision, including an unseen unpublish | [Domain rules](entity-lifecycle.md), SQL persistence/webhook tests, immediate reader/admin Postman assertions |
| Hard-delete deleted content and retain unpublished content | SQL row/retention checks; consumer visibility and SQL retention tests |
| EF Core and relational persistence | Explicit [SQL migrations](persistence.md), constraints, real SQL tests, restart/replay evidence |
| Consumer listing and administrator visibility | Bounded no-tracking projections/keyset pagination; full visibility matrix in [consumer tests](consumers.md) |
| Users cannot update content; local administrator disable preserves CMS fields | No content-update action; forbidden reader mutations; persistent override and recreation tests |
| Processing choice explained; read-only/writer contexts; optimized reads | [Synchronous ADR](decisions/001-synchronous-processing.md), [read/write ADR](decisions/003-access-and-read-write-boundaries.md), executed SQL/projection assertions |
| Processed and failed event logging | Safe committed outcomes/correlation and classified failures; log privacy and [operational evidence](operations.md) |
| Processing/ingestion/authentication tests | Unit, HTTP, and SQL commands above; CI reports |
| README and repository deliverables | Self-contained [README](../README.md), API/OpenAPI/Postman, preserved original, [deployment](deployment.md) and [release procedure](release.md) |

## Fresh-clone and release review

Use a clean directory and the revision under review, then follow [Running locally](../README.md#running-locally), run the appropriate suites above, and use the [Postman guide](postman/README.md). Confirm there is no reliance on existing `.tmp/`, developer secrets, populated environments or the private planning folder. SDK/package downloads and initial SQL startup can exceed five minutes; the quickstart is a short operator path after prerequisites are installed.

Release verification records the reviewed merge SHA, CI run, source tag, image revision label and digest. Publication is a separate operator action after approval; see the [release procedure](release.md). A release candidate PR alone is not a published version or a cloud deployment.
