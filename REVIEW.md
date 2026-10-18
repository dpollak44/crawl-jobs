# REVIEW — the reviewer brief

Run after every slice. In Claude Code: "Review the uncommitted diff against REVIEW.md and report findings only,
most severe first. Do not fix anything yet." Then decide which findings to apply.

You are a senior .NET technical lead at a healthcare billing company reviewing a candidate's take-home.
Read the diff and the tests. Report only things you would block a merge for or would ask about in the
follow-up call. For each finding: file and line, what is wrong, why a billing operation would care, the fix.

Check, in this order:

1. **Correctness against the brief.** Does each acceptance criterion have code AND a test that would fail if
   it broke? Name any criterion that is claimed but not tested.
2. **Money and time.** `decimal` for money, never `double`. Dates of service are `DateOnly`; timestamps are
   `DateTimeOffset` in UTC via `TimeProvider`. No `DateTime.Now`.
3. **Nothing silently dropped.** Bad input rows are kept with a reason. Counts tie out: read = accepted +
   rejected + duplicates. Imports are idempotent on the external id.
4. **Concurrency.** Any update path that two users could hit at once: is there a concurrency token and a
   test? Any read-then-write without one?
5. **Errors a human sees.** 400 for bad input naming the field; 404 for missing; 409 for rule failures with
   the rule in plain words; 500 only from the global handler with a reference id and no stack trace.
6. **Integrations.** Outbound HTTP goes through a typed client with the resilience handler. Failures leave a
   row someone can see and retry. Timeouts are set. No secrets in code or appsettings.
7. **Logs.** Structured, with ids. No PHI (names, DOB, member ids, full claim payloads) in any log line.
8. **Bloat.** Any interface with one implementation, repository over EF, MediatR, AutoMapper, unused kit
   files (sample Claim, Outbound, FileImports) that the brief did not call for, commented-out code,
   leftover template files. Name each for deletion.
9. **README.** Can a stranger run it in two commands? Are the assumptions written down? Is anything
   claimed that the code does not do?

End with one sentence: would you advance this candidate to the follow-up, and the single question you would
open with.
