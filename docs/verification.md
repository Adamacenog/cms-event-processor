# Verification and reviewer walkthrough

The service has one production project, two test projects, and an executable Postman collection. The private owner planning files are unnecessary for checkout, build, test, or operation.

## Prerequisites

Install the exact SDK from [global.json](../global.json), Git, and a compatible SQL Server when exercising storage. Docker with x86-64 Linux containers can supply SQL Server; a dedicated external test server is supported through `CMS_TEST_SQL_CONNECTION`. Only the optional automated Postman acceptance needs **Node.js 24** and its locked Newman dependency. Only the optional [load exercise](operations.md#reproduce-the-synthetic-load-exercise) needs Python. Neither tool is needed to provision accounts or run the API.

## Test commands

Run from the repository root after the README's locked restore, tool restore, and Release build:

| Suite | Command | Requirements |
| --- | --- | --- |
| Unit + HTTP/host integration | `dotnet test CmsEventProcessor.slnx -c Release --no-build --no-restore --filter "Category!=Sql&Category!=Acceptance"` | .NET only |
| SQL-specific integration | `dotnet test CmsEventProcessor.IntegrationTests -c Release --no-build --no-restore --filter "Category=Sql"` | Docker or dedicated external SQL |
| Published API + Postman | `dotnet test CmsEventProcessor.IntegrationTests -c Release --no-build --no-restore --filter "Category=Acceptance"` | SQL plus Node 24 and `npm ci --prefix scripts/acceptance --ignore-scripts` |
| All suites | `dotnet test CmsEventProcessor.slnx -c Release --no-build --no-restore` | All prerequisites above |

Append `--logger trx --results-directory artifacts/test-results` for ignored machine-readable reports. Unit/HTTP and SQL runs may additionally collect coverage with `--collect "XPlat Code Coverage"`. The published child process is a separate executable, so the acceptance case verifies runtime behavior without claiming child-process coverage.

External-server mode must point to a **dedicated test server** where the configured principal may create/delete isolated `cms_test_...` catalogs. It must never be an owner's demo/production database connection. A SQL recovery test takes only its generated catalog offline and back online. The external server itself is never stopped. Read [SQL execution boundaries](persistence.md#test-execution-and-evidence) before supplying that private environment variable.

## What the automated collection proves

`PostmanAcceptanceTests` publishes the Release API into an owned ignored folder, runs its actual `accounts init` command, and captures the newly generated credentials privately in memory. It applies the documented migration CLI to an empty generated catalog, then starts real Kestrel on an ephemeral loopback port. This exercises the shipped host without `WebApplicationFactory`, test controllers, developer configuration, or shared credentials.

The C# owner sends a temporary environment to the locked Newman bridge through standard input. The bridge executes the checked-in collection unchanged; it prints only request/assertion/failure counts. There are no exported private environments, raw Newman reports, credentials in process arguments, or uploaded HTTP bodies. Both processes have their inherited application settings isolated. Failures report fixed messages rather than raw child output. Native logs are checked for passwords, synthetic identifiers, and content.

The collection runs **29 requests and 69 assertions** in order. It checks committed ingestion, exact keyed replay/body conflicts, input validation, unseen unpublish revisions, immediate reader/admin differences, republish, local disabling, override persistence, forbidden reader mutation, hard deletion, stale replay, bounded listings, generated OpenAPI, and protected live/ready checks. HTTP redirects are disabled in automated acceptance. A successful batch is queried immediately: there is no processing-status polling or artificial processing delay. A bounded readiness wait occurs only while the native host starts.

After the collection, a separate SQL observer verifies no synthetic snapshot remains, deletion/replay metadata exists, and migration history is present. The owner stops its child processes, removes private account/connection configuration, and deletes its generated database even when assertions fail. Nonsecret published binaries may remain under ignored `.tmp/` for local diagnosis. A successful test validates only that isolated run; it is not a public demo or production load test.

## CI and platform evidence

[CI](../.github/workflows/ci.yml) starts from a fresh GitHub checkout. Windows and Ubuntu verify locked restore, pinned tools, formatting, Release compilation, and the database-free suites. Ubuntu additionally executes actual SQL tests, restores the locked acceptance runner, audits critical advisories, and executes the published API/Postman case on an independently created SQL instance. The image build follows successful verification. Only .NET test/coverage reports are uploaded; private settings and raw process captures stay out of artifacts.

The existing [operational measurement](performance.md) records Windows native and Linux-container recovery evidence from 2026-10-27. Current branch acceptance results and hosted run links belong in its PR; CI is authoritative for the exact reviewed revision. A Linux image build is not proof of every container startup configuration. macOS execution and an Azure deployment have not been performed. The documented Apple Silicon path runs the native app against external SQL; unsupported SQL container emulation is not a prerequisite.

## Acceptance tooling dependency scope

Newman **6.2.3** is optional development tooling, outside the .NET application and runtime image. Its lockfile includes same-major fixes for lodash, Handlebars, Forge, flatted, underscore, qs, and jose. The recorded collection must pass after any override/dependency change.

As checked on 2026-10-27, `npm audit` still reports nine affected package entries (five high/four moderate) through three upstream issues: [Faker template execution](https://github.com/advisories/GHSA-qxc2-j82w-r537), [CSV column parsing](https://github.com/advisories/GHSA-8cw4-87c7-c6xx), and [UUID caller-buffer bounds](https://github.com/advisories/GHSA-w5hq-g745-h8pq). Resolving them requires major dependency changes outside Newman's pinned API surface; they have not been forced into this runner. The checked-in collection uses generated GUIDs, no CSV inputs or Faker templates, and a loopback-only owned API with disposable synthetic credentials. This narrows exposure; it does not mean the upstream libraries are vulnerability-free. Use this bridge only for the repository's trusted collection and isolated fixture, never arbitrary collections or production credentials. CI fails on critical audit findings; its passing audit step does not certify absence of high/moderate advisories.

## Requirement-to-evidence map

These are the employer's requirements, paraphrased from the [brief](assessment/brief.md); atomicity, replay, ordering policy, and operational extras remain documented design choices.

| Assessment concern | Implementation and evidence |
| --- | --- |
| .NET 9 or above; platform-independent service | .NET 10 pinned SDK; Windows/Ubuntu builds; external SQL path for native macOS; [setup](../README.md#running-locally) |
| `/cms/events`, Basic auth, compliant generated username/GUID password, batches | [Webhook](webhook.md), identity/authentication tests, actual provisioning in acceptance |
| Restricted content and distinct CMS/consumer identities | [Access matrix](api.md#access-matrix), cross-scheme/role HTTP cases, private setup, safe logging |
| Validate/sanitize incoming data | Complete envelope/UTF-8/Unicode/duplicate/depth/count/byte checks; [contract](api.md#post-cmsevents); arbitrary valid payloads preserved |
| Latest revision, including an unseen unpublish | [Domain rules](entity-lifecycle.md), SQL persistence/webhook tests, immediate reader/admin Postman assertions |
| Hard-delete deleted content and retain unpublished content | SQL row/retention checks; consumer visibility and final acceptance SQL observer |
| EF Core and relational persistence | Explicit [SQL migrations](persistence.md), constraints, real SQL tests, restart/replay evidence |
| Consumer listing and administrator visibility | Bounded no-tracking projections/keyset pagination; full visibility matrix in [consumer tests](consumers.md) |
| Users cannot update content; local administrator disable preserves CMS fields | No content-update action; forbidden reader mutations; persistent override and recreation tests |
| Processing choice explained; read-only/writer contexts; optimized reads | [Synchronous ADR](decisions/001-synchronous-processing.md), [read/write ADR](decisions/003-access-and-read-write-boundaries.md), executed SQL/projection assertions |
| Processed and failed event logging | Safe committed outcomes/correlation and classified failures; log privacy and [operational evidence](operations.md) |
| Processing/ingestion/authentication tests | Unit, HTTP, SQL, and published-host collection commands above; CI reports |
| README and repository deliverables | Self-contained [README](../README.md), API/OpenAPI/Postman, preserved original, [deployment](deployment.md) and [release procedure](release.md) |

## Fresh-clone and release review

Use a clean directory and the revision under review, then follow [Running locally](../README.md#running-locally), run the appropriate suites above, and use the [Postman guide](postman/README.md). Confirm there is no reliance on existing `.tmp/`, developer secrets, populated environments, global Newman, or the private planning folder. SDK/package/Node downloads and initial SQL startup can exceed five minutes; the quickstart is a short operator path after prerequisites are installed.

Release verification records the reviewed merge SHA, CI run, source tag, image revision label and digest. Publication is a separate operator action after approval; see the [release procedure](release.md). A release candidate PR alone is not a published version or a cloud deployment.
