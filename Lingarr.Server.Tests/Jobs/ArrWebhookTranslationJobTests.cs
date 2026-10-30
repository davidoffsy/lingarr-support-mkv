using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Jobs;
using Lingarr.Server.Models;
using Lingarr.Server.Services.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Jobs;

public class ArrWebhookTranslationJobTests
{
    private readonly LingarrDbContext _db;
    private readonly Mock<IMediaSubtitleProcessor> _processor = new();
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly ArrWebhookTranslationJob _job;
    private readonly Movie _movie;

    public ArrWebhookTranslationJobTests()
    {
        _db = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase($"arr-translate-{Guid.NewGuid()}").Options);
        _movie = new Movie
        {
            RadarrId = 12,
            Title = "Movie",
            Path = "/movies",
            FileName = "movie",
            DateAdded = DateTimeOffset.UtcNow
        };
        _db.Movies.Add(_movie);
        _db.SaveChanges();
        _jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-id");
        _job = new ArrWebhookTranslationJob(
            _db,
            _processor.Object,
            _jobs.Object,
            NullLogger<ArrWebhookTranslationJob>.Instance);
    }

    [Fact]
    public async Task Execute_RetryableFirstAttempt_SchedulesTwoMinuteRetry()
    {
        _processor
            .Setup(p => p.ProcessMediaDetailed(It.IsAny<IMedia>(), MediaType.Movie))
            .ReturnsAsync(MediaProcessingResult.Retry());

        await _job.Execute(_movie.Id, MediaType.Movie, 0, CancellationToken.None);

        _jobs.Verify(
            c => c.Create(
                It.Is<Job>(job =>
                    job.Type == typeof(ArrWebhookTranslationJob)
                    && (int)job.Args[0]! == _movie.Id
                    && (int)job.Args[2]! == 1),
                It.Is<IState>(state => state is ScheduledState)),
            Times.Once);
        var delay = ScheduledDelay();
        Assert.True(delay > TimeSpan.FromMinutes(1.5) && delay < TimeSpan.FromMinutes(2.5), delay.ToString());
    }

    [Fact]
    public async Task Execute_RetryableLastAttempt_DoesNotSchedule()
    {
        _processor
            .Setup(p => p.ProcessMediaDetailed(It.IsAny<IMedia>(), MediaType.Movie))
            .ReturnsAsync(MediaProcessingResult.Retry());

        await _job.Execute(_movie.Id, MediaType.Movie, 2, CancellationToken.None);

        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task Execute_Translated_DoesNotSchedule()
    {
        _processor
            .Setup(p => p.ProcessMediaDetailed(It.IsAny<IMedia>(), MediaType.Movie))
            .ReturnsAsync(MediaProcessingResult.Created());

        await _job.Execute(_movie.Id, MediaType.Movie, 0, CancellationToken.None);

        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task Execute_MissingMedia_DoesNotProcess()
    {
        await _job.Execute(999, MediaType.Movie, 0, CancellationToken.None);

        _processor.Verify(
            p => p.ProcessMediaDetailed(It.IsAny<IMedia>(), It.IsAny<MediaType>()),
            Times.Never);
        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    private TimeSpan ScheduledDelay()
    {
        var invocation = _jobs.Invocations.Single(call => call.Method.Name == nameof(IBackgroundJobClient.Create));
        var state = Assert.IsType<ScheduledState>(invocation.Arguments[1]);
        return state.EnqueueAt - DateTime.UtcNow;
    }
}
