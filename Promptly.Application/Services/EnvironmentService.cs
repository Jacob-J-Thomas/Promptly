using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Environment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.Services;

public class EnvironmentService : IEnvironmentService
{
    private readonly PromptlyDbContext _dbContext;
    private readonly IEncryptionService _encryptionService;
    private readonly ILogger<EnvironmentService> _logger;

    public EnvironmentService(
        PromptlyDbContext dbContext,
        IEncryptionService encryptionService,
        ILogger<EnvironmentService> logger)
    {
        _dbContext = dbContext;
        _encryptionService = encryptionService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Environment>?> GetEnvironmentsByProjectAsync(
        Guid projectId,
        TenantAccessScope scope)
    {
        if (!await IsOwnedProjectAsync(projectId, scope))
        {
            return null;
        }

        return await _dbContext.Environments
            .ForTenant(scope)
            .Where(environment => environment.ProjectId == projectId)
            .OrderByDescending(environment => environment.CreatedAt)
            .ToListAsync();
    }

    public async Task<Environment?> GetEnvironmentByIdAsync(
        Guid environmentId,
        TenantAccessScope scope)
    {
        return await _dbContext.Environments
            .ForTenant(scope)
            .FirstOrDefaultAsync(environment => environment.Id == environmentId);
    }

    public async Task<Environment?> CreateEnvironmentAsync(
        Guid projectId,
        string name,
        string baseUrl,
        Dictionary<string, string>? headers,
        TenantAccessScope scope)
    {
        if (!await IsOwnedProjectAsync(projectId, scope))
        {
            return null;
        }

        var environment = new Environment
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            BaseUrl = baseUrl,
            CreatedAt = DateTime.UtcNow
        };

        if (headers != null && headers.Count > 0)
        {
            var headersJson = JsonSerializer.Serialize(headers);
            environment.DefaultHeadersEncryptedJson = _encryptionService.Encrypt(headersJson);
        }

        _dbContext.Environments.Add(environment);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Environment {EnvironmentId} created in project {ProjectId}", environment.Id, projectId);

        return environment;
    }

    public async Task<Environment?> UpdateEnvironmentAsync(
        Guid environmentId,
        string name,
        string baseUrl,
        Dictionary<string, string>? headers,
        TenantAccessScope scope)
    {
        var environment = await GetEnvironmentByIdAsync(environmentId, scope);
        if (environment == null)
        {
            return null;
        }

        environment.Name = name;
        environment.BaseUrl = baseUrl;

        if (headers != null && headers.Count > 0)
        {
            var headersJson = JsonSerializer.Serialize(headers);
            environment.DefaultHeadersEncryptedJson = _encryptionService.Encrypt(headersJson);
        }
        else
        {
            environment.DefaultHeadersEncryptedJson = null;
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Environment {EnvironmentId} updated", environmentId);

        return environment;
    }

    public async Task<bool> DeleteEnvironmentAsync(
        Guid environmentId,
        TenantAccessScope scope)
    {
        var environment = await GetEnvironmentByIdAsync(environmentId, scope);
        if (environment == null)
        {
            return false;
        }

        _dbContext.Environments.Remove(environment);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Environment {EnvironmentId} deleted", environmentId);

        return true;
    }

    public async Task<Dictionary<string, string>?> GetDecryptedHeadersAsync(
        Guid environmentId,
        TenantAccessScope scope)
    {
        var environment = await GetEnvironmentByIdAsync(environmentId, scope);
        if (environment == null || string.IsNullOrWhiteSpace(environment.DefaultHeadersEncryptedJson))
        {
            return null;
        }

        try
        {
            var decryptedJson = _encryptionService.Decrypt(environment.DefaultHeadersEncryptedJson);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(decryptedJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt headers for environment {EnvironmentId}", environmentId);
            throw;
        }
    }

    private async Task<bool> IsOwnedProjectAsync(Guid projectId, TenantAccessScope scope) =>
        await _dbContext.Projects
            .ForTenant(scope)
            .AnyAsync(project => project.Id == projectId);
}
