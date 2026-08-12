# Test suites

Promptly's automated test foundation is being added in dependency-ordered slices under issue #18.

The expanding C# gate exercises the public behavior of `ExpectationEvaluator`, `BoundedRegexMatcher`, `MappingService`, and every production source file in the `Promptly.Infrastructure` assembly. It fails zero-test runs and enforces at least 90% line and branch coverage for each measured assembly and each named Application class:

```bash
dotnet test tests/Promptly.Application.UnitTests/Promptly.Application.UnitTests.csproj --configuration Release --settings tests/Promptly.runsettings --logger "trx;LogFileName=Promptly.Application.UnitTests.trx" --results-directory artifacts/test-results/csharp
```

The ignored `artifacts/test-results/csharp` directory receives the TRX result plus Cobertura and JSON coverage reports. GitHub Actions runs the same gate for every pull request and `main` push, verifies the exact measured assembly and Application source cohorts plus all checked-in Infrastructure C# sources, and retains those artifacts for 14 days.

This expanding gate is not evidence of repository-wide 90% C# coverage; Application services outside the named cohort, Domain, Server, SDK, and CLI remain outside its denominator. Issues #18 and #19 remain open until every unit-testable production area is included in the required aggregate gates.
