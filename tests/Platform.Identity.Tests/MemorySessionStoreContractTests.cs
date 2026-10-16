using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Plan row 3.4: "a store contract suite passes". The in-memory store passes the suite in
/// Platform.Testing unchanged; the Phase 2 store will subclass the same suite.
/// </summary>
public sealed class MemorySessionStoreContractTests : SessionStoreContract
{
    // The suite's records expire at 17:00 on the shared clock's day, so a store reading the shared
    // clock sees every one of them as live.
    protected override ISessionStore CreateStore() => new MemorySessionStore(maxSessions: 10_000, TestClock.New());
}
