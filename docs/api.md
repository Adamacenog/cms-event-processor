# API contract

**Status: ingestion, consumer listing/lookup, administrator disabling, and authenticated OpenAPI are implemented.** Health routes remain approved design and rate limiting is not active yet. See the [webhook](webhook.md), [consumer](consumers.md), and [Postman](postman/README.md) guides.

## Common behavior

- JSON request/response field names use camel case. Requests containing bodies use `Content-Type: application/json`.
- All endpoints require authentication. External requests use HTTPS; local loopback HTTP is limited to development and the tunnel connector.
- Requests supply `Authorization: Basic <base64(username:password)>`. Base64 is encoding, not encryption. Credentials are generated locally or shared privately, never embedded in documentation or committed Postman environments.
- Accounts are provisioned through setup configuration, not a registration or login endpoint. See [authentication and credential setup](../README.md#authentication-and-credential-setup) for initial provisioning, startup loading, deployment secrets, and rotation.
- CMS and consumer authentication are separate. Reader/admin roles come from server-side configuration.
- Confidential responses use `Cache-Control: no-store`.
- Failures use `application/problem+json` with `type`, `title`, `status`, and a correlation `traceId`. Validation failures additionally contain an `errors` map. Do not echo payload values, credentials, or internal exception details.
- Missing/invalid credentials return `401` with an appropriate Basic `WWW-Authenticate` challenge. An authenticated identity lacking the required role within a recognized scheme returns `403`.
- Supplying CMS credentials to a consumer-only route, or consumer credentials to a CMS-only route, returns `401`: those credentials are not valid in the route's authentication scheme. An authenticated reader accessing an administrator route returns `403`.
- IDs are opaque, case-sensitive strings. Encode them correctly when used in URL path segments.

## Access matrix

| Endpoint | CMS | Reader | Administrator |
| --- | --- | --- | --- |
| Process batch and receive final outcomes | Yes | No | No |
| List/read entities | No | Visible only | All retained entities |
| Disable entity | No | No | Yes |
| Health checks (planned) | No | No | Yes |
| Generated OpenAPI reference | Yes | Yes | Yes |

There is no processing-status endpoint. Idempotency results are returned only by repeating the webhook request under the same CMS identity and key.

## POST /cms/events

Process a batch synchronously within the HTTP request. Success means the entire batch transaction has committed; database I/O still uses `async`/`await`.

### Request

The body is a JSON array of **1–100 events**, with a maximum request-body size of **1 MiB (1,048,576 bytes)** and JSON depth limit of **32**.

| Field | Rules |
| --- | --- |
| `type` | Required string. Recognize `publish`, `unPublish`, and `delete` case-insensitively; normalize internally. Other types are rejected. |
| `id` | Required string, 1–128 characters; no leading/trailing whitespace or control characters. Preserve its value and case; do not silently trim or normalize it. |
| `timestamp` | Required ISO 8601 extended timestamp with seconds, uppercase `T`, explicit `Z` or `±HH:mm` offset, and zero to seven fractional digits; normalize to UTC for comparison. Greater precision is rejected rather than truncated. |
| `version` | Required positive 64-bit integer for publish/unpublish. Optional on delete; if supplied, validate it but do not use it to order deletion. |
| `payload` | Required JSON object for publish/unpublish. Optional on delete; if supplied, validate it as an object but do not copy it into deletion metadata. Arbitrary valid content properties are allowed. |

The request must use UTF-8 JSON without compression. Reject malformed UTF-8/JSON, invalid Unicode escapes, and duplicate property names, including in nested payload objects. Envelope names are case-sensitive. Reject unknown envelope properties so misspelled fields are visible to the sender; this does not restrict arbitrary fields inside `payload`. Optional delete fields may be omitted; explicit null values are invalid. Preserve valid content values instead of applying HTML stripping or lossy transformations.

An optional single `Idempotency-Key` header is a nonblank string of at most 128 characters, without control characters and with valid Unicode, scoped to the authenticated CMS principal. Repeated header values are rejected. Store its SHA-256 body hash and successful response metadata in the same transaction as entity changes:

- Same unexpired key and identical request bytes: return the original `200` result, including the batch ID and outcomes, without reapplying events.
- Same unexpired key and different bytes, including different whitespace: return `409`.
- No key: process the request; domain replay rules still protect duplicate entity effects, but a replay may return skip outcomes and a new batch ID.
- The guarantee lasts seven days after successful completion. Expired keys are ignored/replaced transactionally; bounded opportunistic cleanup removes expired metadata without a background service.
- Failed transactions do not reserve a new key or cache a successful result. Submitted bodies are never persisted separately, including on failure.

A keyed replay reports the original transaction; it does not restore its historical state. Consumer queries always return the current snapshot, which may reflect later events or administrator actions.

Example using synthetic content:

```json
[
  {
    "type": "publish",
    "id": "article-001",
    "payload": { "title": "Example article" },
    "version": 1,
    "timestamp": "2026-10-26T12:00:00Z"
  },
  {
    "type": "unPublish",
    "id": "article-001",
    "payload": { "title": "Revised example article" },
    "version": 2,
    "timestamp": "2026-10-26T12:01:00Z"
  }
]
```

### Response

Return `200 OK` only after the batch transaction commits, or when replaying a previously committed keyed result. For the request above:

```json
{
  "batchId": "ab37e325-4c07-4a72-b5b0-a85b00bfd763",
  "outcomes": [
    {
      "index": 0,
      "id": "article-001",
      "outcome": "applied",
      "reasonCode": "published"
    },
    {
      "index": 1,
      "id": "article-001",
      "outcome": "applied",
      "reasonCode": "unpublished"
    }
  ]
}
```

`batchId` is a UUID used for correlation, not the address of a stored job. Each outcome contains its zero-based input `index`, source `id`, `outcome` (`applied` or `skipped`), and a safe `reasonCode`. Applied reasons are `published`, `unpublished`, or `deleted`; skipped reasons are `stale` or `duplicate`. The final state in this example is unpublished version 2. Do not return a status URL or `Location` header for polling.

The batch is atomic. A same-version payload conflict returns Problem Details with `409`, a safe `errorCode` of `version_payload_conflict`, and the failing `eventIndex`; none of that batch's mutations commits. Idempotency-key conflicts also use `409`, with `errorCode: idempotency_key_conflict`. Do not return earlier tentative mutations as successful outcomes on a failed request.

| Status | Meaning |
| --- | --- |
| `200` | Batch committed, or original successful keyed result replayed |
| `400` | Invalid JSON, invalid batch/event fields, or invalid idempotency key; no batch mutations |
| `401` | Missing/invalid CMS authentication |
| `409` | Same-version payload conflict, or unexpired idempotency key reused for a different body |
| `413` | Request body exceeds the limit |
| `415` | Unsupported content type |
| `429` | Reserved for planned request/authentication rate limiting; not currently emitted |
| `503` | Transient database failure, lock unavailability, or processing deadline exceeded; retry with the same key/body |

Use a configurable processing deadline, default 30 seconds, covering lock acquisition and database operations through commit. Propagate request cancellation; roll back uncommitted work. A timeout/disconnect around commit can leave the caller uncertain, so neither a missing response nor a `503` proves rollback when commit acknowledgment was lost. Retry with the same key and identical body to recover a committed result safely. The service has no automatic transaction retry loop or pending work to resume after restart.

Return deadline failures as `503` with safe `errorCode: processing_timeout` when the connection still permits a response. Never echo submitted payloads, credentials, stack traces, or SQL errors. After a successful response, consumers can query the primary database immediately, subject to subsequent concurrent mutations; no processing poll or sleep is needed.

Other processing `503` codes are `storage_unavailable` (including lock failure) and `commit_unknown` (commit acknowledgment not confirmed). These responses include `Retry-After: 1`; retry remains the caller's choice. No 503 around commit proves rollback. A canceled/disconnected caller may receive no response. Reading/validating the bounded body precedes the processing deadline, which starts when the SQL attempt begins.

Example validation response, with fixed field names and no submitted values:

```json
{
  "type": "about:blank",
  "title": "Request validation failed.",
  "status": 400,
  "traceId": "example-trace",
  "errors": { "[0].version": ["Supply a positive 64-bit integer; only delete may omit it."] }
}
```

## GET /openapi/v1.json

Implemented generated OpenAPI covers ingestion and consumer actions and their transport DTOs. Any configured CMS, reader, or administrator account may retrieve it; missing/invalid credentials receive 401. Documentation access grants no additional business permissions. Operations declare CMS Basic or consumer Basic security, with administrator-only disabling described explicitly. Responses use no-store; test controllers and future health routes are excluded. Download/import the document into a client; no interactive documentation UI is bundled. Conditional input schemas, paging bounds, and response shapes are generated/refined from actual types, with byte/depth/duplicate/replay rules described alongside the schemas.

## GET /api/entities

List current snapshots. A reader sees only `cmsPublished && !adminDisabled`; an administrator sees all retained snapshots through the same route.

| Query parameter | Rules |
| --- | --- |
| `pageSize` | Integer from 1 to 100; default 20 |
| `cursor` | Optional opaque continuation from a prior response; malformed values return `400` |

Supply each paging field at most once. Unknown fields, empty supplied values, invalid integers, and malformed/cross-visibility cursors return sanitized 400 errors. Cursors are canonical versioned base64url, bounded to 520 characters and bound to reader/admin visibility. They are opaque continuations, not signed authorization tokens, encrypted values, or snapshots; changing a cursor never bypasses server-side filtering.

Order by source ID using the database's explicit case-sensitive binary ordering. Apply visibility before pagination. A cursor continues strictly after the last returned ID. It is not an authorization token and does not provide snapshot isolation across requests.

Example response (`200`):

```json
{
  "items": [
    {
      "id": "article-001",
      "payload": { "title": "Example article" },
      "version": 1,
      "cmsTimestamp": "2026-10-26T12:00:00Z",
      "cmsPublished": true,
      "adminDisabled": false
    }
  ],
  "nextCursor": null
}
```

Both roles use the same DTO. An empty result is `200` with `items: []` and `nextCursor: null`. Do not return a separate total count or load all matches to calculate pagination. Deletion markers never appear in this API.

Return `400` for invalid pagination, `401` for invalid consumer authentication, and `503` if reads are unavailable.

SQL applies visibility and strict continuation before limiting to pageSize plus one projected row. The extra row only detects continuation; there is no offset scan, total-count query, or tracking. The cursor remains usable if its source row is deleted. Switching reader/admin visibility requires starting a new sequence.

## GET /api/entities/{id}

Return one snapshot using the same DTO and visibility policy as listing. Percent-encode the source ID as one path segment. The original segment is decoded once so an encoded slash and its literal percent spelling remain distinct. Case/Unicode are preserved; hidden, missing, and deleted lookup problems share the same shape.

- `200`: visible snapshot.
- `400`: invalid ID.
- `401`: invalid consumer authentication.
- `404`: absent/deleted entity, or an entity hidden from the reader.
- `503`: reads unavailable.

Read failure codes are storage_unavailable or read_timeout. Each read uses a fresh read context and one cooperative Processing:Timeout budget (default 30 seconds), with caller cancellation and no automatic retry. Safe 503 responses include Retry-After: 1. Aborted/disconnected callers may receive no response.

## PUT /api/entities/{id}/disable

Apply the local administrator override. No request body is required.

The operation is idempotent: disabling an already disabled entity succeeds. It does not change CMS content, source version, CMS timestamp, or CMS publication state. Subsequent publishes do not clear it. There is no consumer content-update endpoint and no local re-enable endpoint in this assessment.

- `204`: override applied or already present.
- `400`: invalid ID or unexpected nonempty request body.
- `401`: invalid consumer authentication.
- `403`: authenticated consumer is not an administrator.
- `404`: no retained entity exists, including a hard-deleted entity.
- `503`: write storage/coordination unavailable.

Coordinate this mutation with ingestion transactions so a racing publish cannot lose the override. A hard delete still removes the entity and its override.

Every nonempty body is rejected, including unknown-length streams and `{}`. A successful 204 has no JSON body and waits for commit acknowledgment. Safe 503 codes are storage_unavailable, processing_timeout, and commit_unknown, with Retry-After: 1. Repeat this idempotent operation to confirm an uncertain commit; no key or replacement content is needed. CMS credentials receive 401 and readers receive 403 before validation/storage access.

## GET /health/live and GET /health/ready

Administrator authentication is required. Return minimal status JSON, never connection strings, entity information, dependency details, or exception messages.

- Liveness returns `200` while the HTTP process is serving requests.
- Readiness returns `200` when database access and the expected schema are available; otherwise `503`.
- A failed domain batch alone does not make the whole application unready.
- Invalid authentication returns `401`; an authenticated reader receives `403`.

## Review and documentation obligations

Update this contract alongside endpoint changes. Generate OpenAPI from implemented DTOs/actions and verify that examples match it. Postman credentials belong in local/private environments; committed templates contain empty credential values. Acceptance scenarios assert final webhook outcomes and query entity state immediately, without status polling or fixed processing sleeps.
