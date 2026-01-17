using Microsoft.AspNetCore.Identity;

namespace Promptly.Domain.Entities;

public class User : IdentityUser
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    public ICollection<Project> Projects { get; set; } = new List<Project>();
    public ICollection<TestRun> CreatedTestRuns { get; set; } = new List<TestRun>();
}
