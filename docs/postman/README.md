# Postman business walkthrough

Import [the collection](cms-events.postman_collection.json) and [the local template](local.template.postman_environment.json). Start SQL, provision accounts, configure connections, apply migrations, and run the API using the [README](../../README.md#running-locally).

Select a private environment, fill cmsUsername/cmsPassword, readerUsername/readerPassword, and adminUsername/adminPassword from setup, and adjust baseUrl if needed. The default URL is loopback development HTTP; use HTTPS for external connections. Keep populated environments private; an ignored filename such as local.private.postman_environment.json is suitable for a local export. Never save credentials in the collection or shared template.

Run all 29 requests in order using Collection Runner. The first request creates fresh synthetic IDs/keys and exact JSON bytes for that run. Assertions verify committed outcomes, stable keyed replay, a whitespace/body-key conflict, validation, unkeyed stale/duplicate decisions, deletion, authenticated OpenAPI, and no-store responses. No status polling or processing sleeps are used. The run deletes its synthetic snapshot; content-free deletion/replay metadata follows the documented retention rules. Consumer assertions cover immediate reads, unseen unpublishes, role visibility, republish, persistent local override, reader forbid, hard deletion, stale replay, and bounded listings. Each request uses the matching private role credentials. The final two administrator requests check minimal live/ready status JSON and no-store.

For a private manually configured instance, use Node 24 and the repository's locked Newman runner:

```sh
npm ci --prefix scripts/acceptance --ignore-scripts
node scripts/acceptance/node_modules/newman/bin/newman.js run docs/postman/cms-events.postman_collection.json --environment docs/postman/local.private.postman_environment.json
```

The private environment must be populated locally. Do not put passwords in CLI arguments, committed files, or shared output. For a temporary HTTPS demonstration, derive a private environment from [the demo template](demo.template.postman_environment.json) and follow [setup/teardown](../deployment.md#temporary-cloudflare-demonstration). Both shared templates keep credentials empty.

For automated verification without supplying API credentials, run the [isolated acceptance suite](../verification.md#test-commands). It creates its own migrated SQL catalog and published native host, generates credentials through the .NET command, passes them to Newman over standard input, and reports only counts. CI executes this same checked-in collection on Ubuntu. Successful ingestion is queried immediately; only initial host startup uses a bounded readiness wait. Private configuration and the generated database are cleaned up afterward, including on assertion failure. No global Newman installation or owner's running demo is used.

A successful collection run checks its target instance; it does not establish production capacity, cloud availability, or exhaustive recovery. The [verification guide](../verification.md#acceptance-tooling-dependency-scope) records pinned versions, compatible overrides, and remaining upstream development-tool advisories. Automated acceptance deliberately accepts only its loopback fixture; the manual UI/CLI can target your separately configured HTTPS demo.
