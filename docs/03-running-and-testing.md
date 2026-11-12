# 3. Running and testing

You need the .NET 10 SDK. Use synthetic data only; agency staff sign-in does not exist yet.

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
dotnet run --project src/PriorAuth.Api --no-launch-profile -- --urls http://localhost:5020 --environment Development
```

Open http://localhost:5020 for the reviewer queue. Provider systems must sign in, so to send requests as a provider would, run the sample client in a second terminal:

```bash
dotnet run --project samples/PriorAuth.SampleClient -- http://localhost:5020
```

It registers, asks whether an order needs authorization, signs in, downloads the questionnaire, submits a request with answers and a document, and waits for you to decide it in the queue. [5. Sign-in](05-sign-in.md) and [6. CRD and DTR](06-crd-and-dtr.md) explain what it is doing. [requests.http](../src/PriorAuth.Api/requests.http) holds the calls that need no token.

For a demo to people who are not developers, run `dotnet run --project samples/PriorAuth.ClinicDemo` instead and open http://localhost:5030. It does the same steps from a browser page; the README's "Demo for management" section has the script.

To start with an empty database, stop the app and delete `src/PriorAuth.Api/priorauth.db`. Do this too after pulling changes that alter the data model: the local SQLite file is created once and is not upgraded.

The developer guide for provider systems is served at http://localhost:5020/developers.html.

### Running on SQL Server

SQLite is for local development. On SQL Server the app applies its migrations at start-up:

```bash
dotnet run --project src/PriorAuth.Api --no-launch-profile -- --urls http://localhost:5020 --environment Development --Database:Provider=SqlServer --ConnectionStrings:PriorAuth="Server=(localdb)\MSSQLLocalDB;Database=PriorAuthDev;Integrated Security=true"
```

After changing the data model, add a migration:

```bash
dotnet ef migrations add NameOfTheChange --project src/PriorAuth.Api -o Data/Migrations
```

### Settings

| Setting | Default | Purpose |
|---|---|---|
| `Database:Provider` | `Sqlite` | `Sqlite` or `SqlServer` |
| `ConnectionStrings:PriorAuth` | `Data Source=priorauth.db` | The database connection |
| `Plan:TimeZone` | `America/Los_Angeles` | The plan's time zone. "Today" for approvals, authorization numbers and CRD answers is the date there. An unknown zone stops start-up. |
| `Plan:Name` | `Behavioral Health Plan (synthetic)` | The name shown as the source of CRD cards |
| `AllowedHosts` | `localhost;127.0.0.1;[::1]` | Host names the server answers to. Any other `Host` header is refused. A real deployment must set its own public host name here. |
| `Auth:PublicBaseUrl` | none | The public https address, for example `https://pa.example.gov`. Required outside Development: start-up fails if it is missing or is not an absolute https URL. Token audiences and published addresses use it, never the caller's `Host` header. |

The commands above pass `--environment Development` after the `--`. Without it, `dotnet run --no-launch-profile` runs in the Production environment and stops at start-up asking for `Auth:PublicBaseUrl`. Only in Development does the server fall back to the address the caller used. Put the setting after the `--` too: `dotnet run` keeps some options, including `--environment`, for itself when they come before it.

Settings can also be given as environment variables, with `__` in place of `:` (for example `Plan__TimeZone`).

## The endpoints

### For providers

| Method and path | Purpose | Answers with |
|---|---|---|
| `GET /fhir/metadata` | What the server supports | `CapabilityStatement` |
| `GET /fhir/.well-known/smart-configuration` | Where and how to sign in | JSON |
| `POST /auth/token` | Sign in with a signed assertion | Access token, valid 300 seconds |
| `GET /auth/authorize` | Listed in discovery because SMART requires it; there is no user sign-in | Always 400 `unsupported_response_type` |
| `GET /cds-services` | CRD: lists the CDS Hooks services | JSON |
| `POST /cds-services/prior-auth-order-sign` | CRD: is approval needed for these draft orders? | Orders marked with coverage information |
| `POST /cds-services/prior-auth-order-select` | CRD: the same, for the orders selected while writing | Selected orders marked with coverage information |
| `POST /fhir/Questionnaire/$questionnaire-package` | DTR: the questionnaire for a service | `Parameters` holding package Bundles, and any warnings |
| `GET /fhir/Questionnaire/{id}` | DTR: one questionnaire | `Questionnaire` |
| `POST /fhir/Questionnaire/$log-questionnaire-errors` | DTR: report a problem with one of the plan's questionnaires | `OperationOutcome` saying it was recorded |
| `GET` or `POST /fhir/ValueSet/$expand` | DTR: expand a value set | Always 404: the plan publishes no value sets |
| `GET /requirements` | The plan's service list, with each service's review rule, modifiers, questionnaire and paperwork | JSON list |
| `GET /requirements?code=H2019` | The entries for one code (several when they differ by modifier). Add `&system=` to match the code system exactly | JSON list, empty if the service is not on the list |
| `POST /fhir/Claim/$submit` | Send an authorization request | `Bundle` holding a `ClaimResponse` |
| `POST /fhir/Claim/$inquire` | Ask for the current state | `Bundle` holding a `ClaimResponse`, or an empty `Bundle` if nothing matches |
| `GET /fhir/ClaimResponse/{id}` | Read one answer by id | `ClaimResponse` with `ETag` and `Last-Modified`; 304 if `If-None-Match` matches; 404 if unknown |
| `GET /health` | Whether the service and its database are up, for monitoring | `Healthy` or 503 `Unhealthy` |

Submit, inquire, read and the DTR endpoints need an access token; without one the answer is 401, and with a token lacking the right scope it is 403. Other errors:

| Status | When |
|---|---|
| 400 | The body is not valid FHIR or breaks a rule in [What a request must contain](02-how-it-works.md#what-a-request-must-contain). |
| 404 | No such request, questionnaire or address. |
| 406 | The caller asked for anything other than JSON, with `Accept` or `_format`. The FHIR API speaks FHIR JSON only. |
| 413 | A `$submit` body larger than about 71 MB. |
| 415 | A body sent to `/fhir` that is not `application/fhir+json` or `application/json`. |
| 422 | A request identifier reused for a different member or service, or a CDS Hooks call of the wrong shape. |

Every error from `/fhir`, including 401, 403, 404 for an unknown address and 405, comes back as a FHIR `OperationOutcome`. An unexpected failure is logged in full on the server and answered with a 500 `OperationOutcome` that reveals nothing about the inside.

Every response carries security headers: a Content Security Policy, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`. Responses from `/fhir`, `/internal`, `/auth` and `/cds-services` also carry `Cache-Control: no-store`, because they can hold member information. The server does not announce itself in a `Server` header.

### For agency staff

These are a stopgap until staff sign-in exists. They answer only when all of these hold, and otherwise return 403:

- The connection comes from the server's own machine.
- No proxy forwarded it (no `X-Forwarded-For`, `Forwarded`, `X-Forwarded-Host` or `X-Real-IP` header).
- The `Host` header is `localhost`, `127.0.0.1` or `[::1]`.
- For a `POST`, the header `X-Requested-By: prior-auth-reviewer` is present. The reviewer page sends it; a form or link on another site cannot.

On a shared machine (terminal server, Citrix) every signed-in user counts as local, so do not run it there.

| Method and path | Body |
|---|---|
| `GET /internal/requests?status=open&skip=0&take=50` | `status` is `open` (default), `decided` or `all`. `take` defaults to 50, at most 200. Newest first. |
| `POST /internal/requests/{id}/approve` | `{ "through": "2027-02-28", "reviewer": "name" }` |
| `POST /internal/requests/{id}/deny` | `{ "reason": "text", "reviewer": "name" }` |
| `POST /internal/requests/{id}/request-information` | `{ "missing": "text", "reviewer": "name" }` |
| `GET /internal/requests/{id}/documents/{documentId}` | Downloads one supporting document |
| `GET /internal/clients` | |
| `POST /internal/clients` | `{ "name": "provider name", "jwks": { "keys": [ ... ] } }` |
| `POST /internal/clients/{clientId}/deactivate` | |

A broken rule returns status 422 with the reason in `detail`. A decision on a request someone else changed a moment before returns 409; reload and check before deciding again.

## Automated tests

```bash
dotnet test
```

The tests start the real application against a temporary database and call it over HTTP, signing in the way a provider system would. There are 233 (counting each case of a test that runs with several inputs). All but the rule checks in [DomainRuleTests.cs](../tests/PriorAuth.Api.Tests/DomainRuleTests.cs) and [ServiceRuleTests.cs](../tests/PriorAuth.Api.Tests/ServiceRuleTests.cs), which need no web server, go through HTTP. They are in [PriorAuthApiTests.cs](../tests/PriorAuth.Api.Tests/PriorAuthApiTests.cs), [RequestIntegrityTests.cs](../tests/PriorAuth.Api.Tests/RequestIntegrityTests.cs), [ServerBoundaryTests.cs](../tests/PriorAuth.Api.Tests/ServerBoundaryTests.cs), [SignInTests.cs](../tests/PriorAuth.Api.Tests/SignInTests.cs), [SupportingDocumentTests.cs](../tests/PriorAuth.Api.Tests/SupportingDocumentTests.cs), [CrdAndDtrTests.cs](../tests/PriorAuth.Api.Tests/CrdAndDtrTests.cs), [ReviewRuleTests.cs](../tests/PriorAuth.Api.Tests/ReviewRuleTests.cs), [ServiceRuleTests.cs](../tests/PriorAuth.Api.Tests/ServiceRuleTests.cs) and [DomainRuleTests.cs](../tests/PriorAuth.Api.Tests/DomainRuleTests.cs). Each test name is a sentence stating a behaviour:

| Area | Behaviours covered |
|---|---|
| Requirements list | States the rule and documentation for a listed service; is empty for an unlisted one. |
| Submitting | A request is pended; an unlisted service is answered "not required"; a resend does not create a second request, even when two copies arrive at once; invalid FHIR and non-preauthorization claims are rejected; a request for two services, a Bundle whose first entry is not the Claim, an identifier over 200 characters and another resource type are refused with 400; the Bundle may be wrapped in `Parameters`. |
| Request identity | A request number cannot be reused for another member or another service; an inquiry must match the identifier system too; an inquiry with no identifier is refused; a plain retry does not answer an information request; document contents are stored once, not also inside the kept Bundle. |
| Decisions | An approval returns an end date and number; an approval cannot end in the past; an approval through the plan's today is allowed in the evening; a denial needs a reason and carries no number or end date; an information request keeps the request open; a decided request cannot be decided again; approve and deny at the same moment cannot both succeed; every step is in the history and moves the version on. |
| Reading | A response can be read by id; an unknown request returns 404; reading the same answer twice gives the same created time and a cheap 304; every reference in the answer resolves inside it; the answer names the line the provider numbered. |
| Review queue | Shows open requests by default and decided ones on request; questionnaire answers show the plan's wording and flag gaps. |
| Server boundary | The review queue refuses a caller on another machine or a request forwarded by a proxy; another host name is refused; an internal change without the review page header is refused; answers are never cached and pages cannot be framed; XML gets 406; a non-JSON body gets 415; errors without a body still come back as `OperationOutcome`; the capability statement describes the instance and the questionnaire endpoints; `/health` reports healthy. |
| Service list | A level modifier picks that level; no modifiers matches the entry that needs none when no other entry for the code is reviewed; falling back to a no-review entry while a sibling is reviewed, two equally specific entries that both fit, and a reviewed code without its modifier are all ambiguous; modifiers the list does not use do not stop a match; codes and modifiers in the list are trimmed like the ones sent; without modifiers, entries that disagree are ambiguous and entries that agree are definite; a code only matches in its own code system; the first listed coding decides the service; a well-formed unlisted code is "not listed" and an unrecognisable one is "unrecognised"; codes ignore case and spaces. Start-up stops on: a malformed code, the same code and modifiers twice, a reviewed service without a questionnaire or with one that does not exist, a no-review service with a questionnaire, a questionnaire id not starting with a letter, colliding choice options or an unknown question type. "Today" is the plan's local date. |
| Review rules | A residential level with its modifier is pended for prior authorization; the same code without a modifier is pended for a person to check; a listed service the plan may not prior-authorize is answered not required; an ambiguous service is pended with a note naming the entries; an inpatient admission is pended for concurrent review; an unlisted service needs none; an unrecognisable code is pended for a person to check. |
| Getting a token | A valid RS384 or ES384 assertion gets a 300-second token; refused when signed with another key, from an unregistered or deactivated client, addressed to another server, expired, too long-lived, issued in the future, reused (even by two requests at once), without a `kid` or with one that matches no key, with a `typ` other than JWT, or asking for no supported scope; refusals do not reveal whether a client id exists; only the key named by `kid` is tried; token responses are never cached; a foreign `Host` header cannot change the audience; SMART v1 and v2 scope spellings both work; discovery lists what SMART requires; the public address must be https outside Development. |
| Registering | Refused: a private key, a symmetric key, an RSA key under 2048 bits, an EC key on a curve other than P-384, a missing or repeated `kid`, a body of the wrong shape (422) or not JSON (400). |
| CRD | Discovery lists the services, each with a `coverage-info` configuration option; a listed service is marked as needing authorization with its questionnaire; an unlisted one needs none and no coverage is claimed; an inpatient admission needs none and is told to send it for concurrent review; a service the plan may not prior-authorize says so; a code whose entries differ by modifier and rule is `conditional`, one whose entries agree is answered; a code in an unknown system is `conditional`; prefetched coverage is used; no coverage gives an info card naming the order and its topic; an order missing `authoredOn` or `requester` gets an explanation; an already-annotated order gets no update; order-select answers only the selected orders and never asks for documentation; turning `coverage-info` off suppresses the answer; the date is the plan's local date; unsigned, wrong-audience, wrong-hook, non-JSON and malformed calls are refused. |
| DTR | A package starts with the questionnaire and includes a draft response with the DTR profile, subject, context and intended use; it can be chosen from the order or by canonical; two services sharing a questionnaire get one package naming both orders; an order whose code needs a modifier gets a warning; a draft questionnaire says so and lists the documents; it does not depend on parameter order; unfound questionnaires and orders not understood come back as warnings; nothing applicable gives 404; error reports are accepted; `$expand` reports every value set as not found; questionnaires need a token and the right scope; answers sent with a request reach the reviewer as readable text. |
| Supporting documents | A document reaches the reviewer unchanged and is served as a download; unaccepted types, oversized files and links are refused; sending the missing document answers an information request; resending at other times changes nothing. |
| Using the API | No token gives 401; the descriptive endpoints need none; a token without the read scope cannot read; a deactivated client loses read access at once; a provider cannot see another provider's request; two providers can use the same request number; the registered name is recorded, not the claimed one. |

By default the tests use a temporary SQLite file. To run the same tests on SQL Server, using the real migrations, set an environment variable first. Each test class gets its own database and drops it afterwards.

```powershell
$env:PRIORAUTH_TEST_SQLSERVER = 'Server=(localdb)\MSSQLLocalDB;Integrated Security=true'; dotnet test
```

Previous: [2. How it works](02-how-it-works.md) · Next: [4. Status and open questions](04-status-and-open-questions.md)
