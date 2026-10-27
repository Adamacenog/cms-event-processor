# SQL persistence and transactional processing

The application processes validated batches against real SQL Server. Direct integration tests exercise this layer, and the [implemented webhook](webhook.md) adds authenticated HTTP/SQL coverage. Consumer controllers remain subsequent work.

## Responsibilities

| Area | Responsibility |
| --- | --- |
| Application/Processing/Models | Immutable inputs, exact-byte digests, response metadata, and safe operation classifications |
| Application/Processing/Contracts | Purpose-specific mutation factory/transaction operations |
| Application/Processing/Services | Ordered batch evaluation, replay/expiry decisions, local disabling, and one-attempt deadlines |
| Infrastructure/Persistence/Models | EF rows containing current content, deletion watermarks, or successful replay metadata |
| Infrastructure/Persistence/Contexts | Separate writer and no-tracking reader; reader SaveChanges overloads reject writes |
| Infrastructure/Persistence/Services | Transaction-owned SQL lock, state persistence, response storage, and cleanup |
| Infrastructure/Persistence/Migrations | Checked-in schema and model snapshot, applied only by an explicit operator command |

No repository framework, hosted worker, durable inbox, automatic transaction replay, or new production project was added. The SQL provider's transitive PKCS dependency is explicitly pinned to patched 9.0.22 to keep restore vulnerability auditing enabled; the [Microsoft advisory](https://github.com/advisories/GHSA-846f-f764-76mx) identifies the affected earlier versions.

## Schema and privacy

Snapshots use a bounded source-ID primary key with Latin1_General_100_BIN2 collation, JSON-object and positive-version checks, a reader-visible filtered index, and a SQL rowversion independent of the CMS version. DeletionMarkers contains only identity and source deletion time.

ReplayResults has a composite primary key of exact CMS principal and SHA-256 key digest, the SHA-256 exact-body digest, successful response JSON, and completion/expiry times. Hashing opaque keys avoids SQL's padded-string equality changing their meaning. Key case and whitespace are not normalized. Response metadata includes source IDs/outcomes; it contains no copied entity payload or submitted body.

Only Snapshots retains current payloads. Hard deletion removes that row and the override while retaining the deletion watermark and any unexpired metadata-only replay results. This is not a guarantee about backups or erasure of identifiers.

## Atomic processing and concurrency

1. Create a fresh writer/transaction under one linked request/deadline token.
2. Acquire the database-scoped exclusive application lock before reading mutable state or replay records. Every negative acquisition return code stops the attempt.
3. Check an optional principal/key digest before domain evaluation. An unexpired exact-body match returns the original response; a different body conflicts.
4. For new processing, delete at most 100 expired metadata rows, then evaluate events in submitted order. Each applied event flushes inside the transaction so later same-ID events see its state.
5. A version/content conflict rejects the batch with its failing event index and no tentative successful outcomes.
6. Store successful keyed response metadata with seven-day expiry, then commit entity and response changes together.
7. Return/log applied or skipped outcomes only after successful commit acknowledgment.

The global lock is shared with EntityDisabler. A local disable preserves source fields and uses the same transaction protocol. Missing/deleted snapshots return NotFound; repeated disable succeeds without rewriting the row.

A conflict, cancellation, or precommit failure rolls the transaction back. Opportunistic cleanup rolls back too. Unkeyed requests create no response-history row. Fast unexpired replays end a read-only transaction without domain changes or expiry cleanup.

Coordination uses [transaction-owned SQL application locks](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql), with success codes checked explicitly. Global write serialization is an explicit assessment-scale throughput tradeoff. The reader index and future bounded projections support reads; higher write concurrency needs measured evidence before redesign.

## Failure and deadline behavior

| Result | Meaning |
| --- | --- |
| Succeeded | Commit acknowledged, or an existing committed response was replayed |
| VersionConflict | Current-version content disagrees; the attempted batch does not commit |
| IdempotencyConflict | Unexpired scoped key has different exact request bytes |
| StorageUnavailable | Connection, lock, or storage could not complete before commit |
| TimedOut | The processing deadline expired before a commit attempt |
| CommitUnknown | A commit was attempted but its acknowledgment was not confirmed |
| NotFound | Local disabling found no retained entity |

The default Processing:Timeout is 30 seconds, configurable up to five minutes. Processing:LockWait defaults to five seconds and must fit within that total budget. The deadline is cooperative and covers connection, locking, queries, writes, and commit through the same token; it is not reset per event.

Caller cancellation propagates as cancellation. Rollback uses a separate two-second cleanup token, and transaction/context disposal always runs; provider cleanup can add time beyond the processing budget. SQL owns lock lifetime until its transaction ends. Disconnection detection/cleanup is not a distributed lease guarantee.

A failure after commit begins never becomes a claim of guaranteed rollback. Retry the same key/body to recover a successful result if it committed. A commit confirmed by SQL but followed by a lost response is also replayable. The service does not resume interrupted work or retry it automatically.

Unlike an [EF retrying execution strategy](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency), this attempt is never replayed automatically. EF uses a non-retrying strategy, connection retries are disabled, and failed pool connections do not impose a cached blocking period on later caller attempts. Logs contain correlation, event positions, safe outcomes, and failure types. Provider logging is suppressed because SQL exception messages may include confidential values even with sensitive-data logging disabled.

## Local SQL setup

Use SQL Server 2022 Developer only for development/test. The pinned image is mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04. It requires an x86-64 Linux container engine. A remote compatible SQL Server is the alternative for environments such as Apple Silicon.

Copy [.env.example](../.env.example) to the ignored .env file. Set a private complex SQL_SA_PASSWORD in that file. SQL_PORT defaults to 1433 and can be changed if that local port is occupied. Keep the file private and never paste its values into shared examples.

From the repository root:

```sh
docker compose up --detach --wait sql
dotnet restore CmsEventProcessor.slnx --locked-mode
dotnet tool restore
dotnet build CmsEventProcessor.slnx --configuration Release --no-restore
```

SQL binds only to 127.0.0.1 and retains development data in the named sql-data volume. Stop it with docker compose stop sql; bringing it back preserves that data. Do not remove the volume when data is needed.

Initialize API accounts using the README command. Then add a Persistence section to CmsEventProcessor/appsettings.Local.json without replacing Authentication. The [configuration shape](examples/persistence.example.json) contains deliberately unusable placeholders.

For the supplied local SQL service, both connections initially use this shape with the private password:

```text
Server=127.0.0.1,1433;Database=CmsEventProcessor;User Id=sa;Password=<private SQL password>;Encrypt=True;TrustServerCertificate=True;ConnectRetryCount=0
```

Use SQL_PORT if customized. Escape connection-string and JSON values correctly. The sa account and trusted development certificate simplify the local demonstration; a deployed application should use appropriately scoped runtime accounts and trusted TLS. Separate migration privileges from runtime access with CMS_MIGRATION_CONNECTION.

## Explicit migration command

From the repository root, after build and private writer configuration:

```sh
dotnet tool run dotnet-ef -- database update --project CmsEventProcessor --context WriteContext --configuration Release --no-build
```

The design factory reads CMS_MIGRATION_CONNECTION first. Otherwise, it reads the writer from ignored local JSON or Persistence__WriteConnectionString. EF sets the working directory to the startup project, so the normal private file is CmsEventProcessor/appsettings.Local.json. The command works from the repository root or project directory, requires no API accounts, and never starts the web host. For a separate mounted/private folder, append -- --configuration-directory <absolute-folder>; the SQL tests use that option to avoid a developer's real configuration. Do not run database updates without explicitly configuring the intended writer/migration connection; the fallback connection exists for schema generation.

Web startup validates read/write connection shapes but never creates/migrates a database or assumes storage is healthy. Normal host credentials and SQL readiness are distinct concerns. Run migrations before exercising processing. Missing schema/database access results in an unavailable processing attempt.

To add later schema changes:

```sh
dotnet tool run dotnet-ef -- migrations add <MigrationName> --project CmsEventProcessor --context WriteContext --output-dir Infrastructure/Persistence/Migrations
```

## API container configuration

The API image is built with the existing Dockerfile. Add Persistence__WriteConnectionString and Persistence__ReadConnectionString to the private runtime environment described in the [container guide](development.md#container-build-and-https-execution). When joining the Compose network, use Server=sql,1433 and the same migrated database.

The default network is cms-event-processor_default. Add --network cms-event-processor_default to the documented docker run command. Credentials and certificates remain read-only mounts/environment secrets. SQL is never exposed publicly.

## Test execution and evidence

The integration project tags real database cases Category=Sql. By default, one Testcontainers SQL instance is started and each case creates/migrates/deletes a unique cms_test_<guid> catalog. Docker failures fail these tests rather than silently skipping them.

```sh
dotnet test CmsEventProcessor.slnx --configuration Release --no-build --no-restore --filter "Category!=Sql"
dotnet test CmsEventProcessor.IntegrationTests/CmsEventProcessor.IntegrationTests.csproj --configuration Release --no-build --no-restore --filter "Category=Sql"
```

For an environment without a compatible container engine, set CMS_TEST_SQL_CONNECTION privately to a dedicated server connection with permission to create/drop test databases. The supplied catalog is not deleted; tests use their own generated catalog names. Never point the tests at a shared application connection. No Docker API is used by that fixture path.

Tests cover migrations and the actual CLI, case-sensitive IDs, SQL checks/unique keys/rowversion, read-only contexts, same-ID batches, full rollback, scoped exact-body replay across service restart, expiry/replacement/cleanup, concurrent requests, local disabling, cancellation, deadlines, lock failure, connection interruption, and a lost postcommit acknowledgment.

Failure probes are explicit: one test kills the active SQL session before commit; another throws after a real SQL commit to model lost acknowledgment. These do not claim every network or operating-system crash scenario was reproduced. Probe classes remain in the test assembly and are not shipped.

CI runs unit/HTTP checks on Windows and Ubuntu. SQL container cases run on Ubuntu, where GitHub-hosted Linux Docker is available; the Windows job excludes Category=Sql. This checks Windows development and Linux deployment assumptions without claiming macOS execution. SQL integration tests can also run locally on Windows with Docker Desktop's Linux engine.

TRX/coverage and the actual SQL product version are generated under ignored artifacts/test-results. Hosted results for this new source exist only after owner-approved publication.
