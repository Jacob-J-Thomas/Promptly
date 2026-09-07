using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Data;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;

namespace Promptly.Application.UnitTests;

public sealed class ProjectAuditLoggingTests
{
    [Theory]
    [InlineData("owner\r\nFORGED EVENT")]
    [InlineData("owner\nFORGED EVENT")]
    [InlineData("owner\u2028FORGED\u2029EVENT")]
    public async Task Owned_crud_preserves_identity_without_logging_untrusted_subjects(string ownerId)
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new PromptlyDbContext(options);
        context.Users.Add(new User { Id = ownerId, UserName = "synthetic-owner" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var logger = new RecordingLogger();
        var service = new ProjectService(context, logger);
        var scope = new TenantAccessScope(ownerId, ProjectId: null);

        var created = await service.CreateProjectAsync("initial", null, scope);
        Assert.NotNull(created);
        Assert.Equal(ownerId, created.OwnerUserId);
        var updated = await service.UpdateProjectAsync(created.Id, "updated", null, scope);
        Assert.NotNull(updated);
        Assert.Equal(ownerId, updated.OwnerUserId);
        Assert.True(await service.DeleteProjectAsync(created.Id, scope));

        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(created.Id, Assert.IsType<Guid>(entry.Fields["ProjectId"]));
            Assert.False(entry.Fields.ContainsKey("UserId"));
            Assert.DoesNotContain(ownerId, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("FORGED", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(entry.Message, character => character is '\r' or '\n' or '\u2028' or '\u2029');
            Assert.DoesNotContain(entry.Fields.Values, value => Equals(value, ownerId));
        });
        Assert.Contains("created", logger.Entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("updated", logger.Entries[1].Message, StringComparison.Ordinal);
        Assert.Contains("deleted", logger.Entries[2].Message, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger : ILogger<ProjectService>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            Entries.Add(new LogEntry(formatter(state, exception), fields));
        }
    }

    private sealed record LogEntry(string Message, Dictionary<string, object?> Fields);
}
