# 3. Running and testing

You need the .NET 10 SDK. Use synthetic data only; the app has no sign-in yet.

## Two things you can run

| | Learning prototype | The service |
|---|---|---|
| Folder | `prototype/PriorAuth.Prototype` | `src/PriorAuth.Api` |
| Purpose | Understand the flow by playing both sides | The code that will become the real API |
| Storage | Memory, wiped on restart | SQLite file `priorauth.db` |
| Address | http://localhost:5019 | http://localhost:5020 |

### The learning prototype

```bash
dotnet run --project prototype/PriorAuth.Prototype --no-launch-profile --urls http://localhost:5019
```

Open http://localhost:5019. On the left you are the clinic's EHR; on the right you are the reviewer. Press the steps top to bottom, decide on the right, then check the status again.

![Prototype walkthrough page with the EHR on the left and the reviewer on the right](images/prototype-walkthrough.jpg)

### The service

```bash
dotnet run --project src/PriorAuth.Api --no-launch-profile --urls http://localhost:5020
```

Open http://localhost:5020 for the reviewer queue. To send requests as a provider would, open [requests.http](../src/PriorAuth.Api/requests.http) in Visual Studio, Rider or VS Code (REST Client extension) and run the requests in order.

To start with an empty database, stop the app and delete `src/PriorAuth.Api/priorauth.db`.

## The endpoints

### For providers

| Method and path | Purpose | Answers with |
|---|---|---|
| `GET /fhir/metadata` | What the server supports | `CapabilityStatement` |
| `GET /requirements` | All services needing approval | JSON list |
| `GET /requirements?code=H2019` | One service and its paperwork | JSON list, empty if no approval is needed |
| `POST /fhir/Claim/$submit` | Send an authorization request | `Bundle` holding a `ClaimResponse` |
| `POST /fhir/Claim/$inquire` | Ask for the current state | `Bundle` holding a `ClaimResponse`, or 404 |
| `GET /fhir/ClaimResponse/{id}` | Read one answer by id | `ClaimResponse`, or 404 |

Errors come back as a FHIR `OperationOutcome` with status 400 or 404.

### For the reviewer screen

| Method and path | Body |
|---|---|
| `GET /internal/requests` | |
| `POST /internal/requests/{id}/approve` | `{ "through": "2027-02-28", "reviewer": "name" }` |
| `POST /internal/requests/{id}/deny` | `{ "reason": "text", "reviewer": "name" }` |
| `POST /internal/requests/{id}/request-information` | `{ "missing": "text", "reviewer": "name" }` |

A broken rule returns status 422 with the reason in `detail`.

## Automated tests

```bash
dotnet test
```

The tests start the real application against a temporary database and call it over HTTP. There are 17, in [PriorAuthApiTests.cs](../tests/PriorAuth.Api.Tests/PriorAuthApiTests.cs). Each test name is a sentence stating a behaviour:

| Area | Behaviours covered |
|---|---|
| Requirements list | States the documentation for a listed service; is empty for an unlisted one. |
| Submitting | A request is pended; an unlisted service is answered "not required"; a resend does not create a second request; invalid FHIR and non-preauthorization claims are rejected. |
| Decisions | An approval returns an end date and number; an approval cannot end in the past; a denial needs a reason; a denial returns its reason; an information request keeps the request open; a decided request cannot be decided again; every step is in the history. |
| Reading | A response can be read by id; an unknown request returns 404; the capability statement advertises submit and inquire. |

Previous: [2. How it works](02-how-it-works.md) · Next: [4. Status and open questions](04-status-and-open-questions.md)
