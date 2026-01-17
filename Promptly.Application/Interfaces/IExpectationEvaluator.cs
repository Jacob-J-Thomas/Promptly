using Promptly.Domain.ValueObjects;
using ExpectationResult = Promptly.Domain.ValueObjects.ExpectationResult;

namespace Promptly.Application.Interfaces;

public interface IExpectationEvaluator
{
    /// <summary>
    /// Evaluate a single expectation against a canonical trace
    /// </summary>
    Task<ExpectationResult> EvaluateAsync(object expectation, CanonicalTrace trace);
}
