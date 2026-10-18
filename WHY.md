# WHY — the decisions behind this project, in my words

For the follow-up conversation. Each entry is: what I did, why, and what I would say if asked. The posting
asks for architecture ownership, code review of outsourced developers, integrations, reliability and
rollback, Azure, and AI-assisted tooling. Every choice below maps to one of those.

## The shape of the solution

**Two projects plus tests, not a layered onion.** `Domain` holds the rules and nothing else; `Api` holds
HTTP, validation and persistence. A four-hour project with a repository layer, MediatR and AutoMapper would
tell you I reach for ceremony. What I want you to see is that the rules are isolated enough to unit-test
without a database, and that nothing else is abstracted until there is a second implementation.
_If asked "why no repository?"_: EF Core's `DbContext` already is a unit of work and a repository. Wrapping it
hides the query and makes the integration tests lie.

**Minimal API with typed results.** `Results<Ok<T>, NotFound, ProblemHttpResult>` makes the compiler and the
OpenAPI page agree on every status code an endpoint can return. Controllers would be fine too; the brief did
not need attribute routing or filters.

**Rules return `Result`, constructors throw.** A claim that cannot be submitted is an expected outcome, so it
is a value, not an exception, and the endpoint turns it into a 409 with the rule in plain words. A negative
amount reaching the constructor is a programming error, so that throws.

## Nothing silently dropped

Billing operations live on reconciliation. An import that says "47 rows loaded" when the file had 50 is a
problem that surfaces weeks later as missing revenue. So the import returns read, accepted, rejected and
duplicate counts, and a test holds the API to read = accepted + rejected + duplicates. Every rejected line is
stored with its line number, raw text and reason, so an analyst can fix the file without asking engineering
what happened. Re-importing the same file is safe: known references are counted as duplicates, never
re-inserted and never rejected.

## Concurrency

Two analysts can open the same claim. Without a concurrency token the last save silently wins and one
person's work disappears. The entity carries a `Version` that EF Core checks on every update; the stale
writer gets a 409 that says "this record was changed by someone else, reload it", and a test proves the race.
_If asked why not pessimistic locking_: these are short web requests; a conflict is rare and the user can
retry in a second. Locks would need timeouts and cleanup for the case where a browser tab is left open.

## Integrations: retries, then a visible failure, then a retry button

Outbound calls go through a typed `HttpClient` with .NET's standard resilience handler: retries with
exponential backoff and jitter, a per-attempt timeout, a total timeout and a circuit breaker. The numbers
live in configuration, not code. That handles the transient case: a payer gateway that blips for two seconds.

The case that matters more is the partner that is down for an hour. Retrying in a loop just makes our
service slow. So every outbound call is written to a table **before** it is attempted, with its status,
attempt count and last error. A failed call is a row you can list (`GET /outbound?status=Failed`) and retry
(`POST /outbound/{id}/retry`), and the Error-level log line with the call id is the alert hook: in Azure that
line is what an Application Insights alert rule fires on. Nothing is lost if the process dies mid-call either;
the row is still Pending.

The integration tests prove both paths against a scripted fake of the partner: three 503s then a 200
succeeds on the first dispatch, and four 500s leave a Failed row that a manual retry then clears. Retry
delays are shortened to 10 ms in tests through the same configuration a deployment would use.

## Errors people see

Validation failures are 400s that name the field in plain words. Rule failures are 409s that state the rule.
Anything unexpected goes through one global handler that returns "Something went wrong on our side, quote
reference X" and writes one structured error log line with that reference, the method and the path. Stack
traces never leave the server. A test feeds the handler an exception whose message contains a method name and
asserts the response does not.

## Logs

Structured JSON outside Development, so any shipper (Application Insights, Datadog, a file) gets fields,
not prose. Request logging records method, path, status and duration only. Bodies and headers are never
logged: in a billing system they contain protected health information, and a log is the easiest place to
leak it.

## Tests

Domain rules are tested with no host. Endpoints are tested over real HTTP through `WebApplicationFactory`
against a SQLite in-memory database, with only the external partner faked. I do not test against mocks of
my own code: a test that passes against a fake `DbContext` tells you nothing about the SQL. Test names are
sentences so the test list reads as the specification.

## Build hygiene a reviewer of outsourced code cares about

Warnings are errors and the analyzers are on, so a pull request cannot introduce a nullable slip or an
unawaited task without failing CI. Package versions are in one file (`Directory.Packages.props`), so an
upgrade is one diff line. `dotnet format` runs in CI, which keeps review comments about design rather than
whitespace. The PR template asks for the user-visible change, how to verify it, and how to roll it back,
because those are the three things I need from a contractor's PR before I read the code.

## .NET 10, SQLite, Docker

.NET 10 is the current long-term-support release; the runtime is one line in `Directory.Build.props` if the
team is on 8. SQLite keeps the project runnable with no setup; the only SQLite-specific code is one value
converter in the `DbContext` with a comment saying to delete it on SQL Server. The Dockerfile is a standard
multi-stage build running as a non-root user with a health endpoint, which is what Azure Container Apps or
App Service would run.

## How I used AI

I planned the design, decided the scope and the assumptions, and reviewed every diff. Claude Code typed
most of the code and all of the boilerplate, and I ran it against a written review checklist after each
slice. The commit history shows the increments. I would run a team the same way: the tool makes the typing
cheap, so the leverage moves to deciding what to build, reading what was built, and holding it to a
standard. The posting mentions AI-assisted document processing; the same discipline applies there, with the
added rule that a model's output is an input to validation, never a result.

## Scalability, said honestly

An internal billing tool has tens of users, not millions, so I did not build for scale; I built so that
scaling is a sequence of known steps rather than a rewrite. The API is stateless and runs behind any load
balancer. Lists are paged with a configured cap so nothing returns an unbounded result. The upload has a
configured size limit with a message that tells the user what to do. The outbound-call table is already an
outbox, so moving dispatch to a queue worker is a deployment change, not a design change. And the first
real step, SQLite to Azure SQL, is a connection string. *If asked "would this handle a million claims?"*:
the database would, once it is Azure SQL with the indexes that are already declared; the import path would
need to move to a worker first, and the README says so.

## Audit trail

In healthcare billing, "who changed this claim and when" is a question from an auditor, not a developer.
Every state change writes an audit row with the actor, the time, and the before and after values, in the
same `SaveChanges` as the change itself, so there can never be a change without its record. The actor is
the authenticated user once authentication exists; until then it is a header, and the code says so. The
history is readable from the API.

## What I would do next with a day

- EF migrations instead of create-on-boot, run as a release step with a rollback script.
- Authentication and per-user audit on every state change (who, when, from what to what).
- A scheduled retry of Failed outbound calls with a cap, and an alert when the cap is hit.
- Pagination on list endpoints.
- Load the import asynchronously for large files, with the summary available by id (the endpoint already
  returns one).
