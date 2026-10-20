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
| **R**epudiation | A user denies reading or probing another tenant. | Successful reads write an `AuditLog` row. Every denied decision logs a `TenantAccessDenied` warning (event 4030) with subject, requested tenant and reason. | Low. Logs need retention and an alert rule outside this repo. |
| **I**nformation disclosure | Enumerate tenant keys via 404 vs 403. | Authorization runs before the existence check, so a non-admin gets 403 for an unknown tenant. | Low |
| **D**enial of service | Mass key guessing, each guess a DB lookup. | `PerCaller` fixed-window limit (60/min per subject, per IP when anonymous) on all controllers. Returns 429 with `Retry-After`. | Medium-low. Behind a proxy, configure `ForwardedHeaders` or all anonymous callers share one IP. The limit is per instance, not distributed. |
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

## Still open

- Configure the production JwtBearer `Authority` and `ValidAudiences`. Today
  `appsettings.json` has none, so token validation fails and the API fails
  closed with 401.
- Configure `ForwardedHeaders` before deploying behind a proxy.
- Add an alert on bursts of `TenantAccessDenied` for one subject.
