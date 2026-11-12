# Phase 2: staff sign-in and the reviewer portal

## Why

Today the reviewer page has no sign-in. Anyone at the server's keyboard can decide a request, the reviewer's name is typed text, and the page refuses every other computer. Real member data cannot go near it. Phase 2 makes the people who decide requests real, identified, and limited to what their role allows.

## Settled in the grilling (do not reopen)

- Windows sign-in (Windows Integrated Authentication) against the plan's Active Directory. No passwords in the app.
- Roles come from configurable AD groups: **coordinator**, **licensed clinician**, **physician**, **admin**. Anyone in none of them is refused.
- A development-only stand-in sign-in for the Mac and the tests, refused at start-up outside Development.
- One seam for staff sign-in, so OpenID Connect can be added later. Only Windows is built now.
- Decisions record the signed-in account; the typed name box goes away.
- Razor Pages portal, in the same service. The portal is internal; the provider API is public. The split is done at the plan's gateway (documented, not built).
- One shared queue with claim and assign.
- Four decision types: approve in full; approve with changes; deny with a coded reason plus text; request information. Partial approval and denial are clinician-only. Inpatient and PHF (concurrent review) decisions are physician-only.
- Admins register provider systems in the portal.

## Goals

- Every portal page and every `/internal/*` call needs a signed-in staff member with a role.
- Who may do what is enforced in the domain, not only hidden in the page.
- The portal replaces `wwwroot/index.html` at `/`.

## Non-goals (later phases)

- Deadlines, extensions, priority, overdue highlighting, provider emails, the adverse-decision export (phase 3).
- Audit log, persisted keys, IIS deployment script (phase 4).
- OpenID Connect sign-in, user management inside the app, multi-service requests.

## Anti-goals

- Any way to reach the portal or `/internal/*` without a role, in any environment other than the dev stand-in.
- The dev stand-in being switchable on in Production by configuration.

## Approaches

1. **Chosen: Razor Pages over the existing domain, with the `/internal` JSON endpoints kept behind the same sign-in.** The pages call the domain directly; the JSON endpoints stay for the tests and tools.
2. Keep the static page and add sign-in to its JSON calls. Less work, but every rule would live in JavaScript and the page would stay a prototype.
3. A separate portal application. Cleaner network split, but two things to deploy and share a database; not worth it for one on-premises server.

## Design

### Sign-in

- Setting `StaffSignIn:Mode`: `Windows` (default outside Development) or `Development`. Start-up fails if `Development` is set outside the Development environment.
- **Windows:** the Negotiate handler. A claims transformation maps the configured AD groups to roles: `StaffSignIn:Groups:Coordinator`, `:LicensedClinician`, `:Physician`, `:Admin` (for example `COUNTY\PA-Intake`).
- **Development:** a cookie sign-in. `/staff/dev-sign-in` lists test users from `StaffSignIn:TestUsers` (account, display name, roles) and signs in as the one picked. The demo app and the tests use the same page's form post.
- Every sign-in updates a **StaffMember** row (account, display name, roles, last seen), so coordinators can assign requests to people who have used the portal.
- No role: a plain page saying which groups give access and to ask IT.

### Who may do what (domain rules)

| Action | Coordinator | Licensed clinician | Physician | Admin |
|---|---|---|---|---|
| See the queue and requests | yes | yes | yes | no |
| Claim / release a request | yes | yes | yes | no |
| Assign to someone else | yes | no | no | no |
| Request information | yes | yes | yes | no |
| Approve, approve with changes, deny (prior authorization) | no | yes | yes | no |
| Approve, approve with changes, deny (concurrent review) | no | no | yes | no |
| Register and deactivate provider systems | no | no | no | yes |

A request assigned to someone else cannot be decided until it is released or reassigned. These rules live on `AuthorizationRequest`, taking the acting staff member, and throw `DomainRuleException` with a sentence the reviewer can act on.

### Decisions

- **Approve in full:** end date. As today.
- **Approve with changes:** end date plus a required description of the change (for example "6 hours a week, not 10"). Stored as approved with the change recorded; PAS answers review action `A6` (modified) and the disposition names the change.
- **Deny:** a reason code from `DenialReasons` in configuration (default list: not medically necessary, not a covered service, insufficient documentation, member not eligible, other) plus required text.
- **Request information:** as today.

### Portal pages

- **Queue** (`/`): waiting for a decision (default), mine, decided, all. Columns: received, member, service, program, review type, status, assigned to. Waiting and mine: oldest first, so nothing sits at the bottom. Decided and all: newest first, so the 100-row page never cuts off the latest decisions (changed during the build).
- **Request** (`/requests/{id}`): details, documents (download), history, assignment controls, and only the decision forms the signed-in person may use.
- **Provider systems** (`/providers`, admin): list, register (name, contact email, public key set), deactivate. The contact email is stored now and used for phase 3 emails.
- Forms post with antiforgery tokens. Concurrency conflicts show "someone else changed this request; reload".

### Internal JSON endpoints

`/internal/requests` and `/internal/clients` require staff sign-in with the matching role, instead of "same machine only". Decision bodies lose the `Reviewer` field; the signed-in account is used. Changes still require the `X-Requested-By` header.

### Data

New: `StaffMembers` table; on requests `AssignedTo`, `AssignedToName`, `DecidedByAccount`, `Modification`, `DenialReasonCode`; on provider systems `ContactEmail`. One SQL Server migration.

### Clinic demo

In Development it signs in through the dev stand-in as the test admin before registering itself. Nothing else changes.

### Testing

Integration tests sign in through the dev stand-in. The role table is tested in domain tests. The Windows group mapping is tested as a claims transformation, without a domain controller. Windows sign-in itself must be checked on a domain-joined server.

## Left to the builder

Page layout and wording, the exact test users, how the queue filters are shown.
