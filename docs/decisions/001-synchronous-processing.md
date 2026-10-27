# ADR 001 - Synchronous transactional batch processing

- **Context:** the assessment permits synchronous or asynchronous processing and prioritizes correctness, clarity, and a straightforward local setup. Bounded request sizes make synchronous transactional processing a practical choice.

## Decision

Process each validated batch within the webhook request. Use an application batch processor and one SQL transaction to apply events in submitted order. Return `200` with final outcomes only after commit; a processing conflict rolls back the entire batch and returns `409`.

Synchronous describes when the result is returned. Continue using `async`/`await` for database I/O; do not block threads or launch fire-and-forget work. No hosted worker, durable inbox, queue, scheduled retry loop, or processing-status endpoint is needed.

Use one transaction-owned SQL application-lock resource to coordinate ingestion and administrator mutations. Bound processing with a configurable deadline, default 30 seconds, and propagate request cancellation. Return `503` for transient storage/locking/deadline failures when possible; callers own retries. Do not enable implicit automatic transaction replay in the initial implementation.

Keep the optional CMS-scoped idempotency key. Commit its SHA-256 request-body hash and successful response metadata alongside entity changes. An identical keyed replay returns the original success without mutations; a different body under the same unexpired key is a conflict. Failed transactions do not reserve a new key or cache a successful response. Expire results after seven days, replacing expired keys transactionally and cleaning up expired metadata opportunistically in bounded work during requests.

Never persist submitted batch bodies or failed-work payloads. A lost response or interrupted commit acknowledgment can be ambiguous; retrying with the same key/body recovers the recorded success if commit occurred. Without a key, domain replay rules protect entity effects, but the response may contain fresh skip outcomes. A restart does not autonomously resume an interrupted request.

The complete contract lives in [architecture](../architecture.md#synchronous-transactional-processing) and [API](../api.md).

## Alternatives considered

| Alternative | Tradeoff |
| --- | --- |
| Durable SQL inbox and hosted worker | Survives accepted-work interruption independently of the client, but adds queue state, polling, retries, retention, and a status API. Not selected for this bounded assessment workload. |
| In-memory background processing | Shortens acknowledgment latency but can lose acknowledged work on restart without durable storage. |
| Azure Service Bus and independent worker | Useful when decoupling or separate scaling is required, but introduces broker/database coordination and deployment dependencies. |

## Consequences

- Request success directly represents committed processing; the reviewer can query entity state immediately through the primary database path.
- Transactional rollback and response replay are easier to demonstrate without a separate job lifecycle.
- Request latency includes processing and lock contention. Keep input bounded, enforce deadlines, and measure throughput before adding parallelism.
- Global mutation serialization is an explicit assessment-scale tradeoff. Async database I/O does not remove that throughput limit.
- The CMS must retry transient failures and ambiguous responses. There is no promise of automatic eventual completion or end-to-end exactly-once delivery.
- There is no continuous queue polling or worker-driven minimum-replica requirement. Hosting still has cost and availability constraints.

## Implementation and required evidence

The [SQL processing guide](../persistence.md) describes the implemented transaction boundary, scoped response digests, expiry cleanup, cooperative deadlines, and conservative commit-uncertainty classification. The [webhook guide](../webhook.md) describes implemented authenticated HTTP behavior and real SQL-backed boundary tests. The read/write contexts provide application-level separation; provider logs are suppressed to avoid disclosure of SQL exception values.


Real SQL/HTTP tests cover commit-before-success, whole-batch rollback, concurrent ingestion/admin mutations, keyed request races, response loss after commit, cancellation before commit, commit uncertainty, database unavailability, and deadline handling. Verify expiry/cleanup and the absence of persisted submitted bodies. Measure request/transaction duration, lock contention, failures, and throughput.

## Reference

[SQL Server application locks](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver17) support transaction ownership and automatic release on commit or rollback; acquisition return codes must be handled.
