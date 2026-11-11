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
  Dockerfile                      Container image (non-root, volumes for logs and reports)
  Controllers/                    InvoiceReportController
  Data/                           ReportingDbContext, migrations, development seed data
  Domain/                         Tenant, Invoice, AuditLog entities
  Reporting/                      Report service, month parsing, response records; quarterly CSV
                                  and email jobs, Mailjet and SMTP senders, failed-email log
  Security/                       TenantAccess policy, requirement, handler; claim parsing; rate-limit policy;
                                  claim and role constants
  Tenancy/                        Tenant resolution (header -> validated tenant)
tests/InvoiceReporting.Api.Tests/ xUnit unit and WebApplicationFactory integration tests
frontend/                         Angular application (standard starter)
docs/                             Training note and repository-settings guide
.github/workflows/ci.yml          CI for backend and frontend
docker-compose.yml, .env.example  Local development stack: API, SQL Server, Mailpit
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
| Database provider (tests) | SQL Server 2022 in Docker, via Testcontainers (`Testcontainers.MsSql`) |
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

The monitor counts in memory, per process. When you run more than one instance
behind a load balancer, one subject's denials are split across instances and
event 4031 may never fire. In that deployment, build the alert in your log
platform from event 4030 instead: count `TenantAccessDenied` events grouped by
`Subject` over the same window, and alert when a count reaches the threshold.
For example: "event id 4030, grouped by Subject, count >= 5 in 5 minutes".
Keep event 4031 as a backup signal.

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
- Or, instead of LocalDB: Docker with Compose (see
  [Run with Docker Compose](#run-with-docker-compose)).

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

### Run with Docker Compose

Compose runs the API, SQL Server and Mailpit in containers, with no LocalDB
needed. It is for local development only. SQL Server needs about 2 GB of RAM.

1. Create your token signing key once, on your machine, if you have not
   already done so: run any `dotnet user-jwts create` command shown above.
2. Copy the settings file and fill it in. `.env` is git-ignored:

   ```bash
   cp .env.example .env
   ```

   - `MSSQL_SA_PASSWORD`: any strong password for the local SQL Server.
   - `USER_SECRETS_DIR`: your user-secrets folder, mounted read-only so the
     container accepts your `dotnet user-jwts` tokens.
     Windows: `C:/Users/<you>/AppData/Roaming/Microsoft/UserSecrets`.
     macOS and Linux: `~/.microsoft/usersecrets`, written as a full path.

3. Start everything:

   ```bash
   docker compose up --build
   ```

| Service | Address |
| --- | --- |
| API | <http://localhost:5080> |
| Hangfire dashboard | <http://localhost:5080/hangfire> |
| Mailpit inbox | <http://localhost:8025> |
| SQL Server (from host tools) | `localhost,14333`, login `sa` |

All ports are published on `127.0.0.1` only. The API runs in Development, so it
applies migrations and seeds the test tenants on startup. Data, report CSVs and
failed-email logs are kept in the named volumes `invoice-reporting-sql`,
`invoice-reporting-reports` and `invoice-reporting-logs`.

`docker compose down` stops everything and keeps the data. `docker compose down -v`
also deletes the volumes. Do that after you change `MSSQL_SA_PASSWORD`,
because SQL Server keeps the password it was first started with.

If every call returns `401`, check `USER_SECRETS_DIR`: the container can't
see your token signing key.

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

#### Run a quarterly report now (administrators)

`POST /api/admin/quarterly-reports/{year}/{quarter}`

Builds one tenant's quarterly CSV inside the request, then queues its emails
in Hangfire. A quarter that was already reported is not built again; only its
emails are queued again (people who already got the email are skipped).

| Input | Where | Rules |
| --- | --- | --- |
| `year`, `quarter` | path | Year 2000–2100, quarter 1–4. |
| `X-Dealer-Id` | header | Required. Tenant key, same rules as above. |
| `Authorization` | header | Required. `Bearer <JWT>` with the `PlatformAdmin` role. |

Two checks, both required: the `PlatformAdmin` role (`[Authorize(Roles = ...)]`),
then the same `TenantAccess` policy as the monthly report. A standard user
gets `403`, even for their own tenant. Each call is audited as
`TriggerQuarterlyInvoiceReport` with the caller's subject.

`200 OK`: `{ "tenantKey": "dealer-alpha", "quarter": "2026-Q3", "result": "Generated" }`
(`result` is `Generated` or `AlreadyGenerated`).

Errors: `400` (bad year, quarter or tenant header), `401`, `403`, `404`
(unknown tenant), `429`. If the caller disconnects, the run is cancelled: the
request's token reaches every database query and the file write, and no CSV,
"generated" audit entry or email job is recorded.

**Long runs.** The CSV is built inside the request, so the call takes as long
as the quarter's query and file write. A reverse proxy or client timeout
shorter than that (many proxies default to 60 to 120 seconds) closes the
connection and cancels the run, as above. Nothing is half-recorded: call the
endpoint again, or let the scheduled job run it. For very large tenants,
raise the proxy timeout for `/api/admin/quarterly-reports`. For the quarter
that just ended, the Hangfire dashboard's **Trigger now** is another option:
it queues every tenant as background jobs, with no request timeout. A queued
mode for this endpoint that returns `202 Accepted` is not provided.

The Hangfire dashboard's **Trigger now** is a second way to run the job. It
needs a `PlatformAdmin` caller too (no per-tenant check: the dashboard covers
every tenant); see the dashboard note under
[Quarterly report emails](#quarterly-report-emails).

#### Async and cancellation: what stays synchronous

The report pipeline is async from end to end. The caller's token (the
request-aborted token over HTTP, Hangfire's shutdown token in a job) reaches
every database query, the CSV write, the zip, the provider lookup and the
send. The invoices are streamed (`IAsyncEnumerable<InvoiceSummary>` read with
`await foreach`): each row goes from the query straight to the CSV file, so a
large quarter is never held in memory. The database query stays open while
the file is written.

The backend build fails on blocking async code. Rules from
`Microsoft.VisualStudio.Threading.Analyzers` are set to errors in the API's
`.csproj`: VSTHRD002 (`.Result`, `.Wait()`, `Task.WaitAll`/`WaitAny`,
`GetAwaiter().GetResult()`), VSTHRD100 (`async void`) and VSTHRD103 (a sync
call inside an async method where an async version exists).
`Microsoft.CodeAnalysis.BannedApiAnalyzers` bans `Task.Run` and `TaskFactory`
through `backend/InvoiceReporting.Api/BannedSymbols.txt` (error RS0030). These
rules check types, not names, so renamed variables and calls split across
lines are still caught.

A few calls stay synchronous on purpose. .NET has no async version of them,
and wrapping them in `Task.Run` would only move the wait to another thread.
Each one is a single metadata operation on the local disk, not a data
transfer:

| Call | Where | Why it is acceptable |
| --- | --- | --- |
| `Directory.CreateDirectory` | `QuarterlyReportCsvWriter`, `FailedReportEmailLogFile` | Returns at once when the folder exists, which is every run after the first. |
| `File.Move(temp, path, overwrite: true)` | `QuarterlyReportCsvWriter` | A rename on the same volume. It is what makes the report replace atomic. |
| `File.Delete` | `QuarterlyReportCsvWriter` (temp cleanup, stale temp files after each schedule run), `FailedReportEmailLogFile` (retention) | Cleanup only. Failures are ignored and retried on the next run or day. |
| `Directory.EnumerateFiles`, `FileInfo` | `QuarterlyReportCsvWriter.DeleteStaleTempFiles` | Lists one folder once per quarter to find stale temp files. |
| `IMonitoringApi` (processing, scheduled, enqueued jobs) | `HangfireQuarterlyReportActivity` | Hangfire's API is synchronous. Read once every 5 minutes, only while the cleanup waits. |
| `File.Exists` | `QuarterlyReportEmailJob` | Checks the CSV is there before claiming the email. |
| `File.GetLastWriteTime` | `QuarterlyReportZip` | Sets the zip entry's date. |
| `new FileInfo(path).Length` | `QuarterlyReportEmailJob` | Only in the "too large" log message. |
| `File.WriteAllText` probe | `FailedReportEmailLogFile.CheckDirectory` | Runs once at startup, before any request or job. |
| `IBackgroundJobClient.Enqueue` | Scheduler, report job, email job | Hangfire's API is synchronous. It writes one job row to Hangfire's storage. |

The `CancellationToken.None` uses in `backend/` are also intentional:

- **Hangfire placeholders** in `Enqueue(...)` and the recurring job: Hangfire
  replaces them with its shutdown token when the job runs.
- **SMTP `DisconnectAsync` after the server accepted the email**: a failed
  QUIT must not undo a send.

Cleanup in `QuarterlyReportEmailJob` that must outlive the job's token
(freeing an email claim after a failure or a cancel, marking an email sent,
failure audit writes and their duplicate check) uses its own 10-second token
(`QuarterlyReportEmailJob.CleanupTimeout`) instead of the job's token or
`CancellationToken.None`. It runs while the job is stopping, but a stuck
database cannot hold up shutdown. Freeing a claim never throws, so the
original error or cancellation reaches Hangfire; a claim that could not be
freed is taken over once its lease ends.

### Quarterly report emails

After `QuarterlyReportJob` writes a tenant's CSV, `QuarterlyReportEmailJob`
emails it to each opted-in recipient (`QuarterlyReportRecipients.WillReceiveReport`):

- A standard user receives the report for their own tenant (`Users.TenantId`).
- A `PlatformAdmin` user receives one email per tenant listed for them in
  `UserTenantAssignments`. These assignments affect email only, not API access.

Each recipient gets a separate Hangfire job, so one bad address does not block
the others. A recipient never gets the same report twice: each send is claimed
in `QuarterlyReportEmailDeliveries` first, and a job that takes over an
interrupted send asks Mailjet whether the email already went out.

Two senders exist. Development uses SMTP to a local Mailpit server, and
production uses the Mailjet Send API over HTTPS. `Reporting:Email:Provider`
picks one (`Smtp` or `MailjetApi`).

#### Local development (Mailpit)

[Mailpit](https://mailpit.axllent.org/) catches every message and delivers
nothing. Docker Compose starts it for you (see
[Run with Docker Compose](#run-with-docker-compose)). When you run the API with
`dotnet run`, start Mailpit on its own:

```bash
docker run -d --name mailpit -p 127.0.0.1:8025:8025 -p 127.0.0.1:1025:1025 axllent/mailpit
```

To check the emails by hand:

1. Open the Hangfire dashboard: <http://localhost:5080/hangfire> with Compose,
   or <http://localhost:5014/hangfire> with `dotnet run`.
2. On **Recurring jobs**, trigger `quarterly-invoice-report`.
3. Open Mailpit at <http://localhost:8025>. Expect one email per opted-in user
   of each tenant, with that tenant's CSV attached, and a Message-ID of the form
   `quarterly-report.<quarter>.t<tenant>.u<user>@...`.
4. Trigger the job again. No new emails should arrive.

The dashboard can trigger and delete jobs for every tenant. By default it
needs both a request from the same machine and an authenticated caller with
the `PlatformAdmin` role (`401` without a token, `403` otherwise). The caller
is authenticated with the app's JWT bearer scheme, so a browser must send the
`Authorization` header (for example through a header-injecting extension or
an authenticating reverse proxy).

In Development, `appsettings.Development.json` sets
`Hangfire:Dashboard:AllowAnyCallerInDevelopment` to `true`, so the dashboard
accepts any caller and works through Docker's port mapping. Compose publishes
the port on `127.0.0.1` only. Set it to `false` to test the admin check
locally. The setting is ignored outside Development.

#### Settings

All settings live under `Reporting:Email`. Email is off by default outside
Development (`Enabled: false`).

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Turns report email on |
| `Provider` | `Smtp` | `Smtp` (Mailpit, local development) or `MailjetApi` (production) |
| `FromAddress`, `FromName` | none, `Invoice Reporting` | Sender. The address must be on a verified sending domain |
| `MaxAttachmentBytes` | `10485760` (10 MB) | Largest zipped report that is emailed. Cannot be set above 10 MB |
| `FailedLogDirectory` | `logs` | Folder for the failed-email log files, relative to the app folder |
| `FailedLogRetentionDays` | `90` | Days to keep failed-email log files |
| `Host`, `Port`, `Security`, `Username`, `Password` | | SMTP only |
| `Mailjet:ApiKey`, `Mailjet:SecretKey` | | Mailjet only. Secrets: never put them in an appsettings file |
| `Mailjet:BaseUrl` | `https://api.mailjet.com` | Mailjet only. Must be https |
| `Mailjet:Timeout` | `00:00:30` | Mailjet only |
| `Mailjet:SandboxMode` | `false` | Mailjet only. Mailjet checks the request and sends nothing |

The app does not start when a required setting for the chosen provider is
missing or invalid.

#### Production (Mailjet API)

Set these as environment variables (or in your secret store). Never commit the
keys:

```bash
Reporting__Email__Enabled=true
Reporting__Email__Provider=MailjetApi
Reporting__Email__Mailjet__ApiKey=<Mailjet API key>
Reporting__Email__Mailjet__SecretKey=<Mailjet secret key>
Reporting__Email__FromAddress=reports@<your verified domain>
```

To try Mailjet from your machine, keep the keys in user secrets instead:

```bash
dotnet user-secrets set "Reporting:Email:Provider" "MailjetApi" --project backend/InvoiceReporting.Api
dotnet user-secrets set "Reporting:Email:Mailjet:ApiKey" "<key>" --project backend/InvoiceReporting.Api
dotnet user-secrets set "Reporting:Email:Mailjet:SecretKey" "<secret>" --project backend/InvoiceReporting.Api
```

How failures are handled:

| Failure | What the job does |
| --- | --- |
| Zipped CSV over `MaxAttachmentBytes` | Not sent, not retried. Recorded as failed (`too-large`) |
| Mailjet rejects the email (4xx other than 408/429) | Not retried. Recorded as failed (`rejected:http-<status>:<Mailjet code>`) |
| Rate limit (429), timeout status (408), Mailjet error (5xx), no connection | Retried by Hangfire up to 5 times. Each attempt is audited; the last one is recorded as failed (`retries-exhausted:temporary`) |
| No answer from Mailjet (the email may have gone out) | The claim is kept. After 5 minutes a retry asks Mailjet whether the email was sent, and sends only if it was not |
| The CSV is missing | Retried, in case the report job is still writing it. The last retry is recorded as failed (`retries-exhausted:csv-missing`) |
| Another job is still sending the same email | Retried later. The last retry is recorded as failed (`retries-exhausted:in-progress`) |
| The job is stopped (server shutdown) | Not a failure: nothing is audited or logged as failed, and Hangfire runs the job again. Stopped before sending, the claim is freed. Stopped mid-send, the claim is kept, as for no answer above |

#### Sending domain: SPF, DKIM and DMARC

Without these records, report emails land in spam or are refused. Do this
once per sending domain, before go-live:

1. In Mailjet, add and verify the domain under **Account settings > Sender
   domains & addresses**.
2. **SPF:** add `include:spf.mailjet.com` to the domain's existing SPF TXT
   record, for example `v=spf1 include:spf.mailjet.com ~all`. A domain must
   have only one SPF record, so do not add a second one.
3. **DKIM:** publish the TXT record `mailjet._domainkey.<domain>` with the
   public key Mailjet shows.
4. **DMARC:** start with `_dmarc.<domain>` set to
   `v=DMARC1; p=none; rua=mailto:<reports address>`. Once the reports show
   Mailjet mail passing SPF and DKIM, change `p=none` to `p=quarantine`.
5. Set `FromAddress` to an address on that domain.

#### Test against the Mailjet sandbox

Sandbox mode makes Mailjet check the full request (keys, sender, payload) and
send nothing:

```bash
dotnet user-secrets set "Reporting:Email:Mailjet:SandboxMode" "true" --project backend/InvoiceReporting.Api
```

Run the API with the user-secrets settings above and trigger the job. Each
send should succeed and nothing should be delivered. Sandbox sends may not be
stored by Mailjet, so the "already sent" lookup can only be tested with a real
send to a test inbox.

#### For support: finding failed report emails

Every email that will not be sent without help is written to the audit log
once:

```sql
SELECT OccurredAtUtc, TenantId, Detail
FROM AuditLogs
WHERE Action = 'EmailQuarterlyInvoiceReportFailed'
ORDER BY OccurredAtUtc DESC;
```

`Detail` has the form `<quarter>:user-<id>:<reason>`, for example
`2026-Q3:user-345:rejected:http-400:mj-0013`. It never contains the email
address. To find the user, run `SELECT Id, Email FROM Users WHERE Id = 345`.

| Reason | Meaning | What to do |
| --- | --- | --- |
| `too-large` | The CSV is over 10 MB, too big to email | Send the report another way |
| `rejected:...` | Mailjet refused the email: usually a bad address, or a problem with the sending domain | Fix the address or domain, then re-send |
| `retries-exhausted:temporary` | Mailjet or the network was down for all retries | Re-send |
| `retries-exhausted:outcome-unknown` or `:lookup-failed` | Mailjet did not answer, so the email may have arrived | Check with the recipient or in the Mailjet message log before re-sending |
| `retries-exhausted:csv-missing` | The quarterly CSV was never written, or was deleted | Check the `QuarterlyReportJob` run for that tenant in Hangfire, then re-send |
| `retries-exhausted:in-progress` | Another job kept the email claimed for all retries. That job may still have sent it | Check `QuarterlyReportEmailDeliveries` for the tenant, user and quarter: `Status = 'Sent'` means it went out. If not sent, re-send |

Failed attempts that Hangfire retried are audited separately as
`EmailQuarterlyInvoiceReportAttemptFailed`. They are for troubleshooting only;
most are followed by a successful send.

The same failures are also written to a daily log file, which works even when
the database is down. It is in the `logs` folder of the deployed backend
(`/app/logs` in the container): `logs/failed-report-emails-YYYYMMDD.log`. Each
line looks like this:

```text
2026-11-03T09:15:02.123Z FAILED tenant=12 user=345 quarter=2026-Q3 reason=rejected:http-400:mj-0013 job=8812
```

Everything is in UTC: the time on each line, the date in the file name (a new
file starts at 00:00 UTC), and the 90 days the files are kept. `job` is the
Hangfire job id, which you can open in the dashboard. With more than one server, each server
writes its own files, so check them all. To find one tenant:

```bash
grep "tenant=12 " logs/failed-report-emails-*.log
# Windows PowerShell:
Select-String "tenant=12 " logs\failed-report-emails-*.log
```

**Re-sending:** at the start of each quarter, the `quarterly-invoice-report`
recurring job reports on the quarter that just ended. Trigger it again from the
Hangfire dashboard to rebuild the CSVs and queue the emails again. Recipients
who already got their email are skipped. Older quarters are re-run manually
through the API.

#### Deployment: volumes

The container keeps two kinds of files that must survive restarts and
deploys. Mount a persistent volume on both:

| Path | Contents |
| --- | --- |
| `/app/logs` | Failed-email log files for support |
| `/app/reports` | Quarterly CSV files, read by the email jobs |

```bash
docker build -f backend/InvoiceReporting.Api/Dockerfile -t invoice-reporting-api .
docker run -d --name invoice-reporting-api -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__Reporting="<connection string>" \
  -e ConnectionStrings__Hangfire="<connection string>" \
  -e Reporting__Email__Enabled=true \
  -e Reporting__Email__Provider=MailjetApi \
  -e Reporting__Email__Mailjet__ApiKey="<key>" \
  -e Reporting__Email__Mailjet__SecretKey="<secret>" \
  -e Reporting__Email__FromAddress="reports@<verified domain>" \
  -v invoice-reporting-logs:/app/logs \
  -v invoice-reporting-reports:/app/reports \
  invoice-reporting-api
```

Production needs the JWT bearer settings as well (see
[Authentication and claims](#authentication-and-claims)). Production does not
apply migrations on startup.

- Without the volumes, the files are written inside the container and lost
  when it is replaced. Nothing warns you, so check the mounts in the deploy
  configuration.
- The app runs as the non-root user `app` (UID 1654). A new named volume is
  writable as it is. A host folder (bind mount) must be owned by UID 1654:
  `sudo chown -R 1654:1654 /srv/invoice-reporting/logs`.
- With more than one API container, all of them must share the same
  `/app/reports` storage. Otherwise an email job can run on a container that
  does not have the CSV.
- To check the log volume after a forced failure:
  `docker exec invoice-reporting-api ls /app/logs`.

## Run the frontend

```bash
cd frontend
npm ci
npm start          # http://localhost:4200
```

The front end is the unchanged Angular starter. It does not call the API yet.

## Build, lint and test

These are the same commands CI runs. The backend tests need Docker running
(Docker Desktop on Windows and macOS). The first run downloads the SQL Server
image (about 1.5 GB).

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

Integration tests run against SQL Server 2022 in a Docker container started by
Testcontainers, the same image as `docker-compose.yml`. One container serves
the whole test run, and each test class gets its own database, created by the
real EF Core migrations and seeded with fixed fictitious data. So the tests
check the same collation, `decimal(18,2)` precision, filtered unique index and
error numbers as production, and they fail if a migration breaks. The real JWT
bearer path (including the `PlatformAdmin` role claim from
`dotnet user-jwts --role`) was checked by hand.

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
