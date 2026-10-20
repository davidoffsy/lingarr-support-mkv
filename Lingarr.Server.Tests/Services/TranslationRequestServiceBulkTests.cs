using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Hubs;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Subtitle.Bilingual;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

public class TranslationRequestServiceBulkTests : IDisposable
{
    private const string MkvPath = "/movies/Foo/Foo.mkv";
    private const string EnSidecar = "/movies/Foo/Foo.en.srt";

    private readonly LingarrDbContext _db;
    private readonly Mock<ISubtitleService> _subtitles = new();
    private readonly Mock<IMediaFileLocator> _locator = new();
    private readonly Mock<IEmbeddedSubtitleService> _embedded = new();
    private readonly Mock<ISettingService> _settings = new();
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly TranslationRequestService _service;
    private readonly Movie _movie;

    public TranslationRequestServiceBulkTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase("bulk-presence-" + Guid.NewGuid())
            .Options;
        _db = new LingarrDbContext(options);

        _movie = new Movie
        {
            RadarrId = 1,
            Title = "Foo",
            Path = "/movies/Foo",
            FileName = "Foo",
            DateAdded = DateTimeOffset.UtcNow
        };
        _db.Movies.Add(_movie);
        _db.SaveChanges();

        _settings.Setup(s => s.GetSettingAsJson<SourceLanguage>(SettingKeys.Translation.SourceLanguages))
            .ReturnsAsync(new List<SourceLanguage> { new() { Code = "en", Name = "English" } });
        _settings.Setup(s => s.GetSetting(SettingKeys.Translation.IgnoreCaptions))
            .ReturnsAsync("false");
        _settings.Setup(s => s.GetSetting(SettingKeys.Translation.ScanEmbeddedSubtitles))
            .ReturnsAsync("false");

        _subtitles.Setup(s => s.SelectSourceSubtitle(
                It.IsAny<List<Subtitles>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>()))
            .Returns((List<Subtitles> subs, IReadOnlyList<string> sources, string _) =>
            {
                var selected = subs.Find(sub => sources.Contains(sub.Language));
                if (selected == null)
                {
                    return null;
                }

                return new SelectedSourceSubtitle
                {
                    Subtitle = selected,
                    SourceLanguage = selected.Language,
                    AvailableLanguages = [..subs.ConvertAll(sub => sub.Language)]
                };
            });

        _jobs.Setup(client => client.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns("job-1");

        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hubClients = new Mock<IHubClients>();
        hubClients.Setup(clients => clients.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<TranslationRequestsHub>>();
        hub.Setup(context => context.Clients).Returns(hubClients.Object);

        var events = new Mock<ITranslationRequestEventService>();
        events.Setup(service => service.LogEvent(
                It.IsAny<int>(),
                It.IsAny<TranslationStatus>(),
                It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        _service = new TranslationRequestService(
            _db,
            _jobs.Object,
            hub.Object,
            new Mock<ITranslationServiceFactory>().Object,
            new Mock<IProgressService>().Object,
            new Mock<IStatisticsService>().Object,
            new Mock<IMediaService>().Object,
            _settings.Object,
            _subtitles.Object,
            _locator.Object,
            _embedded.Object,
            events.Object,
            new Mock<IBilingualSubtitleExportService>().Object,
            NullLogger<TranslationRequestService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task CreateBulkRequest_TargetSidecarExists_DoesNotProbe()
    {
        SetupSidecars(Sidecar(EnSidecar, "en"), Sidecar("/movies/Foo/Foo.zh.srt", "zh"));
        SetupFindMkv(MkvPath);

        await _service.CreateBulkRequest(Bulk("zh"));

        Assert.Empty(_db.TranslationRequests);
        _embedded.Verify(
            service => service.ProbeDetailedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateBulkRequest_ZhCnTarget_PgsZh_Skips()
    {
        SetupSidecars(Sidecar(EnSidecar, "en"));
        SetupFindMkv(MkvPath);
        _embedded.Setup(service => service.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmbeddedSubtitleProbeResult.Success(
                [],
                [
                    new SkippedEmbeddedSubtitleTrack
                    {
                        Index = 4,
                        CodecName = "hdmv_pgs_subtitle",
                        Reason = EmbeddedSubtitleProbeResult.SkipReasonImage,
                        Language = "zh"
                    }
                ]));

        await _service.CreateBulkRequest(Bulk("zh-CN"));

        Assert.Empty(_db.TranslationRequests);
        _embedded.Verify(
            service => service.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateBulkRequest_ScanDisabled_NoSidecars_DoesNotProbe()
    {
        SetupSidecars();
        SetupFindMkv(MkvPath);

        await _service.CreateBulkRequest(Bulk("zh"));

        Assert.Empty(_db.TranslationRequests);
        _embedded.Verify(
            service => service.ProbeDetailedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _locator.Verify(locator => locator.FindMkv(It.IsAny<IMedia>()), Times.Never);
        _locator.Verify(locator => locator.FindMkv(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateBulkRequest_UsesMediaLocatorNotPathStem()
    {
        SetupSidecars(Sidecar(EnSidecar, "en"));
        _locator.Setup(locator => locator.FindMkv(It.IsAny<IMedia>())).Returns((string?)null);
        _locator.Setup(locator => locator.FindMkv(_movie.Path, _movie.FileName)).Returns(MkvPath);

        await _service.CreateBulkRequest(Bulk("zh"));

        var request = Assert.Single(_db.TranslationRequests);
        Assert.Equal(EnSidecar, request.SubtitleToTranslate);
        _locator.Verify(locator => locator.FindMkv(It.Is<IMedia>(media => media.Id == _movie.Id)), Times.Once);
        _locator.Verify(locator => locator.FindMkv(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _embedded.Verify(
            service => service.ProbeDetailedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateBulkRequest_MissingTarget_CreatesRequest()
    {
        SetupSidecars(Sidecar(EnSidecar, "en"));
        SetupFindMkv(MkvPath);
        _embedded.Setup(service => service.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmbeddedSubtitleProbeResult.Success([], []));

        await _service.CreateBulkRequest(Bulk("zh"));

        var request = Assert.Single(_db.TranslationRequests);
        Assert.Equal("zh", request.TargetLanguage);
        Assert.Equal(EnSidecar, request.SubtitleToTranslate);
    }

    private BulkTranslateRequest Bulk(string target) => new()
    {
        MediaIds = [_movie.Id],
        TargetLanguage = target,
        MediaType = MediaType.Movie
    };

    private void SetupFindMkv(string? path)
    {
        _locator.Setup(locator => locator.FindMkv(It.Is<IMedia>(media => media.Id == _movie.Id)))
            .Returns(path);
    }

    private void SetupSidecars(params Subtitles[] sidecars)
    {
        _subtitles.Setup(service => service.GetSubtitles(_movie.Path!, _movie.FileName!))
            .ReturnsAsync(new List<Subtitles>(sidecars));
    }

    private static Subtitles Sidecar(string path, string language) => new()
    {
        Path = path,
        FileName = System.IO.Path.GetFileNameWithoutExtension(path),
        Language = language,
        Format = ".srt"
    };
}
