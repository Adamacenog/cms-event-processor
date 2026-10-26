# CMS event processor — formatted assessment

> **Source:** [original assessment](original.md). This document reorganizes the supplied assignment for readability; it does not replace or expand its requirements. The original source is preserved unchanged.

## Scenario

A CMS sends live updates through a webhook/event pipeline. Events include, but are not limited to:

- **Add:** new entities are created.
- **Update:** an existing entity is changed successfully.
- **Delete:** an entity is removed or unpublished. Unpublishing does not remove the entity from the CMS; it disables it.

When an entity is updated, including changes to fields or values, the CMS assigns a new version. New data becomes available to the service only when the CMS publishes an event containing that version.

An **unpublish** event contains the entity fields and the version being unpublished.

### Important versioning case

An entity at version X can be modified to version X+1 and then unpublished without that new revision ever having been published. The service may therefore not already have the latest data in its database. Handle this case appropriately.

### Confidentiality

The CMS handles confidential data and does not identify which data is confidential. Assume **all data is restricted** and must not be publicly available.

Authenticate the REST API so only allowed users can access entities. The consumer API may use Basic Authentication like the CMS webhook, but its users must differ from the organization identity used by the CMS.

## Task

Build a **.NET 9 service** that ingests events, processes them, stores them in a database, and exposes them through a clean API.

The deliverable section of the supplied assignment permits **.NET Core 9 or above**. Our selected target is documented separately in the [architecture](../architecture.md).

## Requirements

### Data ingestion

- Expose the webhook at `/cms/events`.
- Secure it with Basic Authentication.
- Choose a username containing 10–20 characters.
- Use a random GUID as the password.
- Accept batches of events.
- The first entity version is 1; multiple revisions may arrive for an entity.

The supplied example, with typographic JSON issues corrected and illustrative payload objects substituted for `{...}`:

```json
[
  {
    "type": "publish",
    "id": "X",
    "payload": { "title": "Illustrative content" },
    "version": 2,
    "timestamp": "2024-01-01T00:00:00Z"
  },
  {
    "type": "delete",
    "id": "Y",
    "timestamp": "2024-01-01T00:00:00Z"
  },
  {
    "type": "unPublish",
    "id": "Z",
    "payload": { "title": "Illustrative unpublished content" },
    "version": 4,
    "timestamp": "2024-01-01T00:00:00Z"
  }
]
```

These payload field names are examples, not a required content schema. The opening scenario uses business labels such as Add and Update, while the example uses `publish`, `delete`, and `unPublish`; our interpretation is recorded in [ADR 002](../decisions/002-entity-lifecycle.md).

### Application layer

- Validate and sanitize incoming data.
- Hard-delete deleted entities.
- Retain unpublished entities in persistence.

### Storage

- Use EF Core and a relational database of your choice.
- Track each entity's latest data version.

### Consumer REST API

- Provide endpoints for consumers to list entities.
- An administrator receives everything a normal user can see, including disabled entities.
- Choose how administrator identity is determined; separate endpoints are not required.
- Users must not update entity data.
- An administrator may disable an entity locally. This override must not affect CMS data.

### Performance

- Asynchronous processing is allowed; synchronous processing is also acceptable.
- Explain the processing approach selected.
- Use a read-only/writer configuration for the application context.
- Optimize EF read queries.

### Observability

- Log processed events, including failed events.

### Testing

- Test event processing and ingestion constraints.
- Test Basic Authentication with valid and invalid username/password combinations.

## Deliverable

- A .NET Core 9 or above solution.
- A README with setup and running instructions.
- A platform-agnostic solution that works on Mac or Windows.
- A GitHub repository containing the deliverables.

## Assessment guidance

The assignment is intentionally open-ended. Focus on correctness, structure, and clarity. **Correct event processing is the most important part.** Use of available LLMs is permitted.

## Editorial notes

- Added Markdown headings, emphasis, lists, and code fencing.
- Reworded prose for readability without changing its requirements.
- Replaced smart quotation marks in the JSON example with valid JSON quotes and removed its trailing comma.
- Replaced the non-JSON `{...}` placeholders with explicitly illustrative objects.
- Preserved the distinction between the task's .NET 9 wording and the deliverable's 9-or-above wording.
- Kept implementation choices and resolutions of ambiguous requirements in separate project documents.

For the solution's interpretation of these requirements, see the [architecture](../architecture.md) and [API contract](../api.md).
