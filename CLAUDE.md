# Take-home project — working agreement

Four-hour timed assignment for Alteva RCM (healthcare revenue cycle management, C#/.NET, Azure).
Reviewers are a hands-on technical lead and QA. They will read the README first, run the tests
second, and read the code third. Optimise for that order.

## Commands

```bash
dotnet build -warnaserror        # warnings are errors (Directory.Build.props)
dotnet test                      # xUnit v3 on the Microsoft Testing Platform (global.json)
dotnet format --verify-no-changes
dotnet run --project src/Crawler.Api   # API page at the printed URL + /scalar, health at /health
docker build -t takehome . && docker run --rm -p 8080:8080 takehome
```

Definition of done for ANY change: `dotnet build -warnaserror && dotnet test` both green, fresh output.
Never claim a step is done without having run it.

## Layout

- `src/Crawler.Domain` — pure C#: entities, rules, parsers, `Result`. No EF, no ASP.NET, no IO. Unit-tested directly.
- `src/Crawler.Api` — Minimal API, one folder per feature:
  - `Claims/` — sample aggregate with a state machine, an optimistic-concurrency `Version`, an audit row per
    state change (`Data/AuditEntry`, same transaction) and a `/history` endpoint.
  - `FileImports/` — CSV upload; every row is accepted, rejected-with-reason, or counted as a duplicate; the
    summary ties out (read = accepted + rejected + duplicates) and rejections are stored.
  - `Outbound/` — a typed `HttpClient` with the standard resilience handler (retry, timeout, circuit breaker)
    and an `OutboundCall` row per attempt so a failed integration is visible and retryable from the API.
    Its list endpoint shows the paging pattern (`Paging.cs`, cap from configuration).
  - `Errors/GlobalExceptionHandler` — every unhandled exception becomes Problem Details with a plain-English
    message and a reference id; concurrency conflicts become 409. Stack traces never leave the server.
  - `Data/AppDbContext` — EF Core + SQLite; timestamps stored as UTC ticks (SQLite cannot sort DateTimeOffset).
  - `Program.cs` — DI wiring, JSON logs outside Development, request logging without bodies, health check,
    OpenAPI + Scalar page.
- `tests/Crawler.Domain.Tests` — rule tests, no host.
- `tests/Crawler.Api.Tests` — `WebApplicationFactory<Program>` over real HTTP against SQLite in-memory, the
  external system replaced by `FakePayerHandler`, retry delays shortened to 10 ms.
- `Dockerfile`, `.github/` — multi-stage image, CI (build, format, test), PR template.

The sample features exist to be copied from, not kept. **Before the final commit, delete every feature the
brief did not ask for** (including its tests), so the reviewer sees only what was built for them.

## How we work the clock (4 hours, hard stop)

Where the minutes actually go: reading the brief, waiting on build/test cycles, debugging a stuck test,
and David reading diffs. Not typing. So:

1. **0:00–0:05 — setup and prune.** `setup.sh`, then delete every kit feature the brief does not need
   (Claims, FileImports, Outbound and their tests are samples). Fewer files = faster builds, faster
   reads, nothing to clean up later. Keep: Domain project, `Result`, `GlobalExceptionHandler`,
   `AppDbContext` shell, `ApiFactory`, `Program.cs`, Dockerfile, CI.
2. **0:05–0:25 — brief, no code.** Acceptance criteria as a checklist, ambiguities with a recommended
   reading, slice order, stop. David decides. README "What I built" + "Assumptions" written before source.
3. **0:25–3:15 — slices.** Each slice = rule + its unit test, endpoint + one happy-path and one failure
   integration test, `dotnet build && dotnet test` green, one commit. Batch edits within a slice and run
   the build ONCE per slice, not per file. Aim for 4 slices of ~40 minutes; the first one is always slowest.
4. **Review twice, not every slice**: after slice 1 (catches a pattern mistake before it is copied) and at
   3:15. A REVIEW.md pass costs 5–8 minutes.
5. **3:15–3:45 — README, time log, final review fixes.** No new features after 3:15 whatever the state.
6. **3:45–4:00 — prove it from a fresh clone**: `dotnet test`, `dotnet run`, open `/scalar`, one bad request.
   Docker build only here and only if there is time; it is 3–5 minutes and the brief rarely asks.

Tests are not where the time goes. Rules for keeping them cheap:

- One unit test per rule, one integration test per endpoint for the happy path, one for the main failure.
  No tests for getters, mapping, or framework behaviour.
- **Ten-minute rule**: a test that is still red after ten minutes gets simplified or deleted, and the
  gap goes in README "Out of scope". A red test at submission costs more than a missing one.
- Never chase a flaky test; rerun once, then delete it.
- Ambiguity: pick the simplest reading, implement it, write it down. Never build both.

## How David reviews: by explanation, not by editor

David does NOT open an editor during the four hours. He reviews from your explanation. So after every
slice, before asking what to do next, give him:

1. **What changed, in plain English**, three to six sentences: the behaviour added, the rule enforced,
   the error a user now sees. No file-by-file listing, no code in the explanation.
2. **The one decision he must own**: the design choice in this slice a reviewer could challenge, in one
   sentence, with the reason. If there is none, say so.
3. **The proof**: the test names that now pass and the one request he could send on the API page to see it.
4. **Anything you were unsure about** and the reading you chose.

Keep it under 150 words. If he asks "show me", then show the specific code, not the whole diff. Never
proceed to the next slice until he has said go.

## Style — what the reviewer should see

- Domain rules live in the entity and return `Result`; constructors guard with `ArgumentException`.
- Endpoints are thin: validate → call domain → persist → map. No business logic in endpoints.
- `TypedResults` with explicit union return types so the OpenAPI page documents every status code.
- `TimeProvider` injected; never `DateTime.Now`. Money is `decimal`. Dates of service are `DateOnly`.
- 400 ValidationProblem naming the field · 404 · 409 Problem with the rule in plain words · 500 only via the
  global handler.
- Every number that could change (deadlines, limits, retry counts) is configuration with a default.
- Logs are structured with ids and never contain PHI (names, DOB, member ids, payloads).
- Tests are named as sentences: `Submit_twice_returns_409_with_a_readable_reason`. One behaviour per test.
- No new NuGet package without a one-line reason in README. Versions live in `Directory.Packages.props`.
- No interfaces with one implementation, no repository over EF, no MediatR, no AutoMapper.
- Async with `CancellationToken` threaded through. `sealed` by default; file-scoped namespaces.

## Things that cost points in a timed take-home

- A README that does not say how to run it in two commands.
- Tests that pass against a fake instead of the real pipeline.
- Catching `Exception` and returning 500 with no detail, or leaking a stack trace.
- Half-built stretch features, unused kit features, commented-out code, leftover template files.
