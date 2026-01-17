namespace Promptly.Domain.Entities;

public class ProjectSettings
{
    public Guid ProjectId { get; set; }
    public string? JudgeModelDefault { get; set; }
    public string? JudgeProvider { get; set; }
    public string? ModelPricingJson { get; set; }

    public Project? Project { get; set; }
}
