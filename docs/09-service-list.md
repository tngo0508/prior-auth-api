# 9. The service list: which services need approval

CRD, DTR and PAS all answer from one list: [service-requirements.json](../src/PriorAuth.Api/service-requirements.json). It says, for each coded service, which **review rule** applies and which **questionnaire** documents a request. Nothing else decides whether approval is needed, and no person checks a CRD answer, so a mistake in the list becomes a wrong answer for every provider.

This page explains where the list comes from, how a service is matched to it, what each rule makes the API say, and what the plan must confirm before the list is used with real members.

## Where the rules come from

DHCS sets the rules; the plan publishes its list within them. BHIN 26-008 (the interoperability notice) contains no service list: it requires the plan to publish its own and points to these notices for the rules.

| Notice | Covers | What it says |
|---|---|---|
| BHIN 26-030 (August 2026, replaces 22-016) | Outpatient specialty mental health (SMHS) | Day treatment intensive, day rehabilitation, TBS, TFC, ACT and FACT need prior authorization (or MHP referral). Crisis residential and adult residential: referral or concurrent review only, "MHPs may not require prior authorization". MHPs may not apply prior authorization to SMHS not listed by DHCS. |
| BHIN 22-017 | Psychiatric inpatient and psychiatric health facility (PHF) | Concurrent review, not prior authorization. No prior authorization for emergency care. The facility sends admission orders, the initial plan of care and a face sheet within 24 hours of admission. |
| BHIN 24-001 | DMC-ODS | Residential and inpatient services are subject to prior authorization, decided within 24 hours. Plans may not impose prior authorization on non-residential services, including withdrawal management. Covering ASAM 3.7 and 4.0 is optional. |

## The list

| Service | Program | Code | Rule | Questionnaire | Code confirmed? |
|---|---|---|---|---|---|
| Acute psychiatric inpatient | SMHS | Revenue code `0100` | Concurrent review | `dtr-mhp-inpatient` | No |
| Psychiatric health facility (PHF) | SMHS | `H2013` | Concurrent review | `dtr-mhp-phf` | No |
| Crisis residential treatment | SMHS | `H0018` | No prior authorization | none | No |
| Adult residential treatment | SMHS | `H0019` (no modifier) | No prior authorization | none | No |
| Therapeutic foster care (TFC) | SMHS | `S5145` + `HE` | Prior authorization | `dtr-mhp-youth` | Yes |
| Therapeutic behavioral services (TBS) | SMHS | `H2019` | Prior authorization | `dtr-mhp-youth` | Yes |
| Day treatment intensive | SMHS | `H2012` + `TG` | Prior authorization | `dtr-mhp-day-treatment` | No |
| Day rehabilitation | SMHS | `H2012` | Prior authorization | `dtr-mhp-day-treatment` | No |
| ACT and forensic ACT | SMHS | `H0040` | Prior authorization | `dtr-mhp-act` | No |
| rTMS | SMHS | CPT `90867` | No prior authorization | none | Yes |
| ECT | SMHS | CPT `90870` | No prior authorization | none | Yes |
| ASAM 3.1 residential | DMC-ODS | `H0019` + `U1` | Prior authorization | `dtr-dmcods-residential` | No |
| ASAM 3.3 residential | DMC-ODS | `H0019` + `U2` | Prior authorization | `dtr-dmcods-residential` | No |
| ASAM 3.5 residential | DMC-ODS | `H0019` + `U3` | Prior authorization | `dtr-dmcods-residential` | No |
| ASAM 3.2 withdrawal management | DMC-ODS | `H0012` | No prior authorization | none | No |

Codes are HCPCS unless stated. Each entry in the file carries its `Source` and a `Note`.

Left out on purpose:

- **ASAM 3.7 and 4.0.** Covering them is optional. Add them once the plan confirms it covers them and supplies the code. Until then CRD treats them like any service not on the list.
- **STRTP.** It is a facility type, not a service DHCS lists for authorization.

An earlier draft list placed 15 "high-intensity" services under BHIN 26-008. That notice lists no services, and several of its rules and codes did not match the DHCS notices above, so this list replaced it.

## How a service is matched

The rule lives in one place, `ServiceRequirementCatalog.Match` in [ServiceRequirement.cs](../src/PriorAuth.Api/Domain/ServiceRequirement.cs):

1. **Code system and code must both match.** Known systems are HCPCS, CPT and NUBC revenue codes (`https://www.nubc.org/CodeSystem/RevenueCodes`). Case and surrounding spaces are ignored.
2. **Modifiers decide between entries that share a code.** With modifiers known (a PAS request carries them in `Claim.item.modifier`), an entry applies when all its modifiers were sent, and the most specific one wins. Modifiers the list does not use are ignored.
3. **When the modifiers do not settle it, a person decides.** The answer is **ambiguous** when two equally specific entries both fit (`U1` and `U2` both sent), when a reviewed code arrives without the modifier its entry needs (`S5145` without `HE`), or when the only fit is an entry that needs no review while another entry for the code does (`H0019` with no level modifier could be adult residential, or a DMC-ODS residential request missing its modifier).
4. **Without modifiers** (a CRD order cannot carry them), the code alone must settle it: if every entry for the code has the same rule and questionnaire, that is the answer (CRD names them together, for example day treatment intensive or day rehabilitation); if not, the answer is **ambiguous**.
5. **A code that is not on the list** is "not listed" only when it is well formed for a known system. No code, an unknown system, or a malformed code is **unrecognised**.

## What each rule makes the API say

| | CRD `pa-needed` | CRD questionnaire | PAS `$submit` |
|---|---|---|---|
| Prior authorization | `auth-needed` | yes (order-sign) | Pended, review type `PriorAuthorization` |
| Concurrent review | `no-auth`, with a reason telling the facility to notify the plan within 24 hours of admission through `$submit`, naming the questionnaire | no | Pended, review type `ConcurrentReview` |
| No prior authorization (listed) | `no-auth`, with the reason "may not require" | no | Not required, answered at once |
| Not on the list | `no-auth`, with the reason "not on the plan's list" | no | Not required |
| Ambiguous | `conditional`, with a reason naming the modifiers that need review | no | Pended for a person to check, with a note naming the possible services |
| Unrecognised | `conditional`, with a reason asking to check the code | no | Pended for a person to check, never "not required" |

CRD always says `covered` = `conditional`, because the service does not check the member's enrolment.

## Questionnaires

Services share questionnaires, defined once in the `Questionnaires` section of the file and named by each service's `Questionnaire`. All six are **drafts**: written from the DHCS notices and published with FHIR status `draft` and a note in their text, until the plan's clinical lead signs them off. Signing off means changing `Status` to `active`.

| Questionnaire | Used by | Documents asked for |
|---|---|---|
| `dtr-mhp-inpatient` | Psychiatric inpatient | Admission orders, initial plan of care, face sheet |
| `dtr-mhp-phf` | PHF | Admission orders, initial plan of care, face sheet |
| `dtr-mhp-youth` | TBS, TFC | Current assessment, client plan or problem list, target behaviours and transition plan |
| `dtr-mhp-day-treatment` | Day treatment intensive, day rehabilitation | Current assessment, client plan or problem list, program schedule |
| `dtr-mhp-act` | ACT, FACT | Current assessment, client plan or problem list |
| `dtr-dmcods-residential` | ASAM 3.1, 3.3, 3.5 | ASAM assessment or level of care screening, DSM diagnosis |

The DMC-ODS questionnaire leaves the six ASAM dimensions optional: BHIN 24-001 says a complete assessment is not required to authorize.

## What the plan must confirm before real use

| # | Item | Who |
|---|---|---|
| 1 | Each code and modifier marked `"CodeConfirmed": false`, against the current DHCS SMHS and DMC-ODS billing manuals. The H2012 and H0019 modifiers come from county guides, and the H0040 code was not found in a DHCS source. | Billing |
| 2 | Which code an inpatient admission notice will carry (revenue code `0100` or another). | Billing, with the hospitals |
| 3 | The modifiers used for SMHS adult residential, which shares `H0019` with the DMC-ODS levels. | Billing |
| 4 | That rTMS and ECT may not be prior-authorized. This is inferred from BHIN 26-030's rule that unlisted SMHS may not be. | UM lead |
| 5 | Whether the plan covers ASAM 3.7 and 4.0, and their code. | DMC-ODS administrator |
| 6 | Every questionnaire's questions and document list, then set each `Status` to `active`. | Clinical lead |

The research behind this list could not download the current DHCS billing manuals or the latest SMHS access-criteria notice; items 1 to 3 cover what that leaves open.

## Changing the list

Edit [service-requirements.json](../src/PriorAuth.Api/service-requirements.json) and restart. The file is checked at start-up, and the service will not start if:

- a code is not well formed for its system (for example `H0018-33`), or a modifier is not two characters;
- the same code and modifiers appear twice;
- a reviewed service has no questionnaire, names one that does not exist, or a no-review service names one;
- a questionnaire id is not a valid FHIR id, or a question or answer option is malformed.

See [recipe 1 in the codebase guide](08-codebase-guide.md#1-add-or-change-a-service-that-needs-prior-authorization) for the entry format.
