# Prior Authorization

How contracted providers ask the county's Medi-Cal behavioral health plan for approval before delivering a service, and how the plan answers.

## Language

**Behavioral Health Plan**:
The agency in its role as Medi-Cal payer for specialty mental health (MHP) and substance use (DMC-ODS) services. It is the party that receives and decides requests.
_Avoid_: Payer, insurer, the county, the agency

**Provider**:
A contracted organization that delivers services to members and asks the Behavioral Health Plan for authorization.
_Avoid_: Client, vendor, submitter

**Provider System**:
A Provider's software that the Behavioral Health Plan has registered to send Authorization Requests on the Provider's behalf. It can see only the requests it sent.
_Avoid_: Client app, EHR, integration, API user

**Authorization Request**:
A Provider's request for approval of one service for one member over a period.
_Avoid_: Claim, TAR, ticket, case

**Request Identifier**:
The number a Provider System gives an Authorization Request (a system and a value). Together with the Provider System it names exactly one Authorization Request; sending it again is a resend, and it cannot be reused for another member or service.
_Avoid_: Claim number, tracking number, reference number

**Resend**:
A Provider System sending an Authorization Request again under the same Request Identifier, to retry, to check, or to answer an Information Request. It never creates a second request.
_Avoid_: Resubmission, duplicate, update

**Reviewer**:
A member of the plan's clinical staff who decides an Authorization Request. Decisions are always made by a Reviewer, never automatically.
_Avoid_: Adjudicator, approver

**Pended**:
The state of an Authorization Request that has been received and is waiting for a Reviewer.
_Avoid_: Queued, pending, open, in progress

**Information Request**:
A Reviewer's statement of what is missing from an Authorization Request. The request stays undecided until the Provider supplies it.
_Avoid_: RFI, pend for info

**Decision**:
A Reviewer's approval or denial of an Authorization Request. An approval carries an end date; a denial carries a specific reason.
_Avoid_: Determination, adjudication, outcome

**Service Requirement**:
An entry on the plan's service list: a service identified by its code, code system (HCPCS, CPT or NUBC revenue code) and any billing modifiers, with the Review Rule that applies to it and, when it is reviewed, the questionnaire that documents a request. Drugs are never Service Requirements.
_Avoid_: Rule, policy, coverage rule

**Review Rule**:
What the plan does with a request for a Service Requirement: prior authorization (approval before the service), concurrent review (the stay is reviewed after admission), or no prior authorization (the plan may not require it). Set by the DHCS notices, not by the plan alone.
_Avoid_: Policy, coverage rule

**Review Type**:
The kind of review an Authorization Request gets: prior authorization or concurrent review. Recorded on the request when it is received, from its service's Review Rule; empty when no review is needed. A request whose service the list cannot settle is pended as prior authorization for a person to check.
_Avoid_: Request type, queue type

**Concurrent Review**:
Review of a psychiatric inpatient or PHF stay that has already started. The facility notifies the plan within 24 hours of admission; there is no approval beforehand (BHIN 22-017).
_Avoid_: Retro review, notification

**Plan Date**:
The calendar date in the Behavioral Health Plan's time zone. "Today" for approvals, authorization numbers and coverage answers is always the Plan Date, never the UTC date.
_Avoid_: Server date, UTC date
