# Summary

## What I implemented

A job-based crawler as two .NET 8 services and a React UI, run with one `docker compose up`:

- **API**: create a job, read its status and progress, read the page tree with each page's Domain Link
  Ratio, paged history newest-first, cancel. Problem Details errors throughout.
- **Worker**: consumes one message per page from RabbitMQ; fetches with timeouts, retries and explicit
  redirect handling; extracts and normalizes links; computes the ratio; writes page, edges and newly
  discovered pages in one transaction; publishes children after the commit; detects job completion.
- **Event-driven core**: durable queue, delayed retry queue, dead-letter queue; idempotency from the data
  model (page rows exist before their messages; unique keys on pages and edges; status check before any
  fetch); a message that exhausts its retries still leaves the page `Failed` and the job in a final state.
- **Postgres** with three tables and indexes shaped by the queries the system actually runs.
- **UI**: start, job details with polling, progress and tree, history; clear loading and error states.
- **66 tests**: the rules in isolation, the API over real HTTP, and the worker's whole page step against
  fixture HTML with a fake internet and an in-memory queue, including the same message delivered twice.
- Structured logs with job and page ids, health endpoints for both services, CI that builds and tests.

## The choices that shaped it

- **One message per page, not per job.** It is the only design under which "the same task delivered twice
  must not duplicate rows" is a real requirement, and it lets workers scale horizontally with no change.
  The cost is completion detection, solved with a status check after every page.
- **The database is the record; the queue is the to-do list.** Nothing about a job's state lives in the
  broker, so a broker restart loses nothing.
- **Rules isolated from plumbing.** Normalization, scope, extraction and the ratio have no dependencies, so
  they were tested first and a real bug (relative links on slash-terminated pages) was caught before any
  page was fetched.
- **Two kinds of failure, two policies.** A website being down is normal and is handled per page; our own
  infrastructure failing is not and goes through retry and dead-letter.
- **UI at its own address.** Static files belong at the edge; the API stays near the data.

## Trade-offs and what I prioritized

I spent the time on correctness of the rules, the integrity of the message design, and a live run against
real Postgres and RabbitMQ before building the UI. I cut Kafka, push updates, off-site crawling, subdomain
scope, real-infrastructure tests, authentication and politeness controls, each for a stated reason in the
README. A worker dying between committing a page and publishing its children is recovered by a sweeper that
re-publishes pages left queued too long; the remaining gap, a broker outage at the moment a job is created,
is documented with its fix, an outbox, as the first thing I would build next.
