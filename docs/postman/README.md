# Postman business walkthrough

Import [the collection](cms-events.postman_collection.json) and [the local template](local.template.postman_environment.json). Start SQL, provision accounts, configure connections, apply migrations, and run the API using the [README](../../README.md#running-locally).

Select a private environment, fill cmsUsername/cmsPassword, readerUsername/readerPassword, and adminUsername/adminPassword from setup, and adjust baseUrl if needed. The default URL is loopback development HTTP; use HTTPS for external connections. Keep populated environments private; an ignored filename such as local.private.postman_environment.json is suitable for a local export. Never save credentials in the collection or shared template.

Run all 27 requests in order using Collection Runner. The first request creates fresh synthetic IDs/keys and exact JSON bytes for that run. Assertions verify committed outcomes, stable keyed replay, a whitespace/body-key conflict, validation, unkeyed stale/duplicate decisions, deletion, authenticated OpenAPI, and no-store responses. No status polling or processing sleeps are used. The run deletes its synthetic snapshot; content-free deletion/replay metadata follows the documented retention rules. Consumer assertions cover immediate reads, unseen unpublishes, role visibility, republish, persistent local override, reader forbid, hard deletion, stale replay, and bounded listings. Each request uses the matching private role credentials.

For command-line execution with Newman 6.2.2 installed:

```sh
newman run docs/postman/cms-events.postman_collection.json --environment docs/postman/local.private.postman_environment.json
```

The private environment must be populated locally. Do not put passwords in CLI arguments, committed files, or captured output. A successful run checks only the instance it targeted; it does not prove a deployment or operational readiness. CI currently verifies the equivalent HTTP/SQL contracts through .NET tests; isolated Newman execution in CI is planned for the final acceptance walkthrough.
