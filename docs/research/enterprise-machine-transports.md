# Connection patterns for independent machine workloads

- **Ticket** [#1496, Which connection patterns do independent machine workloads require?][ticket] on [map #1494][map]
- **Date** 2026-11-23
- **Branch** `research/enterprise-machine-transports`, based on `83a6c3fa40546ee4c5d35040804774e90f9641db`
- **Method** Read gateway contracts, protocol specifications and vendor or project documentation. The workloads illustrate designs. The proofs below are proposed, and prevalence, priority and product ownership are open. No workloads were deployed and no provider trials or protocol tests were run.

## Answer in brief

1. A service calling an API needs a machine identity. The current gateway serves registered SPA hosts behind an internal ALB. Its API routes require same-origin browser metadata, discard incoming authorization and select the signed-in session’s shell or fragment token. An independent caller needs a distinct admission rule and a way for the receiving service to authorize that caller. [Architecture][plan] · [Request gate][gate] · [Upstream contract][upstream]
2. Who opens the connection matters. A worker calls an RPC server, a webhook producer calls the adopter, and a broker connects producers and consumers that each open their own client connection. An external sender also needs a reachable ingress. [gRPC lifecycle][grpc] · [Stripe webhooks][stripe] · [Kafka protocol][wire]
3. Define success at the boundary an adopter cares about. A reply, a queued delivery, a committed offset, a completed upload and a signed receipt each answer a different question. The applications still need evidence of any business action they promise. [RabbitMQ acknowledgements][confirm] · [Kafka delivery semantics][kafka] · [Transfer workflows][files] · [AS2 receipts][as2]

## Service RPC

**Stock reconciliation over HTTPS.** A scheduled reconciler authenticates to the authorization server as a confidential client, obtains its own token, then calls the inventory API over HTTPS, directly or through an HTTP proxy. In this design the API validates the token and checks the app’s reconciliation permission. The inventory service stores the adjustment and its operation ID. If a POST reply is lost after commit, the caller can reconcile by that ID. A disconnect leaves the write outcome unknown to the proxy. **Proof** — An admitted job can reconcile without signing in. After a lost reply its retry produces one adjustment, while a read-only service account cannot make one. [OAuth client credentials][oauth] · [App-only API permissions][roles] · [HTTP idempotency][http]

**Route planning over native gRPC.** A dispatch worker invokes a planner through a generated client. The worker can open an HTTP/2 connection to a proxy which opens HTTP/2 to the planner. In this example TLS authenticates each server and per-call credentials identify the worker to the planner, which authorizes the method. Unary, client-streaming, server-streaming and bidirectional calls have different message lifetimes. The client and server track the active call and deadline. Both proxy legs must preserve gRPC framing and final status trailers, including a trailers-only response for an immediate error. **Proof** — The worker receives two route updates before completion and then sees the planner’s final error status. Cancelling a second call reaches the planner and stops its work. [gRPC wire protocol][grpcwire] · [Call lifecycle][grpc] · [Credentials][grpcauth]

**Legacy SOAP purchase order over HTTPS.** A procurement adapter POSTs a SOAP 1.2 XML envelope to a supplier’s ERP through an HTTP relay. Assume the supplier admits the adapter’s app identity and authorizes the purchase-order operation. The ERP owns the order and its SOAP Fault. A relay preserving the media type, optional action parameter, envelope and fault can leave SOAP processing at the ERP. Interpreting faults or translating them to a different API adds an application contract. **Proof** — The existing SOAP client reads the ERP’s structured Sender fault on HTTP 400, including its detail, and no order appears for that rejected request. [SOAP 1.2 HTTP binding and fault mapping][soap] · [App-only API permissions][roles]

## Producer-initiated callbacks

**Payment event to accounts receivable.** Stripe initiates an HTTPS POST to a registered, publicly reachable merchant endpoint. That endpoint needs a route from Stripe to the adopter, beyond the current internal ALB. The receiver verifies Stripe’s signature over the original bytes using the endpoint secret and admits the expected account and event types. Stripe schedules delivery retries. In this design the merchant durably records the verified event before returning 2xx and its worker deduplicates the ledger action. Acknowledging at a relay would make the relay responsible for the handoff. **Proof** — Lose the response after recording the event. Redelivery of that event yields one posting. A body that fails signature verification yields none. [Stripe delivery, signatures and duplicates][stripe] · [Gateway architecture][plan]

## Durable business messaging

An API that publishes on a caller’s behalf opens a second connection as a broker client. In a native AMQP or Kafka connection, the broker sees the workload’s principal. A bridge using one shared principal would need a separate way to enforce the callers’ publishing rights. [RabbitMQ access control][rabbitacl] · [Kafka ACLs][kafkaacl]

**Shipping work queue over AMQP 0-9-1.** Order service and warehouse worker each open their own authenticated AMQP connection to RabbitMQ. The order service publishes a persistent work item to an exchange routed to a durable queue. The worker consumes and acknowledges after recording shipment. The broker controls virtual-host, exchange and queue permissions. Its publisher confirm and consumer acknowledgement cover different legs. Loss of the worker connection requeues an unacknowledged delivery. **Proof** — Stop the worker after its shipment commit and before acknowledgement. Another worker receives the delivery and the warehouse records one shipment for that order. This example specifies RabbitMQ’s AMQP 0-9-1 profile. [Queues][queues] · [Confirms and redelivery][confirm] · [Access control][rabbitacl]

**Replayable inventory movements over Kafka.** Stock control produces records. A forecasting group and an audit group independently fetch them. Each client bootstraps against a broker, discovers the brokers serving the partitions and issues Kafka requests over TCP. Broker ACLs admit each service principal to its topics and groups. The cluster retains the partition log under its retention policy, and each group checkpoints an offset per partition. A consumer writing an external database must coordinate its output with that position. **Proof** — Rebuild the forecast from retained offsets in partition order. Audit continues from its own offsets. A restarted forecast worker preserves one output per movement in its database. [Kafka wire protocol][wire] · [Consumer position and delivery semantics][kafka] · [ACLs][kafkaacl]

## Device telemetry

**Refrigerated-trailer temperature over MQTT 5.** Each controller and the fleet collector separately open a long-lived MQTT-over-TLS connection to the broker. The broker authenticates each client and grants the controller publish access to its trailer’s topic and the collector subscribe access. Mosquitto, for example, can bind a client ID to a credential. MQTT QoS applies on each delivery leg. At QoS 1, a duplicate is possible. The broker and client maintain session state across a disconnect when configured to do so. The collector owns the durable reading. Terminating MQTT at the gateway would give it responsibility for device connections, topic authorization and session continuity at that hop. **Proof** — Give the collector a persistent subscription, disconnect it briefly, publish a QoS 1 reading, then reconnect it with the same session before expiry. The collector recovers the reading and counts its sequence once even on redelivery. A second trailer cannot publish to the first trailer’s topic. [MQTT 5 §§3.1.2, 4.1, 4.3][mqtt] · [Mosquitto per-client and topic controls][mosq]

## Partner batch and file exchange

**Nightly payroll file over SFTP.** The payroll partner’s scheduler initiates SSH/SFTP to the adopter’s managed transfer endpoint. In the AWS Transfer Family example, a service-managed partner account has an SSH public key, storage role and home directory. The service writes to backing storage. The receiver can route incomplete and complete uploads to different workflows and keep a batch identifier for import. An SFTP endpoint offered by the gateway would need to enforce those storage permissions and distinguish complete from interrupted uploads. **Proof** — Interrupt the upload halfway. The partial file triggers no payroll import. A complete retransmission produces a byte-identical file and one import, and the account cannot read another partner’s path. [Transfer identities and permissions][sftp] · [Post-upload workflows][files]

**Supplier invoice over signed AS2.** A supplier’s AS2 endpoint POSTs a signed MIME message over HTTP(S) to the buyer’s AS2 endpoint. The trading agreement binds partner IDs, certificates and permitted exchange. The receiver verifies the message and issues the requested message disposition notification (MDN). A synchronous MDN travels in the original response. With asynchronous HTTP delivery the buyer initiates a separate connection back to the supplier. The sender retains the message ID, digest and receipt to reconcile delivery. An HTTP relay can leave signature verification and receipt generation to the buyer’s AS2 endpoint. The buyer’s accounts-payable system separately records invoice approval. **Proof** — For an agreed signed asynchronous receipt, the sender marks receipt only when the MDN signature, disposition, original message ID and integrity check match. An HTTP success while the MDN is outstanding leaves the receipt pending. [AS2 §§7–8][as2] · [Example partner agreement][agreement]

## Evidence and method

**Executed on 2026-11-23.** Retrieved the ticket and map, checked the base revision and inspected the gateway contracts. Checked the document’s reference links. All proof scenarios above are designs for an adopter to exercise.

**Read on 2026-11-23.** The following are the sources for the findings.

- Repository boundary and question — [ticket][ticket], [map][map], [architecture][plan], [request gate][gate], [upstream contract][upstream].
- Calls and callbacks — [RFC 6749 §4.4][oauth], [Microsoft app-only permissions][roles], [RFC 9110 §9.2.2][http], [gRPC wire protocol][grpcwire], [lifecycle][grpc], [credentials][grpcauth], [W3C SOAP 1.2 Part 2 §7][soap], [Stripe webhooks][stripe].
- Brokered work and telemetry — RabbitMQ [queues][queues], [acknowledgements][confirm] and [authorization][rabbitacl]. Apache Kafka 4.1 [protocol][wire], [design][kafka] and [ACLs][kafkaacl]. [OASIS MQTT 5][mqtt] and [Eclipse Mosquitto security][mosq].
- File and document exchange — AWS Transfer Family [service-managed users][sftp], [upload workflows][files] and [AS2 configuration][agreement]. [RFC 4130 §§7–8][as2].

[ticket]: https://github.com/adammedford/spa-platform/issues/1496
[map]: https://github.com/adammedford/spa-platform/issues/1494
[plan]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/docs/plan/01-plan.md#L118
[gate]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/contracts/request-gate.md#L18-L25
[upstream]: https://github.com/adammedford/spa-platform/blob/83a6c3fa40546ee4c5d35040804774e90f9641db/contracts/upstream-contract.md#L7-L21
[oauth]: https://datatracker.ietf.org/doc/html/rfc6749#section-4.4
[roles]: https://learn.microsoft.com/en-us/entra/identity-platform/scenario-protected-web-api-verification-scope-app-roles
[http]: https://datatracker.ietf.org/doc/html/rfc9110#section-9.2.2
[grpcwire]: https://github.com/grpc/grpc/blob/29fc09cdb0c2794b3b983369d3f29417ab9b1ca8/doc/PROTOCOL-HTTP2.md
[grpc]: https://grpc.io/docs/what-is-grpc/core-concepts/
[grpcauth]: https://grpc.io/docs/guides/auth/
[soap]: https://www.w3.org/TR/soap12-part2/#soapinhttp
[stripe]: https://docs.stripe.com/webhooks
[queues]: https://www.rabbitmq.com/docs/queues
[confirm]: https://www.rabbitmq.com/docs/confirms
[rabbitacl]: https://www.rabbitmq.com/docs/access-control
[wire]: https://kafka.apache.org/41/design/protocol/
[kafka]: https://kafka.apache.org/41/design/design/
[kafkaacl]: https://kafka.apache.org/41/security/authorization-and-acls/
[mqtt]: https://docs.oasis-open.org/mqtt/mqtt/v5.0/os/mqtt-v5.0-os.html
[mosq]: https://mosquitto.org/documentation/dynamic-security/
[sftp]: https://docs.aws.amazon.com/transfer/latest/userguide/service-managed-users.html
[files]: https://docs.aws.amazon.com/transfer/latest/userguide/transfer-workflows.html
[as2]: https://datatracker.ietf.org/doc/html/rfc4130#section-7
[agreement]: https://docs.aws.amazon.com/transfer/latest/userguide/as2-example-tutorial.html
