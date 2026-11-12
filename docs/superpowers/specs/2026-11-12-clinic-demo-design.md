# Clinic demo for management: design

## Why

The sample client is a developer reference. Management needs to see what the system does for a clinic and for the plan, step by step, without key pairs, tokens or FHIR.

## Goals

- Show one request for one listed service from order to decision, in a browser.
- Each step says in plain words what happened and who did it.
- Runs on a laptop next to the API, with synthetic data only.

## Non-goals

- No change to the API or the developer sample client.
- Not a real EHR, and no data entry beyond pressing buttons.
- No tests beyond a build: it is a demo, not product code.

## Anti-goals

- Showing codes, URLs or JSON by default. The audience should never have to read FHIR.
- Anything that looks like it could be pointed at real members.

## Approaches

1. A plain-language console demo. Cheap, but still a terminal on a projector.
2. **Chosen:** a small separate web app that plays the clinic's system, run beside the existing reviewer page. Easier for management to follow; the provider-side code stays out of the API.

## Design

- **Project:** `samples/PriorAuth.ClinicDemo`, an ASP.NET Core app on `http://localhost:5030`, plus a `clinic-demo` entry in `.claude/launch.json`. It needs the API running in Development on port 5020.
- **Server side** does what the sample client does: at start-up makes a key pair and registers it (works because it runs on the same machine as the API), signs in, and exposes four small JSON endpoints the page calls: check with the plan (CRD), get the form (DTR), send the request (PAS `$submit`), check status (PAS `$inquire`).
- **Service:** Therapeutic Behavioral Services (TBS, `H2019`), member "Alex Testpatient, 14 (synthetic)", clinic "Sunrise Youth Clinic (synthetic)".
- **Page:** one screen, four step cards unlocked in order:
  1. **The clinician orders TBS** → button "Check with the plan" → "Approval needed. The plan asks for: *Youth services request*" with the time it took.
  2. **The clinic fills in the plan's form** → the plan's questions with synthetic answers already filled in, and one attached document.
  3. **The clinic sends the request** → "Waiting for a reviewer", the request number, and a link that opens the reviewer page.
  4. **The clinic checks back** → the page checks every few seconds and shows "Approved until …" or "Denied: reason".
- A "Start again" button begins a fresh request. Each card has a closed "Technical detail" section showing what was sent, for questions from the room.
- `README` gets a short "Demo for management" section with the click-through script.

## Left to the builder

Visual styling, exact wording of the cards, polling interval.
