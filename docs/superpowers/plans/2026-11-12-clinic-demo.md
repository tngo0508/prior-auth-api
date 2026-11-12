# Clinic Demo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A browser demo that plays a clinic's system and walks one TBS request from order to decision against the running API.

**Architecture:** A separate ASP.NET Core app (`samples/PriorAuth.ClinicDemo`) does the provider side on the server (key, registration, sign-in, CRD, DTR, PAS), the same way the sample client does, and exposes four plain JSON endpoints. One static page calls them and shows plain-language cards.

**Tech Stack:** .NET 10 minimal APIs, Microsoft.IdentityModel.JsonWebTokens 8.23.0, vanilla HTML/JS.

**Spec:** docs/superpowers/specs/2026-11-12-clinic-demo-design.md

## Global Constraints

- Port `http://localhost:5030`; API at `http://localhost:5020` (setting `PlanApi`).
- Service TBS `H2019`; member "Alex Testpatient, 14 (synthetic)"; clinic "Sunrise Youth Clinic (synthetic)".
- No change to `src/` or the sample client. Synthetic data only.
- Codes, URLs and JSON appear only inside a closed "Technical detail" section.
- British spelling, as the rest of the repo.

## Review Focus

- API not running → each step shows "The plan's system is not reachable at …", not a stack trace.
- API restarted with a fresh database (registration gone) → the demo registers again and retries once.
- Demo started before the API → registration happens on first use, not at start-up.
- "Send" pressed twice → the same request number is resent; the API treats it as a resend, no second request.
- Reviewer asks for more information or denies → the status card shows the plan's wording, not "unknown".

---

### Task 1: Clinic demo app (server)

**Files:**
- Create: `samples/PriorAuth.ClinicDemo/PriorAuth.ClinicDemo.csproj` (Web SDK)
- Create: `samples/PriorAuth.ClinicDemo/PlanConnection.cs`
- Create: `samples/PriorAuth.ClinicDemo/Program.cs`
- Modify: `PriorAuth.slnx` (add under `/samples/`), `.claude/launch.json` (add `clinic-demo`)

**Interfaces — Produces:** JSON endpoints, each returning `{ ok, headline, detail?, technical }` or `{ ok: false, headline }`:
- `POST /demo/check` → CRD order-sign for H2019; headline "Approval needed" / other; `questionnaireTitle`, `elapsedMs`.
- `POST /demo/form` → DTR package; `questions: [{ text, answer }]`, `documents: [string]`.
- `POST /demo/send` → PAS `$submit`; `requestNumber`, `status` (`approved|denied|pended|info|not-required`), `headline` = disposition.
- `POST /demo/status` → PAS `$inquire` for the current request; same shape as send.
- `POST /demo/reset` → new request number.

- [ ] Build: `dotnet build samples/PriorAuth.ClinicDemo` → Build succeeded.
- [ ] Smoke run against the API: each endpoint answers `ok: true` in order; with the API stopped, `/demo/check` answers `ok: false` with the "not reachable" headline.

### Task 2: Page and docs

**Files:**
- Create: `samples/PriorAuth.ClinicDemo/wwwroot/index.html`
- Modify: `README.md` (section "Demo for management"), `docs/03-running-and-testing.md` (one line)

- [ ] Four step cards unlocked in order, "Start again", "Open the reviewer page" link to `{PlanApi}/`, status polled every 4 seconds until decided.
- [ ] Browser check: walk the four steps, approve in the reviewer page, card 4 shows "Approved through …". Screenshot.
- [ ] Commit.
