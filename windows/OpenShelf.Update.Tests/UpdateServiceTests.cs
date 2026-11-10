using OpenShelf.Update;
using Velopack;
using Velopack.Sources;

namespace OpenShelf.Update.Tests;

internal sealed class FakeBackend : IUpdateBackend
{
    public bool IsInstalled { get; set; } = true;
    public string? CurrentVersion { get; set; } = "1.0.0";
    public string? OfferVersion { get; set; }
    public Exception? CheckFails { get; set; }
    public Exception? DownloadFails { get; set; }
    public int Checks, Downloads, Applies;
    public string? Applied;

    public Task<UpdateOffer?> CheckAsync(CancellationToken cancel)
    {
        Interlocked.Increment(ref Checks);
        if (CheckFails is not null) throw CheckFails;
        return Task.FromResult<UpdateOffer?>(OfferVersion is null ? null : new UpdateOffer(OfferVersion));
    }

    public async Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken cancel)
    {
        Interlocked.Increment(ref Downloads);
        if (DownloadFails is not null) throw DownloadFails;
        foreach (var p in new[] { 0, 40, 100 })
        {
            progress(p);
            await Task.Yield();
        }
    }

    public void ApplyAndRestart(UpdateOffer offer)
    {
        Applies++;
        Applied = offer.Version;
    }
}

public class UpdateServiceTests
{
    private static UpdateService Make(IUpdateBackend backend, bool automatic = true)
        => new(backend, () => automatic);

    [Fact]
    public async Task Reports_up_to_date()
    {
        var service = Make(new FakeBackend());
        var result = await service.CheckNowAsync();
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(service.AvailableVersion);
    }

    [Fact]
    public async Task Reports_the_available_version()
    {
        var service = Make(new FakeBackend { OfferVersion = "1.2.0" });
        var result = await service.CheckNowAsync();
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.2.0", result.Version);
        Assert.Equal("1.2.0", service.AvailableVersion);
    }

    [Fact]
    public async Task Turns_a_failed_check_into_an_error_result()
    {
        var service = Make(new FakeBackend { CheckFails = new HttpRequestException("offline") });
        var result = await service.CheckNowAsync();
        Assert.Equal(UpdateCheckStatus.Error, result.Status);
        Assert.Contains("offline", result.Message);
    }

    [Fact]
    public async Task Reports_not_installed_without_calling_the_backend()
    {
        var backend = new FakeBackend { IsInstalled = false, OfferVersion = "9.9.9" };
        var service = Make(backend);
        var result = await service.CheckNowAsync();
        Assert.Equal(UpdateCheckStatus.NotInstalled, result.Status);
        Assert.Equal(0, backend.Checks);
        await service.RunBackgroundCheckAsync();
        Assert.Equal(0, backend.Checks);
    }

    [Fact]
    public async Task Download_reports_progress_and_apply_restarts_into_the_offer()
    {
        var backend = new FakeBackend { OfferVersion = "2.0.0" };
        var service = Make(backend);
        var seen = new List<int>();
        service.Prompts.Progress = seen.Add;

        await service.CheckNowAsync();
        await service.DownloadAsync();
        Assert.Equal([0, 40, 100], seen);
        Assert.True(service.ApplyAndRestart());
        Assert.Equal("2.0.0", backend.Applied);
    }

    [Fact]
    public async Task Download_without_a_check_is_refused_and_apply_without_download_does_nothing()
    {
        var backend = new FakeBackend { OfferVersion = "2.0.0" };
        var service = Make(backend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync());
        Assert.False(service.ApplyAndRestart());
        await service.CheckNowAsync();
        Assert.False(service.ApplyAndRestart());
        Assert.Equal(0, backend.Applies);
    }

    [Fact]
    public async Task Interactive_check_says_up_to_date()
    {
        var service = Make(new FakeBackend());
        var said = new List<string>();
        service.Prompts.Notify = said.Add;
        await service.CheckInteractivelyAsync();
        Assert.Single(said);
        Assert.Contains("up to date", said[0]);
    }

    [Fact]
    public async Task Interactive_check_explains_a_build_folder_copy()
    {
        var service = Make(new FakeBackend { IsInstalled = false });
        var said = new List<string>();
        service.Prompts.Notify = said.Add;
        var result = await service.CheckInteractivelyAsync();
        Assert.Equal(UpdateCheckStatus.NotInstalled, result.Status);
        Assert.Contains("installed", said.Single());
    }

    [Fact]
    public async Task Interactive_check_installs_only_after_a_yes()
    {
        var backend = new FakeBackend { OfferVersion = "1.1.0" };
        var service = Make(backend);
        service.Prompts.ConfirmInstall = _ => Task.FromResult(false);
        await service.CheckInteractivelyAsync();
        Assert.Equal(0, backend.Downloads);
        Assert.Equal(0, backend.Applies);

        service.Prompts.ConfirmInstall = v => Task.FromResult(v == "1.1.0");
        await service.CheckInteractivelyAsync();
        Assert.Equal(1, backend.Downloads);
        Assert.Equal(1, backend.Applies);
    }

    [Fact]
    public async Task Interactive_check_reports_a_failed_download()
    {
        var backend = new FakeBackend { OfferVersion = "1.1.0", DownloadFails = new IOException("disk full") };
        var service = Make(backend);
        var said = new List<string>();
        service.Prompts.Notify = said.Add;
        service.Prompts.ConfirmInstall = _ => Task.FromResult(true);
        var result = await service.CheckInteractivelyAsync();
        Assert.Equal(UpdateCheckStatus.Error, result.Status);
        Assert.Contains("disk full", said.Single());
        Assert.Equal(0, backend.Applies);
    }

    [Fact]
    public async Task Background_check_honours_the_automatic_updates_setting()
    {
        var backend = new FakeBackend { OfferVersion = "1.1.0" };
        var on = false;
        var service = new UpdateService(backend, () => on);
        await service.RunBackgroundCheckAsync();
        Assert.Equal(0, backend.Checks);

        on = true;
        await service.RunBackgroundCheckAsync();
        Assert.Equal(1, backend.Checks);
        Assert.Equal(1, backend.Downloads);
    }

    [Fact]
    public async Task Background_check_downloads_quietly_and_restarts_only_on_a_yes()
    {
        var backend = new FakeBackend { OfferVersion = "1.1.0" };
        var service = Make(backend);
        var asked = 0;
        service.Prompts.ConfirmRestart = _ => { asked++; return Task.FromResult(false); };
        await service.RunBackgroundCheckAsync();
        Assert.Equal(1, asked);
        Assert.Equal(1, backend.Downloads);
        Assert.Equal(0, backend.Applies);

        // The same update is not downloaded twice.
        service.Prompts.ConfirmRestart = _ => Task.FromResult(true);
        await service.RunBackgroundCheckAsync();
        Assert.Equal(1, backend.Downloads);
        Assert.Equal(1, backend.Applies);
    }

    [Fact]
    public async Task The_loop_checks_on_launch_then_repeats()
    {
        var backend = new FakeBackend();
        using var service = Make(backend);
        service.InitialDelay = TimeSpan.FromMilliseconds(10);
        service.Interval = TimeSpan.FromMilliseconds(30);
        service.Start();
        service.Start();
        await Until(() => backend.Checks >= 3);
        Assert.True(backend.Checks >= 3);
    }

    [Fact]
    public async Task The_loop_survives_errors_and_stops_on_dispose()
    {
        var backend = new FakeBackend { CheckFails = new InvalidOperationException("boom") };
        var service = Make(backend);
        service.InitialDelay = TimeSpan.Zero;
        service.Interval = TimeSpan.FromMilliseconds(20);
        service.Start();
        await Until(() => backend.Checks >= 2);
        service.Dispose();
        await Task.Delay(100);
        var after = backend.Checks;
        await Task.Delay(100);
        Assert.Equal(after, backend.Checks);
    }

    [Fact]
    public async Task The_loop_does_not_start_when_not_installed()
    {
        var backend = new FakeBackend { IsInstalled = false };
        using var service = Make(backend);
        service.InitialDelay = TimeSpan.Zero;
        service.Start();
        await Task.Delay(100);
        Assert.Equal(0, backend.Checks);
    }

    [Fact]
    public async Task Velopack_backend_run_outside_an_install_reports_not_installed()
    {
        // The real UpdateManager, a source that must never be asked, and a process that Velopack did not install.
        using var backend = new ThrowingSourceHolder();
        var service = Make(new VelopackBackend(backend.Source));
        Assert.False(service.IsInstalled);
        Assert.Null(service.CurrentVersion);
        var result = await service.CheckNowAsync();
        Assert.Equal(UpdateCheckStatus.NotInstalled, result.Status);
        await service.CheckInteractivelyAsync();
        service.Start();
        Assert.Equal(0, backend.Calls);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
    }

    private sealed class ThrowingSourceHolder : IDisposable
    {
        public int Calls;
        public IUpdateSource Source { get; }
        public ThrowingSourceHolder() => Source = new CountingSource(this);
        public void Dispose() { }

        private sealed class CountingSource(ThrowingSourceHolder owner) : IUpdateSource
        {
            public Task<VelopackAssetFeed> GetReleaseFeed(Velopack.Logging.IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            {
                owner.Calls++;
                throw new InvalidOperationException("source must not be used");
            }

            public Task DownloadReleaseEntry(Velopack.Logging.IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
            {
                owner.Calls++;
                throw new InvalidOperationException("source must not be used");
            }
        }
    }
}
