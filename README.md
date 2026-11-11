# Prior Authorization API

A payer-side FHIR Prior Authorization API for a California county Medi-Cal behavioral health plan, written in C# on ASP.NET Core.

It exists to meet the Prior Authorization API requirement of the CMS Interoperability and Prior Authorization Final Rule (CMS-0057-F), which DHCS applies to county Mental Health Plans and DMC-ODS plans through BHIN 26-008.

## Status

In development. Provider systems sign in, but agency staff sign-in does not exist yet, so run it locally with synthetic data only. See [status and open questions](docs/04-status-and-open-questions.md).

## Documentation

Read these in order:

1. [Overview](docs/01-overview.md): what the API is, why it exists, and the three questions it answers.
2. [How it works](docs/02-how-it-works.md): the request flow, the states, the parts of the code and the data, with diagrams.
3. [Running and testing](docs/03-running-and-testing.md): how to run the prototype and the service, the endpoints, and the tests.
4. [Status and open questions](docs/04-status-and-open-questions.md): what is done, what is missing, and the assumptions still to confirm.
5. [Sign-in for provider systems](docs/05-sign-in.md): how a provider's system proves who it is, with a diagram.
6. [Asking first: CRD and DTR](docs/06-crd-and-dtr.md): how an EHR finds out that authorization is needed and what to send, with a diagram.

[GLOSSARY.md](GLOSSARY.md) defines the project's business terms.

## Quick start

Learn the flow with the two-sided prototype at http://localhost:5019:

```bash
dotnet run --project prototype/PriorAuth.Prototype --no-launch-profile --urls http://localhost:5019
```

Run the service, with its reviewer queue at http://localhost:5020:

```bash
dotnet run --project src/PriorAuth.Api --no-launch-profile --urls http://localhost:5020
```

Run the tests:

```bash
dotnet test
```

## Layout

- [src/PriorAuth.Api](src/PriorAuth.Api): the service.
- [tests/PriorAuth.Api.Tests](tests/PriorAuth.Api.Tests): integration tests.
- [samples/PriorAuth.SampleClient](samples/PriorAuth.SampleClient): a sample provider client that signs in and submits a request.
- [prototype/PriorAuth.Prototype](prototype/PriorAuth.Prototype): throwaway learning prototype.
- [docs](docs): documentation, diagrams and screenshots.

## References

- [CMS-0057-F fact sheet](https://www.cms.gov/newsroom/fact-sheets/cms-interoperability-prior-authorization-final-rule-cms-0057-f)
- [DHCS BHIN 26-008](https://www.dhcs.ca.gov/Documents/BHIN-26-008-Interoperability-Requirements-for-MHPs-and-DMC-ODS.pdf)
- [Da Vinci Prior Authorization Support (PAS)](https://hl7.org/fhir/us/davinci-pas/)
- [Da Vinci Coverage Requirements Discovery (CRD)](https://hl7.org/fhir/us/davinci-crd/)
- [Da Vinci Documentation Templates and Rules (DTR)](https://hl7.org/fhir/us/davinci-dtr/)

Work is tracked in this repository's [GitHub issues](https://github.com/tngo0508/prior-auth-api/issues).
