# Change request issue-148: `CapturedLine` carries the event id

- **Raised by:** workstream 4, API and security, during #148 (stage-host 5/10)
- **Tasks:** #148; #140, the `stage-host` spec; change request issue-145, section 4, which defined `CapturedLine`
- **Date:** 2026-10-23
- **Status:** moved to #540, 2026-10-29, by the cutover #439

## What changed

#148 moves `ProxyHost` in `tests/Platform.Proxy.Tests` onto `StageHost`, and with it from the
project's own `RecordingLoggerProvider` to `CapturedLog`. One test cannot make the move whole:
`RuleIdTelemetryTests.An_unlisted_api_name_logs_its_rule_through_the_endpoint_s_template` holds the
`404 unknown_api` Warning line to its `[LoggerMessage]` template (#162) by asserting the line's
`EventId.Name` is set. A source-generated template's line carries an event id named after its method;
the ad hoc `LogWarning` call it replaced carried none.

`CapturedLine` is `(Category, Level, Message, Exception, State)`. Its `State` does not tell the two
apart: an ad hoc `LogWarning("... rule={Rule}", rule)` leaves the same `Rule` and `{OriginalFormat}`
pairs a template does. The event id is the only thing that does, and the capture drops it.

## What is asked of the integrator

One edit in `src/Platform.Testing/CapturedLog.cs`, which AGENTS.md reserves to the integrator:

1. `CapturedLine` gains `EventId EventId`, and `CapturingProvider.Logger.Log` passes the `eventId`
   it is handed. Where the parameter goes in the positional record is the integrator's choice; no
   caller in the solution constructs a `CapturedLine`, so only the deconstructing callers, if any,
   move with it.
2. A test in `tests/Platform.Testing.Tests` that a `[LoggerMessage]` line's captured event id has its
   method's name, and an ad hoc line's has none.

## What #148 did without it

Everything else. `ProxyHost` starts on `StageHost` and holds no copied listener, stand-in or sender;
its capture is the stage host's `CapturedLog`, and the rule-id query goes through
`CapturedLog.LinesFor`. The project's `RecordingLoggerProvider` stays, added in the services delegate
and read by that one test only through `ProxyHost.EventLines`, for the event id.

## Why this is a change request and not an edit

`src/Platform.Testing` is the integrator's (AGENTS.md, OWNERSHIP.md).

## Blocking

Nothing at runtime. Once it lands, workstream 4 deletes `tests/Platform.Proxy.Tests/RecordingLoggerProvider.cs`
and `ProxyHost.EventLines`, the test reads `line.EventId.Name` from the `CapturedLine`, and #148's
first acceptance criterion ("holds no copied ... capture") is met.
