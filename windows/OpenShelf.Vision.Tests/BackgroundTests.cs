using System.Net;
using System.Security.Cryptography;
using ImageMagick;
using OpenShelf.Convert;

namespace OpenShelf.Vision.Tests;

public class ModelStoreTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(respond(request));
        }
    }

    private static (ModelDefinition Definition, byte[] Bytes) Fake()
    {
        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);
        var hash = System.Convert.ToHexString(SHA256.HashData(bytes));
        return (new ModelDefinition("fake.onnx", new Uri("https://example.test/fake.onnx"), hash, bytes.Length, "test"), bytes);
    }

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    [Fact]
    public void The_pinned_model_is_u2netp_with_a_checksum_and_a_licence()
    {
        var model = ModelDefinition.U2Netp;
        Assert.Equal("u2netp.onnx", model.FileName);
        Assert.Equal("https", model.Url.Scheme);
        Assert.Equal(64, model.Sha256.Length);
        Assert.Equal("Apache-2.0", model.Licence);
        Assert.InRange(model.SizeBytes, 4_000_000, 6_000_000);
    }

    [Fact]
    public async Task Downloads_checks_and_installs_the_model_once()
    {
        using var dir = new TempDir();
        var (definition, bytes) = Fake();
        var handler = new FakeHandler(_ => Ok(bytes));
        var store = new ModelStore(dir.File("models"), definition, handler, bundledDirectory: "");
        Assert.False(store.IsModelAvailable);

        var reported = new List<double>();
        await store.EnsureAsync(new SyncProgress(reported.Add));
        Assert.True(store.IsModelAvailable);
        Assert.Equal(bytes, File.ReadAllBytes(store.DownloadedPath));
        Assert.Equal(1.0, reported[^1]);
        Assert.Equal(reported.Order().ToList(), reported);
        Assert.Equal(["fake.onnx"], Directory.GetFileSystemEntries(dir.File("models")).Select(Path.GetFileName));

        await store.EnsureAsync();
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_download_with_the_wrong_checksum_is_discarded()
    {
        using var dir = new TempDir();
        var (definition, bytes) = Fake();
        bytes[10] ^= 0xFF;
        var store = new ModelStore(dir.File("models"), definition, new FakeHandler(_ => Ok(bytes)), bundledDirectory: "");
        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => store.EnsureAsync());
        Assert.Contains("checksum", error.Message);
        Assert.False(store.IsModelAvailable);
        Assert.Empty(Directory.GetFileSystemEntries(dir.File("models")));
    }

    [Fact]
    public async Task A_server_error_reports_a_connection_problem_and_keeps_nothing()
    {
        using var dir = new TempDir();
        var (definition, _) = Fake();
        var store = new ModelStore(dir.File("models"), definition, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), bundledDirectory: "");
        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() => store.EnsureAsync());
        Assert.Contains("internet connection", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(dir.File("models")));
    }

    [Fact]
    public async Task A_cancelled_download_keeps_nothing()
    {
        using var dir = new TempDir();
        var (definition, bytes) = Fake();
        using var cts = new CancellationTokenSource();
        var store = new ModelStore(dir.File("models"), definition, new FakeHandler(_ => Ok(bytes)), bundledDirectory: "");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnsureAsync(new SyncProgress(p => { if (p > 0) cts.Cancel(); }), cts.Token));
        Assert.False(store.IsModelAvailable);
        Assert.Empty(Directory.GetFileSystemEntries(dir.File("models")));
    }

    [Fact]
    public async Task A_bundled_copy_is_used_without_downloading()
    {
        using var dir = new TempDir();
        var (definition, bytes) = Fake();
        Directory.CreateDirectory(dir.File("bundle"));
        File.WriteAllBytes(dir.File("bundle\\fake.onnx"), bytes);
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no network expected"));
        var store = new ModelStore(dir.File("models"), definition, handler, dir.File("bundle"));
        Assert.True(store.IsModelAvailable);
        Assert.Equal(dir.File("bundle\\fake.onnx"), store.FindModel());
        await store.EnsureAsync();
        Assert.Equal(0, handler.Requests);
    }
}

public class BackgroundRemoverTests
{
    [Fact]
    public void Without_the_model_nothing_is_available_and_cutting_out_needs_a_download()
    {
        using var dir = new TempDir();
        var store = new ModelStore(dir.File("models"), handler: new HttpClientHandler(), bundledDirectory: "");
        var remover = new BackgroundRemover(store);
        Assert.False(remover.IsModelAvailable);
    }

    [FactWhen("Model")]
    public async Task Cuts_out_the_subject_and_leaves_the_background_transparent()
    {
        using var dir = new TempDir();
        var path = Generate.SubjectOnPlain(dir.File("Photo.png"));
        var cutout = await new BackgroundRemover().CutOutAsync(path);
        Assert.Equal(640, cutout.Width);
        Assert.Equal(480, cutout.Height);
        Assert.Equal(640 * 480, cutout.Mask.Length);
        Assert.Equal(640 * 480 * 4, cutout.Bgra.Length);
        byte AlphaAt(int x, int y) => cutout.Bgra[(y * 640 + x) * 4 + 3];
        Assert.True(AlphaAt(320, 240) > 240, "the middle of the subject stays opaque");
        Assert.True(AlphaAt(8, 8) < 16, "a corner of the plain background becomes transparent");
        Assert.True(AlphaAt(630, 470) < 16);
        Assert.InRange(cutout.Coverage, 0.1, 0.6);
        Assert.Equal(cutout.Mask[240 * 640 + 320], AlphaAt(320, 240));
        // The subject's own colour is untouched.
        Assert.True(cutout.Bgra[(240 * 640 + 320) * 4 + 2] > 200, "red channel of the core");
    }

    [FactWhen("Model")]
    public async Task Saves_a_transparent_png_beside_the_original()
    {
        using var dir = new TempDir();
        var path = Generate.SubjectOnPlain(dir.File("Photo.png"));
        var saved = await new BackgroundRemover().RemoveAsync(path);
        Assert.Equal(dir.File("Photo No Background.png"), saved);
        Assert.Equal(["Photo No Background.png", "Photo.png"], dir.Names());
        using var image = new MagickImage(saved);
        Assert.Equal(MagickFormat.Png, image.Format);
        Assert.True(image.HasAlpha);
        Assert.Equal(640u, image.Width);
        using var pixels = image.GetPixels();
        Assert.True(pixels.GetPixel(4, 4).ToColor()!.A < 16, "the corner is transparent in the file");
        // A second run takes the next free name.
        var again = await new BackgroundRemover().RemoveAsync(path);
        Assert.Equal(dir.File("Photo No Background 2.png"), again);
    }

    [FactWhen("Model")]
    public async Task A_plain_picture_has_no_clear_subject_and_saves_nothing()
    {
        using var dir = new TempDir();
        var path = Generate.PlainImage(dir.File("Plain.png"));
        var error = await Assert.ThrowsAsync<NoSubjectException>(() => new BackgroundRemover().RemoveAsync(path));
        Assert.Contains("No clear subject was found", error.Message);
        Assert.Equal(["Plain.png"], dir.Names());
    }

    [FactWhen("Model")]
    public async Task A_batch_counts_pictures_without_a_subject()
    {
        using var dir = new TempDir();
        var subject = Generate.SubjectOnPlain(dir.File("A.png"));
        var plain = Generate.PlainImage(dir.File("B.png"));
        var broken = dir.File("C.png");
        File.WriteAllText(broken, "nope");
        var progress = new List<double>();
        var outcome = await new BackgroundRemover().RemoveAsync([subject, plain, broken], new SyncProgress(progress.Add));
        Assert.Equal([dir.File("A No Background.png")], outcome.Saved);
        Assert.Equal([plain], outcome.WithoutSubject);
        Assert.Single(outcome.Failed);
        Assert.Equal(broken, outcome.Failed[0].Path);
        Assert.Equal(1.0, progress[^1]);
    }

    [FactWhen("Model")]
    public async Task Cancelling_saves_nothing()
    {
        using var dir = new TempDir();
        var path = Generate.SubjectOnPlain(dir.File("Photo.png"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackgroundRemover().RemoveAsync(path, cts.Token));
        Assert.Equal(["Photo.png"], dir.Names());
    }

    [FactWhen("Model")]
    public async Task The_downloaded_model_matches_the_pinned_checksum()
    {
        var path = new ModelStore().FindModel()!;
        await using var stream = File.OpenRead(path);
        Assert.Equal(ModelDefinition.U2Netp.Sha256, System.Convert.ToHexString(await SHA256.HashDataAsync(stream)), ignoreCase: true);
    }
}
