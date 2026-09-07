# Test suites

Promptly's automated test foundation is being added in dependency-ordered slices under issue #18.

The expanding C# gate exercises the public behavior of `ExpectationEvaluator`, `BoundedRegexMatcher`, and `MappingService`, including schema-1 extraction and mapping-spec persistence validation. It fails zero-test runs and enforces at least 90% line and branch coverage for both the measured production cohort and each named production class:

```bash
dotnet test tests/Promptly.Application.UnitTests/Promptly.Application.UnitTests.csproj --configuration Release --settings tests/Promptly.runsettings --logger "trx;LogFileName=Promptly.Application.UnitTests.trx" --results-directory artifacts/test-results/csharp
```

The ignored `artifacts/test-results/csharp` directory receives the TRX result plus Cobertura and JSON coverage reports. GitHub Actions runs the same gate for every pull request and `main` push, verifies the exact measured assembly and source cohort, and retains those artifacts for 14 days.

This scoped gate is not evidence of repository-wide 90% coverage; issues #18 and #19 remain open until every unit-testable production area and language stack is included in the required aggregate gates.
