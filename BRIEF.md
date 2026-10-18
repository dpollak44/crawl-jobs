# BRIEF — fill this in from the assignment, then paste the whole file as your first message to Claude

<!-- Delete every bracketed hint before pasting. Keep the headings. -->

## The assignment, verbatim

[Paste the company's brief here, word for word, including any sample data or file names.]

## What they said about how it will be judged

[Anything the recruiter or brief said: "we care about tests", "keep it simple", "show us how you think".
If nothing was said, write: "Nothing stated. Assume: readable code, tests, a README that runs."]

## Fixed facts

- Time: 4 hours, hard stop at [HH:MM]. We stop adding features at [HH:MM minus 45].
- Runtime: .NET [10 / 8 if they said so]. The kit's `Directory.Build.props` holds the one line to change.
- Database: SQLite via EF Core unless the brief names one. [If they ship SQL Server, say so here.]
- UI: [none / required: what kind]. If none, the API page at /scalar is the demo surface.
- Submission: [zip / GitHub repo / their link]. Reviewer runs it on [their machine / unknown].

## How we work (do not skip)

1. Before writing any source: read the assignment above twice. Reply with
   (a) the acceptance criteria as a numbered checklist in the brief's own words,
   (b) every ambiguity you see, each with the simplest reading and your recommended choice,
   (c) the vertical-slice order you propose, smallest first,
   (d) which parts of the kit you will delete because the brief does not need them (delete them first thing after I confirm).
   Then STOP and wait for my answers. Ask me everything up front; do not ask one question at a time later.
2. After I answer: write the README "What I built" checklist and "Assumptions" first, then build slice 1.
3. Every slice: domain rule + test, endpoint + integration test, `dotnet build -warnaserror && dotnet test`
   green with fresh output, then one commit with a plain-English message. Then EXPLAIN the change to me
   in plain English (I do not open an editor; see CLAUDE.md "How David reviews") and wait for my go.
4. Run the reviewer brief in REVIEW.md twice: after slice 1, and at the 3:15 mark. Fix blocking findings only.
5. Tell me the time used after each slice. At the cut-off, stop features and do the README and a clean run.

## Scope I have already decided

[Fill after Claude's question round. One line per decision, e.g. "Overdue is measured against the server clock."]
