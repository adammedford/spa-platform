# Test users and claims on PingFederate: the credential validator, the attribute contract and what the fixtures and suites assert

- **Ticket:** #947, "Test users and claims on PingFederate: the credential validator, the attribute
  contract and what the fixtures and suites assert" (`wayfinder:research`), on the map #944.
- **Date:** 2026-11-04. **Branch:** `research/pingfederate-test-users-and-claims`, pushed and never
  merged, as the map rules.
- **Method:** docs-only. Repository reading at commit `8ceeee3` (origin/main on 2026-11-04):
  `fixtures/cognito/pool.json`, `fixtures/MockApi`, `fixtures/apps`, `fixtures/bff-client.js`,
  `fixtures/spa`, `fixtures/TestIdp`, `fixtures/Fixtures.Tests`, `tests/e2e/{Functional,Negative,
  Deployed,Stack}`, `tests/Gateway.TestHarness`, the composed suites under
  `tests/Gateway.IntegrationTests` and `tests/Gateway.SecurityTests` (grep hits and their context),
  `src/Platform.Identity` (read only, for which claims the gateway consumes), `infra/envs/poc/idp`,
  `infra/modules/idp-cognito`, `infra/envs/poc/apps/main.tf`, `contracts/upstream-contract.md`,
  `contracts/http-contract.md`, `contracts/infra-outputs.schema.json`,
  `contracts/changes/p2-6-pingfederate-profile.md`, `docs/plan/04-tests.md`, ADR-0017 and the G0
  Cognito run's output. Provider facts come from pages of the PingFederate 13.1 administrator's and
  developer's reference guides on docs.pingidentity.com, the `pingidentity/terraform-provider-pingfederate`
  repository on GitHub, and three pages of the Amazon Cognito developer guide, all fetched on
  2026-11-04 (section 4 lists each). Docker Desktop 29.9.1 (arm64, Compose v5) is present on this
  machine, but no Ping DevOps user key or PingFederate licence exists, so **nothing was executed
  against PingFederate**: every provider claim below is read, and a claim no page states is marked
  "not found". AWS credentials exist and `docs/agents/aws-rules.md` governs them: no apply, no plan,
  no credential step, no read of the cell; `infra/` was read as files. No tracked file other than
  this document was changed. No password or token value appears here.

## Answer in brief

1. **What the test surface depends on.** Two usernames, `alice` and `bob`, two resource servers,
   `https://orders.spa-poc.test` and `https://reports.spa-poc.test`, each with `read` and `admin`
   (`fixtures/cognito/pool.json`), and the apps' scopes `openid profile {identifier}/read`, with
   `oauth.resource` on app 1 only (`fixtures/apps/app1.json:6-7`, `app2.json:6`). The mock API, the
   only consumer of the access token, reads five claims: `iss` (exact), `exp` (no skew),
   **`token_use == "access"` unconditionally**, `client_id` (admission and `/whoami`), `scope` as a
   **space-delimited string**, and `aud` only where a listener binds one
   (`fixtures/MockApi/MockApiHost.cs:121-151`, `Routes.cs:40-44,81-116`); `sub` is reported but not
   checked there. The gateway keeps exactly `sub`, `name` and `email` from the ID token, and throws
   without `sub` (`src/Platform.Identity/AuthenticationEvents.cs:21`, `AppBoundTicketStore.cs:90-92`).
   The composed suites assert `client_id`, the read scope, `audience == oauth.resource` (null for
   app 2), the two refusals `client_not_admitted` and `insufficient_scope`, and, in the hermetic tier
   only, `subject == DefaultSubject` and `/bff/session.sub == DefaultSubject`. No suite asserts
   `name` or `email`. The deployed suites read a test user's password with
   `aws ssm get-parameter --with-decryption` on the parameter the outputs name,
   `/spa-platform/<cell>/test-users/<username>`, and type it into Cognito's managed-login form
   (`tests/e2e/Stack/CellAws.cs:32-38`, `DeployedStack.cs:252-273`). Section 1 has the table.
2. **How PingFederate supplies the shape.** The Simple Username Password Credential Validator holds
   users as rows of a `Users` table: a user name and a password "entered twice", plain in the
   console; the admin API and Terraform take the password as a sensitive `value` or as an
   `encrypted_value`, "the encrypted representation of the field's value" for "encrypted or hashed
   fields", and the password policy in `password-rules.xml` applies to its passwords; whether a hash
   computed outside PingFederate is accepted is not found. The HTML Form Adapter's contract has two
   core attributes, `username` and `policy.action`; the IdP adapter grant mapping makes `username`
   the `USER_KEY`, "the identifier of the persistent grants". An OpenID Connect policy's contract
   "begins with a list of standard attributes" and "always contains ... `sub`"; `email` and `name`
   are fulfilled from Access Token, Persistent Grant, Text (with `${attribute}` references), Expression
   (OGNL, enabled by editing `ExpressionManager.xml`), a data store, or No Mapping; delivery defaults
   to UserInfo unless "Include User Info in ID Token" or "Override Default Delivery → ID Token" is
   set; the Attribute Scopes tab fixes each standard scope's standard attributes and lets extra ones
   be attached. Scopes are static text values, case-sensitive, "common" (all clients by default) or
   "exclusive" (no client by default), restricted per client by "Restrict Common Scopes" and
   "Exclusive Scopes", with `invalid_scope` for the rest. The JSON Web Token access token manager
   writes `iss` and `aud` from static "Issuer Claim Value" and "Audience Claim Value" fields, each
   "omitted" when blank, `client_id` and `scope` under configurable names defaulting to those, the
   scope as a **JSON array unless "Space Delimit Scope Values" is selected** (not selected by default),
   `jti` and `iat`; its attribute contract can be extended, and a request's `resource` or `aud`
   selects the manager whose Resource URIs match. No page read says the issued `aud` is copied from
   the request: the documented path is one manager per resource with a static audience. Section 2.
3. **The gaps.** Cognito's, not the gateway's: `token_use` (the mock API's unconditional check and
   the upstream contract's "For Cognito" clause); the space-delimited `scope` string the mock API
   splits; `aud` absent when no `resource` was sent; the managed-login form fill
   (`input[name='username']`, `input[name='password']`, the `/logout` path) and the required
   `cognitoDomain` output; `email` arriving under `profile` (Cognito's `profile` is "all user
   attributes", PingFederate's follows the standard). PingFederate can reproduce every one of them
   by configuration except that two are unproven on paper: that an extended contract attribute
   (`token_use`) appears as a JWT claim, and that `aud` is kept across a refresh that names no
   `resource`. `sub` as a UUID is Cognito's and asserted nowhere in the deployed tier; the hermetic
   assertions `subject == DefaultSubject` are the stand-in's and would hold on a PingFederate whose
   `USER_KEY` is the username. `name` ("Alice Example") has no source in the credential validator and
   may be null, which the browser contract permits. Section 3 lists them with the file:line each
   assertion lives at.

## 1. What the repository depends on

Read at `8ceeee3`. "H" is the hermetic tier (the stand-in `fixtures/TestIdp` behind
`GatewayHarness`), "D" the deployed tier (Cognito in the poc cell), "both" a row of `tests/e2e`
that runs against either stack. "Neutral" means any OpenID Connect provider configured for it
supplies the shape; "Cognito's" means the shape is Cognito's and the test or fixture takes it from
there.

### 1.1 Users, attributes and passwords

| Item | Value today | Where it is defined, read or asserted | Provider |
| --- | --- | --- | --- |
| Usernames | `alice`, `bob` | Defined `fixtures/cognito/pool.json:23,28`; created `infra/modules/idp-cognito/main.tf:118-124` (`username = each.key`); the stand-in's default subject `fixtures/TestIdp/TestIdentityProvider.cs:78` and `login_hint` switch `:308-316`; the deployed stack's `User`/`OtherUser` `tests/e2e/Stack/DeployedStack.cs:48,51`, held to the pool `tests/e2e/Deployed/DeployedStackTests.cs:30-37`; `SignInAs` maps to `DefaultSubject` in H `tests/e2e/Stack/LocalStack.cs:174` and to the form fill in D `DeployedStack.cs:291`; a second person in N-04/N-06/N-26 `tests/e2e/Negative/CsrfTests.cs:32,38`, `LoginTests.cs:62,66`, `CookiePlantingTests.cs:76,83`; fixture tests `fixtures/Fixtures.Tests/TestIdpCodeGrantTests.cs:51-55` | Neutral: two users of these names |
| Username matching | Case-insensitive pool | `infra/modules/idp-cognito/main.tf:39-41` (`case_sensitive = false`); the suites type the lower-case names only | Cognito's setting; nothing depends on it |
| `email` attribute | `alice@spa-poc.test`, `bob@spa-poc.test` | `pool.json:24,29`; set as the Cognito attribute with `email_verified = "true"` `main.tf:126-130`; held non-empty and distinct `fixtures/Fixtures.Tests/CognitoPoolFixtureTests.cs:46,58`; the stand-in derives `subject + "@spa-poc.test"` `TestIdentityProvider.cs:553` | Neutral value; no suite asserts it (section 1.3) |
| `name` attribute | `Alice Example`, `Bob Example` | `pool.json:25,30`; `main.tf:129`; held non-empty `CognitoPoolFixtureTests.cs:47`; the stand-in issues `subject + " (display)"` `TestIdentityProvider.cs:552` | Neutral value; no suite asserts it |
| Password | Never in the fixture; generated | `pool.json:2` ("No password is here"), asserted absent `CognitoPoolFixtureTests.cs:48`; `random_password` of `var.password_length` with one of each class and the symbol set `!#%*-_=+` `main.tf:99-112`; pool policy 16+ characters, all classes `main.tf:45-52`; set permanent with `message_action = "SUPPRESS"` `main.tf:118-124` | Neutral mechanism, Cognito's resource |
| Password parameter | `/spa-platform/<cell>/test-users/<username>`, `SecureString`, `alias/aws/ssm` | Written `main.tf:19,135-143`; named in the outputs as `testUsers.<user>.passwordParameter`, never the value, `infra/modules/idp-cognito/outputs.tf:34-40`, `infra/envs/poc/idp/outputs.tf:29-32`, schema `contracts/infra-outputs.schema.json:89-101`; read by `aws ssm get-parameter --name <parameter> --with-decryption --query Parameter.Value` in the cell's Region `tests/e2e/Stack/CellAws.cs:32-38`, `Cell.cs:104-107`; held in `Password`, which prints as `[password]` `CellAws.cs:187-196`; tests `tests/e2e/Deployed/CellTests.cs:69-70`, `CellAwsTests.cs:18-52`; the recipe `infra/envs/poc/idp/README.md:11,27-39`; a double `tools/scripts/cell_doubles.py:165` | Neutral: a parameter name per user, whatever provider holds the user |
| Login form | Cognito managed login on the cell's `cognitoDomain` | `Cell.cs:26,49,55,61` (`ProviderHost = Field("cognitoDomain")`, required); the fill: `input[name='username']:visible`, `input[name='password']:visible`, `button[type='submit']:visible`, a second step if the password field is hidden `DeployedStack.cs:225-273`; "stopped at the provider's login" means the host is `ProviderHost` and the path is not `/logout` `DeployedStack.cs:345-353`; H needs no form: the stand-in "approves at once" `LocalStack.cs:173-174`, `GatewayHarness.cs:455-457` | Cognito's: field names, host output, logout path |

### 1.2 Access-token claims and scopes

| Claim or value | What is depended on | Where | Provider |
| --- | --- | --- | --- |
| `iss` | Equals the configured issuer, exactly; keys from that issuer's discovery document | `fixtures/MockApi/MockApiOptions.cs:9-17`, `MockApiHost.cs:114-126` (`ValidIssuer`, `ValidateIssuerSigningKey`), `Program.cs:21-24` (metadata defaults to `<issuer>/.well-known/openid-configuration`) | Neutral |
| `exp`, `iat` | Lifetime validated with `ClockSkew = TimeSpan.Zero`; five-minute access tokens so a refresh happens in a run | `MockApiHost.cs:127-130`; `infra/envs/poc/apps/main.tf:89-99` (`access_token_validity = 5`); `tests/e2e/Stack/DeployedStack.cs:53-57`; the expiry row `tests/e2e/Functional/SessionLifecycleTests.cs:24-31` | Neutral (configuration) |
| `token_use` | Must be `access`; any other value fails validation with "token_use is '…', not 'access'" | `MockApiHost.cs:141-151` (unconditional, every listener); `contracts/upstream-contract.md:15` ("For Cognito, `token_use` is `access`"); the stand-in issues it `TestIdentityProvider.cs:519` and a fault switch flips it `TestIdpFaults.cs:67`, proved in `fixtures/Fixtures.Tests/MockApiTests.cs:190-193`, `TestIdpFaultSwitchTests.cs:192-199` | **Cognito's** ([access token page](https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-the-access-token.html): "In an access token, its value is `access`") |
| `client_id` | The admitted client per listener; `/whoami.clientId`; the N-17 and F-07 refusals | `Routes.cs:34-44,88-92,107-111`, `ClientAdmission.cs:21`; `MockApiOptions.cs:39,46`; asserted `tests/e2e/Functional/ApiRouteTests.cs:44`, `OnboardingTests.cs:53-57` (`client_not_admitted`), `tests/e2e/Negative/ApiTests.cs:80-107`, `HostTests.cs:56-60`, `SessionLifecycleTests.cs:31`, `ExposureTests.cs:85`; composed `tests/Gateway.IntegrationTests/Functional/MockApiRouteTests.cs:47,74-85` | Claim name is Cognito's; PingFederate's default claim name is the same (section 2.6) |
| `scope` | A single space-delimited string, split on `' '`; must contain `{identifier}/read` for `/whoami` and `/echo`, `{identifier}/admin` for `/admin` | `Routes.cs:95-97,113-116`, `MockApiOptions.cs:75-82`; asserted `ApiRouteTests.cs:28,45,49-53` (`insufficient_scope`), `MockApiRouteTests.cs:33-34,49,57-66`; values from `fixtures/apps/app1.json:6`, `app2.json:6` (`openid`, `profile`, `https://orders.spa-poc.test/read`, `https://reports.spa-poc.test/read`); the resource servers and their `read`/`admin` scopes `pool.json:3-20`, created `main.tf:76-93`; the apps root refuses a scope no resource server offers `infra/envs/poc/apps/main.tf:50-54,124-128`; the fixture holds the apps to the pool `CognitoPoolFixtureTests.cs:61-96` | Scope **names** are neutral (the pool's own, RFC 6749 strings); the **string format** is Cognito's ([access token page](https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-the-access-token.html) sample `"scope":"phone openid profile resourceserver.1/appclient2 email"`); the stand-in echoes the request's scope `TestIdentityProvider.cs:520,578` |
| `aud` | On `orders` (port 5080) must equal `https://orders.spa-poc.test`; on `reports` not validated because "app 2 passes no `resource`, so its token carries no `aud` at all"; `/whoami.audience` must equal `oauth.resource`, which is **null for app 2** | `MockApiOptions.cs:33-46,59-63`, `MockApiHost.cs:132-136`; asserted `ApiRouteTests.cs:30,46`, `MockApiRouteTests.cs:36,50`; N-17 `tests/e2e/Negative/ApiTests.cs:109-117` (`invalid_token` for app 2's token on `orders`, with the H check that the authorization sent no `resource` at `:113`); the request side `src/Platform.Identity/OidcOptionsSetup.cs:75` (`options.Resource = app.OAuth.Resource`), the stand-in maps `resource` to `aud` `TestIdentityProvider.cs:508-512`; Cognito's behaviour recorded `spikes/g0/cognito/output-cognito.txt:55`, ADR-0017 (`docs/adr/0017-*.md:36`, C-2.7 and C-2.9) and `contracts/changes/p2-6-pingfederate-profile.md:161-175` (item 5: `aud` is "the provider's token format") | RFC 8707 is neutral; "`aud` = the one `resource`, absent otherwise, kept across a refresh that sends none" is Cognito's ([resource binding](https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-define-resource-servers.html#cognito-user-pools-resource-binding): "sets the requested URI as the audience in the `aud` claim", "Token-refresh grants ... carry over the `aud` claim") |
| `sub` | Reported as `/whoami.subject`; not checked by the mock API | `Routes.cs:41`; asserted equal to `harness.Idp.DefaultSubject` in H only `MockApiRouteTests.cs:48`; the e2e F-08 does not read it `ApiRouteTests.cs:40-47` | Neutral presence; the H value (`alice`, the username) is the stand-in's shape; Cognito's is a UUID ("The username might not be unique ... `sub` is the best way to identify a given user") |
| `username` | Issued by the stand-in; consumed by nothing | `TestIdentityProvider.cs:517`; no reader in `src/`, `tests/` or `fixtures/MockApi` (grep) | Cognito's, unused |
| `jti` | Issued; not asserted | `TestIdentityProvider.cs:521` | Neutral |
| Cognito-only claims | `origin_jti`, `event_id`, `version`, `cognito:groups`, `device_key` | No occurrence in `src/`, `tests/` or `fixtures/` outside comments (grep) | Cognito's, unused |

### 1.3 ID-token claims and the browser's view

| Claim | What is depended on | Where | Provider |
| --- | --- | --- | --- |
| `sub` | Kept at sign-in; the session record's `Subject`, which throws when absent; returned by `GET /bff/session`; item 7's logout index removes by it | `src/Platform.Identity/AuthenticationEvents.cs:21,100-113` (`KeptClaims`, `MapInboundClaims = false` in `OidcOptionsSetup.cs:67`), `AppBoundTicketStore.cs:88-92`, `Bff.cs:62-68`; `contracts/http-contract.md:15`; asserted equal to `DefaultSubject` in H `tests/Gateway.IntegrationTests/Functional/SignInTests.cs:107`; the index decision `contracts/changes/p2-6-pingfederate-profile.md:520-534`; `fixtures/bff-client.js:16-33` | Neutral presence and stability; the value's shape is the provider's |
| `name`, `email` | Kept, returned nullable, shown on the fixture pages; asserted by no suite | `AuthenticationEvents.cs:21`, `Bff.cs:66-67`; `contracts/http-contract.md:15` ("`name` and `email` may be null"); `fixtures/spa/app1/index.html:18-20` and `fixtures/spa/app*/assets/app.*.js:48-50`; `tests/e2e/Functional`, `Negative`, `Deployed` and the composed suites contain no `email` or `name` assertion (grep); the gateway never calls the UserInfo endpoint (no `UserInfo` in `src/`, grep) | Neutral, display-only. Under Cognito both arrive because the apps request `openid profile` and Cognito's `profile` "Authorizes all user attributes that the app client can read" ([scopes page](https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-define-resource-servers.html)); `email` as its own scope is the standard's |
| `aud`, `nonce`, `at_hash`, `token_use: id`, `auth_time`, `cognito:username`, `email_verified` | The OpenID Connect handler validates `aud`, `nonce` and `at_hash`; the rest is dropped at sign-in | `AuthenticationEvents.cs:21,100-113`; the stand-in issues them `TestIdentityProvider.cs:549-566` | Protocol claims neutral; `token_use`, `cognito:username`, `email_verified` Cognito's, unused |

### 1.4 Request-side values the suites assert

| Value | Where | Provider |
| --- | --- | --- |
| `client_id` and `redirect_uri` on the authorize request | `tests/e2e/Functional/SignInTests.cs:54-60`, `SessionLifecycleTests.cs:77`, `OnboardingTests.cs:48`, `tests/e2e/Negative/SessionTests.cs:53` | Neutral |
| The authorize and logout endpoint shapes | `tests/e2e/Stack/Cell.cs:55,61` (`/oauth2/authorize`, `/logout` on the managed-login host) | Cognito's (the `cognito` profile, ADR-0011) |
| H-only provider reads (`local.Harness.Idp.Grants`, `.Authorizations`, `.DefaultSubject`) guarded by `stack is LocalStack` | `tests/e2e/Negative/ApiTests.cs:111-114`, `LoginTests.cs:72-77`, `CookiePlantingTests.cs:105-108`, `SessionLifecycleTests.cs:34` | The stand-in's; a third stack skips them by the guard |

## 2. How PingFederate supplies the shape

All pages are PingFederate 13.1 unless noted, read on 2026-11-04.

### 2.1 The Simple Username Password Credential Validator

- **Users and passwords.** "This validator is best used for testing purposes or for an organization
  with few accounts." Users are rows of a table: "On the Instance Configuration tab, click Add a new
  row to 'Users'. Enter a user name, followed by a password (twice). Click Update in the Action
  column." The order of rows is the order PingFederate tries them
  ([Configuring the Simple Username Password Credential Validator](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configure_simple_username_pcv.html)).
  The page says nothing about entering a hash, about case sensitivity of the user name, or about
  the validator's attribute contract: all three are **not found** on it.
- **Plain or hashed in configuration.** The Terraform provider's resource, which fronts the admin API, shows the
  shape in its example: a table `Users`, each row with the fields `Username` and `Relax Password Requirements`
  and the sensitive fields `Password` and `Confirm Password`, both set from a Terraform variable;
  `plugin_descriptor_ref` id `org.sourceid.saml20.domain.SimpleUsernamePasswordCredentialValidator`.
  A sensitive field takes either `value` ("The sensitive value for the configuration field",
  marked Sensitive) or `encrypted_value`: "For encrypted or hashed fields, this attribute contains
  the encrypted representation of the field's value, if a value is defined"
  ([password_credential_validator.md](https://github.com/pingidentity/terraform-provider-pingfederate/blob/main/docs/resources/password_credential_validator.md)).
  So a server profile or an admin-API call supplies the password **in plain text** (the `value`),
  or re-supplies a representation PingFederate itself produced earlier (the `encrypted_value`).
  Whether a hash computed outside PingFederate is accepted as `encrypted_value`: **not found**.
- **Password rules apply.** "Passwords used by instances of the Simple Username Password Credential
  Validator (PCV)" are governed by the password policy in
  `<pf_install>/pingfederate/server/default/data/config-store/password-rules.xml`, changed by
  editing the file and restarting
  ([Configuring a password policy](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_config_password_policy.html)).
  The default rules are not on the page (**not found**); the row field `Relax Password Requirements`
  in the Terraform example is the per-user switch, not described on the docs page read.
- **Attribute contract.** The Terraform schema has `attribute_contract.core_attributes`, "a list of
  read-only attributes that are automatically populated by the password credential validator
  descriptor", and `extended_attributes`; the example sets `attribute_contract = {}`. The SDK guide
  says a validator returns "an AttributeMap containing at least one entry representing the
  principal" ([Developing password credential validators](https://docs.pingidentity.com/pingfederate/13.1/sdk_developers_guide/pf_develop_pass_credent_vallidat.html)).
  The name of that entry for this validator is **not found** on the pages read; the adapter that
  wraps it names its own `username` (2.2). The validator carries no per-user `email` or `name`: the
  rows have `Username`, `Password` and `Relax Password Requirements` only.

### 2.2 The HTML Form IdP adapter

- **Contract.** "The HTML Form Adapter contract includes two core attributes: username and
  policy.action"; extended attributes may be added on the Extended Contract tab
  ([Configuring an HTML Form Adapter instance](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_config_html_form_adapt_instance.html)).
  The tabs are Type, IdP Adapter, Extended Contract, Adapter Attributes, Adapter Contract Mapping,
  Summary.
- **Validators.** The adapter "delegates user authentication to a PCV"; several may be chained and
  tried in order ([HTML Form Adapter](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_html_form_adapt.html);
  instance page above: "From the Password Credential Validator Instance list, select a PCV
  instance").
- **Defaults that matter to a suite.** Challenge Retries, "the account lockout threshold", default
  3; Session State default None ("does not maintain adapter sessions"); Session Timeout 60 minutes
  and Session Max Timeout 480 minutes when sessions are kept (instance page). With `None` a second
  app's login prompts again, unlike Cognito's one-hour managed-login cookie (C-2.6); the deployed
  stack's fill handles both ("When Cognito still holds a session ... there is no form",
  `DeployedStack.cs:225-229`).
- **The form.** The sign-on page is the Login Template, default
  `html.form.login.template.html` in `<pf_install>/pingfederate/server/default/conf/template`
  ([advanced fields](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_html_form_adapter_advanced_fields.html)).
  The input names of that form are **not found** on any docs page read (the instance page, the
  advanced-fields page, the sign-on customisation guide and two GitHub paths of
  `pingidentity-server-profiles` returned no markup or 404). The documented alternative is the
  authentication API: the `checkUsernamePassword` action, sent with content type
  `application/vnd.pingidentity.checkUsernamePassword+json`
  ([Authentication API](https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_authentication_api.html)).
- **`login_hint`.** "the username field in the login form is pre-populated with the login_hint
  parameter value" ([Authorization endpoint](https://docs.pingidentity.com/pingfederate/latest/developers_reference_guide/pf_authorization_endpoint.html)).
  The gateway sends none (#768), so the deployed stack types the name itself.

### 2.3 From the adapter to the grant: `USER_KEY`

"The `USER_KEY` attribute is the identifier of the persistent grants"; "The `USER_NAME` attribute
presents the name shown to the resource owner on OAuth user-facing pages"
([Managing IdP adapter grant mapping](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_oauthsource2targetmappingtasklet_oauthidpadapter2targetmappingsstate.html)).
"The `USER_KEY` attribute values must be unique across all end users", it is "the user identifier
to store and to retrieve persistent grants", and the second mapping stage moves "the user keys, any
extended attributes derived from the first stage" into access tokens
([Mapping OAuth attributes](https://docs.pingidentity.com/pingfederate/13.1/introduction_to_pingfederate/pf_mapp_oauth_attri.html)).
With the adapter's `username` mapped to `USER_KEY`, every `sub` below is the username, `alice`.

### 2.4 The OpenID Connect policy: `sub`, `email`, `name`

- **Policy settings.** "ID Token Lifetime ... The default value is `5` minutes"; "Include User Info
  in ID Token: Select to include additional attributes in the ID tokens"; "Include Session
  Identifier in ID Token: Select to add a session identifier (`pi.sri`) in the ID tokens"; "Access
  Token Manager: Select an access token manager instance" (one per policy)
  ([Configuring policy and ID token settings](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_policymanagementtasklet_policymanagementstate.html)).
  A client picks its policy: "In the OpenID Connect Policy list, select a policy to apply to this
  client" ([Configuring OAuth clients](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_oauth_clients.html)).
- **Contract.** "Every new OpenID Connect policy contract begins with a list of standard attributes";
  "You can optionally remove standard attributes, turn them into non-standard attributes, or add new
  non-standard attributes"; by default all attributes are deliverable through UserInfo, and an
  attribute reaches the ID token when "Override Default Delivery" with "ID Token" is selected on it,
  or when "Include User Info in ID Token" is selected on the policy
  ([Configuring the policy attribute contract](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_policymanagementtasklet_createpolicycontractstate.html)).
  The contract "always contains the user identifier ... `sub` in a JWT or an ID token"
  ([Attribute contracts](https://docs.pingidentity.com/pingfederate/13.1/introduction_to_pingfederate/pf_attr_contract.html)).
  The gateway reads the ID token only, so `email` and `name` must be delivered there, not to
  UserInfo.
- **Scopes to attributes.** "If you have selected a standard scope, its associated standard
  attributes are automatically selected and cannot be modified. You can select additional attributes
  to be associated with the selected scope"; and "If you have selected the `profile` scope, any
  non-standard attributes that are not associated with the `profile` scope become inaccessible to
  your OAuth clients"
  ([Configuring attribute scopes](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_policymanagementtasklet_attributescopesstate.html)).
  The apps request `openid profile` and not `email`; to put `email` in their ID tokens the policy
  attaches `email` to `profile` (or `openid`) on this tab, or the registry entries add the `email`
  scope.
- **Fulfilment sources.** "Map the subject attribute and all extended attributes from one of the
  following sources": Access Token ("The value is provided from the access token"), Persistent
  Grant ("Enables direct mapping from the grant to the ID Token and to user information
  attributes"), Context, Extended Client Metadata, LDAP/JDBC/Other, Expression ("If Expression is
  not available, you can enable it by editing the `org.sourceid.common.ExpressionManager.xml` file
  in the `<pf_install>/pingfederate/server/default/data/config-store` directory"), No Mapping, and
  Text: "The value is what you enter. This can be text only, or you can mix text with references to
  the unique user ID returned from the credentials validator, using the `${attribute}` syntax"
  ([Configuring ID token fulfillment](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_policymanagementtasklet_policycontractmappingstate.html)).
  So `sub` comes from the grant's `USER_KEY` (or the access token's subject), and `email` can be a
  Text value built on the user id, which is exactly the stand-in's `subject + "@spa-poc.test"`; which
  reference name the Text source accepts there is not stated on the page (**not found**). `name` as
  "Alice Example" has no per-user source in this validator: it needs a data store, an Expression,
  or stays unmapped (null, which `contracts/http-contract.md:15` allows).

### 2.5 Scope management

"To define a static scope, use a text value such as `read_bank_account`"; "Scope and scope group
values are case-sensitive"; "Duplicate scopes and scope groups are not allowed"; backslashes and
double quotation marks are not allowed in a scope value; "Common scopes and scope groups are
optional. If defined, they are available to all clients by default"; "Exclusive scopes and scope
groups are optional. If defined, they are restricted from all clients by default"; "The default
scope is the implied permissions when no scope and scope group values are indicated"; a scope
group "must contain at least one static scope" and no group may contain another group or the
default scope; "Scope groups are not expanded in JSON Web Token (JWT) based access tokens or token
introspection responses by default"
([Scopes and scope management](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_scopes_and_scope_management.html),
[Defining scopes](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_defining_scopes.html)).
Per client: "Restrict Common Scopes" limits the client to the selected common scopes and groups and
"the rest, as well as any future common scopes and scope groups, become invalid for the client";
"Exclusive Scopes" makes selected exclusive ones available; "If the client tries to use such scope or
scope group, it will receive an `invalid_scope` error message from PingFederate" (same pages;
[Configuring OAuth clients](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_oauth_clients.html)).
A URL-shaped value such as `https://orders.spa-poc.test/read` is excluded by no rule on the pages
read and allowed by none in so many words: the character rules name only `\` and `"`. The
authorization request's `scope` is "a list of space-separated, case-sensitive strings"
([Authorization endpoint](https://docs.pingidentity.com/pingfederate/latest/developers_reference_guide/pf_authorization_endpoint.html)),
and a refresh that omits `scope` keeps "the scope originally granted"
([OAuth grant type parameters](https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_oauth_grant_type_param.html)).
Four scopes do the pool's job: `openid` and `profile` (standard), the two `read` scopes; the two
`admin` scopes exist and no client is allowed them (exclusive, or common and restricted away), which
keeps F-08's `insufficient_scope` a scope decision.

### 2.6 The JSON Web Token access token manager: `scope`, `aud`, `client_id`, `iss`, `sub`

From [Configuring an access token management instance](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_access_token_management_instance.html)
unless noted:

- **Lifetime.** "Token Lifetime: The amount of time that an access token is considered valid, in
  minutes. The default value is `120` minutes." The suites assume five
  (`DeployedStack.cs:53-57`).
- **`iss`.** "Issuer Claim Value: The value of the Issuer claim (`iss`) in the JWT. If left blank,
  this field is omitted." The mock API requires it equal to the issuer in the discovery document,
  whose sample `issuer` is the base URL, `https://localhost:9031`
  ([OpenID Provider configuration endpoint](https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_openid_provider_config_endpoint.html)).
- **`aud`.** "Audience Claim Value: The value of the Audience claim (`aud`) in the JWT. If left
  blank, this field is omitted." A contract attribute named `aud` "overrides any Audience Claim
  Value defined on the Instance Configuration tab"
  ([Defining the access token attribute contract](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_defining_access_token_attribute_contract.html)).
  Selection by the request: "The `aud` is the resource URI the client wants to access. The provided
  value is matched against resource URIs configured in access token management instances"; for
  `resource`, "If multiple resource parameters are requested, they must match to a single access
  token management instance"; "When the aud parameter is specified, PingFederate ignores the
  resource parameter"; "PingFederate takes an exact match over a partial match"
  ([Authorization endpoint](https://docs.pingidentity.com/pingfederate/latest/developers_reference_guide/pf_authorization_endpoint.html)).
  The Resource URIs tab holds "a list of resource URIs that PingFederate OAuth authorization server
  can use to select this access token management instance when the `aud` or `resource` query
  parameter is provided", and "The resource URIs must correspond to the resource that the resource
  server expects" ([Managing resource URIs](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_beareraccesstokenmgmtplugintasklet_atmselectionsettingsstate.html)).
  Without a match the request errs: "if the incoming authorization or token request includes
  resource parameter(s), then you must add the resource(s) to the Resource URIs within an Access
  Token Manager. Otherwise, the authorization or token request will result in an error"
  ([Upgrade considerations introduced in PingFederate 12.x](https://docs.pingidentity.com/pingfederate/13.1/upgrading_pingfederate/pf_upgrade_considerations_12x.html)).
  A client has a "Default Access Token Manager", may be restricted "to using only the selected
  access token manager", and since 13.1.3 may "Ignore Resource and Audience Parameters" for token
  exchange ([Configuring OAuth clients](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_oauth_clients.html)).
  **No page read says the issued `aud` is copied from the request's `resource`**; the introspection
  sample shows `"aud":"https://rs.example.com/resource"` beside `client_id`, `scope`, `sub` and
  `jti` without saying where it came from
  ([Introspection endpoint](https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_introspec_endpoint.html)).
  The documented shape for this repository is therefore one manager per resource: an `orders`
  manager with Resource URI and Audience Claim Value `https://orders.spa-poc.test`, selected by app
  1's `resource`, and a manager with a blank audience as app 2's client default, so its token
  carries no `aud`, as F-08 and N-17 expect. Whether a refresh that names no `resource` stays on the
  manager the grant was issued from is **not found** (the grant-type page documents `resource` for
  token exchange only); P2-6 item 5 already lists it as a deployed-run fact.
- **`client_id` and `scope`.** "Client ID Claim Name: The name of a JWT claim used to represent the
  OAuth client ID. The default value is `client_id`." "Scope Claim Name: ... The default value is
  `scope`." "Space Delimit Scope Values: When selected, indicates scope strings will be delimited by
  spaces rather than represented as a JSON array. This checkbox is not selected by default." "Expand
  Scope Groups ... not selected by default." The mock API splits one string on spaces
  (`Routes.cs:113-116`), so the fixture selects Space Delimit Scope Values.
- **`jti`, `iat`, `kid`.** "JWT ID Claim Length ... The default value is `22`"; "Include Issued At
  Claim ... By default, the checkbox is selected"; "Include Key ID Header Parameter ... selected by
  default".
- **Keys the mock API can find.** The discovery sample advertises `"jwks_uri":
  "https://localhost:9031/pf/JWKS"`, "a set of public keys for OAuth and OpenID Connect"
  ([OpenID Provider configuration endpoint](https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_openid_provider_config_endpoint.html)).
  A manager either uses the centralized key, so a resource server can "retrieve the key it needs to
  validate the digital signature by contacting PingFederate at the `/pf/JWKS` endpoint", or
  publishes its own at "JWKS Endpoint Path: The path on the PingFederate server to publish a JWKS
  with the keys and certificates", served under `/ext/`, with "Published keys are discoverable using
  the OpenID Provider configuration endpoint" said of keys published to the PingFederate JWKS
  endpoint. The mock API reads keys from the issuer's discovery document and nowhere else
  (`MockApiHost.cs:115,126`), so the manager signs with a key `/pf/JWKS` publishes.
- **The contract: `sub`, `token_use`.** "Define the attribute contract for the access tokens issued
  by this access token management (ATM) instance"; "Extend the Contract" adds "one or more
  attributes"; "Select an attribute from the Subject Attribute Name list"; `iss`, `aud` and `exp` in
  the contract override the instance fields, and contract values "override the value of the client
  ID, the scope, or the persistent access grant GUID"
  ([Defining the access token attribute contract](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_defining_access_token_attribute_contract.html)).
  Fulfilment sources: "Persistent Grant: the associated Value list is populated with the USER_KEY
  and extended attributes from the persistent access-token grant"; "Text: The value is what you
  enter ... you can mix text with references to the USER_KEY using the `${USER_KEY}` syntax";
  Context, Extended Client Metadata, LDAP/JDBC/Other, Expression, No Mapping
  ([Configuring access token fulfillment](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_oauthuserkey2accesstokenmappingtasklet_oauthsource2targetmappingstate.html)).
  A mapping is "Default" or per authentication context: "If the authentication context results in a
  match, PingFederate uses that specific mapping", otherwise "the default mapping for the applicable
  access token manager instance"
  ([Managing access token mappings](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_managing_access_token_mappings.html)).
  So `sub` is a contract attribute from Persistent Grant `USER_KEY`, and `token_use` a contract
  attribute from Text `access`. That a contract attribute appears as a claim of the JWT is what the
  self-contained token model says in general ("Contain identity and security information and
  attributes ... signed by the AS and verified directly by the RS",
  [Token models and management](https://docs.pingidentity.com/pingfederate/13.1/introduction_to_pingfederate/pf_token_model_and_manage.html))
  and what the introspection sample's `given_name`/`family_name` show, but **no sentence read says
  it of an extended attribute by name**; the prototype proves it.

## 3. The gaps

Each row: the Cognito shape, where a test or fixture takes it, and what PingFederate does about it.

1. **`token_use == "access"`: Cognito's claim, the mock API's unconditional check.**
   `fixtures/MockApi/MockApiHost.cs:141-151` fails every token without it; the contract scopes the
   rule "For Cognito" (`contracts/upstream-contract.md:15`). PingFederate issues no such claim.
   Reproducible as an extended access-token contract attribute `token_use` fulfilled from Text
   `access` (2.6), unproven on paper. If the prototype finds it absent, the fixture, not the gateway,
   changes: the check becomes profile-aware, under the contract's own "For Cognito" wording
   (workstream 5's file). The suites' F-08 rows do not name `token_use`; they inherit it through
   every 401.
2. **`scope` as one space-delimited string: Cognito's format, the mock API's parser.**
   `Routes.cs:113-116` reads `FindFirst("scope")` and splits on spaces; the PingFederate default is
   a JSON array (2.6), under which the first element alone would be read and `/whoami` would answer
   `insufficient_scope`. A fixture setting: Space Delimit Scope Values on. The scope **names** travel
   unchanged: static, case-sensitive values, `openid`, `profile`, `https://orders.spa-poc.test/read`,
   `https://reports.spa-poc.test/read`; the `admin` pair defined and granted to no client. The
   URL-shaped value is forbidden by no rule read and confirmed by none; prototype.
3. **`aud` present with one `resource`, absent without one, kept across a refresh: Cognito's.**
   Asserted as `audience == oauth.resource` with null for app 2 (`ApiRouteTests.cs:46`,
   `MockApiRouteTests.cs:50`) and as N-17's `invalid_token` for app 2's token on `orders`
   (`ApiTests.cs:109-117`). PingFederate's documented mechanism is selection of a manager by
   Resource URI with a static Audience Claim Value (2.6): one manager per resource server that binds,
   one with a blank audience for the rest, each client's Default Access Token Manager set
   accordingly, and, because a policy's "Access Token Manager" field names one instance, probably one OpenID
   Connect policy per manager: an inference from the field, not a stated rule, and whether one policy
   serves clients on two managers is the prototype's to settle. Two facts stay
   for the run: that `aud` is not copied from the request in any other way, and whether a refresh
   without `resource` keeps the manager (P2-6 item 5, `contracts/changes/p2-6-pingfederate-profile.md:172-175`).
4. **`sub` as a Cognito UUID versus the username.** Asserted in H only, as `DefaultSubject`
   (`MockApiRouteTests.cs:48`, `tests/Gateway.IntegrationTests/Functional/SignInTests.cs:107`): the
   stand-in's shape, where `sub` is the username (`TestIdentityProvider.cs:516,551`). With
   `USER_KEY = username` (2.3) PingFederate issues `sub = alice`, the stand-in's shape and not
   Cognito's; nothing in D asserts the value, and the gateway needs only presence and stability
   (`AppBoundTicketStore.cs:90-92`; item 7's index by `sub`). No gap for the gateway.
5. **`email` and `name` under `openid profile`: Cognito's `profile`, not the standard's.** Cognito's
   `profile` "Authorizes all user attributes that the app client can read", so `email` arrives with
   the apps' two scopes; PingFederate attaches `email` to the `email` scope by the standard and
   delivers extra attributes to UserInfo by default (2.4), which the gateway never calls. For the
   ID token to carry them: deliver `email` (and `name`) to the ID token ("Override Default Delivery"
   or "Include User Info in ID Token") and attach `email` to `profile` on the Attribute Scopes tab,
   or add `email` to the registry entries' scopes. Values: `email` can follow the stand-in's rule as
   a Text value built on the user id; `name` has no source in the credential validator and may be
   null. No suite asserts either; `contracts/http-contract.md:15` allows null. A display difference,
   not a test gap; it is the fixture's to decide.
6. **The deployed login fill and the `cognitoDomain` output: the stack's Cognito coupling.**
   `DeployedStack.cs:258-273` types into `input[name='username']` and `input[name='password']` and
   clicks `button[type='submit']`; `:345-353` treats "at the provider's login" as host
   `ProviderHost` and path not `/logout`; `Cell.cs:26` refuses outputs without `cognitoDomain`;
   `Cell.cs:55,61` build Cognito's endpoint paths. PingFederate's form field names are **not
   found** in its documentation (2.2); the deployed sitting needs a provider-specific filler, read
   from the template the fixture ships or driven through the authentication API's
   `checkUsernamePassword` action, and a `Cell` that takes the provider host from a neutral output
   name. Workstream 6's change, outside this ticket's claims question but found by it.
7. **Passwords: generated and set, or written plain into configuration.** Cognito's are generated
   by Terraform and set on the user; the suite never sees one outside Parameter Store
   (`CellAws.cs:32-38`). PingFederate's validator takes the password in configuration, plain
   (`value`) or as PingFederate's own encrypted representation (`encrypted_value`) (2.1); the
   parameter path `/spa-platform/<cell>/test-users/<username>` is provider-neutral and stays the
   suites' only source. Open: whether a hash made outside PingFederate is accepted (**not found**),
   and whether the generated passwords pass `password-rules.xml` (defaults **not found**; the row's
   `Relax Password Requirements` exists). The fixture ticket decides how a server profile reads the
   parameter without the value landing in a tracked file.
8. **Case-insensitive usernames.** Cognito's pool is `case_sensitive = false`
   (`infra/modules/idp-cognito/main.tf:39-41`); the validator's rule is **not found**. No test types
   anything but the lower-case names, so no exposure.
9. **Cognito-only claims nothing reads.** `username`, `cognito:username`, `origin_jti`, `event_id`,
   `version`, `cognito:groups`, `device_key`, `email_verified`, `token_use: id`: issued by Cognito
   (and some by the stand-in), consumed by no gateway, fixture or suite code. No gap.
10. **Issuer and keys the mock API can validate.** A blank Issuer Claim Value omits `iss`, which
    the mock API refuses, so the manager sets it to the issuer; the manager signs with a key that
    `/pf/JWKS`, the discovery `jwks_uri`, publishes, or the mock API finds no key (2.6). Fixture
    settings; a prototype check.
11. **Lifetimes.** PingFederate's ID token default is five minutes, equal to the apps root's; the
    manager's access-token default is 120 and must be set to 5, which `DeployedStack.AccessTokenLifetime`
    and `SessionLifecycleTests` assume (1.2).

**Which assertions are therefore Cognito's, not the gateway's:** the `token_use` check
(`MockApiHost.cs:146-149`); the scope-string split (`Routes.cs:114-116`); the null-audience
expectation for app 2 (`ApiRouteTests.cs:46`, `MockApiRouteTests.cs:50`, `ApiTests.cs:109-117`); the
managed-login fill, the `/logout` path test and the `cognitoDomain`, `/oauth2/authorize` and
`/logout` shapes (`DeployedStack.cs:258-273,345-353`, `Cell.cs:26,55,61`). Every other assertion in
section 1 names a provider-neutral shape, and the four H-only `DefaultSubject` and `Harness.Idp`
reads name the stand-in's.

## 4. What I executed and what I read

**Executed**, on 2026-11-04, in the worktree at `8ceeee3`: `git`, `gh issue view/edit`, `grep`,
`sed` and `cat` over the repository's tracked files. Nothing was run against PingFederate (no
licence, no key), against Cognito, against AWS, or against the solution (`dotnet` was not invoked).

**Read only**, repository files at `8ceeee3` (lines cited above): `fixtures/cognito/pool.json`;
`fixtures/apps/app1.json`, `app2.json`; `fixtures/MockApi/MockApiOptions.cs`, `MockApiHost.cs:95-160`,
`Routes.cs`, `ClientAdmission.cs`, `Program.cs`; `fixtures/TestIdp/TestIdentityProvider.cs:70-80,300-350,495-600`,
`TestIdpFaults.cs:67`; `fixtures/Fixtures.Tests/CognitoPoolFixtureTests.cs`, `TestIdpCodeGrantTests.cs:51-88`,
`TestIdpFaultSwitchTests.cs:192-199`, `MockApiTests.cs:190-193`; `fixtures/bff-client.js`;
`fixtures/spa/app1/index.html:15-22`, `app2/index.html`, `app3/index.html`, `app*/assets/app.43eda712.js:7-50`;
`tests/e2e/Functional/ApiRouteTests.cs`, `SignInTests.cs:50-62`, `SessionLifecycleTests.cs:24-34,77`,
`ExposureTests.cs:78-90`, `OnboardingTests.cs:48-57`; `tests/e2e/Negative/ApiTests.cs`, `CsrfTests.cs:25-64`,
`LoginTests.cs:55-80`, `CookiePlantingTests.cs:70-110`, `HostTests.cs:56-60`, `SessionTests.cs:38-69`,
`OriginTests.cs:47,111`, `HeaderTests.cs:34`; `tests/e2e/Deployed/CellTests.cs:60-72`, `CellAwsTests.cs`,
`DeployedStackTests.cs:25-45`, `TokenShapeTests.cs:25`, `RedactedTests.cs:15`, `CellOnboardingTests.cs:188-189,364`;
`tests/e2e/Stack/DeployedStack.cs:40-60,120-130,220-290,340-360`, `Cell.cs:15-115`, `CellAws.cs:11-40,187-196`,
`LocalStack.cs:160-200`, `IBrowserStack.cs`, `Profile.cs:22-64`, `TokenShape.cs`; `tests/Gateway.TestHarness/GatewayHarness.cs:170-215,452-470,690-722`,
`GatewayHarnessOptions.cs:25-27`; `tests/Gateway.IntegrationTests/Functional/MockApiRouteTests.cs:28-90`,
`SignInTests.cs:92-110`, and grep hits across both composed suites; `src/Platform.Identity/AuthenticationEvents.cs:15-30,95-125`,
`Bff.cs:50-80`, `AppBoundTicketStore.cs:85-95`, `OidcOptionsSetup.cs:60-75`, `IdpProfiles.cs` (grep);
`src/Platform.Abstractions/IIdpProfile.cs` (grep); `infra/envs/poc/idp/main.tf`, `outputs.tf`, `README.md:1-80`;
`infra/modules/idp-cognito/main.tf`, `outputs.tf:1-40`; `infra/envs/poc/apps/main.tf:50-128`;
`contracts/upstream-contract.md`, `contracts/http-contract.md:15,59,97`, `contracts/infra-outputs.schema.json:20-101`,
`contracts/platform-config.schema.json:67`, `contracts/test-ids.md:88-96`,
`contracts/changes/p2-6-pingfederate-profile.md:158-176,200-245,379-386,418-470,520-570`;
`docs/plan/04-tests.md:24-65`, `docs/plan/02-contracts.md:236,276,298,319`; `docs/adr/0017-*.md:30-40,120-130`,
`docs/adr/0011-*.md` (grep); `GLOSSARY.md:143-160`; `spikes/g0/cognito/output-cognito.txt:50-60`;
`.scratch/phase-2/spec-p2-6-pingfederate.md` (grep); `tools/scripts/cell_doubles.py:165`.

**Read only**, web pages, each fetched on 2026-11-04 (PingFederate 13.1 unless the URL says
`latest`):

- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configure_simple_username_pcv.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_config_password_policy.html
- https://docs.pingidentity.com/pingfederate/13.1/sdk_developers_guide/pf_develop_pass_credent_vallidat.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_html_form_adapt.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_config_html_form_adapt_instance.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_html_form_adapter_advanced_fields.html
- https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_authentication_api.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_oauthsource2targetmappingtasklet_oauthidpadapter2targetmappingsstate.html
- https://docs.pingidentity.com/pingfederate/13.1/introduction_to_pingfederate/pf_mapp_oauth_attri.html
- https://docs.pingidentity.com/pingfederate/13.1/introduction_to_pingfederate/pf_attr_contract.html
- https://docs.pingidentity.com/pingfederate/13.1/introduction_to_pingfederate/pf_token_model_and_manage.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_oidc_policies.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_policymanagementtasklet_policymanagementstate.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_policymanagementtasklet_createpolicycontractstate.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_policymanagementtasklet_attributescopesstate.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_policymanagementtasklet_policycontractmappingstate.html
- https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_userinfo_endpoint.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_scopes_and_scope_management.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_defining_scopes.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_oauth_clients.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_access_token_management_instance.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_defining_access_token_attribute_contract.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_oauthuserkey2accesstokenmappingtasklet_oauthsource2targetmappingstate.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_managing_access_token_mappings.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configure_access_token_mapping.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_beareraccesstokenmgmtplugintasklet_atmselectionsettingsstate.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_access_token_management.html
- https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_express_oauth_openid_connect_uses_cases.html
- https://docs.pingidentity.com/pingfederate/13.1/upgrading_pingfederate/pf_upgrade_considerations_12x.html
- https://docs.pingidentity.com/pingfederate/latest/developers_reference_guide/pf_authorization_endpoint.html
- https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_token_endpoint.html
- https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_oauth_grant_type_param.html
- https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_introspec_endpoint.html
- https://docs.pingidentity.com/pingfederate/13.1/developers_reference_guide/pf_openid_provider_config_endpoint.html
- https://docs.pingidentity.com/pingfederate/llms.txt (index, first 100,000 characters)
- https://docs.pingidentity.com/solution-guides/customer_use_cases/htg_customize_sso_user_sign_on_windows_pf.html (no form markup)
- https://github.com/pingidentity/terraform-provider-pingfederate/blob/main/docs/resources/password_credential_validator.md
- https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-the-access-token.html
- https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-the-id-token.html
- https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-define-resource-servers.html

Fetches that returned 404 and so support nothing above: a guessed policy-contract URL
(`help_policymanagementtasklet_policyattributecontractstate.html`), the Terraform repository's
`examples/resources/pingfederate_password_credential_validator/resource.tf`, and
`pingidentity-server-profiles` at `master` and `main` for
`baseline/pingfederate/instance/server/default/conf/template/html.form.login.template.html`.
