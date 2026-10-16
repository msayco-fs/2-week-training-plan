# Multi-tenant invoice reporting: authorization training

A training repository. It contains an ASP.NET Core Web API with a
**deliberately vulnerable** multi-tenant reporting endpoint, an Angular
front end from the standard starter, automated tests and a CI workflow.

> **Warning.** Phase 1 contains an intentional cross-tenant authorization flaw.
> Run it only on your own machine or in CI. Do not deploy it.
> See [docs/training/phase-1-vulnerability.md](docs/training/phase-1-vulnerability.md).

## Training objectives

| Phase | Goal | Status |
| --- | --- | --- |
| 1 | Set up the repository and CI. Seed an endpoint that resolves the tenant from a request header but does not check that the caller belongs to that tenant. | Done |
| 2 | Add a tenant-scoped authorization policy with a `PlatformAdmin` bypass, and prove it with `WebApplicationFactory` integration tests. | Not started |

## Repository layout

```text
backend/InvoiceReporting.Api/     ASP.NET Core Web API (controllers)
  Controllers/                    InvoiceReportController
  Data/                           ReportingDbContext, migrations, development seed data
  Domain/                         Tenant, Invoice, AuditLog entities
  Reporting/                      Report service, month parsing, response records
  Security/                       Tenant claim type and role constants (used in Phase 2)
  Tenancy/                        Tenant resolution (header -> validated tenant)
tests/InvoiceReporting.Api.Tests/ xUnit unit and WebApplicationFactory integration tests
frontend/                         Angular application (standard starter)
docs/                             Training note and repository-settings guide
.github/workflows/ci.yml          CI for backend and frontend
InvoiceReporting.slnx             .NET solution
global.json                       Pins the .NET SDK
dotnet-tools.json                 Local tool manifest (dotnet-ef)
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
  -> Authentication (JWT bearer)          401 when no valid identity
  -> [Authorize] on controller
  -> InvoiceReportController
       -> ReportingMonth.TryParse          400 when month is not yyyy-MM
       -> ITenantResolver (X-Dealer-Id)    400 when missing or malformed, 404 when unknown
       -> (Phase 2: tenant authorization goes here)
       -> IInvoiceReportService            one query + audit log row
```

- **Data model.** One shared database. Each `Invoice` and `AuditLog` row has a
  `TenantId` foreign key to `Tenant`. A tenant has a unique, lower-case `Key`
  such as `dealer-alpha`.
- **Tenant resolution** (`Tenancy/TenantResolver.cs`) validates the header
  value and looks it up. It does not decide whether the caller may use that
  tenant.
- **Report service** (`Reporting/InvoiceReportService.cs`) runs one
  parameterized query filtered by tenant and month, sums the returned rows, and
  saves an audit entry.
- **Configuration.** The connection string is read from
  `ConnectionStrings:Reporting`. The committed development value uses
  Windows authentication against LocalDB and has no password.

### Authentication and claims (ready for Phase 2)

| Item | Value | Defined in |
| --- | --- | --- |
| Scheme | JWT bearer, settings from `Authentication:Schemes:Bearer` | `Program.cs` |
| Tenant claim | `tenant_id`, value is a tenant key such as `dealer-alpha` | `Security/TenantClaimTypes.cs` |
| Administrator role | `PlatformAdmin`, in the standard role claim | `Security/AppRoles.cs` |
| Subject | `sub` claim (mapped to `ClaimTypes.NameIdentifier`), stored in the audit log | — |

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
# Administrator token (for Phase 2):
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
| `X-Dealer-Id` | header | Required. Tenant key, `^[a-z0-9][a-z0-9-]{0,31}$` after trim and lower-casing. |
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
authenticated), `404` (unknown tenant).

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

Run only the test that demonstrates the Phase 1 vulnerability:

```bash
dotnet test InvoiceReporting.slnx --filter "Category=KnownVulnerability"
```

### Test database

Integration tests use a private SQLite in-memory database per test class,
created and seeded with fixed fictitious data. SQLite enforces foreign keys and
unique indexes and runs real SQL. It does **not** check SQL Server behavior
such as collation, `decimal(18,2)` precision or query plans. The SQL Server
path was checked by hand against LocalDB.

## CI and pull requests

`.github/workflows/ci.yml` runs on pull requests to `main`, pushes to `main`
and manual dispatch:

- **Backend:** restore, build (Release), test, upload the `.trx` results,
  publish a test report check.
- **Frontend:** `npm ci`, lint, unit tests, production build.

Branch protection must be applied by a repository administrator. See
[docs/repository-settings.md](docs/repository-settings.md).
