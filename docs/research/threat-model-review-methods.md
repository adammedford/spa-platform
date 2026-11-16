# Threat model review methods, and the yardstick a review of an OAuth gateway is held to

- **Ticket:** #1255, "Threat model review methods, and the yardstick a review of an OAuth gateway is
  held to" (`wayfinder:research`), on the map #1251 (P2-10 Assurance).
- **Date:** 2026-11-16. **Branch:** `research/threat-model-review-methods`, pushed and never merged,
  as the map rules.
- **Method:** docs-only. Repository reading at commit `adf0273` (origin/main on 2026-11-16):
  `docs/plan/01-plan.md` -> "Trust boundaries and threats" (the seven boundaries, T1 to T18),
  `docs/plan/06-phase-2-and-risks.md`, `docs/gaps.md` (GAP-13 to GAP-16, GAP-27),
  `docs/plan/07-evidence.md` ("The two runs"), `docs/agents/review.md` ("The bar for a finding",
  "The review record"), `docs/adr/README.md`. Public sources were downloaded over plain HTTPS with
  no credential on 2026-11-16 and read as raw text (`curl`, then `grep` and `python3 -I`), so every
  section number, count and quotation below comes from the source's own text and not from a
  summary; section 7 lists each one. **Not done:** no review of the threat model was performed.
  Where a section shows "what a gap would look like here", the row is an illustration of the form,
  built from a row the repository has already recorded as accepted or from a difference in wording
  between a yardstick and a row; none is a finding, and none was checked against the code. No apply,
  no credential step, no read of the cell; the `aws-mcp` connector failed to connect and was not
  needed. **Prices:** the ticket's price rule did not apply; nothing here has a price page. The
  costs in section 5 are session time, and they are **estimates that nothing measured**; their
  basis is the counts stated beside them.

## Answer in brief

1. **The "draft" is no longer a draft.** The datatracker page for
   `draft-ietf-oauth-browser-based-apps` redirects to **RFC 10017, "OAuth 2.0 for Browser-Based
   Applications", BCP 212, August 2026** (the last draft revision was 27). Its section 6.1,
   "Backend for Frontend (BFF)", is the pattern this gateway implements, and it carries 10 MUST,
   6 SHOULD and 1 SHOULD NOT statements. RFC 9700 is BCP 240, January 2025. ASVS is at **5.0.0**
   (published 30 May 2025), whose chapter numbers differ from 4.x: OAuth and OIDC is now V10.
2. **A threat model review, as the sources practise it, answers the Manifesto's fourth question,
   "Did we do a good enough job?"**, and OWASP's cheat sheet gives that question six checks: does
   the model reflect the system, have all threats been identified, has each a response, do the
   mitigations reduce risk to an acceptable level, is the model documented, can the mitigations be
   tested. Four of the six are an audit of rows that exist; one ("all threats identified") needs an
   enumeration method.
3. **The method that fits is two passes, not one of the five named methods alone.** Pass one audits
   each of T1 to T18 as it stands: mitigation against the yardstick clauses, test ids against what
   the mitigation claims. Pass two enumerates by **STRIDE per element** over the trust-boundaries
   table and the Phase 2 elements (durable store, key ring, cells, fragments), which have no threat
   rows yet; it is the only named method whose input the repository already has. Attack trees fit
   one or two goals at most (T14 is the candidate). PASTA needs business objectives and a business
   stakeholder the map does not have; LINDDUN answers a privacy question nobody has asked, and the
   review record should say privacy was not covered.
4. **The yardstick is the two BCPs first, ASVS second.** RFC 10017 section 6.1 and RFC 9700
   section 2 are short (17 and 35 normative statements), written for exactly this architecture, and
   a gap against them is a sentence with a section number. ASVS 5.0.0 does not prescribe a level;
   it says "Most applications should be striving to achieve" Level 2. The gateway's own part is
   V10.1, V10.2 and V10.5 (10 requirements; V10.3, V10.4, V10.6 and V10.7 bind the upstream APIs
   and the provider), V3 and V7. ASVS also sets the report's shape: every requirement checked is
   listed, not only the failures.
5. **CVSS 4.0 does not score most of what this review will find.** FIRST defines a vulnerability as
   a flaw "that can be exploited" and says CVSS "is designed to classify and rate individual
   vulnerabilities"; its eleven Base metrics need an attack path. A missing threat row or a
   mitigation with no test has none. The OWASP Risk Rating Methodology does cover design concerns
   found by threat modelling, but it needs sixteen factors a finding, four of them business impact,
   and OWASP's own page opens with a disclaimer pointing to more established methods. A plain
   four-level scale with written definitions is what the plan's risk table already uses
   (likelihood and impact, Low to Very high).
6. **No primary source read here addresses a reviewer that is a session of the same model.** NIST
   SP 800-53 CA-2(1) says impartial assessors do not "assess their own work"; SSDF PW.2.1 asks for
   review by people "who were not involved with the design and/or" automated processes. A fresh
   session meets "not involved" in the sense of context, and no source says whether that is enough.
   One research paper reports that language models score their own output higher than people do.
   CA-2(1) names the remedy for a reviewer inside the developer's chain: the results are
   "carefully reviewed and analyzed by independent teams of experts". Here that is a person, so the
   record has to be one a person can re-perform: every row with a verdict, the clause it was
   checked against, the evidence, and the commit.

## 1. Methods: what each needs, what it records, which fits

The frame all the others sit inside is the Threat Modeling Manifesto's four questions: "What are we
working on?", "What can go wrong?", "What are we going to do about it?", "Did we do a good enough
job?". The Manifesto is not a method: it says to "Use the Manifesto as a guide to develop or refine
a methodology that best fits your needs". OWASP's Threat Modeling Cheat Sheet builds its four
phases on the same questions and says "There is no universally accepted industry standard for the
threat modeling process". The SEI's survey of twelve methods says "No one threat modeling method is
recommended over another" and that methods "can be combined".

| Method | Its source | Input it needs | Its record | The input here |
| --- | --- | --- | --- | --- |
| Four questions | Threat Modeling Manifesto; OWASP cheat sheet | A representation of the system; the existing model for question four | Whatever the chosen method records; the cheat sheet's six review checks for question four | Present: the plan's request path, trust-boundaries table and T1 to T18 |
| STRIDE per element | Hernan, Lambert, Ostwald and Shostack, MSDN Magazine, November 2006 | A data flow diagram, or "a different representation ... as long as you apply the same basic method: decompose the system into parts": processes, data stores, data flows, interactors, and trust boundaries | Per element, the applicable categories and for each a threat or the reason there is none. Processes take all six; data flows and data stores take tampering, information disclosure and denial of service; interactors take spoofing and repudiation | Mostly present: seven boundaries, each already a flow with "trusted for" and "enforced by". No diagram, and no element list for the Phase 2 additions |
| Attack trees | Schneier, Dr. Dobb's Journal, December 1999 | One attacker goal a tree; knowledge of attackers to value the leaves | A tree, or an outline, of AND and OR nodes, with a value on each leaf (possible or impossible, cost, special equipment) propagated to the root | Goals are derivable from the rows; T14 ("the shared gateway process is compromised") is a goal with no test |
| PASTA | UcedaVélez and Morana; read from the co-creator's company page only | Seven stages in order, starting from business objectives; "At minimum: security, architecture/DevOps ... and a business stakeholder" | A prioritised risk register tied to business impact, with attack simulations | Absent: no business objectives or impact reference in the plan; a poc with stand-in performers |
| LINDDUN | KU Leuven, linddun.org | PRO: a data flow diagram, iterated over every interaction, with the mapping table and threat trees. GO: a system sketch and the 33-card deck, for a team | Per interaction, privacy threats with "the relevant characteristics and assumptions that were used" | Same representation as STRIDE; the question (privacy) is not in the map's scope |

What follows from the sources, for a table of eighteen threats that already has mitigations and
test ids:

- **The existing table is already the output of questions two and three.** A review is question
  four. The cheat sheet's "Review and Validation" list is the nearest thing any source read here
  gives to a definition of a threat model review: "Does the DFD (or comparable) accurately reflect
  the system? Have all threats been identified? For each identified threat, has a response strategy
  been agreed upon? ... have mitigation strategies been developed which reduce risk to an acceptable
  level? Has the threat model been formally documented? ... Can the agreed upon mitigations be
  tested?"
- **Only "have all threats been identified" needs an enumeration method**, and it is where the
  Phase 2 additions sit: the map scopes the review to "T1 to T18 plus what Phase 2 added (the
  durable store, the key ring, cells, fragments)", and none of those has a row. STRIDE per element
  is the systematic one for a technical design; the cheat sheet says "Start with STRIDE for a
  technical design, and select another technique when the scope requires a different view". The
  2006 article's stopping rule is checkable: the pass is done when tampering, information disclosure
  and denial of service are addressed "against all the data flows and data stores; each of the six
  STRIDE threats against all of the processes; spoofing and repudiation threats for all
  interactors; and unique threats that affect your trust boundaries".
- **Attack trees fit a goal, not a table.** The cheat sheet's selector gives them for "A high-value
  attacker goal or multi-step attack". Schneier's own procedure builds independence in: "Give the
  tree to someone else, and have him think about the process and add any nodes he thinks of."
- **PASTA is the wrong size.** Its first stage is "Define Objectives", "business-impact context
  before any technical scoping", and its co-creator's page says it is "designed to fail if it's run
  as a security-only exercise". The book that defines the stages was not read: stage contents
  beyond the seven names are **unconfirmed**.
- **LINDDUN covers a different property.** The cheat sheet lists it for "Privacy properties and
  harms". RFC 10017 section 6.1.3.4 does raise a privacy point for this architecture (the BFF "is
  able to observe all requests and responses"). The cheat sheet's rule applies: "Do not add
  techniques only to make the model appear comprehensive. State why each technique fits the scope
  and record any concerns it does not address."

## 2. Yardsticks for the content, and what a gap against each looks like here

Every "form of a gap" below is illustrative. Where it names a row, the row's own text or a recorded
gap is the source; whether the code has the gap is the review's question, not this finding's.

### RFC 9700, OAuth 2.0 Security Best Current Practice (BCP 240, January 2025)

Section 2 is the requirements (14 MUST, 5 MUST NOT, 1 REQUIRED, 8 SHOULD, 2 SHOULD NOT,
5 RECOMMENDED by keyword count); section 3 is an attacker model, A1 to A5; section 4 gives each
attack and its countermeasure. Many statements bind the authorization server or the resource
server, which here are the provider and the upstream APIs. Those are the gateway's assumptions, not
its mitigations, and a review can only check that the plan states them.

| Clause | Says | Rows it is checked against | Form of a gap |
| --- | --- | --- | --- |
| 2.1, 4.11 | "Clients and authorization servers MUST NOT expose URLs that forward the user's browser to arbitrary URIs obtained from a query parameter" | T9 | A redirecting endpoint other than `returnUrl` that no row names |
| 2.1, 4.7 | "Clients MUST prevent Cross-Site Request Forgery" at the redirection endpoint; PKCE or a state value "securely bound to the user agent" | T3, T17 | A row whose test proves the cookie's prefix and not the binding of the callback to the browser that started it |
| 2.1.1, 4.5 | "Clients MUST prevent authorization code injection"; PKCE is RECOMMENDED for confidential clients; the challenge or nonce "MUST be transaction-specific and securely bound to the client and the user agent" | T3 | A mitigation that names PKCE "at the provider" and no test of the client half |
| 2.1, 4.4 | A mix-up defence is REQUIRED "When an OAuth client can interact with more than one authorization server" | None of T1 to T18 names mix-up | A missing row, or a missing written reason the condition is not met, once a cell holds a second provider profile |
| 2.3, 4.10.2 | Access tokens "SHOULD be audience-restricted" | T11 | Already written into the row: "One audience per app in Phase 1" |
| 2.2.1 | Authorization and resource servers "SHOULD use mechanisms for sender-constraining access tokens" | T11; the deferred-features table | A SHOULD not followed, with the reason written: the form the plan already uses for sender-constrained tokens ("a PingFederate decision") |
| 2.5 | Asymmetric client authentication is RECOMMENDED | The plan's `private_key_jwt` option | A recommendation not followed while per-app secrets remain, with no row that says so |
| 4.2, 4.3 and attacker A3 | Codes leak through Referer headers, browser history and "URLs stored/logged by browsers (history), proxy servers, and operating systems" | T13, GAP-13 | The recorded acceptance is the worked example: a yardstick clause, a row with "None" under Tests, a gap entry, a closing package |
| 4.13 | "A reverse proxy MUST therefore sanitize any inbound requests to ensure the authenticity and integrity of all header values relevant for the security of the application servers" | T4, T6 | A security-relevant header the row does not list |

### RFC 10017, OAuth 2.0 for Browser-Based Applications (BCP 212, August 2026)

This is the document the ticket calls a draft. Section 5 lists what malicious JavaScript can do;
section 6.1 defines the BFF; 6.1.4 says which of section 5's scenarios the pattern mitigates
(token theft and its consequences) and which it does not: "Proxying Requests via the User's
Browser", of which it says "nothing will be able to prevent such attacks apart from stopping the
execution of malicious code in the first place". Section 9.4 says "it is considered a best practice
to avoid deploying more than one application in a single origin".

| Clause | Says | Rows | Form of a gap |
| --- | --- | --- | --- |
| 6.1.3.1 | "the BFF MUST act as a confidential client" and "MUST use the OAuth 2.0 Authorization Code grant" | T1, T3 | None expected in form; the row to check it against is the boundary "Gateway to identity provider" |
| 6.1.3.2 | Cookies: MUST `Secure`, MUST `HttpOnly`, SHOULD `SameSite=Strict`, SHOULD path `/`, SHOULD NOT set `Domain`, SHOULD use "the __Host-Http- prefix" | T2, T12, T17 | A difference in wording to resolve: the rows say `__Host-` and do not mention `SameSite`; the plan elsewhere says `form_post` would need `SameSite=None` on login-state cookies. Each is a SHOULD, so the gap's form is a deviation without a written reason |
| 6.1.3.3 | "The BFF MUST implement a proper CSRF defense"; `SameSite` alone "falls short when the BFF is hosted alongside other applications within the same site" | T2 | The clause describes this platform's sibling-SPA case exactly; a gap would be a state-changing path the Fetch Metadata and antiforgery checks do not cover |
| 6.1.3.6 | "the BFF MUST enforce strict outbound request controls by validating destination hosts before forwarding requests"; a configurable proxy "MUST ensure that it only allows requests to explicitly permitted hosts and paths" | T5 | A row that proves hosts and not paths, or the reverse |
| 6.1.4.1 | Request proxying through the user's browser is not mitigated by a BFF | T1 | A row that claims more than the pattern gives: T1's mitigation covers theft of tokens, and no row states the residual |
| 6.1.4.1 | "security vulnerabilities in the BFF can have a significant impact on the application" | T14 | Already written: accepted in Phase 1, cells in Phase 2 |
| 6.1.3.4, 6.1.3.5 | The BFF sees all traffic; resource servers need to know of it for rate limiting | T16, the deferred rate-limiting row | A consideration no row or deferred feature records |

### OWASP ASVS 5.0.0 (published 30 May 2025)

- **Which level.** The standard declines to say: "Rather than the ASVS prescriptively stating what
  level an application should be at, an organization should analyze its risks and decide". It
  describes Level 2 as "Most applications should be striving to achieve this level", Level 1 as
  "the minimum requirements" (about 20% of the requirements), Level 3 as the "highest levels of
  security". By count in the 5.0.0 source: 345 requirements, 70 at L1, 183 at L2, 92 at L3, so
  Level 2 is 253. The level is a decision for the spec; the sources support L2 as the default and
  L3 requirements as recommendations where a BCP already asks for them (PAR, sender-constrained
  tokens, `private_key_jwt` are V10.4.13, V10.4.14 and V10.4.16, all L3).
- **Which chapters.** ASVS is a standard for an application, and says components that "serve,
  modify, or validate HTTP traffic, such as ... load balancers, or proxies, may be considered part
  of the application". The chapter mapping below is this finding's reading of the chapter titles
  and requirement texts against the plan, not something ASVS states.

| Chapter | Requirements (L1 / L2 / L3) | Bears on | Whose |
| --- | --- | --- | --- |
| V10 OAuth and OIDC: V10.1 generic, V10.2 client, V10.5 OIDC client | 10 (0 / 9 / 1) | T1, T3, T11, T17, T18 | The gateway |
| V10.3 resource server; V10.4 authorization server; V10.6 OpenID Provider; V10.7 consent | 26 | T11; the provider profile | The upstream APIs and the provider: assumptions to state, not rows to prove |
| V3 Web Frontend Security (V3.3 cookies, V3.4 headers, V3.5 origin separation) | 31 (8 / 11 / 12) | T1, T2, T10, T17 | The gateway |
| V7 Session Management | 19 (6 / 12 / 1) | T3, T12, T18 | The gateway |
| V4 API and Web Service; V12 Secure Communication; V13 Configuration; V14 Data Protection; V16 Security Logging and Error Handling | 16, 12, 21, 13, 17 | T4 to T8, T15, T16 | The gateway and the cell |
| V9 Self-contained Tokens; V11 Cryptography | 7, 24 | The session ticket, the key ring | The gateway, mostly through framework defaults |
| V1, V2, V5, V6, V8, V15, V17 | 30, 13, 13, 47, 13, 21, 12 | Little or nothing the gateway owns (encoding, file handling, passwords, WebRTC) | Not applicable, to be said in the record |

- **Form of a gap.** ASVS requirements are pass or fail ("the verification must result in a 'fail'
  or 'pass' decision"), so a gap is a requirement id with "fail" or "not applicable, because".
  Examples of the form: V10.1.1 (L2) names this architecture, "when using a backend-for-frontend
  pattern ... access and refresh tokens shall only be accessible for the backend", and maps to T1
  and F-05; V3.5.4 (L2), "separate applications are hosted on different hostnames", is the
  platform's isolation model; V7.2.4 (L1), "generates a new session token on user authentication",
  is T12; V10.2.2 (L2) is the mix-up clause above; V10.5.5 (L2) is back-channel logout, which the
  plan defers. ASVS has documentation requirements at the head of chapters (V3.1, V7.1), so a gap
  can be "the decision is not written down", which no test id would ever show.
- **The report shape.** "Traditional penetration testing reports issues 'by exception,' only
  listing failures. However, an ASVS certification report should include scope, a summary of all
  requirements checked, the requirements where exceptions were noted, and guidance on resolving
  issues. Some requirements may be non-applicable ... and this must be noted in the report."
  OWASP certifies nobody: a claim of "ASVS L2" would be self-assessed, and must be worded so.

### OWASP Threat Modeling Cheat Sheet

It is a yardstick for the model's process and shape, not for its OAuth content. Against its six
review checks (section 1), the forms a gap takes here:

| Check | Form of a gap |
| --- | --- |
| The model reflects the system | The boundaries table describes Phase 1 (one task, one cell); no element exists for the durable session store, the shared key ring, a second cell, or fragments |
| All threats identified | A STRIDE category with no row for an element. For example, none of the eighteen rows is worded as a repudiation threat; whether one is needed is the review's call, not this finding's |
| Each threat has a response | The cheat sheet's responses are mitigate, eliminate, transfer, accept. T13 to T16 are "accept", and each already has a gap entry; a gap would be an acceptance with no entry |
| Mitigations reduce risk to an acceptable level | A mitigation weaker than the BCP clause for the same threat |
| Formally documented | The map already answers this with `contracts/threats.md` |
| Mitigations can be tested | "None" under Tests (T13, T14, T15 today), or a test id that proves something narrower than the mitigation claims |

It also says "A threat model is something that should be maintained, updated and refined alongside
the system", and cites NIST SSDF PW.1.2 ("Track and maintain the software's security requirements,
risks, and design decisions") for keeping decisions current.

## 3. Severity scales: what each needs to score a design finding

| Scale | Its source | What it needs per finding | On a design finding |
| --- | --- | --- | --- |
| CVSS 4.0 | FIRST, specification document version 1.2 | Eleven Base metrics: AV, AC, AT, PR, UI, then impact on the vulnerable system (VC, VI, VA) and on subsequent systems (SC, SI, SA). Optionally Threat (E) and Environmental metrics. The score "should be labeled" CVSS-B, CVSS-BT, CVSS-BE or CVSS-BTE | Needs a concrete exploitable flaw. FIRST: a vulnerability is "A weakness or flaw in the functional behavior of an information system ... that can be exploited"; "CVSS is designed to classify and rate individual vulnerabilities"; the Base score assumes "the reasonable worst-case impact"; "CVSS Base (CVSS-B) scores are designed to measure the severity of a vulnerability and should not be used alone to assess risk". A missing row, an untested mitigation or an unwritten decision has no attack vector to enter |
| OWASP Risk Rating Methodology | OWASP community page, author Jeff Williams | Sixteen factors from 0 to 9: threat agent (skill, motive, opportunity, size), vulnerability (ease of discovery, ease of exploit, awareness, intrusion detection), technical impact (confidentiality, integrity, availability, accountability), business impact (financial, reputation, non-compliance, privacy). Averages give likelihood and impact as LOW, MEDIUM or HIGH; a matrix gives Note, Low, Medium, High or Critical | Written to include it: "Early in the life cycle, one may identify security concerns in the architecture or design by using threat modeling." It allows an "Informal Method" ("nothing wrong with reviewing the factors and simply capturing the answers"). The business factors need "people who understand the business". The page opens with a disclaimer: "There are other more mature, popular, or well established Risk Rating Methodologies that can be followed", naming NIST SP 800-30 among others |
| A plain four-level scale | No external standard; the nearest published shapes are CVSS's qualitative ratings (Low, Medium, High, Critical above None) and the Risk Rating matrix | A written definition of each level, and what each level obliges | Scores anything, because it asks only for a judgement. Its weakness is the reverse: without definitions, two reviewers' "High" differ. The repository already uses one: `docs/plan/06-phase-2-and-risks.md` rates fifteen risks by likelihood and impact from Low to Very high, and says "Likelihood and impact are my judgement, not measurements" |

What a design finding needs, whichever scale is used, follows from the Risk Rating page's step one
("the threat agent involved, the attack that will be used, the vulnerability involved, and the
impact"): the row or boundary it is against, the attacker it assumes, the impact if the threat is
realised, and its kind. RFC 9700's A1 to A5 are a ready vocabulary for the attacker. The kinds this
review can produce, read off sections 1 and 2: a threat with no row; a mitigation weaker than a
yardstick clause; a mitigation no test proves; a deviation from a SHOULD with no written reason; a
row that misdescribes the system. Only the second is close to what CVSS means by a vulnerability,
and it is the penetration test, not this review, that turns one into something with an attack
vector. Whether the penetration test's findings use CVSS 4.0 is that activity's question.

## 4. "Review" when the reviewer is a session of the model that wrote the mitigations

### Published practice on independence

- **NIST SP 800-53 Rev 5.2.0, CA-2(1) Independent Assessors.** "Impartiality means that assessors
  are free from any perceived or actual conflicts of interest regarding the development, operation,
  sustainment, or management of the systems under assessment". To achieve it, assessors do not
  "assess their own work". The level required is a decision, not a constant: "Authorizing officials
  determine the required level of independence". The control speaks to design review directly:
  "During the system design and development phase, having independent assessors is analogous to
  having independent SMEs involved in design reviews." And it gives the fallback when the assessor
  sits in the developer's chain: "independence in assessment processes can be achieved by ensuring
  that assessment results are carefully reviewed and analyzed by independent teams of experts to
  validate the completeness, accuracy, integrity, and reliability of the results."
- **NIST SP 800-53, SA-11(2) and SA-11(3).** Threat modelling is done by "the developer", to an
  organisation-defined rigour, and "Produces evidence that meets" organisation-defined acceptance
  criteria; SA-11(3) has "an independent agent" verify the assessment plans "and the evidence
  produced".
- **NIST SP 800-218 (SSDF 1.1), PW.2.1.** "Have 1) a qualified person (or people) who were not
  involved with the design and/or 2) automated processes instantiated in the toolchain review the
  software design to confirm and enforce that it meets all of the security requirements and
  satisfactorily addresses the identified risk information." Its examples include "Review the risk
  models created during software design to determine if they appear to adequately identify the
  risks" and "Record the findings of design reviews to serve as artifacts (e.g., in the software
  specification, in the issue tracking system, in the threat model)."
- **The method authors.** Schneier: "Give the tree to someone else". The Manifesto's pattern
  "Varied Viewpoints": "Assemble a diverse team with appropriate subject matter experts and
  cross-functional collaboration"; its anti-pattern "Hero Threat Modeler". The cheat sheet: "The
  threat model must be reviewed by all stakeholders, not just the development or security teams."
- **On a same-model reviewer.** None of the standards above mentions one, and no standard or
  guidance that does was found: **unconfirmed whether any exists**. One research paper bears on
  it: Panickssery, Bowman and Feng, "LLM Evaluators Recognize and Favor Their Own Generations"
  (arXiv 2404.13076, submitted 15 April 2024). Its abstract reports "self-preference, where an LLM
  evaluator scores its own outputs higher than others' while human annotators consider them of
  equal quality", in GPT-4 and Llama 2. Only the abstract was read; the paper tested neither this
  model nor security review, so it shows that the bias exists as a class and nothing about its
  size here.

Read together: a fresh session is "not involved with the design" in that it holds none of the
writing session's context, which is the sense the map's decision relies on. It is not independent
in CA-2(1)'s sense of a different party with different blind spots, and the Manifesto's "varied
viewpoints" is the property a same-model reviewer most plainly lacks. The sources' answer to
reduced independence is the same each time: the results go to someone independent, with enough
evidence to re-perform them. The map already has that person, since Adam accepts findings, and has
already recorded an outside party's review as an adopter gap.

### What record makes the review checkable by a person

Each item is taken from a source or from this repository's own practice, named beside it.

1. **Every row is listed with a verdict, not only the rows with findings.** ASVS: the report
   includes "a summary of all requirements checked". A record that lists only findings cannot show
   whether a silent row was checked or skipped.
2. **Scope stated as what was included**, with the commit read and each yardstick's version. ASVS:
   "from the perspective of what was included rather than what was not included". The repository's
   review record already pins a 40-character head, because "an abbreviated sha is not a record".
3. **Per row, the clauses it was checked against**, by section or requirement number, and "not
   applicable, because" where one does not bind the gateway. ASVS: non-applicable requirements
   "must be noted".
4. **Per row, the evidence**: the test ids, where each lives, and what it would take to make it
   fail. ASVS: "robust evidence of findings ... work papers, screenshots, scripts, and testing
   logs". The repository's own bar is stricter than reading: "Mutate, do not infer", with the
   example that of five pull requests that stated no mutation run, "every one shipped an assertion
   that could not fail". A review that marks a row "covered" from the test's name alone is the
   inference that rule forbids.
5. **Which verdicts were verified and which are judgement.** `docs/agents/review.md`: "Say which
   findings you checked. A verified finding and a suspicion are both worth reporting and only one
   is worth acting on without looking, so mark which is which."
6. **The method disclosed, and repeatable.** ASVS: certifying organisations "should disclose them
   in the report and this should ideally be repeatable". For the STRIDE pass that means the element
   list and, per element and category, the threat or the reason there is none.
7. **What the reviewer could not decide, and what the method does not cover** (privacy, by the
   cheat sheet's rule in section 1).
8. **One issue per finding, and the reviewed table itself.** SSDF PW.2.1 example 6 names both "the
   issue tracking system" and "the threat model" as where findings are recorded; the map has
   already decided both.
9. **A part the person re-performs.** CA-2(1)'s fallback is review of the results by someone
   independent. `docs/plan/07-evidence.md` records the repository doing this once already: the
   real-handler run was "Run by: An independent reviewer. I rebuilt it, re-ran it and got the same
   result." What share of rows a person re-performs is not given by any source read here; it is a
   decision for the spec.

One limit the repository states about its own review records carries over unchanged: every record
is posted through one account, "so a record cannot show which of them wrote it". A record proves
what was checked; it cannot prove who checked it.

## 5. Recommendation table

Costs are session time and are estimates: nothing was timed, because the ticket performs no review.
"Reviewer session" is one fresh agent session; a count of statements or rows is given so the
estimate can be redone. Phase 2's additions have no rows yet, so their count is unknown and every
estimate that includes them is a floor.

| Choice | Recommended | Why, from the sources | Estimated cost in session time | Considered and not recommended |
| --- | --- | --- | --- | --- |
| Method | The four questions as the frame. Pass one: an audit of T1 to T18 by the cheat sheet's six review checks. Pass two: STRIDE per element over the seven boundaries and the Phase 2 elements. Optionally one attack tree, for T14 | Question four is what a review is; STRIDE is the cheat sheet's default for a technical design and the only method whose input (boundaries, request path) exists; the 2006 article gives a stopping rule a person can check | Pass one: 18 rows, each read against its clauses and its tests' source; about one reviewer session. Pass two: an element list first (seven boundaries, plus four Phase 2 areas of unknown element count), then up to six categories an element; about one further session. A tree for T14: a fraction of a session. Mutation proof of a row's tests is extra and is the dearest part; `gate-evidence` already does it per test id | PASTA: seven stages, business objectives and a business stakeholder, none of which exists here. LINDDUN: a privacy analysis over the same diagram, a second full pass for a question out of scope. Attack trees for every row: a tree a goal, with no stopping rule across goals |
| Yardstick | RFC 10017 section 6.1 with sections 5 and 9.4, and RFC 9700 section 2 with the client-side parts of section 4, as the content yardstick. ASVS 5.0.0 Level 2 for V10.1, V10.2, V10.5, V3 and V7 as the checklist. The cheat sheet's six checks for the model's shape | The BCPs are IETF consensus on this exact architecture and are short. ASVS gives pass or fail items and the positive report shape, and names the BFF pattern in V10.1.1 | RFC 10017 section 6.1: 17 normative statements. RFC 9700 section 2: 35, of which the client's share is under half. Together a fraction of a session to map to rows, once, and the mapping is reusable in `contracts/threats.md`. ASVS core set at L1 and L2: 9 + 19 + 18 = 46 requirements, about half a session. The wider set (V4, V12, V13, V14, V16, V9, V11) adds 110 requirements across all levels, about one more session, much of it about the cell and not the threat table | All of ASVS Level 2 (253 requirements): most of V1, V2, V5, V6, V8 and V17 would be "not applicable". ASVS Level 3 as the pass mark: its OAuth items (PAR, sender-constrained tokens, `private_key_jwt`) are provider decisions the plan has deferred to the profile |
| Severity scale | A plain four-level scale with written definitions, each finding carrying likelihood and impact in the words the plan's risk table already uses, the attacker by RFC 9700's A1 to A5, and its kind. The Risk Rating matrix is a ready rule for combining likelihood and impact | It scores every kind of finding this review produces; it matches the scale already in `docs/plan/06-phase-2-and-risks.md`; "accepted in writing" needs a level a person can read without a calculator | Writing the definitions: once, in the spec. Then a minute or two a finding | CVSS 4.0: eleven metrics a finding and no way to enter a finding with no exploit path; fits the penetration test's findings, not these. The full Risk Rating method: sixteen factors a finding, four of which only Adam can give, under OWASP's own disclaimer |
| Record | Section 4's nine items: a row-by-row record in `contracts/threats.md`, one issue per finding, and a share of rows re-performed by Adam | CA-2(1)'s fallback for an assessor in the developer's chain; ASVS's report shape; the repository's "mutate, do not infer" | Agent time: inside the passes above. Adam's time: set by the share of rows he re-performs, which no source fixes | A findings-only report: cannot show what was checked. A second same-model session reviewing the first: adds no viewpoint the first lacked, by the sources' own account of independence |

## 6. What could not be established

- **Whether any standard or published guidance addresses independence when the reviewer is the same
  model as the author.** None was found among the sources read; the one research paper was read in
  abstract only and tested other models on other tasks.
- **PASTA's stage contents.** Read from the co-creator's company page, which names the seven stages;
  the defining book (UcedaVélez and Morana) was not read.
- **"STRIDE per element" as a term.** The 2006 article gives the element-by-category chart and the
  procedure; the name is from later Microsoft and Shostack material that was not read.
- **ISO and IEC standards on assessor independence** are behind a paywall and were not read; nothing
  here relies on them.
- **How many elements Phase 2 adds.** The map names four areas; the element list is the review's
  first output, so every cost that includes pass two is a floor.
- **Session-time costs.** Unmeasured estimates throughout.
- **Whether RFC 10017 or RFC 9700 has errata.** The errata pages were not read.
- **The chapter-to-row mapping for ASVS** is this finding's reading, not ASVS's.

## 7. What was executed and what was only read

**Executed:** `git fetch`, `git worktree add`, `gh issue view` on the ticket and the map, and
`curl`, `grep` and `python3 -I` over the downloaded sources: keyword counts of the two RFCs'
sections, requirement counts by level across the seventeen ASVS 5.0.0 chapter files, extraction of
the controls from NIST's OSCAL catalogue and of the SSDF practices from NIST's spreadsheet. No
review, no test run, no mutation, no build, no apply, no credential step.

**Read, in the repository at `adf0273`:** the files named under Method.

**Read, public sources, all on 2026-11-16:**

| Source | URL | Gave |
| --- | --- | --- |
| RFC 9700 (BCP 240, January 2025), plain text | `https://www.rfc-editor.org/rfc/rfc9700.txt` | Sections 2, 3 and 4.13 in full; the table of contents for section 4 |
| IETF datatracker, the browser-based apps document | `https://datatracker.ietf.org/doc/draft-ietf-oauth-browser-based-apps/` (redirects to `https://datatracker.ietf.org/doc/rfc10017/`); `https://datatracker.ietf.org/api/v1/doc/document/draft-ietf-oauth-browser-based-apps/?format=json` | That the draft is now RFC 10017, part of BCP 212, August 2026; last draft revision 27 |
| RFC 10017, plain text | `https://www.rfc-editor.org/rfc/rfc10017.txt` | Section 6.1 in full; sections 9.3 and 9.4; the table of contents |
| OWASP ASVS project page | `https://owasp.org/www-project-application-security-verification-standard/` (redirects to `https://asvs.owasp.org/`) | Current stable release 5.0.0, published 30 May 2025 |
| OWASP ASVS 5.0.0 source, tag `v5.0.0_release` | `https://raw.githubusercontent.com/OWASP/ASVS/v5.0.0_release/5.0/en/` (`0x03-What-is-the-ASVS.md`, `0x04-Assessment_and_Certification.md`, and the seventeen chapter files `0x10` to `0x26`) | Levels, scope, report shape; V3, V7, V10 and V15 requirement texts; counts by chapter and level |
| OWASP Threat Modeling Cheat Sheet, source | `https://raw.githubusercontent.com/OWASP/CheatSheetSeries/master/cheatsheets/Threat_Modeling_Cheat_Sheet.md` | The four phases, the technique selector, the responses, the six review checks. Read at the `master` branch as of the date, not at a tagged version |
| OWASP Risk Rating Methodology | `https://owasp.org/www-community/OWASP_Risk_Rating_Methodology` (redirects to `https://community.owasp.org/OWASP_Risk_Rating_Methodology`) | The disclaimer, the sixteen factors, the matrix |
| FIRST, CVSS v4.0 Specification Document, version 1.2 | `https://www.first.org/cvss/v4.0/specification-document` | Metric groups, nomenclature, the qualitative scale |
| FIRST, CVSS v4.0 User Guide, version 1.2 | `https://www.first.org/cvss/v4.0/user-guide` | "Measures Severity, not Risk"; the definitions of vulnerability and reasonable worst-case |
| Threat Modeling Manifesto | `https://www.threatmodelingmanifesto.org/` | The four questions, values, patterns and anti-patterns |
| Hernan, Lambert, Ostwald, Shostack, "Uncover Security Design Flaws Using The STRIDE Approach", MSDN Magazine, November 2006 | `https://learn.microsoft.com/en-us/archive/msdn-magazine/2006/november/uncover-security-design-flaws-using-the-stride-approach` | The element chart, the diagram elements, the stopping rule |
| Microsoft, Threat Modeling Tool threats | `https://learn.microsoft.com/en-us/azure/security/develop/threat-modeling-tool-threats` | The STRIDE category definitions; not quoted |
| Schneier, "Attack Trees", Dr. Dobb's Journal, December 1999 | `https://www.schneier.com/academic/archives/1999/12/attack_trees.html` | Node types, leaf values, the procedure |
| LINDDUN | `https://linddun.org/`, `https://linddun.org/instructions-for-pro/`, `https://linddun.org/go/` | The threat types, PRO's three steps, GO's inputs |
| VerSprite, "What Is PASTA Threat Modeling?" | `https://versprite.com/blog/what-is-pasta-threat-modeling/` (redirects to `https://versprite.com/resources/blog/what-is-pasta-threat-modeling/`) | The seven stage names and who must take part. A vendor page by the method's co-creator, not the book |
| SEI, "Threat Modeling: A Summary of Available Methods" (2018), landing page | `https://www.sei.cmu.edu/library/threat-modeling-a-summary-of-available-methods/` | The abstract only; the paper itself was downloaded and not read |
| NIST SP 800-53 Rev 5.2.0 controls, OSCAL catalogue | `https://raw.githubusercontent.com/usnistgov/oscal-content/main/nist.gov/SP800-53/rev5/json/NIST_SP-800-53_rev5_catalog.json` | CA-2(1), CA-8(1), SA-11(2), SA-11(3), statement and guidance |
| NIST SP 800-218, SSDF 1.1 (February 2022), landing page and practice table | `https://csrc.nist.gov/pubs/sp/800/218/final`; `https://csrc.nist.gov/files/pubs/sp/800/218/final/docs/nist.sp.800-218.ssdf-table.xlsx` | PW.1, PW.1.1, PW.1.2, PW.2, PW.2.1 and their examples. The PDF was fetched and could not be read as text |
| Panickssery, Bowman, Feng, "LLM Evaluators Recognize and Favor Their Own Generations" | `https://arxiv.org/abs/2404.13076` | The abstract and submission date only |
| OWASP, "Threat Modeling Process" | `https://owasp.org/www-community/Threat_Modeling_Process` | Fetched; the page is titled "(Historical)" and nothing here relies on it |
