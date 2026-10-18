# <Assignment name>

<One paragraph: what this service does, in plain English, for a reviewer who has not read the brief.>

## Run it

```bash
dotnet test                              # unit + integration tests
dotnet run --project src/Crawler.Api    # then open the printed URL + /scalar
```

Requires the .NET 10 SDK (`global.json`). SQLite file `crawler.db` is created on first run; delete it to reset.
Or: `docker build -t takehome . && docker run --rm -p 8080:8080 takehome` then <http://localhost:8080/scalar>.

## What I built

- [ ] <acceptance criterion 1 from the brief>
- [ ] <acceptance criterion 2>

## Design in 60 seconds

- **Domain** (`src/Crawler.Domain`): rules on the entity, `Result` instead of exceptions for expected failures,
  no framework references, tested without a host.
- **API** (`src/Crawler.Api`): Minimal API, one folder per feature, validation at the edge, EF Core + SQLite.
- **Nothing silently dropped**: an import reports read / accepted / rejected / duplicate counts that tie out,
  and keeps every rejected line with its reason.
- **Concurrency**: a version token on records two people could edit; the stale writer gets a 409, not a
  silent overwrite.
- **Integrations**: outbound calls retry with backoff and time out; what still fails is a row you can see and
  retry.
- **Errors**: users see plain English and a reference id; the log line carries the detail.
- **Tests**: domain rules without a host; endpoints over real HTTP against SQLite in-memory with the external
  system faked.

## Assumptions

- <Each ambiguity in the brief and the reading I chose, one line each.>

## Configuration

| Key                                 | Default                   | Meaning                                 |
| ----------------------------------- | ------------------------- | --------------------------------------- |
| `ConnectionStrings:Default`         | `Data Source=crawler.db` | SQLite file                             |
| `Payer:BaseUrl`                     | `http://localhost:9999/`  | External system base address            |
| `Resilience:Retry:MaxRetryAttempts` | 3                         | Automatic retries on transient failures |

## Scaling and operations

The service is stateless, so it scales out behind a load balancer (App Service or Container Apps) with no
code change. What changes at volume, and in what order:

1. **Database**: SQLite → Azure SQL. One connection string and deleting one value converter. Indexes already
   exist on the lookup columns.
2. **Large imports**: today the upload is parsed in the request. Past a few MB (configurable limit, default
   10 MB) the file should land in blob storage and a queue-triggered worker should parse it; the summary
   endpoint already returns a result by id, so the API shape does not change.
3. **Outbound calls**: today they are dispatched in the request after being recorded. The recorded row is
   the outbox; a worker draining it from Service Bus removes the partner's latency from the user's request
   and adds scheduled retries for Failed rows.
4. **Lists** are paged with a configured cap, so no endpoint can return an unbounded result.
5. **Observability**: structured JSON logs with a reference id on every error; the health endpoint checks
   the database. Application Insights is one package and one line; the alert rule is "any Error-level log".
6. **Audit**: every state change writes who, when, from and to in the same transaction as the change.

Measured before optimised: this is an internal tool for a billing team, not a public API. The first
scaling step is the database; nothing else should move until load says so.

## Out of scope / next steps

- <Auth, pagination, EF migrations, Azure deployment, alerting… whatever was deliberately left out.>

## Time log

| Block     | What                              |
| --------- | --------------------------------- |
| 0:00–0:20 | Read brief, plan, README skeleton |
| 0:20–1:30 | Vertical slice: …                 |
| …         | …                                 |
