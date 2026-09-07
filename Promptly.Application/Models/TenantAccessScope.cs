namespace Promptly.Application.Models;

public sealed record TenantAccessScope(string OwnerUserId, Guid? ProjectId);
