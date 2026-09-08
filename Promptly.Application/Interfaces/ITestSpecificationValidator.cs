namespace Promptly.Application.Interfaces;

/// <summary>
/// Validates the persisted input and expectation JSON without rewriting it.
/// </summary>
public interface ITestSpecificationValidator
{
    ExpectationValidationResult Validate(string inputSpecJson, string expectationsJson);

    ExpectationValidationResult ValidateInput(string inputSpecJson);
}
