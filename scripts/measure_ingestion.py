#!/usr/bin/env python3
"""Measure synthetic, request-bound ingestion on a dedicated assessment instance.

Credentials are read only from CMS_LOAD_USERNAME/CMS_LOAD_PASSWORD. No secrets,
response bodies, keys, or entity IDs are written to the report. This is a closed
loop exercise, not a saturation or capacity benchmark. See docs/operations.md.
"""

import argparse
import base64
import concurrent.futures
import datetime as dt
import json
import math
import os
from pathlib import Path
import platform
import ssl
import statistics
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


class RejectRedirects(urllib.request.HTTPRedirectHandler):
    """Keep authorization on the configured origin instead of following redirects."""

    def redirect_request(self, request, response, code, message, headers, new_url):
        """Return no redirected request so the caller records a failed HTTP outcome."""
        return None


def distribution(values):
    """Return milliseconds and nearest-rank percentiles, preserving sample count."""
    ordered = sorted(values)
    if not ordered:
        return {"samples": 0}
    return {"samples": len(ordered), "meanMs": round(statistics.mean(ordered), 3),
            **{f"p{percentile}Ms": round(ordered[math.ceil(len(ordered) * percentile / 100) - 1], 3)
               for percentile in (50, 95, 99)}}


def main():
    """Run sequential/concurrent profiles, delete synthetic snapshots, and save evidence."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--requests", type=int, default=40, help="Measured batches per profile (1-200).")
    parser.add_argument("--batch-size", type=int, default=5, help="Synthetic events per batch (1-100).")
    parser.add_argument("--concurrency", type=int, default=4, help="Second profile concurrency (2-16).")
    parser.add_argument("--output", type=Path, default=Path("artifacts/performance/ingestion.json"))
    parser.add_argument("--server-log", type=Path, help="Optional local JSON console log for transaction measurements.")
    parser.add_argument("--insecure-local-tls", action="store_true", help="Trust a dedicated loopback demo certificate only.")
    args = parser.parse_args()
    target = urllib.parse.urlsplit(args.base_url)
    loopback = target.hostname in ("localhost", "127.0.0.1", "::1")
    if (target.scheme not in ("https", "http") or target.scheme == "http" and not loopback or
            target.username or target.password or target.query or target.fragment or target.path not in ("", "/")):
        parser.error("Use an HTTPS origin or a loopback HTTP origin without credentials, path, query, or fragment.")
    if args.insecure_local_tls and not loopback:
        parser.error("The certificate exception is limited to loopback demo hosts.")
    if not (1 <= args.requests <= 200 and 1 <= args.batch_size <= 100 and 2 <= args.concurrency <= 16):
        parser.error("Requests, batch size, or concurrency is outside its documented bound.")
    username, password = os.environ.get("CMS_LOAD_USERNAME"), os.environ.get("CMS_LOAD_PASSWORD")
    if not username or not password:
        parser.error("Set CMS_LOAD_USERNAME and CMS_LOAD_PASSWORD privately before running.")
    auth = "Basic " + base64.b64encode(f"{username}:{password}".encode()).decode()
    context = ssl._create_unverified_context() if args.insecure_local_tls else ssl.create_default_context()
    opener = urllib.request.build_opener(RejectRedirects(), urllib.request.HTTPSHandler(context=context))
    endpoint = args.base_url.rstrip("/") + "/cms/events"
    prefix = "load-" + uuid.uuid4().hex
    generated = []

    def submit(events):
        """Submit one unique keyed batch; return status, duration, and verified outcome count."""
        body = json.dumps(events, separators=(",", ":")).encode()
        request = urllib.request.Request(endpoint, data=body, headers={"Authorization": auth,
            "Content-Type": "application/json", "Idempotency-Key": uuid.uuid4().hex})
        started = time.perf_counter()
        try:
            with opener.open(request, timeout=40) as response:
                result = json.load(response)
                outcomes = result.get("outcomes", [])
                valid = len(outcomes) == len(events) and all(item.get("outcome") == "applied" for item in outcomes)
                return response.status, (time.perf_counter() - started) * 1000, len(events) if valid else 0
        except urllib.error.HTTPError as error:
            error.close()
            return error.code, (time.perf_counter() - started) * 1000, 0
        except (OSError, urllib.error.URLError, ValueError):
            return "transport_or_response_error", (time.perf_counter() - started) * 1000, 0

    def events_for(profile, index):
        """Construct and remember synthetic IDs for later deletion, outside measured scheduling."""
        ids = [f"{prefix}-{profile}-{index}-{item}" for item in range(args.batch_size)]
        generated.extend(ids)
        return [{"type": "publish", "id": source, "version": 1, "timestamp": "2026-01-01T12:00:00Z",
                 "payload": {"title": "Synthetic load exercise", "padding": "x" * 256}} for source in ids]

    profiles = []
    cleanup_ok = True
    try:
        # Three warm-up batches initialize authentication, connection pools, and compiled queries.
        warmup = [submit(events_for("warmup", index)) for index in range(3)]
        if any(status != 200 or count != args.batch_size for status, _, count in warmup):
            raise RuntimeError("Warm-up failed; use a healthy dedicated instance and inspect safe server diagnostics.")
        for concurrency in (1, args.concurrency):
            batches = [events_for(concurrency, index) for index in range(args.requests)]
            start_utc = dt.datetime.now(dt.timezone.utc).isoformat()
            started = time.perf_counter()
            with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as executor:
                results = list(executor.map(submit, batches))
            elapsed = time.perf_counter() - started
            end_utc = dt.datetime.now(dt.timezone.utc).isoformat()
            counts = {}
            for status, _, _ in results:
                counts[str(status)] = counts.get(str(status), 0) + 1
            successes = sum(status == 200 and count == args.batch_size for status, _, count in results)
            profiles.append({"concurrency": concurrency, "requests": args.requests, "batchSize": args.batch_size,
                "startedUtc": start_utc, "finishedUtc": end_utc, "elapsedSeconds": round(elapsed, 3),
                "statuses": counts, "verifiedSuccessfulBatches": successes,
                "batchesPerSecond": round(successes / elapsed, 3),
                "eventsPerSecond": round(sum(count for _, _, count in results) / elapsed, 3),
                "clientLatency": distribution([duration for _, duration, _ in results])})
    finally:
        # A later deletion removes synthetic payloads even when a publish acknowledgment was lost.
        for offset in range(0, len(generated), 100):
            status, _, count = submit([{"type": "delete", "id": source, "timestamp": "2026-01-02T12:00:00Z"}
                                      for source in generated[offset:offset + 100]])
            cleanup_ok &= status == 200 and count == len(generated[offset:offset + 100])
    if args.server_log:
        # Console logging is asynchronous; allow its bounded queue to drain before reading local evidence.
        time.sleep(.5)
        measurements = []
        for line in args.server_log.read_text(encoding="utf-8-sig").splitlines():
            try:
                entry = json.loads(line)
                if entry.get("Category") == "CmsEventProcessor.Application.Observability.OperationTelemetry" and entry.get("EventId") == 7001:
                    measurements.append(entry["State"])
            except (ValueError, KeyError):
                continue
        for profile in profiles:
            start, end = (dt.datetime.fromisoformat(profile[key]) for key in ("startedUtc", "finishedUtc"))
            samples = [item for item in measurements if start <= dt.datetime.fromisoformat(item["CompletedAtUtc"]) <= end]
            profile["transactionLatency"] = distribution([item["DurationMs"] for item in samples])
            profile["lockWait"] = distribution([item["LockWaitMs"] for item in samples])
            profile["contendedTransactions"] = sum(item["Contended"] for item in samples)
            profile["transactionOutcomes"] = {outcome: sum(item["Outcome"] == outcome for item in samples)
                                               for outcome in sorted({item["Outcome"] for item in samples})}
    report = {"workload": "Closed loop; unique keyed synthetic publishes, three warm-up batches; cleanup excluded.",
              "clientOs": platform.platform(), "python": platform.python_version(), "profiles": profiles,
              "syntheticSnapshotsDeleted": cleanup_ok,
              "retainedMetadata": "Content-free deletion markers and successful replay metadata remain; use a disposable database."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0 if cleanup_ok and all(item["verifiedSuccessfulBatches"] == args.requests for item in profiles) else 1


if __name__ == "__main__":
    raise SystemExit(main())
