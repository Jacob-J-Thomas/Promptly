using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;

namespace Promptly.Application.Services;

public class EndpointService : IEndpointService
{
    private readonly PromptlyDbContext _dbContext;
    private readonly ILogger<EndpointService> _logger;

    public EndpointService(PromptlyDbContext dbContext, ILogger<EndpointService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Endpoint>?> GetEndpointsByEnvironmentAsync(
        Guid environmentId,
        TenantAccessScope scope)
    {
        if (!await IsOwnedEnvironmentAsync(environmentId, scope))
        {
            return null;
        }

        return await _dbContext.Endpoints
            .ForTenant(scope)
            .Where(endpoint => endpoint.EnvironmentId == environmentId)
            .OrderBy(endpoint => endpoint.Name)
            .ToListAsync();
    }

    public async Task<Endpoint?> GetEndpointByIdAsync(
        Guid endpointId,
        TenantAccessScope scope)
    {
        return await _dbContext.Endpoints
            .ForTenant(scope)
            .FirstOrDefaultAsync(endpoint => endpoint.Id == endpointId);
    }

    public async Task<Endpoint?> CreateEndpointAsync(
        Guid environmentId,
        string name,
        string path,
        string httpMethod,
        int timeoutSeconds,
        TenantAccessScope scope)
    {
        EndpointTargetPolicy.EnsureRelativeTarget(path);

        if (!await IsOwnedEnvironmentAsync(environmentId, scope))
        {
            return null;
        }

        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = environmentId,
            Name = name,
            Path = path,
            HttpMethod = httpMethod,
            TimeoutSeconds = timeoutSeconds
        };

        _dbContext.Endpoints.Add(endpoint);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Endpoint {EndpointId} created in environment {EnvironmentId}", endpoint.Id, environmentId);

        return endpoint;
    }

    public async Task<Endpoint?> UpdateEndpointAsync(
        Guid endpointId,
        string name,
        string path,
        string httpMethod,
        int timeoutSeconds,
        TenantAccessScope scope)
    {
        EndpointTargetPolicy.EnsureRelativeTarget(path);

        var endpoint = await GetEndpointByIdAsync(endpointId, scope);
        if (endpoint == null)
        {
            return null;
        }

        endpoint.Name = name;
        endpoint.Path = path;
        endpoint.HttpMethod = httpMethod;
        endpoint.TimeoutSeconds = timeoutSeconds;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Endpoint {EndpointId} updated", endpointId);

        return endpoint;
    }

    public async Task<bool> DeleteEndpointAsync(Guid endpointId, TenantAccessScope scope)
    {
        var endpoint = await GetEndpointByIdAsync(endpointId, scope);
        if (endpoint == null)
        {
            return false;
        }

        _dbContext.Endpoints.Remove(endpoint);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Endpoint {EndpointId} deleted", endpointId);

        return true;
    }

    private async Task<bool> IsOwnedEnvironmentAsync(
        Guid environmentId,
        TenantAccessScope scope) =>
        await _dbContext.Environments
            .ForTenant(scope)
            .AnyAsync(environment => environment.Id == environmentId);
}
