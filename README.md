# Crawl Jobs

A small job-based web crawler: submit a website address, a worker crawls it asynchronously through a message
queue, every page and link is stored in Postgres, and a React UI shows progress, the page tree with each
page's Domain Link Ratio, and the history of past jobs.

Built in a four-hour window. The sections at the end say what I built first, what I cut, and what I would do next.

## Run it

Requires Docker. One command brings up five containers (Postgres, RabbitMQ, API, worker, web):

```bash
docker compose up --build
```

| What                                | Where                                                                                               |
| ----------------------------------- | --------------------------------------------------------------------------------------------------- |
| UI                                  | http://localhost:3000                                                                               |
| API + interactive docs              | http://localhost:8080/swagger                                                                       |
| API health (checks DB and broker)   | http://localhost:8080/health                                                                        |
| Worker health                       | shown by `docker compose ps` (checked inside the container; no host port, so workers can be scaled) |
| RabbitMQ management (guest / guest) | http://localhost:15672                                                                              |
| Postgres                            | localhost:5433 (crawler / crawler)                                                                  |

Try it: type `quotes.toscrape.com` into the UI with depth 1 (no scheme needed; https is assumed). It finishes
in about ten seconds. More workers: `docker compose up --scale worker=3`; nothing else changes (see "Scaling").

Run the tests (no Docker needed; they use SQLite in memory and a fake internet):

```bash
dotnet test
```

Local development without containers: `docker compose up db broker`, then `dotnet run --project src/Crawler.Api`,
`dotnet run --project src/Crawler.Worker`, and `npm run dev` in `src/web` (the dev server proxies `/api`).

## How it works

```
 browser ──► web (React, static files)       its own address, as a CDN would be
    │
    └──► api  ──writes──►  Postgres  ◄──writes── worker
          │                                        ▲
          └── publishes "fetch page X" ──► RabbitMQ ┘   one message per page
```

- **API** creates jobs and answers questions. It never crawls. Creating a job writes the job row and the
  starting page row (status `Queued`), then publishes one message.
- **Worker** takes one message, fetches that page, extracts links, computes the ratio, and in one database
  transaction records the page, its links, and a `Queued` row for each newly discovered same-site page.
  Then it publishes one message per new page and asks: does this job have any page still `Queued`? If not,
  the job is complete.
- **The queue holds work, not state.** The database is the only record. Progress, the tree and history are
  all read from the three tables.

### Code layout

| Project                      | Role                                                                                                                                                                                        |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/Crawler.Domain`         | The rules: URL normalization, same-site scope, link extraction, ratio, job/page state. No database, no network, no web. Unit-tested in isolation.                                           |
| `src/Crawler.Infrastructure` | Postgres (EF Core) and RabbitMQ: table mapping, queue topology, the message contract, shared host defaults (JSON logs, health checks). Shared by API and worker so they can never disagree. |
| `src/Crawler.Api`            | REST endpoints, validation, request/response contracts. Thin.                                                                                                                               |
| `src/Crawler.Worker`         | Fetching, redirect handling, the page processor, the queue consumer. Thin; borrows the rules.                                                                                               |
| `src/web` | React + Vite + TypeScript, one page: history first, a New crawl form in place, the selected job beside it with live progress and the tree. Structure: `api/` (client + types), `hooks/` (`useJob` live stream with polling fallback, `useHistory`, `useTree`), `components/`, `pages/Dashboard`, `lib/format`. Plain CSS with design tokens, no UI framework. |
| `tests/*`                    | 83 tests: rules; API over real HTTP against SQLite; the worker's whole page step against fixture HTML with a fake internet and an in-memory queue.                                          |

Dependencies point one way: Domain ← Infrastructure ← API / Worker. The crawl logic can be tested, and
replaced, without starting anything.

## API

| Method | Path                           | What                                                                                                       |
| ------ | ------------------------------ | ---------------------------------------------------------------------------------------------------------- |
| `POST` | `/api/jobs`                    | `{ "url": "https://…", "maxDepth": 2, "maxPages": 200 }` → `202 { "jobId" }`. Depth and page cap optional. |
| `GET`  | `/api/jobs/{id}`               | Status, timestamps, failure reason, progress counts (discovered / fetched / failed / skipped / queued).    |
| `GET`  | `/api/jobs/{id}/tree`          | The page tree: each node has url, depth, status, ratio, link counts, children.                             |
| `GET`  | `/api/jobs?page=1&pageSize=20` | History, newest first, paged (cap in config).                                                              |
| `POST` | `/api/jobs/{id}/cancel`        | Cancel. Remaining pages are skipped; nothing more is fetched.                                              |

Errors follow Problem Details: `400` names the field and says what to change; `404`; `409` states the rule
("A Canceled job cannot be canceled"); `500` carries a reference id and never a stack trace.

### The UI

One page. The job list is the first thing on screen, newest first, refreshing on its own while anything is
running; **New crawl** opens the form in place; the selected job sits beside the list with its status,
timings, live progress bar and, once final, the page tree with each page's ratio drawn as a small bar. The
URL carries the selection (`/jobs/{id}`), so a job can be linked to. Loading, empty and error states are
explicit. The brief's three screens (start, job details, history) are the three regions of this one page.

### Live progress

`GET /api/jobs/{id}/events` streams the job summary as Server-Sent Events: the current summary on connect, a
line whenever it changes, then `event: done` once the job is final and nothing is queued. The job page opens
one stream and the browser does not poll.

How a change reaches the stream: after the worker settles a page (and after cancel), it publishes "job X
changed" to a fanout exchange, `crawl.progress`. The announcement carries no data, only "go look"; the
database stays the only source of truth, so an announcement delivered twice costs one harmless extra read.
Each API instance binds its own exclusive, auto-delete queue to that fanout, so several API instances scale
out with nothing to coordinate, and the queue vanishes with the instance. On a notification the API reads the
summary **once** per job, however many streams are open for it, and pushes it to each; with no open stream the
notification is dropped. Every stream also re-reads on its own every 10 s in case an announcement was lost. A
failure to announce is logged and never fails the page.

If the stream cannot be opened or drops, the page falls back to polling every two seconds, which stays in
the code as the safety net. A production gateway must not buffer this endpoint (the response sets
`X-Accel-Buffering: no`).

## Crawling rules

- **Normalization** (`UrlNormalizer`): resolve relative to the page it was found on; http(s) only; drop the
  fragment; lowercase scheme and host; drop default ports; strip a trailing slash except on the root; keep
  path case and the query string as written. Two spellings of one page produce one key, and that key is the
  uniqueness rule in the database.
- **Same site** (`SiteScope`): same host, ignoring a leading `www.` and ignoring the scheme. Subdomains are
  outside by default (the brief defines the domain as the host); `Crawl:IncludeSubdomains` flips that.
  A same-site link spelled with the other scheme or with/without `www.` is rewritten to the job's own scheme
  and host before it becomes a page, so one page is one row however it was linked. The edge keeps the link
  as written.
- **Depth**: the starting page is depth 0; default max depth 2 fetches three levels.
- **Links counted** for the ratio: every `<a href>`, normalized, de-duplicated; `mailto:` / `tel:` /
  `javascript:` ignored; links to the page itself ignored. Every outgoing link is stored as an edge,
  including off-site ones, so a ratio can be audited.
- **Links followed**: only same-site links, within depth, under the page cap. Off-site pages are counted,
  never fetched. HTML only: a link to a PDF is counted, requested (headers only), and recorded as `Skipped`
  when the reply is not HTML. Bodies are capped at 2 MB.
- **Redirects**: followed by the fetcher itself, up to 5 hops, final address recorded. The .NET client
  refuses https→http redirects on principle; for a crawler the same host over http is the same site, so we
  follow it. A redirect that leaves the site records the page as `Skipped`, not crawled.
- **Domain Link Ratio** = same-site links / all links; 0 when a page has no links.

## Event-driven design

### Message schema

One message type, `CrawlPageMessage`, JSON on RabbitMQ, persistent:

```json
{
  "jobId": "…",
  "pageId": "…",
  "url": "https://example.com/about",
  "depth": 1,
  "attempt": 1
}
```

Properties: `messageId` = `pageId:attempt`, `correlationId` = `jobId`, `contentType` = `application/json`.
Publishing uses broker confirms: "published" means the broker has taken responsibility for the message.
It carries ids, not state. One message per page, not per job: several workers can share a job, progress is
real, and the idempotency requirement means something.

### Idempotency

- A page row exists, status `Queued`, **before** its message is published. Pages are unique on
  `(JobId, Url)`; edges are unique on `(JobId, ParentPageId, ChildUrl)`. The database refuses duplicates.
- The worker checks the page's status first. If it is no longer `Queued`, the message is a duplicate: the
  page is not fetched again, no rows are written, and the worker only re-publishes any of that page's
  children that are still `Queued`. Then it acks.
- Page, edges and newly discovered child rows are written in **one transaction**; child messages are
  published only after it commits, so a message can never refer to a page that is not there yet.
- Two workers discovering the same child in the same instant: the unique index rejects the second insert;
  that message is retried and the second pass finds the rows already present.
- **Sweeper.** A worker that dies after committing a page but before publishing its children would leave
  those children `Queued` with no message in flight. Every 60 s each worker re-publishes any page that has
  had no message sent for more than 120 s on a job that is still running, stamping the page first so two
  workers sweeping at once do not both re-send it. A page that was merely slow gets one duplicate message,
  which is ignored by design. Test:
  `PageProcessorTests.The_sweeper_republishes_pages_left_queued_too_long_on_running_jobs_only`.
- The page cap is a counter on the job row raised only by one conditional `UPDATE … SET PagesDiscovered =
PagesDiscovered + n WHERE PagesDiscovered + n <= MaxPages`, inside the same transaction as the new rows, so
  several workers can reserve at once and the sum can never exceed the cap. The job's status is a concurrency
  token, so a worker holding a stale copy can never overwrite a canceled or failed job.

**The rule behind all of it: every saved job has either a recoverable next step or a recorded final outcome.**
A parent page is acked only after its children are published, so if the broker goes away while children are
being scheduled, the parent is redelivered on recovery and the duplicate path re-publishes any child still
`Queued` (`RescheduleQueuedChildrenAsync`); that case is not a stall. A worker that dies after the ack is
covered by the sweeper. A broker outage at the moment a job is created marks the job `Failed` with the reason.

Tests: `PageProcessorTests.Delivering_the_same_message_twice_creates_no_duplicate_rows`,
`RoutingTests.An_infrastructure_outage_waits_without_spending_an_attempt_however_many_times`.

### Retry policy

Two different failures, handled differently:

| Failure                                                                                                               | Who retries                   | Policy                                                                                                                                                                                                                                                                                            | Then                                                                                                           |
| --------------------------------------------------------------------------------------------------------------------- | ----------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| The target website is slow or down (timeout on headers or body, 5xx, 408, 429, connection refused)                    | HTTP client (`FetchPipeline`) | 10 s per attempt, 2 retries with backoff and jitter, a total timeout, a separate clock on the body read. No circuit breaker: one would be shared by every site, so a single dead site would stop every job; a per-host breaker is next                                                            | Page recorded as `Failed` with the reason; the job continues. If the starting page fails, the job is `Failed`. |
| Our own infrastructure **unreachable** (Postgres or RabbitMQ down: connection refused, socket timeout, broker closed) | Message pipeline              | Republished to `crawl.pages.retry` with the **same** attempt number, so the message waits at the 5 s cadence until the outage ends. An outage never spends the attempt budget and never dead-letters healthy pages. If the broker itself is down, the message is handed back to the queue unacked |
| A one-off failure in our own code (unique-index race between workers, concurrency conflict, unexpected exception)     | Message pipeline              | Republished to `crawl.pages.retry` with `attempt + 1`; the retry queue holds it 5 s (`x-message-ttl`) then dead-letters it back onto the work exchange; up to 3 attempts                                                                                                                          | Page marked `Failed` ("gave up after 3 attempts"), job completion re-checked, message to the dead-letter queue |

"Transient" means: it may succeed if tried again without anyone changing anything. A 404 on a page is not
transient; a database connection reset is.

### Dead-letter queue

`crawl.pages.dead`, bound to the `crawl.dead` exchange. Receives:

- **Poison** messages immediately: unreadable JSON, missing ids, a job or page id that does not exist.
- **Exhausted** messages: transient failures after 3 attempts. Before the message is dead-lettered the page
  is marked `Failed` with the reason, so the job still reaches a final state and the UI shows what happened.

Nothing in the DLQ is retried automatically. It is visible in the RabbitMQ management UI, and every
rejection is logged at Error level with `JobId`, `PageId` and the reason: that log line is the alert hook.

## SQL model and performance thinking

Three tables. Every timestamp is UTC.

| Table   | Key columns                                                                                                                                               | Why                                                                           |
| ------- | --------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| `jobs`  | `Id`, `InputUrl`, `StartHost`, `MaxDepth`, `MaxPages`, `Status`, `PagesDiscovered` (cap counter, concurrency token), timestamps, `FailureReason`          | A job owns its own limits; changing a default later never changes an old job. |
| `pages` | `Id`, `JobId`, `Url` (normalized), `Depth`, `ParentPageId` (first discovery), `Status`, `FinalUrl`, `HttpStatus`, link counts, `DomainLinkRatio`, `Error` | One row per page per job. Written at discovery, completed at fetch.           |
| `links` | `Id`, `JobId`, `ParentPageId`, `ChildUrl`, `IsInternal`                                                                                                   | Every outgoing link, the evidence behind each ratio.                          |

Indexes follow the questions the system asks constantly: `jobs(CreatedAt desc)` for history;
`pages(JobId, Url) unique` for idempotency and the duplicate check; `pages(JobId, Status)` for "any page
still queued?" and progress counts; `pages(JobId, ParentPageId)` for the tree; `links(JobId, ParentPageId,
ChildUrl) unique` for edge idempotency.

Progress is a `GROUP BY status` over one job's pages, not a counter that can drift. The tree is one query
for a job's pages (bounded by the page cap) built in memory, which beats a recursive query at this size.
History lists jobs with one grouped query for the counts of the jobs on that page, never one query per row.
Everything a user reads is indexed by `JobId` first.

Schema is created on boot with `EnsureCreated` (take-home scale). Production would run EF migrations as a
release step; see next steps.

## Scaling

The API is stateless and the worker is a plain consumer, so `docker compose up --scale worker=3` runs three
workers on the same queue with no code change. What makes that safe: the unique keys on pages and edges, the
conditional cap reservation, the status concurrency token on jobs, and the sweeper stamping pages before it
re-sends them. Within one worker, `RabbitMq:Prefetch` messages are processed concurrently. All five containers
restart automatically unless stopped.

Measured: one job of 120 pages (depth 2) with `--scale worker=3` completed in 12 s; the three workers fetched
41 / 41 / 39 pages, every page exactly once (120 rows, 120 distinct addresses), no retries needed, nothing
dead-lettered. (Before the atomic cap reservation the same job took 72 s with 14 collisions retried.)

## Observability

- Structured JSON logs outside Development. Every worker log line carries `JobId`, `PageId` and `Attempt`
  through a logging scope; the API logs job creation with the job id. Request logs record method, path,
  status and duration, never bodies.
- Health: the API checks the database and the broker; the worker exposes its own `/health`. Compose uses
  these to start services in the right order (API after DB and broker; worker after the API has created the
  schema).

## Configuration

| Key                                                                            | Default                          | Meaning                                                                                         |
| ------------------------------------------------------------------------------ | -------------------------------- | ----------------------------------------------------------------------------------------------- |
| `Crawl:DefaultMaxDepth` / `MaxDepthLimit`                                      | 2 / 5                            | Default and ceiling for a job's depth                                                           |
| `Crawl:DefaultMaxPages` / `MaxPagesLimit`                                      | 200 / 1000                       | Default and ceiling for a job's page cap                                                        |
| `Crawl:IncludeSubdomains`                                                      | false                            | Treat `blog.example.com` as inside `example.com`                                                |
| `Crawl:FetchTimeoutSeconds` / `FetchRetries` / `MaxRedirects` / `MaxBodyBytes` | 10 / 2 / 5 / 2 MB                | Fetching limits                                                                                 |
| `RabbitMq:RetryDelayMs` / `MaxAttempts` / `Prefetch`                           | 5000 / 3 / 8                     | Message retry policy; `Prefetch` is also the number of messages one worker handles concurrently |
| `Crawl:SweepIntervalSeconds` / `StuckAfterSeconds`                             | 60 / 120                         | How often, and after how long, a `Queued` page with no message in flight is re-published        |
| `Cors:Origins`                                                                 | `http://localhost:3000`, `:5173` | Addresses the UI may call from                                                                  |
| `Paging:DefaultPageSize` / `MaxPageSize`                                       | 20 / 100                         | History paging                                                                                  |

## What I implemented first, and why

1. **The rules, with their tests, before anything else**: normalization, same-site scope, link extraction,
   the ratio. They are the correctness core, the cheapest thing to get exactly right, and the tests caught a
   real bug before any page was fetched (a relative link on a page ending in `/` resolved from the wrong
   folder because I had tidied the page's own address before using it as the base).
2. **The data model and the message, together.** One message per page and a page row that exists before its
   message is what makes idempotency a property of the design rather than a check bolted on.
3. **The worker, then a live run** against real Postgres and RabbitMQ before writing the UI. The live run
   exposed the https→http redirect refusal; fixing it in the fetcher and adding two tests cost ten minutes
   and would have cost far more discovered through the UI.
4. **The UI last**, against a working back end, so it was built against real responses.

## What I cut, and why

- **Kafka**: RabbitMQ has native dead-lettering and TTL-based retry; the brief's requirements map onto it
  directly with no extra code.
- **Following off-site links**: unbounded and discourteous. Counted, recorded as edges, never fetched.
- **Subdomains as same-site**: the brief defines the domain as the host. Left as a switch, off by default.
- **An outbox**: if the broker is down when a job is created, the job is marked `Failed` with the reason
  rather than left `Pending` forever. The sweeper covers a lost message after creation; the full outbox
  pattern, which also covers creation, is next.
- **Tests against real Postgres and RabbitMQ**: replaced by a fixture test with the two outside dependencies
  faked, plus the compose run as the real-world proof. Testcontainers would be the next step.
- **Authentication, rate limiting, robots.txt**: nothing in the brief needs them; each is a line in next steps.
- **Serving the UI from the API**: deliberately not. Static files belong at the edge; the API stays close to
  the data. Two addresses cost one CORS line.

## What I would do next

- **Outbox.** Write the message into the database in the same transaction as the job or page row and relay
  it, so even a broker outage at job creation cannot lose a job. The sweeper already recovers a lost message
  after creation; the outbox removes the up-to-120-second delay and the `Failed` outcome at creation.
- Scheduled reprocessing of the dead-letter queue with a cap, and an alert rule on the Error log line.
- EF migrations as a release step; Testcontainers for Postgres and RabbitMQ in CI.
- The web container replaced by a CDN; the API behind a gateway; the progress stream behind the same gateway with
  buffering off.
- Politeness: robots.txt, a per-host delay, a shared rate limit across workers.
- **Per-site body limit.** The 2 MB page limit is a safety rule for the unknown internet, and some legitimate
  sites exceed it (one tested home page is 13 MB of HTML and takes ~48 s to download; today it is recorded as
  skipped with that reason and the crawl stops beneath it). Next: an optional per-site override, granted when
  the site is verified legitimate and its other pages crawl cleanly, still under a hard ceiling and the
  body-read timeout. Deliberately not a blanket increase, which would only move the problem to the next
  bigger site.
- **AI-assisted operations decisions.** Approving a per-site override, judging whether a skipped site is
  legitimate, or deciding whether a dead-lettered message is worth replaying are judgement calls a human
  makes today. Next: an AI step that researches the site on the web (reputation, ownership, whether its
  other pages crawled cleanly) and recommends, inside fixed guard rails: a hard ceiling it cannot raise, a
  logged reason for every recommendation, and a human review only for the cases it cannot decide with
  confidence. The aim is that most of these decisions need no person at all.

## Assumptions and known limitations

- The "starting domain" is the host of the URL you typed, with `www.` ignored; `blog.example.com` is outside.
- http and https on the same host are the same site. Query-string order and path capitalisation are kept as
  written, so `?a=1&b=2` and `?b=2&a=1` are two pages.
- Trailing slashes are stripped, so sites that insist on them answer with a redirect, which we follow; it
  costs one extra round trip per page on such sites.
- Non-HTML links are requested to learn their type (headers only; the body is never read). A HEAD request
  first would save a round trip on some servers; not done.
- Schema is created on boot, not migrated: a database created by an older build is not upgraded. After
  pulling a version that changes a table, reset with `docker compose down -v`. EF migrations as a release
  step are the fix (next steps). A job's pages and links are deleted with the job (cascade).
- The page cap counts discovered pages, including ones that later fail or are skipped.
- No authentication; the API trusts its network.
- The UI's TypeScript types mirror the API contracts by hand; a generator would keep them in lockstep.
