# What the shipped gateway path establishes about transport compatibility

- **Ticket** [#1497, What does the shipped gateway path establish about transport compatibility?][ticket], on [map #1494, Enterprise protocol support for SPA and machine-to-machine workloads][map].
- **Date** 2026-11-23
- **Branch** `research/enterprise-gateway-transports`
- **Method** Read the [repository at `83a6c3fa`][pin], its contracts, implementation, test definitions and historical gate records. Compared the configured proof cell with first-party component documentation and wire specifications. The research involved source inspection only.

## Answer in brief

1. The shipped API path forwards HTTP requests initiated by a signed-in browser to a registered upstream. The shell and each fragment have their own OAuth client. The route selects the server-held token. The receiving API decides which clients and scopes it admits. An independent service caller needs its own identity boundary. [Browser contract][browser] · [Request gate][gate] · [Upstream contract][upstream]
2. The proof cell has three observable connections. The browser can negotiate HTTP/2 with the HTTPS ALB. The ALB sends HTTP/1.1 to Kestrel. YARP makes an independent request, using HTTP/1.1 for the cell’s cleartext loopback destinations. A different estate can configure different hops. [Cell listener and target][alb] · [AWS target protocol matrix][aws-target] · [Cell upstreams][poc] · [YARP version policy][yarp-config]
3. Local tests exercise incremental HTTP body traffic, periods of silence, route identity and a finite HTTP/1.1 pool. Historical deployments covered ordinary API requests through token refresh and task replacement. SSE and WebSockets remain deferred. Native gRPC conflicts with the configured target protocol. Native broker traffic requires its own listener or a registered HTTP adapter. [Local proxy tests][tests] · [F-22 deployed record][f22] · [Deferred features][deferred] · [AWS target protocol matrix][aws-target]

## Who makes the request, and which identity reaches the upstream?

The shell owns one document, origin and session. A secure, host-only, HttpOnly cookie identifies that session. Fragments share the document and can invoke each other’s paths. Host resolution pins the exact registered authority, app and any matching fragment for the request. The generated route names one allowed origin. The token step checks that the route matches those pins before asking for the shell or fragment access token. A missing session returns 401. A live session missing the fragment’s token gets `401 fragment_login_required`. The upstream validates signature, issuer, expiry, admitted client, scope and any bound audience. A fragment namespace separates forwarded clients while the scripts share an origin. [Browser contract][browser] · [Pipeline][pipeline] · [Route generation][routes] · [Token step][token] · [Upstream contract][upstream]

A service scheduler, partner callback or device operates independently of the shell. Under the shipped contract, a caller-supplied bearer is removed, Fetch Metadata is an admission check, and the proxy needs a session token for the selected route. Browser headers confer no service identity. An adopter can give a machine caller a separately authenticated API or broker endpoint. Admitting it at this gateway would require a machine identity and authorization contract covering credentials, audience and the identity forwarded to the upstream. [Request gate][gate] · [Header transforms][headers] · [Token step][token]

## Which HTTP requests and headers survive the path?

The two API prefixes are `/api/{name}/` and `/fragments/{fragmentId}/api/{name}/`. Both require `Sec-Fetch-Site: same-origin` and check a supplied `Origin` against the app origin. They accept GET, HEAD, POST, PUT, PATCH, DELETE and OPTIONS. POST, PUT, PATCH and DELETE also need the app’s CSRF request header and cookie. The gate requires a normalized, origin-form target. The global method check refuses CONNECT and TRACE. A recognized route removes its prefix and preserves the request method, query and body. The registry permits only allowlisted HTTP(S) origins. [Request gate][gate] · [Method allowlist][methods] · [Path check][path-check] · [Browser request headers][browser] · [Upstream schema][schema] · [Route generation][routes]

The gateway uses a trusted last hop for forwarded scheme and client address and resolves the app from the request authority. On the upstream connection it strips the browser cookie, bearer, CSRF token and client-supplied forwarding headers, adds the route’s bearer and sets forwarded host, protocol and client address. The contract includes `traceparent`. Upstream status and body pass back, redirects pass through, `Set-Cookie` and `Access-Control-*` are removed on every status, and gateway security headers take precedence. YARP handles connection-specific upgrade fields separately. These rules matter for a protocol that needs custom headers, trailers or a subprotocol. [Forwarded header trust][forwarded] · [Upstream contract][upstream] · [Header transforms][headers] · [YARP forwarder][yarp]

## Which protocols are configured on the two sides of the gateway?

| Connection | Configured behavior and its source |
| --- | --- |
| Browser → internal ALB | One HTTPS listener on 443 terminates TLS 1.2/1.3, preserves the original Host and forwards every registered app through one target group. The proof cell is reached by tunnel. The provider default enables frontend HTTP/2. AWS documents ordinary HTTP/2 on HTTPS listeners and WebSocket upgrades on HTTP/1.1. [Terraform][alb] · [Provider defaults][provider] · [AWS listeners][aws-listener] |
| ALB → gateway’s Kestrel listener | The target is HTTP on the configured gateway port, 8080 by image default. Omitting the target protocol version selects HTTP/1.1. Kestrel 10’s default mixed HTTP/1.1 and HTTP/2 listener uses HTTP/1.1 without TLS negotiation. Cleartext HTTP/2 requires an explicitly HTTP/2 endpoint with prior knowledge. The source pins ASP.NET Core 10.0.15 in the runtime image. [Target and listener][alb] · [Gateway image][image] · [Runtime inventory][runtime] · [Target default][provider-target] · [Kestrel endpoints][kestrel] |
| Gateway’s YARP client → upstream | Each shell or fragment API becomes a cluster with one destination. The code sets `ActivityTimeout` and `MaxConnectionsPerServer`, leaving outgoing version selection to YARP 2.3.0. It prefers HTTP/2 with `RequestVersionOrLower`. An eligible HTTPS destination can negotiate HTTP/2 or HTTP/1.1. The proof cell’s two cleartext loopback mock listeners get HTTP/1.1. An HTTP/2-only cleartext upstream needs a different outgoing policy. [Package pin][package] · [Route generation][routes] · [Proof-cell upstream defaults][poc] · [YARP version policy][yarp-config] · [YARP protocol configuration][grpc-yarp] |

The cell’s topology is an example an adopter can reproduce. Its protocol limits follow from the combination of listener, target group, Kestrel endpoint and destination. For any candidate, capture the negotiated version on each connection before attributing a result to HTTP/2. [AWS compatibility matrix][aws-target] · [Kestrel endpoint defaults in 10.0.15][kestrel-source] · [YARP protocol configuration][grpc-yarp]

## Which behaviors need a gateway proof?

| Workload | Component capability, gateway fit and proof still owed |
| --- | --- |
| Job progress over SSE or streamed fetch | A same-origin, authenticated GET can use the existing API route. SSE is an HTTP response of type `text/event-stream`. It needs no protocol upgrade. YARP disables response buffering by default and local tests read bytes before completion. A browser proof must see separate events before the response ends, observe cancellation reaching the upstream, and reconnect under current user and fragment permissions. An event ID can travel on reconnect. Event retention and replay belong to the upstream. For a streamed fetch, test its actual record format and the client’s own cursor logic. [SSE standard][sse] · [YARP forwarder][yarp] · [Partial-body tests][failure-tests] · [Deferred features][deferred] |
| Collaboration over WebSocket | AWS documents an HTTP/1.1 upgrade through the ALB. YARP can forward the handshake, switch on a successful 101 and relay bidirectional data. YARP can also translate HTTP/1.1 upgrade and HTTP/2 extended CONNECT where both endpoints allow the respective handshakes. The current method gate rejects incoming CONNECT. The existing same-origin GET rule checks Origin when present. The plan calls for an upgrade-specific Origin rule and a long-lived connection decision. A proof must identify the handshake on each hop, verify the agreed origin and session policy and selected subprotocol, exchange data both ways, and observe close and reconnect on a roll. [AWS listeners][aws-listener] · [YARP WebSockets][ws-yarp] · [Method allowlist][methods] · [Deferred features][deferred] |
| Native gRPC and browser gRPC-Web | Native gRPC needs HTTP/2, POST to a service/method path, and a terminal gRPC status in trailers (including trailers-only errors). AWS lists gRPC to an HTTP/1.1 target group as an error. A native deployment would need an HTTP/2-capable target and Kestrel listener, suitable upstream negotiation, compatible routing and caller identity. Check existing HTTP/1.1 callers and health checks before changing a shared target. A browser gRPC-Web client uses a different wire format compatible with HTTP/1.1. YARP can relay it to a gRPC-Web endpoint. A native-only upstream needs translation. A browser proof must use the registry prefix and required CSRF header, then check unary and any claimed server stream, final status and cancellation. gRPC-Web does not supply client or bidirectional streaming in the documented browser path. [AWS target matrix and health checks][aws-target] · [gRPC wire protocol][grpc] · [YARP gRPC and gRPC-Web][grpc-yarp] · [Browser request headers][browser] |
| Device telemetry over native MQTT | A device connects to a broker that authenticates it and authorizes its topic operations. An ALB HTTP listener, named HTTP route and upstream bearer do not implement that broker. Use a separately operated broker/listener for native traffic. A SPA can call a registered HTTP API that uses its own authorized broker connection. MQTT over WebSocket is a separate handshake and subprotocol with broker semantics behind it. A successful HTTP call to an adapter establishes only its HTTP-facing contract. [AWS listeners][aws-listener] · [Upstream contract][upstream] · [MQTT 5, including §6][mqtt] |

## What limits govern a long request or connection?

- **Silence and total time.** The registry sets a per-API idle interval of 1–2,147,483 seconds. Response headers and successful data reads or writes reset YARP’s timer. Connection setup and pool waits count as silence. Before headers the timeout is 504, after headers but before the first body byte it is 502, and after a forwarded byte the caller can see a truncated response with the original status. The contract gives ongoing traffic no absolute proxy deadline. The ALB separately defaults to 60 seconds idle on each client or target connection. HTTP/2 PING resets neither the ALB idle timer nor YARP’s activity timer. AWS also documents a one-hour default HTTP client keepalive followed by graceful connection retirement on a later request. That is a connection reuse policy, distinct from a deadline for an active response. [Registry bounds][schema] · [Upstream failures][upstream] · [YARP timers][yarp-config] · [AWS ALB timers][aws-timers]
- **Size and session lifetime.** Kestrel sets an 8 KiB request-line limit, a 32 KiB total request-header limit, 100 headers and a 30,000,000-byte request-body limit. A transfer protocol must specify its per-request size and where the upload offset is stored. The app session has a fixed 5–720 minute lifetime. The gateway selects the token before forwarding each request. For an open stream, the product still needs a rule for logout, expiry and permission changes, and tests for a fresh authorization decision at reconnect. [Kestrel limits in the gateway][sizes] · [Session schema][schema] · [Token step][token]
- **Capacity.** One task’s pool for one shell or fragment API permits 64 HTTP/1.1 upstream connections. One long HTTP response can occupy one connection. The overflow case times out in the queue before reaching the upstream. Two routes to one origin can have separate pools. The setting supplies neither a task-wide limit nor an HTTP/2 streams limit. On ingress Kestrel has separate limits for client connections and upgraded connections. Both default to unlimited, and the gateway’s Kestrel settings set request sizes only. Per-client limits and measured HTTP/2 capacity remain open. An adopter proof needs idle and active connections at the chosen concurrency, ordinary requests on the same and a sibling route, queue latency and resource recovery after disconnect. [Pool scope][pool] · [Connection-bound tests][bound-tests] · [Gateway Kestrel configuration][sizes] · [Kestrel connection limits][kestrel-limits] · [GAP-29][gap]
- **Task replacement.** The cell configures a 30-second target drain. On stop, the gateway has a 25-second shutdown budget within the container’s 30-second grace period. The contract protects an admitted refresh holder’s durable token write. A roll may interrupt an in-flight proxied response. A long-lived protocol needs a stated disconnect signal and a reconnect/replay policy that works on another task. [Gateway shutdown contract][pipeline] · [Target drain][alb] · [Container stop timeout][ecs] · [AWS ECS draining][drain]

## What was executed and what was read

**Executed on 2026-11-23.** Git and GitHub source inspection, searches of the pinned tree and test definitions, and review of public primary documentation. This investigation ran no product tests, protocol clients, AWS account operations or deployments.

**Local test definitions read on 2026-11-23.** Proxy component tests cover shell/fragment selection against a token source and echo upstream, request/response header transforms, and before/after-body failures. Hermetic composed tests N-33 through N-35 use controlled upstreams and include the HTTP/1.1 pool limit. The register also specifies F-38/N-37 for composed fragment cross-client checks. Their named `CrossClientTests.cs` is absent at the pin, while the proxy’s O11 component test is present. The roadmap records G9 as untagged. These sources establish a narrower scope than a composed or deployed fragment transport trial. [Component tests][tests] · [Fragment routing test][fr-tests] · [Composed upstream failures][composed] · [Test register][test-ids] · [Roadmap state][roadmap-state]

**Historical deployed records read on 2026-11-23.** The October 27 sitting at `9d8a001` recorded an API call succeeding after refresh and another app’s API refusal on onboarding. The November 14 G6 sitting at `fc85d4c` recorded successful discrete requests during a roll and 50 parallel calls across two tasks. The roll used a corrected driver as the record explains. These records concern earlier gateway revisions. Neither the records nor the tests inspected establish browser event delivery, upgraded frames, native gRPC trailers, continuous handover or sustained stream capacity through the deployed path. [F-03 refresh][f03] · [F-07 API admission][f07] · [F-22 roll][f22] · [F-23 concurrency][f23]

**Other sources read on 2026-11-23.** All linked repository files are from the stated pin. The first-party external sources cited above are the Terraform AWS provider 6.68.0, the AWS ALB and ECS guides, ASP.NET Core 10 documentation and Kestrel 10.0.15 source, YARP 2.3.0 source, WHATWG HTML, the gRPC protocol definition and OASIS MQTT 5. The plan’s deferral and the roadmap’s MR-10 support-statement requirement remain the published product position. [Deferred features][deferred] · [MR-10][mr10]

[ticket]: https://github.com/adammedford/spa-platform/issues/1497
[map]: https://github.com/adammedford/spa-platform/issues/1494
[pin]: https://github.com/adammedford/spa-platform/tree/83a6c3fa40546ee4c5d35040804774e90f9641db
[browser]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/contracts/http-contract.md
[gate]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/contracts/request-gate.md
[upstream]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/contracts/upstream-contract.md
[pipeline]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/contracts/pipeline.md
[routes]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/src/Platform.Proxy/RegistryProxyConfig.cs#L90-L132
[token]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/src/Platform.Proxy/AccessTokenStep.cs
[methods]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/src/Platform.Security/MethodAllowlist.cs
[headers]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/src/Platform.Proxy/UpstreamHeaders.cs
[forwarded]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/src/Platform.Security/PlatformForwardedHeaders.cs
[schema]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/contracts/registry.schema.json
[alb]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/infra/modules/cell/alb.tf
[poc]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/infra/envs/poc/platform/variables.tf#L185-L193
[image]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/src/Gateway/Dockerfile
[runtime]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/docs/dependencies.md#L89-L95
[package]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/Directory.Packages.props
[sizes]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/src/Platform.Security/PlatformSecurityLimits.cs
[ecs]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/infra/modules/cell/ecs.tf#L234-L257
[pool]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/src/Platform.Proxy/ProxyLimits.cs
[gap]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/docs/gaps.md#L58-L77
[tests]: https://github.com/adammedford/spa-platform/tree/83a6c3fa40546ee4c5d35040804774e90f9641db/tests/Platform.Proxy.Tests
[failure-tests]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/tests/Platform.Proxy.Tests/UpstreamFailureTests.cs
[bound-tests]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/tests/Platform.Proxy.Tests/ConnectionBoundTests.cs
[fr-tests]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/tests/Platform.Proxy.Tests/FragmentRouteTests.cs#L25-L69
[composed]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/tests/Gateway.SecurityTests/Negative/UpstreamFailureTests.cs
[test-ids]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/contracts/test-ids.md
[roadmap-state]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/docs/roadmap.md#L35-L42
[mr10]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/docs/roadmap.md#L433-L439
[deferred]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/docs/plan/06-phase-2-and-risks.md#L78-L89
[aws-listener]: https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-listeners.html
[aws-target]: https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-target-groups.html#target-group-protocol-version
[aws-timers]: https://docs.aws.amazon.com/elasticloadbalancing/latest/application/edit-load-balancer-attributes.html
[drain]: https://docs.aws.amazon.com/AmazonECS/latest/developerguide/load-balancer-connection-draining.html
[provider]: https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/website/docs/r/lb.html.markdown
[provider-target]: https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/website/docs/r/lb_target_group.html.markdown
[kestrel]: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0#configure-http-protocols
[kestrel-source]: https://github.com/dotnet/aspnetcore/blob/v10.0.15/src/Servers/Kestrel/Core/src/ListenOptions.cs
[yarp]: https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/HttpForwarder.cs
[yarp-config]: https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/ForwarderRequestConfig.cs
[grpc-yarp]: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/grpc?view=aspnetcore-10.0
[ws-yarp]: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/websockets?view=aspnetcore-10.0
[grpc]: https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md
[sse]: https://html.spec.whatwg.org/multipage/server-sent-events.html
[mqtt]: https://docs.oasis-open.org/mqtt/mqtt/v5.0/os/mqtt-v5.0-os.html
[f03]: https://github.com/adammedford/spa-platform/issues/63#issuecomment-6452253970
[f07]: https://github.com/adammedford/spa-platform/issues/63#issuecomment-6453515691
[f22]: https://github.com/adammedford/spa-platform/issues/348#issuecomment-6897603476
[f23]: https://github.com/adammedford/spa-platform/issues/348#issuecomment-6897876207
[path-check]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/src/Platform.Security/RequestGate.cs#L198-L214
[kestrel-limits]: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-10.0#maximum-client-connections
