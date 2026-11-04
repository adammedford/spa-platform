# PingFederate's protocol behaviour the `pingfederate` profile relies on

- **Ticket:** #946, "PingFederate's protocol behaviour the pingfederate profile relies on: the client
  assertion, PAR, form_post, aud under resource, logout and the logout token's claims", on the map
  "Wayfinder: PingFederate as the evaluation provider, locally and in the cell" (#944).
- **Date:** 2026-11-04.
- **Branch:** `research/pingfederate-protocol-behaviour-for-the-profile`.
- **Method:** docs-only. Every PingFederate claim is read from a page under
  `docs.pingidentity.com/pingfederate/13.1/` (the current image is 13.1.4), with the version named
  where only a 13.0 page carries the sentence; the Terraform provider's `oauth_client` resource
  document on `github.com/pingidentity` is read as the admin API's model. The specifications
  PingFederate cites were downloaded in full (RFC 7523, RFC 9126, RFC 8707, RFC 7009, OpenID
  Connect Back-Channel Logout 1.0, OAuth 2.0 Form Post Response Mode, OpenID Connect Core 1.0) and
  quoted from the text. Nothing was executed against PingFederate: Docker Desktop 29.9.1 (arm64,
  Compose v5) is on this machine, but no Ping DevOps user key or PingFederate licence exists yet.
  No AWS apply, plan, credential step or read of the cell (`docs/agents/aws-rules.md`). Anything no
  page states is marked **not found**, with the pages read. The repository context was read first:
  `contracts/changes/p2-6-pingfederate-profile.md` (items 2, 3, 5, 7, 8 and "Item 7"), ADR-0028, and
  the `GLOSSARY.md` entries "Client assertion", "Pushed authorization", "Back-channel logout" and
  "Session index".

## Answer in brief

1. **Client assertion.** PingFederate accepts `private_key_jwt` with `client_assertion_type`
   `urn:ietf:params:oauth:client-assertion-type:jwt-bearer` and the JWT in `client_assertion`, and
   points to RFC 7523 for the claims. Its own stated rules: `iss` must equal `sub` (the
   `issuerMustBeEqualToClientId` switch, on by default); a per-client **Signing Algorithm**, default
   "Allow Any"; an optional per-client **Replay Prevention** that "mandate[s] a unique signed JWT
   from the client for each request"; keys from a pasted **JWKS** ("a JSON object containing a
   `keys` array") or a **JWKS URL**. The `aud` it accepts, the lifetime it tolerates, a `jti`
   requirement and any `kid` or thumbprint rule are **not found** on any page read; the only PF
   statement on `aud` is 13.1's release note PF-36847, "improved the way the `aud` claim is managed
   in `private_key_jwt` OAuth authentications ... aligns PingFederate with RFC 7523", whose section
   3 item 3 lets the token endpoint URL be the `aud`. The assertion is documented at the token
   endpoint, the revocation endpoint and the introspection endpoint, and the PAR endpoint accepts
   "any additional parameters needed for client authentication" (RFC 9126 section 2.1 step 1:
   "Authenticate the client in the same way as at the token endpoint"). It is refused by the
   Session Revocation API and the Session Management API, which "only support Client Secret or
   Client TLS Certificate".
2. **Pushed authorization.** `/as/par.oauth2`, POST only, "all parameters that usually comprise an
   authorization request"; `request_uri` lifetime is the AS setting **PAR Reference Timeout**,
   default 60 s, range 1 to 1800; **PAR Status** defaults to Enabled on the AS and the client's
   **Require Pushed Authorization Requests** is cleared by default. Single use in 13.1 is
   consent-bound: "PingFederate invalidates the `request_uri` when consent details are presented to
   the user, or when the PAR reference timeout expires. ... If consent details aren't presented, the
   client can reuse the same `request_uri`" (PF-38880), which is looser than RFC 9126's "single-use
   reference" and "SHOULD treat `request_uri` values as one-time use"; the handler sends it once,
   so nothing in the gateway depends on the difference. The authorize request after a push carries
   `client_id` and `request_uri` in the sample; a parameter repeated on the front channel loses to
   the pushed one ("the value from the PAR request parameter takes precedence"). The error when PAR
   is required and skipped is **not found** on a PF page; RFC 9126 names `invalid_request`.
3. **`form_post`.** "When set to `form_post`, the authorization response is returned to the client
   in an auto-POST form, in accordance with the OAuth 2.0 Form Post Response Mode specification",
   whose form is `method="post"` to the redirect URI with the parameters
   "encoded in the body using the `application/x-www-form-urlencoded` format", `state` among them.
   `iss` in the authorization response (RFC 9207) is the AS checkbox **Include Issuer in
   Authorization Response**, "By default, this checkbox is cleared". `SameSite` on the relying
   party's cookies is **not found** on any PF page; the only SameSite text is Jetty's
   `sameSiteDefault` `None` for PingFederate's own servlet cookies. The P2-6 item 3 decision
   (`SameSite=None` on the two login-state cookies) rests on the browser rule, not on a PF page.
4. **Audience.** `resource`, `aud` and `access_token_manager_id` select the access token manager
   (ATM) by matching the ATM's configured **Resource URIs**; `access_token_manager_id` beats `aud`,
   which beats `resource`; several `resource` values "must match to a single" ATM, else PF "aborts
   the request". **No page states that the issued JWT's `aud` is filled from `resource`**: the JWT
   ATM's `aud` is its **Audience Claim Value** ("If left blank, this field is omitted"), overridden
   by an `aud` attribute mapped on the **Access Token Attribute Contract**. So one ATM per resource,
   with its Audience Claim Value equal to the resource URI, is the configuration that makes `aud`
   follow `resource`; the introspection sample (`aud` = `https://rs.example.com/resource`) is
   consistent with that. The token endpoint page documents the three ATM parameters "in their
   requests to the PingFederate OAuth AS" without naming grant types, so the token endpoint accepts
   `resource` as far as the pages go; the grant-type page lists only `refresh_token` and `scope`
   for a refresh. Whether a refresh naming no `resource` keeps the `aud` is **not found**; a deployed
   or Local-tier run answers it.
5. **Logout and revocation.** `end_session_endpoint` is `/idp/init_logout.openid` (GET and POST);
   `id_token_hint` is optional and "If this parameter isn't included or can't be validated, the user
   is prompted to confirm the logout request"; `post_logout_redirect_uri` needs `client_id` or
   `id_token_hint` beside it and "must match one of the values registered for the client or it will
   be ignored"; `state` is returned. Revocation is `POST /as/revoke_token.oauth2` with `token` and
   `token_type_hint` (`access_token`, `refresh_token`) and the client's configured method, the
   assertion included. "Only Internally Managed Reference Tokens support direct access token
   revocation"; a JWT access token is revoked indirectly through its refresh token when the ATM's
   **Access Grant GUID Claim Name** is set, or directly when the ATM's **Enable Token Revocation**
   is selected, which "require[s] a Client ID Claim Name and a minimum JWT ID Claim Length of 22
   alphanumeric characters" and is cleared by default. Revoking a refresh token revokes its grant
   and access tokens; revoking an access token leaves the refresh token alone.
6. **Back-channel logout, the one finding that matters most.** PingFederate's documentation of the
   logout token is one sentence, in two places: "When you select OIDC Back-Channel, PingFederate
   sends a logout token to the client's configured Back-Channel Logout URI. This feature conforms
   to the OpenID Connect Back-Channel Logout specification." **Whether the token carries `sub`,
   whether it carries `sid`, and whether it can be `sid`-only is not found on any page read** (the
   nineteen pages are listed in section 6). The circumstantial case, labelled as inference: no
   PingFederate page read uses the word `sid` at all; PingFederate's own session identifier in ID
   tokens is `pi.sri`, added by the OIDC policy's **Include Session Identifier in ID Token**; the
   client record (admin API, Terraform) has no `backchannel_logout_session_required`; the 13.1
   discovery example shows no `backchannel_logout_session_supported` (the spec's default is false,
   "the OP can[not] pass a `sid` Claim"); and the spec requires "either a `sub` or a `sid` Claim".
   Under the spec a token with no `sid` must carry `sub`, which is what ADR-0028's index needs; the
   prototype executes it. Triggers: the only PF sentence is "When a user initiates logout,
   PingFederate sends logout requests"; session expiry and Session Management API revocation as
   triggers, and delivery retries, are **not found** (the spec says the OP "should not retransmit
   ... unless ... potentially recoverable errors"). The feature needs the AS checkbox **Track User
   Sessions for Logout** (cleared by default), the client's **Logout Mode** `OIDC Back-Channel`
   and its **Back-Channel Logout URI**.
7. **Session.** Authentication sessions default to an **Idle Timeout** of 60 minutes and a **Max
   Timeout** of 480 minutes (eight hours), each settable from 1 minute to 1,095 days; the idle
   window slides ("the authentication session is extended by the idle timeout value"); sessions
   are off until enabled per source or for all sources. `prompt` takes `none`, `login`, `consent`
   and `create`; `max_age` re-prompts when "the elapsed time exceeds the value"; the error
   `prompt=none` returns is **not found** on the PF page and is OpenID Connect Core's
   `login_required`.

## 1. The client assertion

**What the gateway will do (item 2 of P2-6).** `PrivateKeyJwt` puts a signed JWT in
`client_assertion` with `client_assertion_type`
`urn:ietf:params:oauth:client-assertion-type:jwt-bearer` on the code exchange, the refresh, the
revocation and the pushed authorization request; `ClientAssertion.Algorithm` defaults to `RS256`
and `KeyId` to the RFC 7638 thumbprint (item 3). The provider's acceptance of `aud`, `alg`, `kid`,
lifetime and `jti` is one of the four facts decision 8 leaves to a run.

**The request shape.** Token endpoint, 13.1: "Private key JWT or Client Secret JWT: Clients must
include request parameters `client_assertion_type` and `client_assertion` in the message body of
their requests. ... For the `private_key_jwt` and `client_secret_jwt` client authentication methods,
the value is `urn:ietf:params:oauth:client-assertion-type:jwt-bearer`. ... `client_assertion`: The
value is the authentication token", with "Learn more ... in Client Authentication and Using
Assertions for Client Authentication" (OpenID Connect Core and RFC 7521).
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_token_endpoint.html

**`iss` = `sub`.** Clients page, 13.1: "When authenticating an OAuth client that uses the private key
JWT authentication scheme, PingFederate validates that the issuer and subject claims in the JWT have
the same value. The following administrative API endpoint exposes the validation on/off switch:
`https://{{pf_base_host_port}}/pf-admin-api/v1/configStore/oauth-credentials-validator/issuerMustBeEqualToClientId`.
To disable validation, send an HTTP POST request with the following body to the endpoint: `{ "id":
"issuerMustBeEqualToClientId", "stringValue": "false", "type":"STRING" }`." RFC 7523 section 3
item 2 B: "For client authentication, the subject MUST be the `client_id` of the OAuth client." The
handler's assertion sets both to the client id, so the default passes.
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_oauth_clients.html

**`alg`.** Same page: "In the Signing Algorithm list, select the algorithm that the client must use
to sign the JWTs for client authentication. The default selection is Allow Any, which enables the
client to use any of the signing algorithms in the list." Discovery advertises
`token_endpoint_auth_signing_alg_values_supported` `RS256 RS384 RS512 HS256 HS384 HS512 ES256 ES384
ES512 PS256 PS384 PS512`. The Terraform model: `client_auth.token_endpoint_auth_signing_algorithm`,
"All asymmetric signing algorithms are allowed for Private Key JWT if value is not present";
"RSASSA-PSS is only supported with Thales Luna, Entrust nShield Connect or Java 11" (the clients
page: PS needs "an HSM and a static-key configuration"). `RS256`, the gateway's default, needs
nothing; `PS256` is the one to avoid on the evaluation container.
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_openid_provider_config_endpoint.html;
https://github.com/pingidentity/terraform-provider-pingfederate/blob/main/docs/resources/oauth_client.md

**Replay and `jti`.** Clients page: "Select Replay Prevention for PingFederate to mandate a unique
signed JWT from the client for each request, to transmit request parameters used in signed request
objects, or to do both. The underlying Assertion Replay Prevention Service is cluster-aware."
Terraform: `client_auth.enforce_replay_prevention`, "Enforce replay prevention on JSON Web Tokens.
This field is applicable only for Private Key JWT Client and Client Secret JWT Authentication". No
page names the `jti` claim as the key of that uniqueness; RFC 7523 section 3 item 7: "The JWT MAY
contain a `jti` (JWT ID) claim ... The authorization server MAY ensure that JWTs are not replayed by
maintaining the set of used `jti` values for the length of time for which the JWT would be
considered valid". The handler's assertion carries a fresh `jti` per request, so the checkbox can be
on; the Local-tier run should switch it on and watch the refresh path, where the same assertion must
never be re-sent.

**`aud`.** **Not found** as an inbound rule on the clients page, the token endpoint page, the PAR
page, the revocation page or the AS settings page. The clients page's one `aud` sentence is about
PingFederate as a client: "The aud claims of outbound Private Key JWT authorizations are set to the
issuer identifier of the PingFederate AS. The complies with RFC 7523 specfication" (sic). The 13.1
release notes, PF-36847 (Security): "We improved the way the `aud` claim is managed in
`private_key_jwt` OAuth authentications. This aligns PingFederate with RFC 7523 specifications and
security best practices", with no detail. RFC 7523 section 3 item 3: "The JWT MUST contain an `aud`
(audience) claim containing a value that identifies the authorization server as an intended
audience. The token endpoint URL of the authorization server MAY be used as a value for an `aud`
element ... The authorization server MUST reject any JWT that does not contain its own identity as
the intended audience ... the precise strings to be used as the audience for a given authorization
server must be configured out of band". The handler sets `aud` to the token endpoint URL; the
outbound sentence suggests PF also recognises its issuer identifier. Which strings 13.1 accepts
inbound is a run's fact.
https://docs.pingidentity.com/pingfederate/13.1/release_notes/pf_release_notes_131.html;
https://www.rfc-editor.org/rfc/rfc7523.html#section-3

**Lifetime.** **Not found** on any PF page read (the AS settings page has a lifetime only for DPoP
JWTs and JARM response tokens). RFC 7523 section 3 item 4: "The JWT MUST contain an `exp` ... Note
that the authorization server may reject JWTs with an `exp` claim value that is unreasonably far in
the future."

**`kid` and the pasted JWKS.** Clients page: "Choose a JSON Web Key Set (JWKS) method: In the JWKS
URL field, enter the location of the JWKS. In the JWKS field, paste the JWKS from the application.
The JWKS is a JSON object containing a `keys` array. This array holds one or more JWKs. For a single
key, the JSON structure still includes the `keys` array. ... only one of the previous values is
required for PingFederate to verify the authenticity of the JWTs." Terraform
`jwks_settings.jwks`: "Either `jwks` or `jwks_url` must be provided if private key JWT client
authentication or signed requests is enabled." **Not found**: any statement that the assertion's
`kid` must match a JWK's `kid`, that `kid` is required, or that a thumbprint is computed; the words
`kid`, `thumbprint` and `x5t` do not occur in the client-authentication part of the page (the ATM
page's "Include Key ID Header Parameter" is about tokens PF issues). Item 3's default of the RFC
7638 thumbprint as `KeyId` is therefore safe only if the pasted JWK carries the same `kid`; the
fixture should write the thumbprint into the JWK's `kid` so a key-id match, if PF performs one,
succeeds, and a one-key JWKS keeps the question moot.

**Which endpoints take it.** Token endpoint: yes (above). Revocation endpoint: "Private key JWT or
Client Secret JWT: Clients must include request parameters `client_assertion_type` and
`client_assertion` in the message body of their requests."
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_token_revoc_endpoint.html.
Introspection endpoint: the same list, and "Client authentication is not required".
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_introspec_endpoint.html.
PAR endpoint: "The PAR endpoint can accept all parameters that usually comprise an authorization
request and any additional parameters needed for client authentication" (sample uses Basic); RFC
9126 section 2.1: the request includes "any additional parameters necessary for client
authentication (e.g., `client_secret` or `client_assertion` and `client_assertion_type`)" and the
server must "1. Authenticate the client in the same way as at the token endpoint".
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_pushed_authoriz_request_endpoint.html;
https://www.rfc-editor.org/rfc/rfc9126.html#section-2.1. Not accepted: "Private Key JWT won't
work with the Session Revocation API endpoint because that endpoint only supports Client Secret or
Client TLS Certificate authentication methods" (clients page), and the Session Management API by
user identifiers says the same of itself.
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_sess_manage_api_user_identif.html.
The gateway calls neither.

**One 13.1 novelty.** PF-38828: "We added support for configuring more than one client
authentication method for an OAuth client. This feature helps you transition clients from one
authentication method to another without requiring a single cutover event." The fixture can keep a
secret beside the JWKS while the assertion path is proven, then drop it.

## 2. Pushed authorization with the assertion

**What the gateway will do (decision 7).** The profile sets `PushedAuthorizationBehavior.Require`;
the mapping's `OnPushAuthorization` adds the assertion and marks client authentication handled; the
browser is sent to the authorize endpoint with `client_id`, `request_uri` and the handler's two
telemetry parameters.

**Endpoint and lifetime.** PAR page, 13.1: "a pushed authorization requests (PAR) endpoint
`/as/par.oauth2` ... The PAR endpoint only accepts the HTTP POST method. After the AS validates the
request and saves the payload, it returns the `request_uri` parameter to serve as a reference to the
payload. The response also indicates the lifetime of the request URI. The default lifetime is 60
seconds." Sample response: `HTTP/1.1 201 Created`, `Cache-Control: no-cache, no-store`,
`request_uri` and `expires_in`. AS settings, 13.1: "PAR Reference Timeout (Seconds): Specifies the
lifetime of the request Uniform Resource Identifier (URI) that the PAR endpoint returns to the
client. The default is 60 seconds. The allowable values are 1 to 1800"; "PAR Reference Entropy
(Bytes): Specifies the length of the request URI. Longer request URIs are more secure. The default
is 24 bytes."
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_authorizationserversettingstasklet_oauthauthorizationserversettingsstate.html

**Single use.** PAR page: "PingFederate invalidates the `request_uri` when consent details are
presented to the user, or when the PAR reference timeout expires. Once consent details are
presented, the `request_uri` is invalidated regardless of whether the user accepts, rejects, or
abandons the flow. If consent details aren't presented, the client can reuse the same `request_uri`
in a subsequent authorization request until the PAR reference timeout is reached." and, in the
sample's prose, "The AS accepts a particular request URI only once." Release note PF-38880
(Improved) records the 13.1 change in the same words. RFC 9126 section 2.2: `request_uri` "is a
single-use reference to the respective request data"; section 4: "the client MUST only use a
`request_uri` value once. Authorization servers SHOULD treat `request_uri` values as one-time use
but MAY allow for duplicate requests due to a user reloading/refreshing their user agent. An
expired `request_uri` MUST be rejected as invalid"; section 7.3: "the authorization server SHOULD
make the request URIs one-time use". 13.1 is on the MAY side of that sentence. The handler pushes
once per challenge, so the gateway never reuses one; a negative test that replays a `request_uri`
should expect success before consent and refusal after, not refusal always.
https://www.rfc-editor.org/rfc/rfc9126.html#section-4

**Required and skipped.** AS settings: "PAR Status: This field determines whether clients can or
must use the PAR endpoint `/as/par.oauth2` on the authorization server to initiate authorization
flows. The default setting is Enabled." Clients page: "Select Require Pushed Authorization Requests
(PAR) to require the client to use the Pushed Authorization Requests (PAR) endpoint `/as/par.oauth2`
on the AS to initiate authorization flows. When cleared, the client can use the PAR endpoint. This
checkbox is cleared by default. ... If PAR is Enabled on the AS and required on the client, the
client must use PAR. ... If PAR is Disabled on the AS and required on the client, the client can't
access the AS." The error returned to a client that is required to push and goes straight to the
authorize endpoint is **not found** on the PAR, authorization-endpoint, clients or AS-settings pages.
RFC 9126 section 4: "Authorization server policy MAY dictate, either globally or on a per-client
basis, that PAR be the only means for a client to pass authorization request data. In this case,
the authorization server will refuse, using the `invalid_request` error code, to process any
request to the authorization endpoint that does not have a `request_uri` parameter with a value
obtained from the PAR endpoint"; sections 5 and 6 define the server and client metadata
`require_pushed_authorization_requests`, default `false`. Discovery: "When PAR is enabled on the AS, the well-known
endpoint `/.well-known/openid-configuration` includes these PAR parameters in responses:
`pushed_authorization_request_endpoint` ... `require_pushed_authorization_requests`" (PAR page);
the handler's `Require` fails closed on a document without the endpoint (decision 7), which is the
case when PAR Status is Disabled.

**What the authorize URL may carry after a push.** PAR page sample: `GET
/as/authorization.oauth2?client_id=s6BhdRkqt3&request_uri=urn%3Aexample%3Abwc4JK-ESC0w8acc191e-Y1LTC2`.
Extra front-channel parameters are tolerated: "If the name of a parameter from the subsequent
authorization request matches the name from the initial PAR request parameter, then the value from
the PAR request parameter takes precedence." The handler's two telemetry parameters are therefore
harmless. Authorization endpoint page: "`request_uri`: This parameter indicates that the client is
using the pushed authorization requests (PAR) protocol ... The payload can contain any of the
parameters that usually comprise an authorization request and any additional parameters needed for
client authentication."
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_authorization_endpoint.html

**Also in 13.1.** PF-38526: "a new configuration option to limit the Pushed Authorization Request
(PAR) to the parameters mentioned in the specification when the connection is configured to use
JWT-secured Authorization Request (JAR)": not the gateway's case (no request objects).

## 3. `form_post`

**What the gateway will do (item 3, decision 5).** The profile sets `ResponseMode = form_post` and
`SameSite=None` on `__Host-spa-corr.*` and `__Host-spa-nonce.*`; the request gate takes `POST
/signin-oidc`; the handler reads a form-encoded POST callback itself.

**The provider's side.** Authorization endpoint, 13.1: "`response_mode`: When set to `form_post`,
the authorization response is returned to the client in an auto-POST form, in accordance with the
OAuth 2.0 Form Post Response Mode specification." Discovery example: `"response_modes_supported": [
"fragment", "query", "form_post" ]`. The specification (OpenID Foundation, 27 April 2015), section
2: "Authorization Response parameters are encoded as HTML form values that are auto-submitted in
the User Agent, and thus are transmitted via the HTTP POST method to the Client, with the result
parameters being encoded in the body using the `application/x-www-form-urlencoded` format. The
action attribute of the form MUST be the Client's Redirection URI. The method of the form attribute
MUST be POST." Its example form carries `<input type="hidden" name="state" .../>` and the response
page is served `Content-Type: text/html;charset=UTF-8`, `Cache-Control: no-cache, no-store`,
`Pragma: no-cache`. So the POST the gateway receives is `application/x-www-form-urlencoded` with
`code` and `state`, which is the branch `HandleRemoteAuthenticateAsync` already reads.
https://openid.net/specs/oauth-v2-form-post-response-mode-1_0.html

**`iss` in the response.** AS settings, 13.1: "Include Issuer in Authorization Response: When
selected, PingFederate's issuer identifier `iss` parameter is returned in the authorization
response. By default, this checkbox is cleared." The page does not cite RFC 9207 and the discovery
example does not show `authorization_response_iss_parameter_supported`. The handler ignores an
unexpected `iss` form field, so either setting works; the fixture may switch it on for the mix-up
defence it buys at no cost.

**`SameSite` on the return.** **Not found** on any PF page: "Configuring the PingFederate cookie"
lists `cookie-name` (PF), `cookie-length`, `cookie-secure-flag` (true), `cookie-httponly-flag`
(true), `cookie-domain`, `cookie-max-age` (-1) and no SameSite parameter; the one SameSite text is
the 10.x upgrade note that "As of PingFederate 10.3, the Jetty configuration uses the native servlet
SameSite cookie configuration" with `org.eclipse.jetty.cookie.sameSiteDefault` set to `None` in
`jetty-runtime.xml`, which is about PingFederate's own cookies. Nothing on the provider side bears
on the gateway's cookies; item 3's `SameSite=None` is the browser rule for a cross-site top-level
POST and stands on its own.
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_pf_cookie.html;
https://docs.pingidentity.com/pingfederate/13.1/upgrading_pingfederate/pf_upgrade_considerations_introduced_pf_10x.html

## 4. Audience under `resource`

**What the gateway will do (item 5, decision 8).** `oauth.resource` reaches the authorize request
as `options.Resource`, survives the code exchange and is left off the refresh; the recommendation
is a deployment that issues JWT access tokens whose `aud` follows `resource`. Two facts were left to
a run: whether `aud` survives a refresh naming no `resource`, and whether the token endpoint
accepts `resource`.

**Selection, not filling.** Token endpoint page, 13.1 ("OAuth access token management parameters"):
"PingFederate supports multiple access token management (ATM) instances. Clients can specify an ATM
instance by providing the ATM ID (`access_token_manager_id`) or a resource URI (`aud` or
`resource`) in their requests to the PingFederate OAuth AS. ... `access_token_manager_id`: ... When
specified, PingFederate uses the desired ATM instance for the request if it is eligible; otherwise
it aborts the request. When the `access_token_manager_id` parameter is specified, PingFederate
ignores the `aud` and `resource` parameter. When the `aud` parameter is specified, PingFederate
ignores the `resource` parameter. `aud`: The `aud` is the resource URI the client wants to access.
The provided value is matched against resource URIs configured in access token management
instances. When a match is found, PingFederate uses the corresponding access token management
instance for the request if it is eligible; otherwise it aborts the request. `resource`: ... If
multiple resource parameters are requested, they must match to a single access token management
instance." Matching is exact or partial (same scheme and authority, a more specific path), exact
first. The same section is on the authorization endpoint page. The "Managing resource URIs" page:
resource URIs "can use to select this access token management instance when the `aud` or
`resource` query parameter is provided. The resource URIs must correspond to the resource that the
resource server expects."
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_beareraccesstokenmgmtplugintasklet_atmselectionsettingsstate.html

**What sets the token's `aud`.** ATM instance page, 13.1, JSON Web Tokens type: "Audience Claim
Value: The value of the Audience claim (`aud`) in the JWT. If left blank, this field is omitted.
When no value is specified, PingFederate does not validate the `aud` value, if any is included in
the access token. You can also extend the contract of the access tokens with an attribute named
`aud` in the Access Token Attribute Contract tab. When mapping attribute values from authentication
sources to the access tokens issued by this ATM instance, the value specified in the Contract
Fulfillment window overrides this value." The attribute-contract page: "Adds the Audience claim
(`aud`) to the access token." **No page read states that `resource` is copied into `aud`.** The
introspection sample's `"aud":"https://rs.example.com/resource"` beside `"client_id"` and `"scope"`
is consistent with an ATM whose Audience Claim Value is the resource URI. Configuration that gives
the gateway what item 5 recommends: one JWT ATM per API, its Resource URI equal to the registry's
`oauth.resource` and its Audience Claim Value the same string. RFC 8707 section 2: "The
authorization server SHOULD audience-restrict issued access tokens to the resource(s) indicated by
the `resource` parameter"; PF leaves the restriction to the ATM's configuration.
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_access_token_management_instance.html;
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_defining_access_token_attribute_contract.html;
https://www.rfc-editor.org/rfc/rfc8707.html#section-2

**The token endpoint and `resource`.** The three parameters are documented on the token endpoint
page itself, for "requests to the PingFederate OAuth AS", with no grant type named; so yes, as far
as the pages go. The grant-type parameters page lists, for `authorization_code`: `code`,
`code_verifier`, `redirect_uri`, `scope` ("If omitted, the scope is treated as equal to the scope
originally authorized"); for `refresh_token`: `refresh_token` and `scope` ("If omitted, the scope is
treated as equal to the original set"); neither names `resource`, `aud` or
`access_token_manager_id`, which appear only under token exchange.
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_oauth_grant_type_param.html

**A refresh naming no `resource`.** **Not found**: no page read says which ATM a refresh uses when
the request names none (the "Access token management" page's fallback is "the default ATM instance
of the client if configured", stated for requests in general, and "PingFederate uses the ATM
instance specified by the OpenID Connect policy associated with the client" for OpenID Connect).
RFC 8707 section 2.2: "In the case of a `refresh_token` or `authorization_code` grant type request,
such policy may limit the acceptable resources to those that were originally granted". With one
ATM per API and the client's default ATM set to it, a refresh that names nothing lands on the same
ATM and the same `aud`; with several ATMs eligible, the run decides. A guard the fixture can add:
the client's **Restrict to Selected ATM** ("restrict this client to using only the selected access
token manager"), so a refresh cannot wander.
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_access_token_management.html

## 5. Logout and revocation

**RP-initiated logout.** 13.1: "Endpoint: `/idp/init_logout.openid`. This endpoint supports the HTTP
GET and POST methods. When using the HTTP POST method, the required Content-Type value is
`application/x-www-form-urlencoded`." Discovery: `"end_session_endpoint":
"https://localhost:9031/idp/init_logout.openid"` and `"ping_end_session_endpoint":
"https://localhost:9031/idp/startSLO.ping"` (the asynchronous front-channel logout endpoint, not
the one the handler uses). Parameters: "`id_token_hint`: An optional parameter containing an ID
token previously issued by PingFederate to the relying party (RP). If this parameter isn't included
or can't be validated, the user is prompted to confirm the logout request." "`client_id`: An
optional parameter ... This parameter must be included if an encrypted `id_token_hint` is
provided." "`post_logout_redirect_uri`: An optional URI where the user's browser should be
redirected after a logout has been performed. If this parameter is provided, either `client_id` or
`id_token_hint` must also be included so PingFederate can determine which client is requesting the
logout. The requested `post_logout_redirect_uri` must match one of the values registered for the
client or it will be ignored." "`state`: ... If included in the logout request, PingFederate passes
this value as the `state` parameter when redirecting back to the relying party." Also: "PingFederate
prompts users to confirm the logout request if the PingFederate cookie isn't configured."
Consequence for T-B13: a refresh answer with no ID token blanks the hint, and PF then shows a
confirmation page instead of returning to `/signout-callback-oidc`; the policy's **Return ID Token
On Refresh Grant** ("Select to return an ID token when the OAuth access token is refreshed")
removes that case at the provider. Registration: the client's **Post-Logout Redirect URIs** (13.0
wording: "URIs to which the OpenID Provider (OP) can redirect the end-user's user agent after a
logout is performed, if the relying party (RP) initiated logout request includes the
`post_logout_redirect_uri` parameter and it matches one the URIs configured in this field ...
Wildcards are allowed"); the field appears only once the AS's Track User Sessions for Logout is on.
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_openid_connect_rp_logout_endpoint.html;
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_policymanagementtasklet_policymanagementstate.html;
https://docs.pingidentity.com/pingfederate/13.0/administrators_reference_guide/pf_configuring_oauth_clients.html

**Revocation (RFC 7009).** 13.1: "This endpoint accepts only the HTTP POST method. Endpoint:
`/as/revoke_token.oauth2`." Parameters: "`token` (Required): The token that the client wants to
revoke. To avoid recording this parameter in web server logs, only pass it in the message body
using the HTTP POST method." "`token_type_hint`: ... PingFederate supports values of `access_token`
and `refresh_token`." Client authentication: the client's configured method, the assertion
included (section 1). Which tokens: "Only Internally Managed Reference Tokens support direct access
token revocation. JSON web token (JWT) type access tokens do not support direct revocation. JWT
access tokens can only be indirectly revoked if the associated refresh token is revoked, and the
JWT's configuration field Access Grant GUID Claim Name is set for the given access token manager
instance. However, you can optionally enable direct revocation for self-contained JWT access tokens
assigned to them by enabling JWT access token revocation in access token managers. When enabled,
the JWTs require a Client ID Claim Name and a minimum JWT ID Claim Length of 22 alphanumeric
characters." Cascade: "When the authorization server revokes a refresh token, it also revokes the
associated access grant and access tokens. When the authorization server revokes an access token,
the associated access grant and refresh token remain untouched with the exception of the implicit
grant type." ATM page, **Enable Token Revocation**: "When selected, PingFederate will directly revoke
a JWT access token if the client that was issued the token requests its revocation. ... This
checkbox is cleared by default. When this feature is enabled, validation of access tokens may take
longer, depending on the configuration of the session revocation service." RFC 7009 section 2.2:
200 "if the token has been revoked successfully or if the client submitted an invalid token"; the
PF page gives no response codes. What `SessionEnd` does today (revoke the refresh token) is the
path that works on a JWT ATM without the checkbox, provided **Access Grant GUID Claim Name** is set,
and it also ends the access tokens; the fixture sets that claim name and may leave Enable Token
Revocation off.
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_token_revoc_endpoint.html;
https://www.rfc-editor.org/rfc/rfc7009.html#section-2.2

## 6. Back-channel logout and the logout token's claims

**What the gateway will do (item 7, ADR-0028).** `/signout-backchannel-oidc` validates the token's
mandatory steps, checks `events`, no `nonce`, `sub`, `typ` absent or `logout+jwt`, `iat`, `jti`, and
ends every session of that `sub` on the app through `ISessionIndex.RemoveSubjectAsync`; "A token
with `sid` and no `sub` is refused."

**What PingFederate documents about the token.** One sentence. 13.1, "Asynchronous Front-Channel
Logout": "You can set the logout mode for a client as Ping Front-Channel, OIDC Front-Channel, or
OIDC Back-Channel. ... When you select OIDC Back-Channel, PingFederate sends a logout token to the
client's configured Back-Channel Logout URI. This feature conforms to the OpenID Connect
Back-Channel Logout specification." The same sentence on the 13.0 clients page under **Logout
Mode** ("None: When selected, PingFederate doesn't send logout requests or tokens to the client. ...
OIDC Back-Channel: When selected, PingFederate sends a logout token to the client's Back-Channel
Logout URI") and under **Back-Channel Logout URI** ("When the Logout Mode is set to OIDC
Back-Channel, PingFederate sends a logout token to the client's Back-Channel Logout URI"); the 13.1
clients page lists the fields ("Logout Mode list, PingAccess Logout Capable checkbox, Front-Channel
Logout URIs field, Back-Channel Logout URIs field, Post-Logout Redirect URIs field") without the
per-mode text. The 11.3 release notes, where the feature arrived: "PingFederate supports this
feature whether it's configured as an OpenID Connect provider (OP) or a relying party (RP)", with a
link to the specification.
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_asynchron_front_channel_logout.html;
https://docs.pingidentity.com/pingfederate/13.1/release_notes/pf_release_notes_113.html

**`sub`, `sid`, `sid`-only: not found.** No page read states which claims the logout token carries.
Pages read for it, all 13.1 unless marked: Configuring OAuth clients (13.1 and 13.0); Asynchronous
Front-Channel Logout; Configuring authorization server settings; Configuring tracking options for
logout; Sessions; Configuring authentication sessions; OpenID Connect RP-initiated logout endpoint;
OpenID Provider configuration endpoint; Configuring policy and ID token settings; Session Revocation
API endpoint; Back-Channel Session Revocation; Session Management API by user identifiers; Security
audit logging; Creating an OpenID Connect IdP connection; OpenID Connect Relying Party support;
Single logout; OAuth Client Management Service; release notes 11.3, 13.1 and 13.1.1; the Terraform
`oauth_client` document. The 13.1.1 note PF-39587, "a defect that caused the subject field to be
missing from the SLO audit log entries after OpenID Connect (OIDC) backchannel logout in Redis
mode", is about the audit log, not the token.

**The inference, labelled as such.** Four facts point the same way and none is a statement about
the token. (a) The word `sid` occurs on none of the pages above; the discovery example's
`claims_supported` has `sub` and no `sid`. (b) PingFederate's session identifier in ID tokens is
its own claim: OIDC policy, "Include Session Identifier in ID Token: Select to add a session
identifier (`pi.sri`) in the ID tokens. Doing this could be useful for the relying parties, such as
PingAccess for client session management"; the Session Revocation API's "ID value corresponds to
that of the `pi.sri` element in the ID token". The specification ties `sid` in logout tokens to
`sid` in ID tokens ("If supported, the `sid` Claim is also included in ID Tokens issued by the OP").
(c) The client record has no `backchannel_logout_session_required`: not in the Terraform
`oidc_policy` block (`logout_mode`, `back_channel_logout_uri`, `logout_uris`,
`post_logout_redirect_uris`, `ping_access_logout_capable`, `id_token_signing_algorithm`,
`sector_identifier_uri`, `pairwise_identifier_user_type`, `grant_access_session_revocation_api`,
`grant_access_session_session_management_api`, `policy_group`) and not in the OAuth Client
Management Service page. (d) The 13.1 discovery example shows no `backchannel_logout_supported` and
no `backchannel_logout_session_supported` (an example, not a claim that a live server omits them;
the PAR fields are also absent from it and appear "When PAR is enabled"). The specification,
section 2.1: "`backchannel_logout_session_supported`: OPTIONAL. Boolean value specifying whether the
OP can pass a `sid` (session ID) Claim in the Logout Token to identify the RP session with the OP.
... If omitted, the default value is `false`." Section 2.4: "A Logout Token MUST contain either a
`sub` or a `sid` Claim, and MAY contain both. If a `sid` Claim is not present, the intent is that
all sessions at the RP for the End-User identified by the `iss` and `sub` Claims be logged out."
So: a conforming token without `sid` carries `sub`, which is ADR-0028's shape; a `sid`-only token
would need a session claim PingFederate's documentation never names. This is the expectation the
prototype must execute, with a logging endpoint, before the spec relies on it; the deployed run
records it on the profile ticket as decision 8 asks.
https://openid.net/specs/openid-connect-backchannel-1_0.html;
https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_session_revocation_api_endpoint.html;
https://github.com/pingidentity/terraform-provider-pingfederate/blob/main/docs/resources/oauth_client.md

**The other claims, from the specification PingFederate says it conforms to.** Section 2.4: `iss`,
`aud`, `iat`, `exp`, `jti` and `events` REQUIRED ("`events`: ... MUST contain the member name
`http://schemas.openid.net/event/backchannel-logout` ... The corresponding member value MUST be a
JSON object and SHOULD be the empty JSON object `{}`"); "`nonce`: PROHIBITED. A nonce Claim MUST NOT
be present"; "It is RECOMMENDED that Logout Tokens be explicitly typed. This is accomplished by
including a `typ` (type) Header Parameter with a value of `logout+jwt`." Section 2.5: "The OP uses an
HTTP POST to the registered back-channel logout URI ... The POST body uses the
`application/x-www-form-urlencoded` encoding and must include a `logout_token` parameter ... The
POST body MAY contain other values in addition to `logout_token`. Values that are not understood by
the implementation MUST be ignored." Section 2.8: "If the logout succeeded, the RP MUST respond with
HTTP 200 OK ... If the logout request was invalid or the logout failed, the RP MUST respond with
HTTP 400 Bad Request." Section 4: "OPs are encouraged to use short expiration times in Logout
Tokens, preferably at most two minutes in the future". Whether PingFederate sets `typ`, and the
`exp` it uses, are **not found**; ADR-0028 accepts `typ` absent or `logout+jwt`, so either answer
passes.

**What triggers it.** AS settings, 13.1, **Track User Sessions for Logout**: "When selected,
PingFederate links the sessions for identity provider (IdP) adapters that are used by clients to
the PingFederate authentication session of the resource owner. When a user initiates logout,
PingFederate sends logout requests to close the adapter sessions through the browser. Selecting
this checkbox also lets you configure the logout mode and logout endpoints for each client. ... By
default, this checkbox is cleared." The Asynchronous Front-Channel Logout page adds the SAML and
WS-Federation case: "when signing off an SLO-enabled SAML 2.0 or WS-Federation session, because the
service provider (SP)-initiated logout request reaches the PingFederate identity provider (IdP)
server, the same logout process applies as well." So a user's logout at `/idp/init_logout.openid`
(the gateway's own RP-initiated logout, from any app's session) or at `/idp/startSLO.ping` is the
documented trigger. **Not found**: whether authentication-session expiry (idle or max timeout)
sends a logout token; whether `POST /pf-ws/rest/sessionMgmt/users/{user_key}/revoke` (which
"revokes all PingFederate runtime authentication sessions associated with the given user
identifier", audit event `SRI_REVOKED`) sends one; the Session Management API page and the
authentication-sessions page say nothing about notifying clients. The Back-Channel Session
Revocation feature is a different mechanism (clients poll `/pf-ws/rest/sessionMgmt/revokedSris`
with the `pi.sri`), not back-channel logout.

**Retries.** **Not found** on any PF page. Specification section 2.5: "The OP should not retransmit
a Back-Channel Logout Request unless the OP suspects that previous transmissions may have failed
due to potentially recoverable errors (such as network outage or temporary service interruption at
either the OP or RP)." ADR-0028's per-task `jti` memory and its acceptance of a replay that reaches
another task are the right fit for an OP that may retransmit once.

**The settings the fixture needs.** AS: Track User Sessions for Logout on. Client: Logout Mode
`OIDC Back-Channel` (Terraform `oidc_policy.logout_mode = "OIDC_BACK_CHANNEL"`, default `NONE`) and
`back_channel_logout_uri` = `https://{host}/signout-backchannel-oidc`, one client per app, as
ADR-0028 decision 1 asks; the 13.1 UI labels the field "Back-Channel Logout URIs" (plural) where
13.0 and the admin model say one URI.

## 7. Session, `prompt=none` and `max_age` (a note for the fragments map #765)

Authentication sessions, 13.1: "Idle Timeout: ... You can enter an integer that represents a time
period between 1 minute and 1,095 days. The default inactivity timeout value is 60 minutes"; "Max
Timeout: ... The default maximum timeout value is 480 minutes (eight hours)" and it "cannot be less
than the Idle Timeout"; emptying Max Timeout means sessions "do not expire until they are removed
from the system". Sessions are off until enabled: "Enable Sessions for All Authentication Sources"
or per source, each "not selected by default"; **Persistent** sets the `PF.PERSISTENT` cookie,
default "94608000 seconds, or 3 years", and "require[s] an external storage". Sessions page: "the
authentication session is extended by the idle timeout value" on each request inside the window,
until the max timeout. The HTML Form Adapter carries the same 60/480 defaults of its own. Against
#765's eager chain: the silent window is an hour of inactivity, extended by any visit to any app on
the same PingFederate, capped at eight hours by default, both policy-set.
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_config_auth_sessions.html;
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_sessions.html

Authorization endpoint, 13.1: "`prompt`: Specifies whether the AS prompts the end user for
reauthentication and consent. ... PingFederate supports values of `none`, `login`, `consent`, and
`create`." "`max_age`: Sets an allowable elapsed time in seconds since the end user last
authenticated. If the elapsed time exceeds the value of `max_age`, PingFederate prompts the end
user for authentication. The user's last authenticated time is tracked by Authentication Sessions
and various adapters that have built-in session capabilities, such as the HTML Form Adapter." The
error a `prompt=none` request gets with no session is **not found** on the page; OpenID Connect
Core 1.0 section 3.1.2.6: "`login_required`: The Authorization Server requires End-User
authentication. This error MAY be returned when the `prompt` parameter value in the Authentication
Request is `none`". https://openid.net/specs/openid-connect-core-1_0.html#AuthError

## What was executed and what was only read

**Executed.** Nothing against PingFederate, AWS or the cell. On this machine: `curl` downloads of
the specification texts (RFC 7523, RFC 9126, RFC 8707, RFC 7009 from rfc-editor.org; Back-Channel
Logout 1.0, Form Post Response Mode 1.0 and OpenID Connect Core 1.0 from openid.net) and of
twelve `docs.pingidentity.com` pages (a thirteenth returned 404), with a local Python script that stripped the HTML and
printed the sentences quoted above; `WebFetch` summaries of the other pages, each load-bearing
sentence then confirmed against the downloaded text where one existed; `gh issue view` on #946 and
#944; `git` for the worktree and the branch.

**Read, all on 2026-11-04.** PingFederate 13.1 unless marked:

- Configuring OAuth clients, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_oauth_clients.html, and the 13.0 page for the Logout Mode descriptions, https://docs.pingidentity.com/pingfederate/13.0/administrators_reference_guide/pf_configuring_oauth_clients.html
- Configuring authorization server settings, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_authorizationserversettingstasklet_oauthauthorizationserversettingsstate.html
- Token endpoint, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_token_endpoint.html
- OAuth grant type parameters, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_oauth_grant_type_param.html
- Authorization endpoint, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_authorization_endpoint.html
- Pushed authorization requests endpoint, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_pushed_authoriz_request_endpoint.html
- Token revocation endpoint, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_token_revoc_endpoint.html
- Introspection endpoint, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_introspec_endpoint.html
- OpenID Connect RP-initiated logout endpoint, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_openid_connect_rp_logout_endpoint.html
- OpenID Provider configuration endpoint, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_openid_provider_config_endpoint.html
- Asynchronous Front-Channel Logout, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_asynchron_front_channel_logout.html
- Back-Channel Session Revocation, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_backchannel_sess_revocat.html
- Session Revocation API endpoint, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_session_revocation_api_endpoint.html
- Session Management API by user identifiers, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_sess_manage_api_user_identif.html
- Configuring tracking options for logout, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_config_tracking_options_for_logout.html
- Sessions, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_sessions.html
- Configuring authentication sessions, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_config_auth_sessions.html
- Configuring policy and ID token settings, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_policymanagementtasklet_policymanagementstate.html
- Access token management, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_access_token_management.html
- Configuring an access token management instance, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_access_token_management_instance.html
- Defining the access token attribute contract, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_defining_access_token_attribute_contract.html
- Managing resource URIs, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_beareraccesstokenmgmtplugintasklet_atmselectionsettingsstate.html
- Security audit logging, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_security_audit_logging.html
- Configuring the PingFederate cookie, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_pf_cookie.html
- Upgrade considerations introduced in PingFederate 10.x, https://docs.pingidentity.com/pingfederate/13.1/upgrading_pingfederate/pf_upgrade_considerations_introduced_pf_10x.html
- Creating an OpenID Connect IdP connection, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_creating_oidc_idp_connection.html
- OpenID Connect Relying Party support, https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_oidc_relying_party_support.html
- Single logout, https://docs.pingidentity.com/pingfederate/13.1/introduction_to_pingfederate/pf_single_logout.html
- OAuth Client Management Service, https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_oauth_client_manage_service.html
- Release notes 13.1 (June 2026), https://docs.pingidentity.com/pingfederate/13.1/release_notes/pf_release_notes_131.html; 13.1.1 (July 2026), https://docs.pingidentity.com/pingfederate/13.1/release_notes/pf_release_notes_1311.html; 11.3 (June 2023), https://docs.pingidentity.com/pingfederate/13.1/release_notes/pf_release_notes_113.html
- Terraform provider `pingidentity/pingfederate`, resource `pingfederate_oauth_client`, https://github.com/pingidentity/terraform-provider-pingfederate/blob/main/docs/resources/oauth_client.md
- OpenID Connect Back-Channel Logout 1.0, Final incorporating errata set 1 (15 December 2023), https://openid.net/specs/openid-connect-backchannel-1_0.html
- OAuth 2.0 Form Post Response Mode (27 April 2015), https://openid.net/specs/oauth-v2-form-post-response-mode-1_0.html
- OpenID Connect Core 1.0, section 3.1.2.6, https://openid.net/specs/openid-connect-core-1_0.html
- RFC 7523, https://www.rfc-editor.org/rfc/rfc7523.html; RFC 9126, https://www.rfc-editor.org/rfc/rfc9126.html; RFC 8707, https://www.rfc-editor.org/rfc/rfc8707.html; RFC 7009, https://www.rfc-editor.org/rfc/rfc7009.html
- In the repository: `contracts/changes/p2-6-pingfederate-profile.md`, `docs/adr/0028-back-channel-logout-outside-bff-by-subject.md`, `GLOSSARY.md`, and `docs/research/testidp-n-clients-one-host.md` on its branch, for the shape of this file.

A page not in this list was not read; a page the fact-check of the Ping products cited but this
list omits was not relied on. The `pf_oauth_client_session_management` page the 13.1 navigation
names returned 404 and is not cited.
