# 4. Status and open questions

Last updated 2026-11-12.

## The November 2026 audit

On 2026-11-11 the code was audited against the CMS Prior Authorization API workflow (CMS-0057-F as applied by BHIN 26-008) and the Da Vinci PAS, CRD and DTR 2.0.1 guides. Findings that could be fixed in code were fixed the same day: request checks, request identity and resends, concurrency, server boundary rules, sign-in hardening and CRD and DTR conformance. The findings that need a decision, data from the plan, or larger work are in the table below. The full list of findings, with what was done about each, is in [7. November 2026 audit](07-audit-2026-11.md).

## What exists

- A learning prototype you can drive from one page.
- A service with a database, the request rules, the provider endpoints, and 263 automated tests that pass on SQLite (and, set up for it, on SQL Server).
- A staff portal: plan staff sign in with Windows, get their roles (coordinator, licensed clinician, physician, admin) from Active Directory groups, and work the review queue, claim and assign requests, approve, approve with changes, deny with a coded reason, or ask for more information. Admins register provider systems there. Decisions are recorded against the signed-in account.
- Sign-in for provider systems (SMART Backend Services, SMART v1 and v2 scope names), with each provider limited to its own requests.
- A sample provider client.
- Supporting documents: sent with a request, shown to the reviewer, and used to answer an information request.
- SQL Server support with migrations.
- CRD (CDS Hooks for order-sign and order-select) and DTR (a questionnaire per service, error reporting), in basic form, with answers flowing to the reviewer.
- Server boundary rules: FHIR JSON only, security headers, no caching of member data, errors as `OperationOutcome`, host name filtering, a `/health` check, and a start-up check of the service list, the plan's time zone and the public address.
- A developer guide for provider systems, served by the app at `/developers.html`.
- This documentation.

## What does not exist yet

| Gap | Why it matters | Issue |
|---|---|---|
| Single instance only | The token signing key is created at start-up and the memory of used sign-in assertions lives in one process. A restart signs every provider out, and a second instance would not accept the first one's tokens. The key must move to a secret store and the memory to shared storage first. | [#14](https://github.com/tngo0508/prior-auth-api/issues/14) |
| Windows sign-in not yet tried on the plan's network | Staff sign-in with Windows Integrated Authentication is built, and the mapping from AD groups to roles is tested, but it has not run on an IIS server joined to the plan's domain. The plan must name the four AD groups (`StaffSignIn:Groups`). | [#18](https://github.com/tngo0508/prior-auth-api/issues/18) |
| No audit log of access | Who read which member's request, and changes to provider registrations, are not logged in a form that can be reviewed. | [#19](https://github.com/tngo0508/prior-auth-api/issues/19) |
| Service list not yet confirmed | The list is built from BHIN 26-030, 22-017 and 24-001, but several codes and modifiers, and every questionnaire, still need the plan's confirmation. The checklist is in [9. The service list](09-service-list.md#what-the-plan-must-confirm-before-real-use). | [#1](https://github.com/tngo0508/prior-auth-api/issues/1) |
| Decision timeframes and priority not tracked | The rule requires a decision within 72 hours for expedited requests and 7 calendar days for standard ones. The request's priority (`Claim.priority`) is not read or stored, and nothing measures, shows or warns on the time left. | [#17](https://github.com/tngo0508/prior-auth-api/issues/17) |
| No public metrics | The rule requires the plan to publish prior authorization metrics each year (approval and denial rates, approvals after appeal, average decision time). Nothing collects or reports them. | [#22](https://github.com/tngo0508/prior-auth-api/issues/22) |
| No appeals | A denial is final in this system. Appeals, and approvals after appeal, are not recorded. | [#22](https://github.com/tngo0508/prior-auth-api/issues/22) |
| No member notice tracking | The written notice sent to the member for a denial is not tracked. | [#26](https://github.com/tngo0508/prior-auth-api/issues/26) |
| Partial approval and denial reasons are not X12-coded | A reviewer can approve with changes (PAS answers A6, Modified, with the change as text) and must pick a denial reason from the plan's list, but neither is sent as X12 codes. The plan's denial reason list in `DenialReasons` is a placeholder until the plan supplies its own. | [#24](https://github.com/tngo0508/prior-auth-api/issues/24) |
| Information requests are free text | What is missing is sent as text in a `CommunicationRequest`, not as coded questions (PAS Task or CDex). | [#29](https://github.com/tngo0508/prior-auth-api/issues/29) |
| No Patient Access or Provider Access feed | The rule requires prior authorization requests and decisions to be available through the plan's Patient Access and Provider Access APIs. Nothing is shared with them. | [#11](https://github.com/tngo0508/prior-auth-api/issues/11) |
| No PAS update or cancel | A provider cannot change or withdraw a request once sent. A Claim carrying a `modifierExtension` (how PAS marks updates and cancellations) is refused with 400 rather than misread. | [#28](https://github.com/tngo0508/prior-auth-api/issues/28) |
| No 42 CFR Part 2 handling | Substance use disorder records under 42 CFR Part 2 (relevant to DMC-ODS services) are stored and shown like any other document. Consent and redisclosure rules are not applied. | [#25](https://github.com/tngo0508/prior-auth-api/issues/25) |
| US Core / USCDI conformance not validated | The Patient, Coverage, practitioner and other resources in a request are not checked against US Core or the PAS profiles, and the server does not declare which US Core version it supports. | [#30](https://github.com/tngo0508/prior-auth-api/issues/30) |
| Members and providers matched by name | The member is matched by identifier only when both copies of a request carry one; otherwise by name. There is no check against enrolment. | [#21](https://github.com/tngo0508/prior-auth-api/issues/21), [#35](https://github.com/tngo0508/prior-auth-api/issues/35) |
| No CORS | The API sends no CORS headers, so a provider application running in a web browser cannot call it directly. Server-to-server callers are not affected. Whether browser clients are needed is undecided. | [#33](https://github.com/tngo0508/prior-auth-api/issues/33) |
| No subscriptions or notifications | Deliberately not built. Providers learn a decision by asking (`$inquire` or reading the response, cheaply with `If-None-Match`). PAS subscriptions are optional, and pushing would mean outbound connections to every provider. | |
| No JWKS URL or key rotation | A provider's public keys are registered as a fixed set. Replacing a key means registering again. | [#31](https://github.com/tngo0508/prior-auth-api/issues/31) |
| FHIR output not machine-validated in the build | Response Bundles were checked once with the Firely validator against PAS 2.0.1 during the audit, but no validator runs in the build and there has been no Inferno run. Known gaps are listed in [How it works](02-how-it-works.md). | [#8](https://github.com/tngo0508/prior-auth-api/issues/8) |
| No continuous integration | The tests run only when a developer runs them. | [#34](https://github.com/tngo0508/prior-auth-api/issues/34) |
| Documents are not scanned | Files from providers are restricted by type and size and always served as downloads, but are not virus-scanned. | [#15](https://github.com/tngo0508/prior-auth-api/issues/15) |
| CRD and DTR are basic | No automatic filling of answers (CQL), two of six hooks, no eligibility check. The full list is in [CRD and DTR](06-crd-and-dtr.md). | [#16](https://github.com/tngo0508/prior-auth-api/issues/16), [#23](https://github.com/tngo0508/prior-auth-api/issues/23) |
| No hosted environment | Nothing is deployed. Hosting, TLS, backups and monitoring are undecided. A deployment must set `AllowedHosts` and `Auth:PublicBaseUrl`; `/health` is ready for monitoring. | [#4](https://github.com/tngo0508/prior-auth-api/issues/4) |
| Public sandbox | BHIN 26-008 requires public documentation; the developer guide exists but is only served locally, and there is no sandbox. | [#9](https://github.com/tngo0508/prior-auth-api/issues/9) |
| Other request channels | Requests by fax, phone or portal do not reach the same queue. | [#36](https://github.com/tngo0508/prior-auth-api/issues/36) |

## Decisions made so far

| Decision | Reason |
|---|---|
| The agency is the payer; this is the server side. | The rule places the obligation on the plan. |
| A person decides every request; nothing is approved automatically. | Keeps clinical judgment where it is today and is the smallest design that meets the rule. |
| FHIR only, no X12 278. | Permitted by CMS enforcement discretion (GL-2024-02). |
| Submit and inquire first, with a simple requirements list; CRD and DTR later. | Gets the core request-and-answer path working before the more complex standards. |
| Custom build. | No vendor or shared solution is available to the agency. |
| One service per request. | Each service is reviewed on its own. Until each line of a Claim can be decided separately, a Claim with several lines is refused rather than half reviewed. |
| Providers ask; nothing is pushed. | No outbound connections to secure. Reading a response supports `If-None-Match`, so asking often is cheap. |
| Dates follow the plan's time zone. | Reviewers and providers think in local dates; an evening approval must not be dated tomorrow. |

## Assumptions that still need confirming

These were assumed so that building could continue. Each could change the design.

1. **This service is the system of record.** The agency said it has no authorization system today. It is not yet clear whether that means requests are handled on paper and by fax, or that no service requires prior authorization at all. The build assumes the first ([#2](https://github.com/tngo0508/prior-auth-api/issues/2)).
2. **Target version is Da Vinci PAS 2.0.1**, the version the final rule names. HL7 has since published 2.1.0 and 2.2.x ([#3](https://github.com/tngo0508/prior-auth-api/issues/3)).
3. **SQLite locally, SQL Server in production**, hosted wherever the agency runs internet-facing .NET applications ([#4](https://github.com/tngo0508/prior-auth-api/issues/4)).
4. **The service list will come from utilization management staff**, signed off, and not be derived by the developer ([#1](https://github.com/tngo0508/prior-auth-api/issues/1)).

5. **FHIR only, relying on CMS enforcement discretion for X12 278.** The plan has not yet confirmed it relies on this ([#27](https://github.com/tngo0508/prior-auth-api/issues/27)).

Previous: [3. Running and testing](03-running-and-testing.md) · Next: [5. Sign-in for provider systems](05-sign-in.md), [6. Asking first: CRD and DTR](06-crd-and-dtr.md), [7. November 2026 audit](07-audit-2026-11.md)
