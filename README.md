# Multi-tenant invoice reporting: authorization training

A training repository. It contains an ASP.NET Core Web API with a
multi-tenant reporting endpoint, an Angular front end from the standard
starter, automated tests and a CI workflow. Phase 1 seeded a cross-tenant
authorization flaw on purpose. Phase 2 fixes it.

> **Warning.** The Phase 1 commit still contains the intentional flaw. Never
> deploy it. See [docs/training/phase-1-vulnerability.md](docs/training/phase-1-vulnerability.md).
> This is training code. It has not had a production security review.

## Training objectives

| Phase | Goal | Status |
| --- | --- | --- |
| 1 | Set up the repository and CI. Seed an endpoint that resolves the tenant from a request header but does not check that the caller belongs to that tenant. | Done |
| 2 | Add a tenant-scoped authorization policy with a `PlatformAdmin` bypass, and prove it with `WebApplicationFactory` integration tests. | Done |
| 3 | Write a STRIDE-lite threat model of the header tenant pattern and sweep the repository for the same flaw. Harden with denial logging, rate limiting and a claim-derived tenant. See [day-03-threat-model.md](day-03-threat-model.md). | Done |

## Repository layout

```text
backend/InvoiceReporting.Api/     ASP.NET Core Web API (controllers)
  Controllers/                    InvoiceReportController
  Data/                           ReportingDbContext, migrations, development seed data
  Domain/                         Tenant, Invoice, AuditLog entities
  Reporting/                      Report service, month parsing, response records
  Security/                       TenantAccess policy, requirement, handler; claim parsing; rate-limit policy;
                                  claim and role constants
  Tenancy/                        Tenant resolution (header -> validated tenant)
tests/InvoiceReporting.Api.Tests/ xUnit unit and WebApplicationFactory integration tests
frontend/                         Angular application (standard starter)
docs/                             Training note and repository-settings guide
.github/workflows/ci.yml          CI for backend and frontend
InvoiceReporting.slnx             .NET solution
global.json                       Pins the .NET SDK
dotnet-tools.json                 Local tool manifest (dotnet-ef)
day-03-threat-model.md            STRIDE-lite threat model and repository sweep
```

## Versions

| Component | Version |
| --- | --- |
| .NET SDK / target framework | 10.0.401 / `net10.0` |
| ASP.NET Core, EF Core, JwtBearer, Mvc.Testing | 10.0.13 |
| Database provider (app) | SQL Server (`Microsoft.EntityFrameworkCore.SqlServer`) |
| Database provider (tests) | SQLite in-memory (`Microsoft.EntityFrameworkCore.Sqlite`) |
| xUnit | 2.9.3 |
| Node.js / npm | 24.x / 11.19.0 |
| Angular / Angular CLI | 22.2 |
| Front-end test runner | Vitest 5 (Angular CLI default, jsdom) |
| Front-end linter | angular-eslint |

## Architecture

```text
HTTP request
  -> Authentication (JWT bearer)
  -> Rate limiter (PerCaller)             429 when over the per-caller limit
  -> [Authorize] on controller            401 when no valid identity
  -> InvoiceReportController
       -> ReportingMonth.TryParse          400 when month is not yyyy-MM
       -> tenant key: X-Dealer-Id, or the tenant_id claim when a
          standard user omits the header
       -> ITenantResolver                  400 when missing or malformed
       -> IAuthorizationService            403 when the TenantAccess policy fails (logged)
            (TenantAccess, TenantScope)
       -> tenant exists?                   404 when unknown (only reachable once authorized)
       -> IInvoiceReportService            one query + audit log row
```

- **Data model.** One shared database. Each `Invoice` and `AuditLog` row has a
  `TenantId` foreign key to `Tenant`. A tenant has a unique, lower-case `Key`
  such as `dealer-alpha`.
- **Tenant resolution** (`Tenancy/TenantResolver.cs`) validates the header
  value and looks it up. It does not decide whether the caller may use that
  tenant.
- **Tenant authorization** (`Security/TenantAccessHandler.cs`) decides that,
  using only the authenticated principal. See
  [Tenant authorization policy](#tenant-authorization-policy).
- **Report service** (`Reporting/InvoiceReportService.cs`) runs one
  parameterized query filtered by tenant and month, sums the returned rows, and
  saves an audit entry.
- **Configuration.** The connection string is read from
  `ConnectionStrings:Reporting`. The committed development value uses
  Windows authentication against LocalDB and has no password.

### Tenant authorization policy

The controller calls `IAuthorizationService.AuthorizeAsync(User, new TenantScope(key), "TenantAccess")`
after resolving the tenant and before running the report. The handler allows
the request only when:

1. The caller is authenticated, **and**
2. Either:
   - the caller has the role claim `PlatformAdmin` (administrator bypass), or
   - the caller has **exactly one** `tenant_id` claim. After trimming, the
     claim must match `^[A-Za-z0-9][A-Za-z0-9-]{0,31}$`, and its lower-case
     form must equal the resolved tenant key.

The pattern is checked before lowercasing. `ToLowerInvariant` maps some
non-ASCII characters to ASCII (for example the Kelvin sign U+212A becomes
`k`), so checking first stops such a claim from matching a real tenant key.

Everything else is denied: no claim, an empty claim, two or more claims, a
claim with non-ASCII or other invalid characters, a different tenant, or a role
other than exactly `PlatformAdmin`.

Every denial is logged as a `TenantAccessDenied` warning (event id 4030) with
the subject, the requested tenant key and a reason code (`NotAuthenticated`,
`NoTenantRequested`, `MissingOrInvalidTenantClaim`, `TenantMismatch`). The raw
claim value is never logged. When one subject is denied
`Security:TenantDenialAlert:Threshold` times (default 5) within the window
(default 5 minutes), `TenantDenialMonitor` logs one `TenantAccessProbeSuspected`
error (event id 4031). Route that event to your alerting channel.

A standard user may omit `X-Dealer-Id`. The controller then uses the caller's
own `tenant_id` claim (same validation rules) as the requested key, and the
policy still runs. A `PlatformAdmin` must always send the header.

| Situation | Response |
| --- | --- |
| No or invalid token | `401` |
| Over the per-caller rate limit | `429` with `Retry-After` |
| Bad `month`; malformed `X-Dealer-Id`; or `X-Dealer-Id` missing and no usable `tenant_id` claim (or caller is an admin) | `400` ProblemDetails |
| Authenticated, policy denied (includes unknown tenant for non-admins) | `403`, empty body; report not run, no audit row |
| Authorized, tenant does not exist (admin, or user whose own tenant is gone) | `404` |
| Authorized | `200` with the report |

**Claim requirements.** The identity provider issues `tenant_id` as the tenant
key, for example `dealer-alpha`. Case does not matter (`Dealer-Alpha` is
accepted), but only ASCII letters, digits and `-` are. Administrators get the
`PlatformAdmin` role in the token's role claim. The JWT bearer handler maps the
`role` claim to `ClaimTypes.Role`. The application has no endpoint that sets
roles or claims, and it never reads roles from headers or the query string.
Only the identity provider can make someone an administrator.

### Authentication and claims

| Item | Value | Defined in |
| --- | --- | --- |
| Scheme | JWT bearer, settings from `Authentication:Schemes:Bearer` | `Program.cs` |
| Tenant claim | `tenant_id`, value is a tenant key such as `dealer-alpha` | `Security/TenantClaimTypes.cs` |
| Administrator role | `PlatformAdmin`, in the standard role claim | `Security/AppRoles.cs` |
| Subject | `sub` claim (mapped to `ClaimTypes.NameIdentifier`), stored in the audit log | — |

Outside Development the API refuses to start unless
`Authentication:Schemes:Bearer` sets `Authority` (or `MetadataAddress`) and at
least one audience (`Audience`, `ValidAudience` or `ValidAudiences`).

Behind a reverse proxy, list the proxy addresses in
`ForwardedHeaders:KnownProxies`. `X-Forwarded-For` and `X-Forwarded-Proto` are
honored only from those addresses and loopback, so callers cannot spoof their
IP to get around the per-IP rate limit.

Tests replace JWT bearer with `TestAuthHandler`, which builds the identity
from test-only headers (`X-Test-Subject`, `X-Test-Tenant`, `X-Test-Roles`).
Only the test factory registers this handler. The application never does.

## Prerequisites

- .NET SDK 10.0.401 or a later 10.0 feature band
- Node.js 24 and npm 11
- For running the API locally: SQL Server LocalDB (installed with Visual
  Studio), or another SQL Server instance. Tests do not need SQL Server.

## Run the backend

```bash
dotnet tool restore
dotnet run --project backend/InvoiceReporting.Api --launch-profile http
```

In the Development environment the API applies migrations and seeds two
fictitious tenants (`dealer-alpha`, `dealer-bravo`) on startup. To use a
different SQL Server, override the connection string with user secrets or an
environment variable. Do not commit it:

```bash
dotnet user-secrets set "ConnectionStrings:Reporting" "<connection string>" --project backend/InvoiceReporting.Api
```

Create a development token. The signing key is stored in your user secrets,
not in the repository:

```bash
dotnet user-jwts create --project backend/InvoiceReporting.Api \
  --name alice@alpha.test --claim tenant_id=dealer-alpha --output token
# Administrator token:
dotnet user-jwts create --project backend/InvoiceReporting.Api \
  --name admin@platform.test --role PlatformAdmin --output token
```

Call the endpoint:

```bash
curl -H "Authorization: Bearer <token>" -H "X-Dealer-Id: dealer-alpha" \
  "http://localhost:5014/api/invoice-reports/monthly?month=2026-09"
```

### Endpoint contract

`GET /api/invoice-reports/monthly?month=yyyy-MM`

| Input | Where | Rules |
| --- | --- | --- |
| `month` | query | Required. `yyyy-MM`. |
| `X-Dealer-Id` | header | Required for `PlatformAdmin`; optional for other callers, who default to their `tenant_id` claim. Tenant key, `^[a-z0-9][a-z0-9-]{0,31}$` after trim and lower-casing. |
| `Authorization` | header | Required. `Bearer <JWT>`. |

`200 OK`:

```json
{
  "month": "2026-09",
  "invoices": [
    { "id": 1, "customerName": "Sample Customer 001", "amount": 1200.00, "issuedDate": "2026-09-03" }
  ],
  "total": 1200.00
}
```

Errors: `400` (bad month or tenant header, as ProblemDetails), `401` (not
authenticated), `403` (not allowed to access that tenant, empty body), `404`
(unknown tenant, only after authorization succeeds), `429` (rate limit
exceeded; `RateLimiting:PerCaller` defaults to 60 requests per minute per
subject).

## Run the frontend

```bash
cd frontend
npm ci
npm start          # http://localhost:4200
```

The front end is the unchanged Angular starter. It does not call the API yet.

## Build, lint and test

These are the same commands CI runs.

```bash
# Backend
dotnet restore InvoiceReporting.slnx
dotnet build InvoiceReporting.slnx --configuration Release --no-restore
dotnet test InvoiceReporting.slnx --configuration Release --no-build

# Frontend
cd frontend
npm ci
npm run lint
npm run test:ci
npm run build
```

Run only the tenant authorization integration tests:

```bash
dotnet test InvoiceReporting.slnx --filter "FullyQualifiedName~TenantAuthorizationTests"
```

| Test | Scenario | Expected |
| --- | --- | --- |
| A | Tenant A user requests Tenant B (regression test for the Phase 1 flaw) | `403`, no invoice data, report not run |
| B | Tenant A user requests Tenant A | `200`, only Tenant A invoices, correct total |
| C | `PlatformAdmin` (with no tenant claim, or with Tenant A's) requests Tenant B | `200`, Tenant B report, audited against Tenant B |
| D | No identity | `401`, no data |
| E | Missing, empty, unknown, wrong-case or duplicate `tenant_id` claim; unknown tenant; malformed header; missing header with no usable claim | `403` or `400`, no data |
| F | Mixed-case `tenant_id` claim, same tenant and cross-tenant | `200` and `403` |
| G | Standard user omits the header | `200` for their own tenant from the claim; `404` if that tenant does not exist |
| Extra | Admin flag sent in the query string or headers; roles `platformadmin`, `Admin`, `TenantAdmin` | `403` |

### Test database

Integration tests use a private SQLite in-memory database per test class,
created and seeded with fixed fictitious data. SQLite enforces foreign keys and
unique indexes and runs real SQL. It does **not** check SQL Server behavior
such as collation, `decimal(18,2)` precision or query plans. The SQL Server
path, and the real JWT bearer path (including the `PlatformAdmin` role claim
from `dotnet user-jwts --role`), were checked by hand against LocalDB.

Tests authenticate with `TestAuthHandler`, not JWT bearer, so automated tests
do not cover token validation or the JWT claim mapping.

## CI and pull requests

`.github/workflows/ci.yml` runs on every pull request, pushes to `main`
and manual dispatch:

- **Backend:** restore, build (Release), test, upload the `.trx` results,
  publish a test report check.
- **Frontend:** `npm ci`, lint, unit tests, production build.

Branch protection must be applied by a repository administrator. See
[docs/repository-settings.md](docs/repository-settings.md).
