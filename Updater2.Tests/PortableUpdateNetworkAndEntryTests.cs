using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DS4Updater.Dtos;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests;

[TestClass]
public sealed class PortableUpdateNetworkAndEntryTests
{
    [TestMethod]
    public void PortableDoubleClickAndMalformedArgumentsNeverSelectTheLegacyLifecycle()
    {
        using var folder = new FixtureFolder();
        string executable = Path.Combine(folder.Path, "DS4Updater.exe");
        Assert.IsFalse(PortableUpdateRequest.ShouldUsePortableLifetime(Array.Empty<string>(), executable));
        File.WriteAllText(Path.Combine(folder.Path, PortableUpdateProcessGuard.MarkerFileName), "damaged marker");
        Assert.IsTrue(PortableUpdateRequest.ShouldUsePortableLifetime(Array.Empty<string>(), executable));
        Assert.IsTrue(PortableUpdateRequest.ShouldUsePortableLifetime(new[] { "--portable_safe_typo" }, executable));
        Assert.ThrowsException<ArgumentException>(() => PortableUpdateRequest.Parse(Array.Empty<string>(), executable));
        File.Delete(Path.Combine(folder.Path, PortableUpdateProcessGuard.MarkerFileName));
        Directory.CreateDirectory(Path.Combine(folder.Path, PortableUpdateProcessGuard.MarkerFileName));
        Assert.IsTrue(PortableUpdateRequest.ShouldUsePortableLifetime(Array.Empty<string>(), executable));
    }

    [TestMethod]
    public void RetainedWorkerDoubleClickDoesNotNeedAMarkerToAvoidLegacyCleanup()
    {
        using var folder = new FixtureFolder();
        string worker = Path.Combine(folder.Path, "Updates", "portable-worker-" + Guid.NewGuid().ToString("N"), "DS4Updater.exe");
        Assert.IsTrue(PortableUpdateRequest.ShouldUsePortableLifetime(Array.Empty<string>(), worker));
        Assert.ThrowsException<ArgumentException>(() => PortableUpdateRequest.Parse(Array.Empty<string>(), worker));
    }

    [TestMethod]
    public void WorkerRecordBindsExactRequestLocationLauncherAndCopiedImage()
    {
        using var folder = new FixtureFolder();
        var fixture = CreateWorker(folder.Path);
        Assert.AreEqual(fixture.Record, PortableWorkerSession.ValidateWorker(fixture.Request, fixture.Executable, root => root));
        Assert.ThrowsException<InvalidDataException>(() => PortableWorkerSession.ValidateWorker(
            fixture.Request with { ReleaseTag = "VIIPERRC4.4" }, fixture.Executable, root => root));
        Assert.ThrowsException<InvalidDataException>(() => PortableWorkerSession.ValidateWorker(
            fixture.Request with { OriginalExe = "Other.exe" }, fixture.Executable, root => root));
        File.WriteAllText(fixture.Executable, "a changed worker image");
        Assert.ThrowsException<InvalidDataException>(() => PortableWorkerSession.ValidateWorker(fixture.Request, fixture.Executable, root => root));
    }

    [TestMethod]
    public void WorkerValidationItselfRejectsRetargetingWithoutDependingOnTheAppParser()
    {
        using var folder = new FixtureFolder();
        var fixture = CreateWorker(folder.Path);
        var differentTarget = fixture.Request with { TargetDirectory = Path.Combine(folder.Path, "other") };
        Assert.ThrowsException<ArgumentException>(() => PortableWorkerSession.ValidateWorker(differentTarget, fixture.Executable, root => root));
        Assert.ThrowsException<InvalidDataException>(() => PortableWorkerSession.ValidateWorker(fixture.Request, fixture.Executable, root => root + "-changed"));
    }

    [TestMethod]
    public void WorkerRejectsAnUnrelatedLauncherAndOversizedRecord()
    {
        using var folder = new FixtureFolder();
        var fixture = CreateWorker(folder.Path);
        string recordPath = Path.Combine(Path.GetDirectoryName(fixture.Executable), "request.json");
        File.WriteAllText(recordPath, JsonSerializer.Serialize(fixture.Record with { LauncherPath = Path.Combine(folder.Path, "other.exe") }));
        Assert.ThrowsException<InvalidDataException>(() => PortableWorkerSession.ValidateWorker(fixture.Request, fixture.Executable, root => root));
        File.WriteAllText(recordPath, new string(' ', 8193));
        Assert.ThrowsException<InvalidDataException>(() => PortableWorkerSession.ValidateWorker(fixture.Request, fixture.Executable, root => root));
    }

    [TestMethod]
    public async Task MetadataBodyHasADeadlineEvenAfterHeadersWereReceived()
    {
        using var folder = new FixtureFolder();
        using var operations = new PortableUpdateOperations(folder.Path, null,
            new ResponseHandler(() => new StreamContent(new StalledStream())), TimeSpan.FromMilliseconds(100));
        await ExpectCancellation(() => operations.FetchReleaseAsync("VIIPERRC4.5.1", CancellationToken.None));
    }

    [TestMethod]
    public async Task AssetBodyHasADeadlineEvenAfterHeadersWereReceived()
    {
        using var folder = new FixtureFolder();
        using var operations = new PortableUpdateOperations(folder.Path, null,
            new ResponseHandler(() => new StreamContent(new StalledStream())), TimeSpan.FromMilliseconds(100));
        await ExpectCancellation(() => operations.DownloadAsync(Asset(100), CancellationToken.None));
    }

    [TestMethod]
    public async Task BuildReceiptBodyHasADeadlineAndNeverWritesThePackageArchive()
    {
        using var folder = new FixtureFolder();
        using var operations = new PortableUpdateOperations(folder.Path, null,
            new ResponseHandler(() => new StreamContent(new StalledStream())), TimeSpan.FromMilliseconds(100));
        await ExpectCancellation(() => operations.DownloadBuildReceiptAsync(Asset(100), CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFiles(folder.Path).Length);
    }

    [TestMethod]
    public async Task BuildReceiptIsMemoryBoundedAndRetainsNoPackageFile()
    {
        using var folder = new FixtureFolder();
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic receipt");
        using var operations = new PortableUpdateOperations(folder.Path, null,
            new ResponseHandler(() => new ByteArrayContent(bytes)));
        CollectionAssert.AreEqual(bytes, await operations.DownloadBuildReceiptAsync(Asset(bytes.Length), CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            operations.DownloadBuildReceiptAsync(Asset(bytes.Length + 1), CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            operations.DownloadBuildReceiptAsync(Asset(PortableReleaseResolver.MaximumReceiptBytes + 1), CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFiles(folder.Path).Length);
    }

    [TestMethod]
    public void LegacyPreCopyArchiveCheckRequiresBothPublishedSizeAndDigest()
    {
        using var folder = new FixtureFolder();
        string path = Path.Combine(folder.Path, "synthetic-package.zip");
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic package, never executed");
        File.WriteAllBytes(path, bytes);
        var asset = Asset(bytes.Length) with { digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)) };
        var identity = new PortableReleaseIdentity("VIIPERRC4.6", new Version(9, 2, 3, 4), asset, true);
        identity.VerifyArchive(path);
        Assert.ThrowsException<InvalidDataException>(() =>
            (identity with { Asset = asset with { size = bytes.Length + 1 } }).VerifyArchive(path));
        Assert.ThrowsException<InvalidDataException>(() =>
            (identity with { Asset = asset with { digest = "sha256:" + new string('b', 64) } }).VerifyArchive(path));
        File.WriteAllBytes(path, new byte[bytes.Length]);
        Assert.ThrowsException<InvalidDataException>(() => identity.VerifyArchive(path));
    }

    [TestMethod]
    public async Task CallerCancellationStillStopsTheResponseBody()
    {
        using var folder = new FixtureFolder();
        using var cancel = new CancellationTokenSource();
        using var operations = new PortableUpdateOperations(folder.Path, null,
            new ResponseHandler(() => new StreamContent(new StalledStream())));
        Task download = operations.DownloadAsync(Asset(100), cancel.Token);
        cancel.Cancel();
        await ExpectCancellation(() => download);
    }

    [TestMethod]
    public async Task TruncatedAndOversizedBodiesCannotBecomeApprovedArchives()
    {
        foreach (int length in new[] { 2, 101 })
        {
            using var folder = new FixtureFolder();
            using var operations = new PortableUpdateOperations(folder.Path, null,
                new ResponseHandler(() => new ByteArrayContent(new byte[length])));
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => operations.DownloadAsync(Asset(100), CancellationToken.None));
        }
    }

    private static GitHubReleaseAsset Asset(long size) => new("DS4Windows_VIIPER_x64.zip",
        "https://github.com/Pav-Osmolski/DS4Windows/releases/download/VIIPERRC4.5.1/DS4Windows_VIIPER_x64.zip",
        size, "sha256:" + new string('a', 64));

    private static (PortableUpdateRequest Request, PortableWorkerRecord Record, string Executable) CreateWorker(string target)
    {
        var request = new PortableUpdateRequest(target, 123, 638000000000000000, "VIIPERRC4.5.1", "DS4Windows.exe", true);
        string executable = Path.Combine(target, "Updates", "portable-worker-" + Guid.NewGuid().ToString("N"), "DS4Updater.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable));
        byte[] bytes = Encoding.UTF8.GetBytes("fixture image only; never executed");
        File.WriteAllBytes(executable, bytes);
        File.WriteAllText(Path.Combine(target, request.LaunchExe), "fixture app only; never executed");
        var record = new PortableWorkerRecord(1, request, Convert.ToHexString(SHA256.HashData(bytes)),
            456, 638000000000000001, Path.Combine(target, "DS4Updater.exe"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable), "request.json"), JsonSerializer.Serialize(record));
        return (request, record, executable);
    }

    private static async Task ExpectCancellation(Func<Task> action)
    {
        try { await action(); Assert.Fail("The response body did not observe its deadline/cancellation."); }
        catch (OperationCanceledException) { }
    }

    private sealed class ResponseHandler(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FixtureFolder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DS4Updater-entry-network-test-" + Guid.NewGuid().ToString("N"));
        internal FixtureFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
