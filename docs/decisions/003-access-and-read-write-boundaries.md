# ADR 003 — Authentication, visibility, and read/write contexts

- **Context:** all content is restricted; the CMS organization identity must differ from consumer identities. Administrators see disabled entities and may disable them locally. The assessment explicitly requests a read-only/writer context configuration.

## Decision

Use separate CMS and consumer Basic authentication schemes with explicit authorization policies. Consumer identities receive reader or administrator roles from server configuration, never from a client-supplied role header. Basic credentials are allowed only over HTTPS for external traffic; local loopback development is the exception.

Use one production project with Api, Application, Domain, and Infrastructure folders, plus separate unit/integration test projects. These folders define logical responsibilities within one assembly; code review maintains dependency direction. Keep shared identity contracts and safe results in Application, and account storage/hash/provisioning implementations in Infrastructure. HTTP authentication and command presentation depend on those contracts; the API composition root selects the implementations. Stored hashes do not cross into the authentication result model. Two purpose-specific interfaces are sufficient; internal helpers remain concrete.

Use generated GUID passwords and 10–20-character unique usernames. Store password hashes in untracked configuration or a secret provider, use established password hashing/verification, and never commit live credentials. User management, registration, and JWT issuance are outside this assessment.

An explicit setup utility provisions the CMS, reader, and administrator accounts, saves their hashes and server-side permissions, and displays newly generated passwords once. The API loads those definitions at startup without creating accounts or changing credentials. Adding an account or rotating a password requires an explicit configuration update and application restart; rerunning setup must not silently overwrite existing accounts. Deployment uses secret-provided account definitions rather than checked-in credentials. See the [credential setup lifecycle](../../README.md#authentication-and-credential-setup).

Apply visibility in database queries. Normal readers receive only published entities without a local admin disable; administrators receive all retained entities. Entity lookup follows the same rule and returns `404` when hidden from a reader. CMS identities cannot read consumer endpoints, and consumer identities, including administrators, cannot ingest events. The webhook returns final outcomes directly to its CMS caller; successful replay results are scoped to that CMS identity and have no separate query endpoint.

Use separate EF read and write contexts. The read context defaults to no tracking, uses projections and bounded cursor pagination, and rejects save operations. Connection strings are separately configurable but initially target one SQL database. This does not claim a physical read replica or database-enforced separation of privileges.

## Consequences

- The assessment's authentication expectations are visible and testable without implementing an identity platform.
- Basic Authentication requires careful transport security, credential handling, and rate limiting. A production identity-provider migration is a separate decision.
- An administrator's disable affects local visibility only and does not mutate CMS payload/version information.
- No generic repository layer is needed to disguise EF query capabilities.
- A read-only SQL login can strengthen permissions, but it is not necessary to demonstrate the separate context behavior locally.

## Required evidence

Test valid/invalid credentials, malformed headers, missing authentication, wrong-role access, CMS-scoped idempotency keys, every visibility combination, hidden lookups, read-context save rejection, bounded pagination, and admin/ingestion races. Verify that confidential payloads and credentials never appear in logs.

Verify username/password-generation constraints, hash-only persistence, preservation of existing accounts when setup is rerun, startup loading without regeneration, and explicit rotation taking effect after restart. Application logs must not contain the one-time setup passwords.

## References

- [EF tracking versus no-tracking queries](https://learn.microsoft.com/en-us/ef/core/querying/tracking).
- [EF efficient querying](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying).
