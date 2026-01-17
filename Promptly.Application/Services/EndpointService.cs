using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using Promptly.Application.Data;

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

    public async Task<IEnumerable<Endpoint>> GetEndpointsByEnvironmentAsync(Guid environmentId)
    {
        return await _dbContext.Endpoints
            .Where(e => e.EnvironmentId == environmentId)
            .OrderBy(e => e.Name)
            .ToListAsync();
    }

    public async Task<Endpoint?> GetEndpointByIdAsync(Guid endpointId)
    {
        return await _dbContext.Endpoints
            .FirstOrDefaultAsync(e => e.Id == endpointId);
    }

    public async Task<Endpoint> CreateEndpointAsync(
        Guid environmentId,
        string name,
        string path,
        string httpMethod,
        int timeoutSeconds)
    {
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
        int timeoutSeconds)
    {
        var endpoint = await GetEndpointByIdAsync(endpointId);
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

    public async Task<bool> DeleteEndpointAsync(Guid endpointId)
    {
        var endpoint = await GetEndpointByIdAsync(endpointId);
        if (endpoint == null)
        {
            return false;
        }

        _dbContext.Endpoints.Remove(endpoint);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Endpoint {EndpointId} deleted", endpointId);

        return true;
    }
}
