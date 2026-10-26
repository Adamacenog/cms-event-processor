# ADR 002 — Versioned snapshots, deletion markers, and local overrides

- **Context:** revisions may be missing, unpublish includes content, delete has no version, and the assessment does not specify delivery order or how local disabling interacts with later publishes.

## Decision

Store a current snapshot rather than event-sourced revision history. Treat the example's `publish` as both creation and update; use `unPublish` for disabling while retaining data and `delete` for hard deletion. Add/Update in the scenario are business descriptions, not extra accepted wire aliases.

Keep `CmsPublished` and `AdminDisabled` independent. A normal reader sees an entity only when it is published and not locally disabled. CMS publish/unpublish never clears the override. A true hard delete ends that entity lifecycle, including its local override.

Accept any positive first-observed version. An unpublish can create an unknown entity or update it to an unseen revision. Use versions and normalized source timestamps according to the [architecture's decision table](../architecture.md#entity-lifecycle). Equal-time delete wins; equal-time/version unpublish wins over publish. Conflicting payloads for the same current version fail the batch.

Hard-delete content while retaining a metadata-only marker. A publish/unpublish at or before its timestamp is stale; a strictly later one may create a new lifecycle, with a new version sequence and no inherited override. A delete for an unknown entity also creates a marker.

## Assumptions and limits

- CMS source timestamps are comparable per entity and do not move backwards as revisions advance. This is an assumption because the input contains no definitive sequence for deletes.
- The first created version is 1, but the first delivered version need not be 1.
- Same-version republish is valid if content is unchanged and its timestamp is later.
- IDs are opaque and case-sensitive. Hard deletion followed by later reuse is treated as a new lifecycle.
- Old revision content is not retained, so conflicting payloads for an already superseded revision are not detectable. They are stale events.
- A request retried after its idempotency result expires is processed anew. Domain state still protects duplicate entity effects, but the response can contain fresh skip outcomes instead of the original result.
- A deletion marker is ordering metadata, not an entity exposed to consumers. Submitted batch bodies are not persisted separately; idempotency metadata and database backups have their own lifetime. This is not a comprehensive data-erasure guarantee.

## Alternatives considered

Arrival-order-only processing is easy but lets delayed events corrupt state. Version-only comparison cannot order versionless deletes or same-version publication changes. Removing all metadata on delete permits stale resurrection. Clearing an admin override on publish undermines the local administrative decision.

## Required evidence

Unit and SQL integration tests cover missing revisions, unseen unpublish, duplicates, stale delivery, timestamp/version ties, payload conflicts, unknown-ID deletes, local overrides, and recreation. Add an end-to-end Postman example specifically demonstrating unseen-unpublish and stale-after-delete behavior.
