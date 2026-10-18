## What changes for the user

<!-- One or two sentences in plain English. If a billing analyst would not notice, say "internal only". -->

## How to verify

<!-- The exact steps or test names a reviewer runs. "dotnet test" alone is not enough; name the test that proves the change. -->

## Rollback

<!-- Revert the commit? Also undo a migration / config change? Say which. -->

## Checklist

- [ ] Tests added or updated for the behaviour that changed
- [ ] Errors a user can see are plain English, with no stack traces
- [ ] No PHI in logs (no names, DOB, member ids, claim details in log lines)
- [ ] Config values have a default and are documented in the README
