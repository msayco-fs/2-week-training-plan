# Day 07: Quarterly report capacity

Measured on 2026-11-03 against the local Docker SQL Server. Covers the per-tenant
`QuarterlyReportJob` path: `InvoiceRepository.GetSummariesAsync`, then
`QuarterlyReportCsvWriter.WriteAsync`. Email sending is not included.

## Summary

- **Throughput:** about 115k to 375k rows/s per tenant, end to end. Plan with
  **100k rows/s**, to keep a margin for production network and load.
- **Run time at 4x volume:** a tenant with 250k rows per quarter today grows to
  1M rows, which takes about **3 s** measured and **10 s** at the planning rate.
  Run time is not the limit.
- **Batching threshold:** the 10 MB email limit is reached first, at about
  **214k rows** with typical customer names and **45.8k rows** with 200-character names.
  Batch by CSV size, not run time.
- **Problem found:** the report query scans the whole `Invoices` table for every
  tenant. Fixed with a covering index (see [Index finding](#index-finding)).

## Test setup

- SQL Server 2022 in Docker (`docker-compose.yml`), schema from the EF Core migrations.
- 2.9M fictitious invoice rows: 4 tenants, each with the same number of rows in
  Q2 2026 and Q3 2026, so the date filter has to skip the other quarter.
- Small .NET 10 console harness calling the real `InvoiceRepository` and
  `QuarterlyReportCsvWriter`. A new `DbContext` per run, like one Hangfire job.
  One warmup run, then 3 timed runs per tenant.
- Dev laptop, database on the same machine: no network hop.

## Results

| Test tenant (Q3 2026) | DB rows/s | DB + CSV rows/s | DB + CSV time | CSV size |
| --- | --- | --- | --- | --- |
| 50k rows, 20-char names | ~240k | ~185k | 0.27 s | 2.2 MB |
| 200k rows, 20-char names | 260k–660k | 180k–460k | 0.4–1.1 s | 9.1 MB |
| 1M rows, 20-char names | ~520k | ~375k | 2.6–2.8 s | 46.6 MB |
| 200k rows, 200-char names | ~160k | ~115k | 1.7–1.8 s | 43.7 MB |

The database fetch takes most of the time. Building the CSV in memory runs at
1.2M to 3.5M rows/s. Long customer names slow the fetch about 3x, because each
row carries up to 400 bytes of `nvarchar` data.

### CSV size and memory per row

| | 20-char name | 200-char name (column max) |
| --- | --- | --- |
| CSV bytes per row | 48.9 | 228.9 |
| Memory allocated while writing | ~340 B | ~1 KB |

CSV size is about `rows × (29 + customer name length)` bytes. At 1M rows the
writer allocates about 340 MB to 1 GB for a short time.

## Run time at 4x volume

Formula: `run time ≈ (4 × rows per tenant-quarter today) / rows per second`.

| Rows today | Rows at 4x | Measured rate | Planning rate (100k rows/s) |
| --- | --- | --- | --- |
| 12.5k | 50k | 0.3 s | 0.5 s |
| 50k | 200k | 0.5–1.1 s | 2 s |
| 250k | 1M | 2.7 s | 10 s |

Hangfire runs tenants in parallel, so a full quarterly run takes about
`total rows / (rows per second × worker count)`. The database is shared by all
workers, so it is the limit.

## Batching threshold

`MaxAttachmentBytes` is 10 MB (10,485,760 bytes). Mailjet rejects messages over
15 MB, and base64 adds about a third. A larger CSV is not emailed and is
recorded as failed with reason `too-large`.

`max rows per email ≈ MaxAttachmentBytes / (29 + average name length)`

| Names | Bytes per row | Max rows per email | Crosses the limit at 4x if today's rows exceed |
| --- | --- | --- | --- |
| 20 characters (typical) | 48.9 | ~214k | ~53.6k |
| 200 characters (worst case) | 228.9 | ~45.8k | ~11.4k |

Recommendation: split the report by size. Start a new part at about 80% of
`MaxAttachmentBytes` (8 MB). If a fixed row count is simpler, 40k rows per part
is safe for any name length.

## Index finding

The query plan for `GetSummariesAsync` is a **Clustered Index Scan + Sort** on
`PK_Invoices` for every tenant, even the 50k-row one. `IX_Invoices_TenantId_IssuedDate`
is not used, because it does not contain `CustomerName` or `Amount`. The cost of
every tenant's report therefore grows with the size of the whole table.

Tested with `INCLUDE (CustomerName, Amount)` added to that index:

| | Current index | Covering index |
| --- | --- | --- |
| Plan | Clustered Index Scan + Sort | Index Seek, no sort |
| Avg logical reads | ~56k pages | ~5k pages |
| 50k-row tenant, DB time | ~200 ms | ~95 ms |
| 1M-row tenant, DB time | ~1.9 s | 1.3–1.9 s |

Small tenants run about 2x faster. Large tenants barely change, because sending
the rows takes most of their time. The benefit grows as the table grows.

### Fix applied

Migration `20261104053026_AddInvoiceCoveringIndex` (2026-11-04) adds the
`INCLUDE (CustomerName, Amount)`. SQL Server cannot add included columns to an
existing index, so the migration drops and recreates it. On a large production
table this blocks writes to `Invoices` while it runs, so apply it off-peak.

Checked after the migration, with 1M fictitious rows (4 tenants of 250k) and one
tenant's Q3 2026 (about 126k rows):

| | Clustered index (forced) | Covering index |
| --- | --- | --- |
| Plan | Clustered Index Scan + Sort | Index Seek, ordered, no sort |
| Logical reads | 10,948 | 1,320 |

## Open items

1. ~~Add an EF Core migration for the covering index.~~ Done, see
   [Fix applied](#fix-applied).
2. Get the largest tenant-quarter row count from a production read replica,
   with the query below. Tested on dev, its CSV size estimate was within
   0.2 MB of the real files.
3. Replace the example rows above with the production count to get a specific
   4x target.
4. Batch reports by CSV size.

The query is read-only and returns totals only, no customer data. It scans the
whole `Invoices` table, so run it on a read replica or off-peak.

```sql
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 20
  TenantId,
  YEAR(IssuedDate)              AS [Year],
  DATEPART(quarter, IssuedDate) AS [Quarter],
  COUNT(*)                      AS [Rows],
  AVG(LEN(CustomerName))        AS AvgNameLen,
  MAX(LEN(CustomerName))        AS MaxNameLen,
  CAST(SUM(29 + LEN(CustomerName)) / 1048576.0 AS decimal(10,1)) AS EstCsvMB
FROM Invoices
WHERE IssuedDate >= DATEADD(year, -2, CAST(GETDATE() AS date))
GROUP BY TenantId, YEAR(IssuedDate), DATEPART(quarter, IssuedDate)
ORDER BY [Rows] DESC;
```

## Limits of this test

- Single laptop, no network between the app and the database. Production will be slower.
- Synthetic data with one customer name length per tenant. Real data mixes name lengths.
- Email sending (Mailjet upload, base64 encoding) was not timed.
