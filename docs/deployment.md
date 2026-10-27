# Deployment options and temporary demo

The verified delivery baseline is the native API or Linux container with separately configured SQL Server. No Azure resources, public tunnel, registry image, or hosted demo is created by the build or CI. This guide describes operator choices; cloud configurations below are proposals requiring environment-specific validation.

## Choose an environment

| Option | Fit | Work required |
| --- | --- | --- |
| Native API + local SQL container | Fast local review on an x86-64 Docker host | Follow the [README](../README.md#running-locally), apply migrations, provision accounts, run Postman |
| Native API + external SQL Server/Azure SQL | Native Windows/macOS, including Apple Silicon | Configure both connections and server TLS/network permissions; use external test mode only on a dedicated test server |
| Linux API container + SQL | Reproducible local packaging | Follow [container HTTPS execution](development.md#container-build-and-https-execution); mount private hashes/connections and certificate |
| Local API + temporary Cloudflare URL | Short evaluator demonstration with synthetic content | Keep the machine running; start/stop the connector below; share credentials separately |
| Azure VM running the container + Azure SQL | A direct Kestrel HTTPS path with familiar Docker operations | Provisioning, DNS/trusted certificate, network restrictions, runtime secrets, backup/monitoring, and cost review |
| Azure Container Apps + Azure SQL | A possible managed hosting destination | Resolve ingress/TLS and protected probes first, then validate scaling, SQL resume, secret delivery, and costs |

SQL Server Developer edition in Compose is for development/testing. It is not a production licensing choice. A remote database does not imply a read replica: both configured paths initially target the same primary. See [read/write separation](architecture.md#persistence-and-readwrite-separation).

## Temporary Cloudflare demonstration

Use a dedicated synthetic-data database and freshly provisioned CMS/reader/admin accounts. Start the local API on `http://127.0.0.1:5080` with the `local` profile and verify the Postman walkthrough locally first. Keep SQL's existing loopback binding; the connector targets the API only.

Install `cloudflared` from [Cloudflare's downloads](https://developers.cloudflare.com/tunnel/downloads/), then run in a separate terminal:

```sh
cloudflared tunnel --url http://127.0.0.1:5080
```

Copy the printed **HTTPS** URL into a private environment derived from [the demo template](postman/demo.template.postman_environment.json). Share that URL and the three role credentials through separate private channels. Basic authentication remains enforced on every endpoint; the temporary hostname is not an access credential. The public leg uses HTTPS and the connector reaches Kestrel over loopback HTTP, which the local profile explicitly permits. Do not enable public HTTP or arbitrary forwarded-header trust. Requests through the connector share its actual-peer request budget.

Confirm anonymous consumer access receives `401`, then run the collection using its matching role credentials. Keep the console/credentials private. Only synthetic content belongs in this demonstration. Cloudflare describes Quick Tunnels as temporary development/test endpoints with changing hostnames and no uptime guarantee; they require no domain or account. [Quick Tunnel behavior and limitations](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/).

To tear down, stop `cloudflared` with Ctrl+C; that temporary URL stops reaching the API. Stop the API, and stop local SQL with `docker compose stop sql` if no longer needed. The SQL volume retains data. Revoke/rotate shared credentials before reusing the instance; the new hashes take effect after API restart. Do not remove a database/volume unless its data is explicitly disposable. No paid hosting resource is required for this local option; existing machine/network/tooling costs still apply.

## Azure VM and Azure SQL: direct HTTPS procedure

This is a proposed operator path using the existing image behavior. It has not been deployed in Azure.

1. Choose the subscription, region, availability needs, and a cost estimate before provisioning. Use an owned resource group, a Linux VM with Docker, and an appropriately sized Azure SQL database. Keep SQL reachable only from the selected application/migration networks.
2. Build the reviewed source or pull an explicitly published image by digest, following the [release procedure](release.md). Provision the three API identities through the .NET setup command and deliver their hashes as a private read-only file or secret configuration. Deliver a trusted hostname-matching Kestrel certificate separately.
3. Configure the Azure SQL primary in both private runtime connections with `Encrypt=True;TrustServerCertificate=False;ConnectRetryCount=0`. Use separately scoped migration and runtime principals. Apply the checked-in migrations explicitly through a trusted operator/CI path before starting the application. Runtime startup never migrates.
4. Run the existing [HTTPS container command](development.md#container-build-and-https-execution). On a VM intended for external access, replace the local-only publish address with the intended interface, for example `--publish 8443:8443`, and restrict that port through the VM firewall/network security group. Use DNS and the matching trusted certificate; never expose SQL to application clients.
5. Verify anonymous `401`, the complete role-aware Postman walkthrough, and authenticated live/ready responses over the actual public HTTPS URL. Configure an external monitor with private administrator credentials. Review infrastructure/access logs for confidential path/query/header retention, configure backups and tested restoration, and allow a termination grace period beyond the host drain budget.
6. Record the source SHA, image digest, migration version, endpoint, secret-rotation method, rollback image, and costs. A previous image is a viable rollback only when its schema remains compatible; restore or migrate through a reviewed recovery plan otherwise.

The application does not currently implement managed-identity-specific setup or an Azure secret provider. SQL authentication and hash/certificate configuration are the documented baseline; an identity-provider integration needs its own implementation and validation.

## Azure Container Apps: transport and probe design

The current host rejects non-loopback HTTP and ignores forwarded headers. A platform HTTPS URL alone is insufficient evidence of end-to-end compatibility: Container Apps HTTP ingress terminates TLS and communicates protocol metadata through forwarded headers. Consequently, simply changing the image target port to HTTP does not make this application ready for that ingress. This follows from the application's transport check and [Microsoft's ingress contract](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview).

Two candidate approaches require a deployment pilot: preserve Kestrel TLS through raw TCP ingress with an application-managed trusted certificate, or implement a reviewed trusted-proxy adapter that accepts forwarded protocol metadata only from explicitly trusted infrastructure. External TCP ingress requires a VNet-enabled environment; choose unique exposed ports and validate DNS/certificate routing. Do not remove HTTPS enforcement or trust every proxy to make a deployment start. The transport choice changes cost and edge features; it is intentionally not hidden in a supposedly turnkey provisioning script.

Health endpoints require Basic administrator authorization. Do not configure an anonymous HTTP probe against them and mistake `401` for a database outage. TCP startup/liveness probes can establish listener availability, while an external authenticated monitor checks dependency readiness. A platform HTTP probe/header approach requires secure credential delivery and validation of the selected probe's TLS capabilities. [Probe behavior and supported options](https://learn.microsoft.com/en-us/azure/container-apps/health-probes).

After transport validation, start with one small bounded replica configuration and a measured workload. Minimum replicas zero can trade idle compute cost for cold starts; keeping a replica ready trades cost for latency. Synchronous processing needs no queue poller or worker-driven minimum replica. Request retry/backoff must account for both API startup and SQL availability. Horizontal API scaling does not remove the database-wide mutation lock. [Scaling rules](https://learn.microsoft.com/en-us/azure/container-apps/scale-app).

## Cost and availability assumptions

The assessment does not require paid hosting. The local temporary demonstration is the selected way to avoid provisioning cloud services; **US$5 is a demo spending ceiling, not an Azure price promise**. No resources are provisioned here.

Container Apps Consumption has monthly grants and usage-based charges, but registry, logs, networking, and SQL can add separate costs. VM hosting also has compute, disk, IP/network, and database costs. Build an estimate using the actual region, subscription, duration, traffic, retention, and replica configuration. Recheck [Container Apps pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/) and the [Azure pricing calculator](https://azure.microsoft.com/en-us/pricing/calculator/) before provisioning.

Azure SQL's free tier is eligibility/limit dependent. Selecting a normal serverless database without the free-limit setting can incur charges; limit-exhaustion behavior can either pause service or permit billed overage. Verify those settings instead of assuming every small database is free. [Azure SQL free-tier terms and setup](https://learn.microsoft.com/en-us/azure/azure-sql/database/free-tier?view=azuresql).

Serverless auto-pause/resume can delay the first connection, and recurring readiness queries or retained connections can affect idleness. The application's short readiness budget can report unavailable while storage wakes. Configure and measure monitor frequency, connection pooling, resume behavior, and caller retry before promising availability. [Serverless compute](https://learn.microsoft.com/en-us/azure/azure-sql/database/serverless-tier-overview?view=azuresql). Budget alerts help observe spending but should not be treated as an automatic hard spending cap; tear down disposable resources after an authorized pilot.
