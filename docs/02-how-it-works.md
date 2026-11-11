# 2. How it works

## One request from start to finish

```mermaid
sequenceDiagram
    autonumber
    participant EHR as Provider's EHR
    participant API as Prior Authorization API
    participant DB as Database
    participant REV as Reviewer screen

    EHR->>API: GET /requirements?code=H2019
    API-->>EHR: Needs approval, with the paperwork list
    EHR->>API: POST /fhir/Claim/$submit (Bundle with a Claim)
    API->>DB: Save the request as Pended
    API-->>EHR: ClaimResponse: queued, A4 (Pended)
    REV->>API: GET /internal/requests
    API-->>REV: The queue
    REV->>API: POST /internal/requests/{id}/approve
    API->>DB: Save the decision and a history entry
    EHR->>API: POST /fhir/Claim/$inquire
    API-->>EHR: ClaimResponse: complete, A1, end date, authorization number
```

The first answer to a submitted request is "pended", because a person has to decide. The provider learns the decision by asking again (`$inquire`) or reading the response by its id.

## The states a request can be in

```mermaid
stateDiagram-v2
    [*] --> Pended: service is on the list
    [*] --> NotRequired: service is not on the list
    Pended --> InformationRequested: reviewer asks for something missing
    InformationRequested --> InformationRequested: reviewer asks again
    Pended --> Approved: reviewer approves, with an end date
    Pended --> Denied: reviewer denies, with a reason
    InformationRequested --> Approved
    InformationRequested --> Denied
    Approved --> [*]
    Denied --> [*]
    NotRequired --> [*]
```

Rules the code enforces, in [AuthorizationRequest.cs](../src/PriorAuth.Api/Domain/AuthorizationRequest.cs):

- Only an open request (Pended or InformationRequested) can be acted on. A decided request cannot be changed.
- An approval must have an end date that is today or later.
- A denial must state a specific reason. This is a requirement of the rule.
- An information request must say what is missing.
- Every action must name the reviewer, and every action is written to the request's history.
- A provider that sends the same request identifier twice gets the same answer, not a second request.

## The parts of the system

```mermaid
flowchart TB
    subgraph Callers
        EHR["Provider's EHR"]
        STAFF["Reviewer's browser"]
    end
    subgraph App["PriorAuth.Api (ASP.NET Core, .NET 10)"]
        FE["Fhir/FhirEndpoints.cs<br/>metadata, requirements, submit, inquire, read"]
        RE["Review/ReviewerEndpoints.cs<br/>queue, approve, deny, request information"]
        MAP["Fhir/PasMapper.cs<br/>FHIR to domain and back"]
        DOM["Domain/AuthorizationRequest.cs<br/>states and rules"]
        CAT["Domain/ServiceRequirement.cs<br/>the requirements list"]
        DATA["Data/PriorAuthDb.cs<br/>Entity Framework Core"]
    end
    CFG["service-requirements.json"]
    DB[("SQLite database<br/>(SQL Server later)")]

    EHR --> FE
    STAFF --> RE
    FE --> MAP
    FE --> CAT
    MAP --> DOM
    RE --> DOM
    FE --> DATA
    RE --> DATA
    DATA --> DB
    CFG --> CAT
```

| File | What it is for |
|---|---|
| [Program.cs](../src/PriorAuth.Api/Program.cs) | Wires everything together and starts the app. |
| [Domain/AuthorizationRequest.cs](../src/PriorAuth.Api/Domain/AuthorizationRequest.cs) | The request, its states and the rules. No FHIR and no database code here. |
| [Domain/ServiceRequirement.cs](../src/PriorAuth.Api/Domain/ServiceRequirement.cs) | A service that needs approval and its paperwork, plus the catalog that looks them up. |
| [Fhir/PasMapper.cs](../src/PriorAuth.Api/Fhir/PasMapper.cs) | Reads what we need from the incoming FHIR `Bundle`, and builds the `ClaimResponse`. |
| [Fhir/FhirJson.cs](../src/PriorAuth.Api/Fhir/FhirJson.cs) | Reads and writes FHIR JSON with the Firely SDK, and builds FHIR error responses. |
| [Fhir/FhirEndpoints.cs](../src/PriorAuth.Api/Fhir/FhirEndpoints.cs) | The URLs a provider's EHR calls. |
| [Review/ReviewerEndpoints.cs](../src/PriorAuth.Api/Review/ReviewerEndpoints.cs) | The URLs the reviewer screen calls. |
| [Data/PriorAuthDb.cs](../src/PriorAuth.Api/Data/PriorAuthDb.cs) | The database mapping. |
| [service-requirements.json](../src/PriorAuth.Api/service-requirements.json) | The list of services needing approval. Placeholder content today. |
| [wwwroot/index.html](../src/PriorAuth.Api/wwwroot/index.html) | The reviewer screen. |

## What is stored

```mermaid
erDiagram
    AuthorizationRequest ||--o{ HistoryEntry : has
    AuthorizationRequest {
        string Id PK
        string ProviderRequestId UK
        string ProviderName
        string MemberName
        string ServiceCode
        string ServiceName
        string SubmittedBundleJson
        datetime ReceivedAtUtc
        string Status
        string AuthorizationNumber
        date AuthorizedThrough
        string Reason
        datetime DecidedAtUtc
        string DecidedBy
    }
    HistoryEntry {
        int Id PK
        string AuthorizationRequestId FK
        datetime AtUtc
        string Actor
        string Text
    }
```

The provider's original FHIR `Bundle` is kept exactly as received in `SubmittedBundleJson`, so the response can echo the right references and so there is a record of what was sent.

## The reviewer screen

![Reviewer queue showing a pended request and one waiting for information](images/review-queue.jpg)

The screen is a single page served by the same app. It lists requests newest first and lets a reviewer approve, deny or ask for information. When a rule is broken, for example a denial with no reason, the server refuses and the page shows the server's message.

## How FHIR maps to the domain

| Incoming FHIR | Stored as |
|---|---|
| `Claim.identifier[0].value` | `ProviderRequestId` |
| `Claim.provider.display` | `ProviderName` |
| `Patient.name[0]` in the same Bundle | `MemberName` |
| `Claim.item[0].productOrService.coding[0].code` | `ServiceCode` |

| Domain state | `ClaimResponse.outcome` | Review action code | Other fields |
|---|---|---|---|
| Pended | `queued` | A4 | |
| InformationRequested | `queued` | A4 | `disposition` says what is missing |
| Approved | `complete` | A1 | `preAuthRef`, `preAuthPeriod.end` |
| Denied | `complete` | A3 | `disposition` carries the reason |
| NotRequired | `complete` | NA | |

This mapping follows a reading of Da Vinci PAS 2.0.1. It has not been checked against the published profiles yet ([issue #8](https://github.com/tngo0508/prior-auth-api/issues/8)).

Previous: [1. Overview](01-overview.md) · Next: [3. Running and testing](03-running-and-testing.md)
