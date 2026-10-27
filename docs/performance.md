# Recorded ingestion exercise

Measured locally on **2026-10-27**, using [the checked-in script](../scripts/measure_ingestion.py) and the operational instrumentation described in the [operations guide](operations.md). These results describe one short synthetic closed-loop workload. They establish neither saturation throughput nor a production capacity target.

## Environment and workload

- Windows 11 build 26200, AMD Ryzen 5 5500U, 12 logical processors; Python 3.13.1 client and published Release Kestrel API on the same machine.
- .NET SDK 10.0.301, runtime/package line 10.0.9; EF Core SQL Server provider 10.0.9.
- SQL Server 2022 CU27 (`16.0.4295.3`) in Docker Desktop's Linux VM, through a loopback-published port; Docker reports 12 CPUs and approximately 5.78 GiB available to its VM. This is a shared development machine without pinned CPU allocation or an isolated storage benchmark.
- A fresh explicitly migrated database, writer and reader targeting that same primary. The 29-request Postman walkthrough ran first against that database. No other business workload ran during the measured phases.
- Default 30-second processing budget, five-second lock wait, 600 requests/minute/peer limit, ten-second shutdown budget, and telemetry export disabled. Safe transaction JSON debug logging enabled; normal safe application outcome logging enabled. Authentication and log overhead are included.
- Three warm-up batches, then 40 measured batches at concurrency one and 40 at concurrency four. Each batch contains five unique version-one publishes, a unique idempotency key, and synthetic payloads with a title and 256-character padding. Both profiles use the same payload shape and fresh IDs; the second runs later against a somewhat larger database.
- All **80 measured batches / 400 events returned verified committed success**, without retry, throttling, or skips. Server logs contain exactly 40 acknowledged transaction samples per measured phase. Client timing includes authentication and HTTP; transaction timing begins after SQL transaction creation and includes lock wait/disposal. Percentiles use nearest rank; with only 40 samples, p99 is the maximum.

## Results

All durations below are milliseconds. Measurements are rounded from the ignored machine-readable report at `artifacts/operations-verification/ingestion.json`.

| Measurement | Concurrency 1 | Concurrency 4 |
| --- | ---: | ---: |
| Measured phase seconds | 7.766 | 3.212 |
| Successful batches/second | 5.151 | 12.454 |
| Successful events/second | 25.754 | 62.268 |
| Client mean | 193.635 | 305.600 |
| Client p50 / p95 / p99 | 192.887 / 212.335 / 225.394 | 279.039 / 420.390 / 433.154 |
| Transaction mean | 72.970 | 175.233 |
| Transaction p50 / p95 / p99 | 71.029 / 86.712 / 108.809 | 151.927 / 286.293 / 289.219 |
| Lock-command mean | 1.802 | 97.909 |
| Lock-command p50 / p95 / p99 | 1.718 / 2.141 / 2.449 | 85.840 / 183.624 / 190.704 |
| SQL-reported contended acquisitions | 0 / 40 | 39 / 40 |
| HTTP / transaction outcomes | 40 × 200 / committed | 40 × 200 / committed |

Concurrency overlapped authentication and I/O, increasing observed throughput while increasing latency and lock wait. The shared SQL mutation lock serialized concurrent mutations as designed. This evidence supports keeping the simple coordination model for the assessment and measuring a representative real workload before considering finer-grained locking. It does not establish the maximum useful concurrency, a latency SLO, performance for large/deep payloads, or behavior under independently scheduled arrivals.

All synthetic snapshots were deleted after measurement; cleanup time is excluded. Content-free deletion markers and successful replay metadata remain until the dedicated database is discarded. The run reports no source IDs, passwords, request bodies, or idempotency keys. Follow [the reproduction instructions](operations.md#reproduce-the-synthetic-load-exercise) and retain your own report rather than expecting identical results on another machine.

## Recovery evidence

Automated tests separately verify actual SQL database offline/online recovery, missing migration history or tables, either unavailable configured connection, bounded readiness, and domain conflicts that leave readiness healthy. Existing SQL-backed HTTP cases verify rollback after cancellation/partial writes, a lost acknowledgment injected after actual commit, safe keyed replay, and persisted state across new hosts. Export tests receive actual OTLP protobuf on a temporary local collector and check confidential sentinels, trace state, baggage, and incoming trace IDs are excluded.

A separate **real Linux SIGTERM exercise passed** against the built non-root HTTPS image and the same isolated SQL instance. A separate SQL session held the transaction-owned mutation application lock; a DMV check confirmed an actual HTTP request waiting for that lock. `docker stop --time 15` initiated shutdown, and the host logged stopping before the holding session released its lock. The in-flight keyed batch returned `200`, the container exited with code zero, and restarting that container followed by an identical keyed request returned the original complete response. Private configuration/certificate files were copied into this disposable verification container and removed with it. The initial Windows bind-mount startup attempt timed out in Docker Desktop before creating a container; the copied-file run established the shutdown evidence. This is one successful drain within the default budget, not a proof of forced termination or every timing window.

To repeat that boundary manually on a dedicated disposable database: hold `CmsEventProcessor:Mutation:v1` with `sys.sp_getapplock` in an open SQL transaction, send a synthetic keyed batch, confirm the database request is waiting, start a graceful container stop with more than ten seconds of grace, then commit the holding transaction promptly after the host starts stopping. Save the successful response, restart the same image/configuration, and verify an identical key/body returns that response. Do not run this lock-holding exercise against a shared or real-content instance. No diagnostic lock/shutdown HTTP endpoint is shipped.

These local scenarios and injected failures cover particular failure boundaries. They do not prove arbitrary network partitions, every process-crash timing, deployment orchestration, macOS behavior, or cloud availability.
