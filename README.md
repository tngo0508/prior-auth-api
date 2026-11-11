# Prior Authorization API

A payer-side FHIR Prior Authorization API for a California county Medi-Cal behavioral health plan, written in C# on ASP.NET Core.

It exists to meet the Prior Authorization API requirement of the CMS Interoperability and Prior Authorization Final Rule (CMS-0057-F), which DHCS applies to county Mental Health Plans and DMC-ODS plans through BHIN 26-008. The compliance date is January 1, 2027.

## Status

Prototype only. Nothing here is production code, and it must only ever be run with synthetic data.

## What the API must do

- List the covered services that need prior authorization (drugs are excluded).
- State the documentation the plan requires for each.
- Accept an authorization request and return a response: approved with an end date, denied with a specific reason, or more information needed.

## Design decisions so far

- The agency is the payer, so this is the server that providers' EHRs call.
- The API is a facade. Every request pends for a human reviewer; there is no automated approval.
- FHIR only. The X12 278 transaction is not used, relying on CMS enforcement discretion (GL-2024-02).
- Delivery order: Da Vinci PAS submit and inquiry plus a requirements list first, then CRD and DTR.

## Run the prototype

```bash
dotnet run --project prototype/PriorAuth.Prototype --no-launch-profile --urls http://localhost:5019
```

Open http://localhost:5019 for the reviewer page, and send the requests in [requests.http](prototype/PriorAuth.Prototype/requests.http) in order.

The prototype has no authentication, no persistence, and its FHIR output has not been validated against the Da Vinci profiles. The service list in it is placeholder data.

## Layout

- [prototype/PriorAuth.Prototype](prototype/PriorAuth.Prototype): throwaway prototype of the submit, review and inquire flow.
- [GLOSSARY.md](GLOSSARY.md): the project's domain language.

## References

- [CMS-0057-F fact sheet](https://www.cms.gov/newsroom/fact-sheets/cms-interoperability-prior-authorization-final-rule-cms-0057-f)
- [DHCS BHIN 26-008](https://www.dhcs.ca.gov/Documents/BHIN-26-008-Interoperability-Requirements-for-MHPs-and-DMC-ODS.pdf)
- [Da Vinci Prior Authorization Support (PAS)](https://hl7.org/fhir/us/davinci-pas/)
- [Da Vinci Coverage Requirements Discovery (CRD)](https://hl7.org/fhir/us/davinci-crd/)
- [Da Vinci Documentation Templates and Rules (DTR)](https://hl7.org/fhir/us/davinci-dtr/)

Work is tracked in this repository's GitHub issues.
