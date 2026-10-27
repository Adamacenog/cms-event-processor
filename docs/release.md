# Release verification and publication

Releases originate from an approved, merged revision with passing checks. The current delivery work prepares and verifies source; tagging, registry publication, and a GitHub release are separate operator actions after authorization. Nothing in CI pushes images, creates tags, or provisions hosting.

## Review the candidate

1. Confirm the PR is approved/merged and record its exact merge SHA. Review its complete diff and the [assessment evidence](verification.md#requirement-to-evidence-map).
2. From a fresh checkout of that SHA, run locked .NET restore, pinned tool restore, formatting, Release build, and all applicable [verification suites](verification.md#test-commands). Provide Docker or dedicated external test SQL for all suites. Preserve the original assessment hash: `FAF1CB49482F841F77DBB57A650799DAB51D5DD8700156F833E42ED241BC0E24`.
3. Confirm hosted Windows/Ubuntu checks, actual SQL integration, and the image build refer to that revision. Run the 29-request Postman walkthrough manually against the native or container instance. Check that the checkout, reports, collection templates, publish output, and image contain no private local settings or real content. Anonymous access must fail; role-specific access must match the contract.
4. Exercise the intended runtime setup using explicit migrations and private generated accounts. Verify HTTPS, live/ready behavior, final consumer state, and stop/restart. Record the platforms actually exercised. A cloud option remains a proposal until its actual transport, secrets, schema, and public access checks pass.

## Tag and build after authorization

The initial planned version is `v1.0.0`. Replace the SHA/image placeholders below with the reviewed merge and the owner's registry repository. Do not use a moving branch tip as the release identity.

```sh
git checkout --detach <reviewed-merge-sha>
git tag --annotate v1.0.0 <reviewed-merge-sha> --message "CMS Event Processor v1.0.0"
git push origin v1.0.0
docker build --label org.opencontainers.image.revision=<reviewed-merge-sha> --label org.opencontainers.image.version=1.0.0 --tag ghcr.io/<owner>/cms-event-processor:v1.0.0 .
```

Verify the final image's revision/version labels match the tag's source and expected version. The Dockerfile pins SDK/runtime versions and restores locked production dependencies. It builds the application only; local secrets, test projects, developer tooling, and generated reports are excluded. Run it as the non-root image user with private read-only configuration/certificate mounts. On Linux, map an appropriate UID/GID when host file permissions require it, as described in [container setup](development.md#container-build-and-https-execution).

After the image passes the same runtime walkthrough, authenticate to the registry through a secure interactive or token-input flow, then publish only the selected repository/tag:

```sh
docker push ghcr.io/<owner>/cms-event-processor:v1.0.0
```

Record the returned immutable digest and deploy by that digest. A version tag or revision label identifies intended source; the saved registry digest identifies the actual artifact. Labels alone are not supply-chain attestation. Create the GitHub release from the source tag with the behavior summary, setup links, CI evidence, image digest, platform coverage, and known limitations. Do not attach populated environments, credentials, SQL backups, raw private logs, or CMS payloads.

## Retain a release record

| Field | Record at publication |
| --- | --- |
| Source | Reviewed merge SHA and source tag |
| Checks | Passing hosted run and local/fresh-checkout evidence |
| Image | Registry repository, immutable digest, platform, revision/version labels |
| Storage | Applied migration IDs, connection/secret delivery method, backup and recovery owner |
| Access | Private credential delivery and rotation procedure; actual HTTPS/probe validation |
| Limits | Tested OS/runtime; untested cloud/macOS paths; measured workload scope |
| Recovery | Compatible previous image/digest and reviewed database recovery plan |

Keep secrets out of the release record. A tag/image release does not itself authorize paid cloud infrastructure, public exposure, or removal of a database/volume.
