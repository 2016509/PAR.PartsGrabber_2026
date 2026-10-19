# Continuous PartsGrabber processing

Branch `codex/continuous-parts-grabber`, based on `AddKubernates`, in PartsGrabber
and `2_SystemCheckerMssqlWebAPI`. The worker still uses the common API. The deployed
test configuration points to `http://system-checker-mssql-webapi:6050/api`, with
no `ServiceUrls` section. This change does not migrate the worker to microservices.

## Settings and scheduling

```json
"Module": {
  "MaxConcurrentParts": 2,
  "MaxConcurrentSaves": 2,
  "ClaimBatchSize": 1,
  "PollIntervalMs": 2000,
  "SourceRefreshSeconds": 60
}
```

Free slots are refilled immediately when a part finishes. ClaimBatchSize caps
each claim rather than total active processing; a value of 1 still fills all
available slots. PollIntervalMs replaces the old cycle-level Interval delay.
Each part receives a fresh DI scope, parsers and a deep copy of source/proxy
metadata. Source metadata is refreshed before claiming and cached for 60 seconds.
Failed metadata loading does not claim or falsely finish parts.

The worker shares per-host parser gates, the existing browser and save limits.
One parser invocation per normalized host is allowed across all active parts;
different hosts may proceed together. Chromium retains its existing single-operation
gate and starts once. MaxConcurrentSaves bounds result/image saving across parts.
These are per-process concurrency limits, not a distributed requests-per-second
budget. Image requests still use the existing HTTP/browser paths.

## Lease and result safety

Each claim receives a unique worker token and attempt number. Existing
Module.LeaseSeconds (default 3600), MaxAttempts (5) and RetryDelaySeconds (300)
remain configurable. Heartbeats renew ownership every 10–30 seconds. Both normal
failures and expired crash leases stop when attempts are exhausted.

New endpoints under `/api/PartsAndReplaces/continuous` check owner, attempt and
expiry before renewing, failing or writing results. Each archive write locks the
parent part row in the same SQL transaction as its insert. Cross-part writes are
rejected. Names/replacements/pictures retain their existing archive deduplication
keys. Final results update only the fields produced by this worker and retain
an ownership receipt so a lost completion response can be retried safely.

Archive writes and the final result are separate transactions. A crash can leave
partial archives/files; retries reuse archive deduplication. This is at-least-once
processing, not one atomic transaction over all sites and image files.

Parsing keeps the existing one-minute timeout. If partial results are returned,
the prior partial-result handling remains. If parsing throws cancellation without
returning results, the part is retried. Saving starts a fresh timeout budget after
parsing; a canceled/failed save does not mark the part successfully processed.
Existing source-health decisions and internal/cache lookup rules remain.

SIGTERM/Ctrl+C stop new claims and drain admitted work with active heartbeats.
Forced termination relies on lease expiry. Browser concurrency is not increased.

## Validation and rollout

Run `dotnet test Tests/PartsGrabber.Tests.csproj -c Release`. The test project also
references the common API project. Four SQL tests require an explicitly set
`PARTS_GRABBER_TEST_CONNECTION`; their fixtures and trigger effects are rolled back.
The 18 tests cover slot refill, fresh processors/source snapshots, host gating,
timeout budgets, failed saves, owner isolation, archive idempotence, bounded retries
and safe completion. No real supplier-site load benchmark was performed.

Stop the old worker during transition because its older writes are not fenced.
Deploy the matching SystemCheckerMssqlWebAPI first, then the new PartsGrabber.
No SQL schema migration is required; all lease columns already exist. Source
changes alone do not modify the server's mounted appsettings.json; include the
new settings there when deploying, or use their defaults. Start with 2 parts and
measure queue latency, browser memory and supplier responses before increasing it.
