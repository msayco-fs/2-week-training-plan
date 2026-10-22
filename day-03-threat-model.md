# Day 3: STRIDE-lite threat model for the header-supplied tenant identifier

## Pattern under review

`GET /api/invoice-reports/monthly` accepts an `X-Dealer-Id` header that names
the tenant to report on. `TenantResolver` validates it
(`^[a-z0-9][a-z0-9-]{0,31}$`, after trim and lower-casing) and looks it up.
Resolution is not authorization: the `TenantAccess` policy
(`TenantAccessHandler`) then checks the authenticated principal.

- A **PlatformAdmin** must send the header and may read any tenant.
- A **standard user** may omit the header, and the tenant then comes from the
  `tenant_id` claim. If the header is sent, it must equal that claim.

Trust boundary: the header is untrusted client input. JWT claims are trusted
only because JwtBearer validates the signature, issuer and audience.

## STRIDE-lite

| Threat | Example | Control | Residual |
| --- | --- | --- | --- |
| **S**poofing | A `dealer-alpha` user sends `X-Dealer-Id: dealer-bravo`. | The policy compares the header to the signed `tenant_id` claim. Standard users can omit the header entirely. Test `A_CrossTenantRequest_ByStandardUser_Returns403_...`. | Low |
| **S**poofing (admin) | Role or admin flag sent in a header or query string. | Role read only from the principal. Test `AdminBypass_CannotBeClaimedThroughQueryStringOrRequestHeaders`. | Low |
| **T**ampering | Crafted header (SQL meta-characters, Unicode look-alikes, trailing newline). | Strict ASCII regex, parameterized EF Core query, `\z` anchor and explicit `A-Z` in the claim regex. | Low |
| **R**epudiation | A user denies reading or probing another tenant. | Successful reads write an `AuditLog` row. Every denied decision logs a `TenantAccessDenied` warning (event 4030) with subject, requested tenant and reason. `TenantDenialMonitor` logs one `TenantAccessProbeSuspected` error (event 4031) when one subject is denied 5 times in 5 minutes. | Low. Log retention and routing event 4031 to a pager are operations tasks outside this repo. Counts are per instance, so with more than one instance a subject's denials can be spread out and event 4031 may never fire. In that deployment, alert on event 4030 grouped by subject in the log platform; event 4031 is then a backup signal only. |
| **I**nformation disclosure | Enumerate tenant keys via 404 vs 403. | Authorization runs before the existence check, so a non-admin gets 403 for an unknown tenant. | Low |
| **D**enial of service | Mass key guessing, each guess a DB lookup. | `PerCaller` fixed-window limit (60/min per subject, per IP when anonymous) on all controllers. Returns 429 with `Retry-After`. `UseForwardedHeaders` honors `X-Forwarded-For` only from loopback and `ForwardedHeaders:KnownProxies`, so callers cannot spoof a new IP per request. | Low. The proxy addresses must be listed when deploying behind a proxy. The middleware skips the proxy check when the connection has no remote IP (Unix socket or named pipe), so only expose such a listener to the local proxy. The limit is per instance, not distributed. |
| **E**levation of privilege | Several `tenant_id` claims, or a blank claim. | Ambiguous or malformed claims are denied (fail closed). | Low. `PlatformAdmin` is a full bypass by design, so its issuance must be controlled at the identity provider. |

## Structural risk and guard

The header is a client-chosen object key (CWE-639, OWASP API1). Any new action
that accepts it must call `AuthorizeAsync(User, new TenantScope(key),
TenantAccess)` before it uses the tenant. `TenantInputInventoryTests` fails the
build when a controller action binds a tenant identifier and is not on the
reviewed list.

The guard covers controller action parameters only. It does not see
minimal-API endpoints, request-body properties, or direct `Request.Headers`
reads. Reviewers must still check those by hand.

## Sweep of the repository (2026-10-20)

| Location | Client-supplied tenant id | Authorization | Outcome |
| --- | --- | --- | --- |
| `InvoiceReportController.GetMonthly` | `X-Dealer-Id` header | `TenantAccess` policy | Reviewed. Fixed in Phase 2. Hardened here (claim default, denial logging, rate limit). |
| `Program.cs` | None (no minimal APIs) | n/a | No match |
| `frontend/` | None (starter app, no API calls) | n/a | No match |
| `tests/.../TestAuthHandler.cs` | `X-Test-Tenant` header becomes a claim | None | Flagged. Test-only. It must never be registered in the API. |
| Branch `phase-1/repository-setup` (PR #1) | `X-Dealer-Id` header | None (seeded flaw) | Flagged. Never merge it without Phase 2. |

## Deployment hardening

Items first listed as open in this review are now enforced in code:

| Item | Control | Test |
| --- | --- | --- |
| Production JWT settings | Outside Development, startup fails unless `Authentication:Schemes:Bearer` sets `Authority` (or `MetadataAddress`) and at least one audience. | `StartupConfigurationTests` |
| Proxy client IP | `UseForwardedHeaders` runs first and trusts only loopback and `ForwardedHeaders:KnownProxies`. An invalid address fails startup. | `RateLimitingTests.SpoofedForwardedFor_...`, `StartupConfigurationTests` |
| Probe alerting | `TenantDenialMonitor` logs event 4031 at Error level once per window when a subject reaches the denial threshold (`Security:TenantDenialAlert`). | `TenantDenialMonitorTests`, `TenantAccessHandlerTests` |

Still the operator's job: provide the real identity-provider values, list the
real proxy addresses, and route event 4031 to an alerting channel.
