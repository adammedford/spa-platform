using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// The startup floor (#173): an empty WebApplication answering 200 on /healthz, built the way the
// gateway is (tools/bench/floor/Dockerfile follows src/Gateway/Dockerfile). The same host builder as
// src/Gateway/Program.cs, not CreateSlimBuilder: the floor is a minimum on the gateway's chain, and a
// slimmer builder would measure a chain the gateway does not run. Nothing else belongs here, because
// every line added is start-up cost the gap over the floor would then hide.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();
app.MapGet("/healthz", () => Results.Ok());
app.Run();
