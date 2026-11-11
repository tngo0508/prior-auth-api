# 4. Status and open questions

Last updated 2026-11-11.

## What exists

- A learning prototype you can drive from one page.
- A service with a database, the request rules, the provider endpoints, a reviewer screen, and 37 passing automated tests.
- Sign-in for provider systems (SMART Backend Services), with each provider limited to its own requests.
- A sample provider client.
- This documentation.

## What does not exist yet

| Gap | Why it matters | Issue |
|---|---|---|
| Single instance only | The token signing key and the memory of used sign-in assertions live in one process. | [#14](https://github.com/tngo0508/prior-auth-api/issues/14) |
| No sign-in for agency staff | The reviewer's name is typed in, not verified. As a stopgap, the internal endpoints only answer callers on the server's own machine. The service must not hold real data until this is done. | [#2](https://github.com/tngo0508/prior-auth-api/issues/2) |
| Placeholder service list | The four services and their paperwork are invented examples. | [#1](https://github.com/tngo0508/prior-auth-api/issues/1) |
| FHIR output not validated | The response shape follows a reading of PAS 2.0.1 and has not been run through a validator or the Inferno test kit. | [#8](https://github.com/tngo0508/prior-auth-api/issues/8) |
| Attachments | A provider cannot yet send the supporting documents with the request. | [#12](https://github.com/tngo0508/prior-auth-api/issues/12) |
| Simple requirements list | Questions 1 and 2 are a plain list, not the CRD and DTR standards. | [#7](https://github.com/tngo0508/prior-auth-api/issues/7), [#10](https://github.com/tngo0508/prior-auth-api/issues/10) |
| Database creation | The database is created on first run. Schema changes need migrations before real data is stored. | [#13](https://github.com/tngo0508/prior-auth-api/issues/13) |
| Public documentation and sandbox | Required by BHIN 26-008. | [#9](https://github.com/tngo0508/prior-auth-api/issues/9) |

## Decisions made so far

| Decision | Reason |
|---|---|
| The agency is the payer; this is the server side. | The rule places the obligation on the plan. |
| A person decides every request; nothing is approved automatically. | Keeps clinical judgment where it is today and is the smallest design that meets the rule. |
| FHIR only, no X12 278. | Permitted by CMS enforcement discretion (GL-2024-02). |
| Submit and inquire first, with a simple requirements list; CRD and DTR later. | Gets the core request-and-answer path working before the more complex standards. |
| Custom build. | No vendor or shared solution is available to the agency. |

## Assumptions that still need confirming

These were assumed so that building could continue. Each could change the design.

1. **This service is the system of record.** The agency said it has no authorization system today. It is not yet clear whether that means requests are handled on paper and by fax, or that no service requires prior authorization at all. The build assumes the first.
2. **Target version is Da Vinci PAS 2.0.1**, the version the final rule names. HL7 has since published 2.1.0 and 2.2.x ([#3](https://github.com/tngo0508/prior-auth-api/issues/3)).
3. **SQLite locally, SQL Server in production**, hosted wherever the agency runs internet-facing .NET applications ([#4](https://github.com/tngo0508/prior-auth-api/issues/4)).
4. **The service list will come from utilization management staff**, signed off, and not be derived by the developer ([#1](https://github.com/tngo0508/prior-auth-api/issues/1)).

Previous: [3. Running and testing](03-running-and-testing.md) · Next: [5. Sign-in for provider systems](05-sign-in.md)
