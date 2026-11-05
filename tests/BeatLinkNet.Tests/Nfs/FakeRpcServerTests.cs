using System.Net;
using BeatLink.CrateDigger;
using BeatLink.CrateDigger.Nfs;

namespace BeatLink.Tests.Nfs;

public class FakeRpcServerTests : IDisposable
{
    private readonly FakeRpcServer _server = new();
    private readonly FileFetcher _fetcher;
    private readonly List<string> _tempFiles = [];

    public FakeRpcServerTests()
    {
        _fetcher = new FileFetcher
        {
            PortmapperPort = _server.Port,
            RetransmitTimeout = 100
        };
    }

    public void Dispose()
    {
        _fetcher.RemovePlayer(IPAddress.Loopback);
        _server.Dispose();
        foreach (var file in _tempFiles)
        {
            File.Delete(file);
        }
        GC.SuppressFinalize(this);
    }

    private FileInfo NewTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"beat-link-net-nfs-test-{Guid.NewGuid():N}");
        _tempFiles.Add(path);
        return new FileInfo(path);
    }

    private static byte[] MakeContent(int length)
    {
        var content = new byte[length];
        for (var i = 0; i < length; i++)
        {
            content[i] = (byte)(i * 31 + 7);
        }
        return content;
    }

    private byte[] SetUpExportPdb(int length)
    {
        var root = _server.AddMount("/B/");
        var pioneer = _server.AddDirectory(root, "PIONEER");
        var rekordbox = _server.AddDirectory(pioneer, "rekordbox");
        var content = MakeContent(length);
        _server.AddFile(rekordbox, "export.pdb", content);
        return content;
    }

    [Fact]
    public void FetchDownloadsWholeFileWithChunkedReads()
    {
        var content = SetUpExportPdb(5000);
        var destination = NewTempFile();

        _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/export.pdb", destination);

        Assert.Equal(content, File.ReadAllBytes(destination.FullName));
        List<(uint Offset, uint Count)> expected = [(0u, 2048u), (2048u, 2048u), (4096u, 904u)];
        Assert.Equal(expected, _server.ReadRequests);
    }

    [Fact]
    public void FetchChunksExactlyAtReadSizeBoundary()
    {
        var content = SetUpExportPdb(4096);
        var destination = NewTempFile();

        _fetcher.Fetch(IPAddress.Loopback, "/B/", "/PIONEER/rekordbox/export.pdb", destination);

        Assert.Equal(content, File.ReadAllBytes(destination.FullName));
        List<(uint Offset, uint Count)> expected = [(0u, 2048u), (2048u, 2048u)];
        Assert.Equal(expected, _server.ReadRequests);
    }

    [Fact]
    public void FetchHonorsConfiguredReadSize()
    {
        var content = SetUpExportPdb(3000);
        _fetcher.ReadSize = 2500;
        var destination = NewTempFile();

        _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/export.pdb", destination);

        Assert.Equal(content, File.ReadAllBytes(destination.FullName));
        List<(uint Offset, uint Count)> expected = [(0u, 2500u), (2500u, 500u)];
        Assert.Equal(expected, _server.ReadRequests);
    }

    [Fact]
    public void FetchRetransmitsWhenARequestIsDropped()
    {
        var content = SetUpExportPdb(100);
        _server.DropNextDatagrams = 1;  // Swallow the first request (the portmapper GETPORT).
        var destination = NewTempFile();

        _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/export.pdb", destination);

        Assert.Equal(content, File.ReadAllBytes(destination.FullName));
        Assert.Equal(0, _server.DropNextDatagrams);  // The drop really happened, so a retransmission saved us.
    }

    [Fact]
    public void MountsAreCachedAndRemovePlayerForcesRemount()
    {
        SetUpExportPdb(100);

        _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/export.pdb", NewTempFile());
        Assert.Equal(1, _server.MountCallCount);

        _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/export.pdb", NewTempFile());
        Assert.Equal(1, _server.MountCallCount);  // Cached mount was reused.

        _fetcher.RemovePlayer(IPAddress.Loopback);
        _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/export.pdb", NewTempFile());
        Assert.Equal(2, _server.MountCallCount);  // The cache was invalidated, so we mounted again.
    }

    [Fact]
    public void MountFailureSurfacesAsIOException()
    {
        SetUpExportPdb(100);
        _server.MountStatus = 13;  // EACCES

        var e = Assert.Throws<IOException>(() =>
            _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/export.pdb", NewTempFile()));
        Assert.Contains("Unable to mount path \"/B/\"", e.Message);
        Assert.Contains("13", e.Message);
    }

    [Fact]
    public void UnknownMountPathSurfacesAsIOException()
    {
        SetUpExportPdb(100);

        var e = Assert.Throws<IOException>(() =>
            _fetcher.Fetch(IPAddress.Loopback, "/C/", "PIONEER/rekordbox/export.pdb", NewTempFile()));
        Assert.Contains("Unable to mount path \"/C/\"", e.Message);
    }

    [Fact]
    public void MissingFileThrowsFileNotFoundException()
    {
        SetUpExportPdb(100);

        var e = Assert.Throws<FileNotFoundException>(() =>
            _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/missing.pdb", NewTempFile()));
        Assert.Contains("lookup of element \"missing.pdb\" returned status of 2", e.Message);
    }

    [Fact]
    public void OtherLookupFailuresThrowPlainIOException()
    {
        SetUpExportPdb(100);
        _server.LookupFailName = "rekordbox";
        _server.LookupStatus = 5;  // NFSERR_IO

        var e = Assert.Throws<IOException>(() =>
            _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/export.pdb", NewTempFile()));
        Assert.Contains("lookup of element \"rekordbox\" returned status of 5", e.Message);
    }

    [Fact]
    public void FetchingADirectoryThrowsIOException()
    {
        SetUpExportPdb(100);

        var e = Assert.Throws<IOException>(() =>
            _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox", NewTempFile()));
        Assert.Contains("is not a normal file", e.Message);
    }

    [Fact]
    public void ReadFailureSurfacesAsIOException()
    {
        SetUpExportPdb(100);
        _server.ReadStatus = 70;  // NFSERR_STALE

        var e = Assert.Throws<IOException>(() =>
            _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/export.pdb", NewTempFile()));
        Assert.Contains("NFS read call returned status: 70", e.Message);
    }

    [Fact]
    public void EmptySourcePathThrowsArgumentException()
    {
        SetUpExportPdb(100);

        Assert.Throws<ArgumentException>(() =>
            _fetcher.Fetch(IPAddress.Loopback, "/B/", "///", NewTempFile()));
    }

    [Fact]
    public void CancelledFetchThrowsOperationCanceledException()
    {
        SetUpExportPdb(100);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            _fetcher.Fetch(IPAddress.Loopback, "/B/", "PIONEER/rekordbox/export.pdb", NewTempFile(), cancellation.Token));
    }

    [Fact]
    public void ReadSizeAndRetransmitTimeoutAreValidated()
    {
        Assert.Throws<ArgumentException>(() => _fetcher.ReadSize = 1023);
        Assert.Throws<ArgumentException>(() => _fetcher.ReadSize = NfsClient.MaxData + 1);
        _fetcher.ReadSize = NfsClient.MaxData;
        Assert.Equal(NfsClient.MaxData, _fetcher.ReadSize);

        Assert.Throws<ArgumentException>(() => _fetcher.RetransmitTimeout = 0);
        Assert.Throws<ArgumentException>(() => _fetcher.RetransmitTimeout = 30_001);
        _fetcher.RetransmitTimeout = 250;
        Assert.Equal(250, _fetcher.RetransmitTimeout);
    }

    [Fact]
    public void ExportListsTheMountedFileSystems()
    {
        _server.AddMount("/B/");
        _server.AddMount("/C/");

        using var portmapper = new PortmapperClient(IPAddress.Loopback, _server.Port) { RetransmitTimeout = 100 };
        var port = portmapper.GetPort(MountClient.Program, MountClient.Version);
        using var mountClient = new MountClient(IPAddress.Loopback, port) { RetransmitTimeout = 100 };

        var exports = mountClient.Export();
        Assert.Equal(2, exports.Count);
        Assert.Contains(exports, entry => entry.Directory == "/B/" && entry.Groups.Count == 0);
        Assert.Contains(exports, entry => entry.Directory == "/C/" && entry.Groups.Count == 0);
    }

    [Fact]
    public void GetAttrReportsFileAttributes()
    {
        var root = _server.AddMount("/B/");
        _server.AddFile(root, "test.bin", MakeContent(1234));

        using var portmapper = new PortmapperClient(IPAddress.Loopback, _server.Port) { RetransmitTimeout = 100 };
        var mountPort = portmapper.GetPort(MountClient.Program, MountClient.Version);
        using var mountClient = new MountClient(IPAddress.Loopback, mountPort) { RetransmitTimeout = 100 };
        var mounted = mountClient.Mount(FileFetcher.PathEncoding.GetBytes("/B/"));
        Assert.True(mounted.IsOk);

        var nfsPort = portmapper.GetPort(NfsClient.Program, NfsClient.Version);
        using var nfsClient = new NfsClient(IPAddress.Loopback, nfsPort) { RetransmitTimeout = 100 };
        var lookup = nfsClient.Lookup(mounted.Directory!, FileFetcher.PathEncoding.GetBytes("test.bin"));
        Assert.Equal(NfsStatus.Ok, lookup.Status);
        Assert.Equal(NfsFileType.Regular, lookup.Attributes!.Type);
        Assert.Equal(1234u, lookup.Attributes.Size);

        var attributes = nfsClient.GetAttr(lookup.File!);
        Assert.Equal(NfsStatus.Ok, attributes.Status);
        Assert.Equal(1234u, attributes.Attributes!.Size);
        Assert.Equal(NfsFileType.Regular, attributes.Attributes.Type);

        var rootAttributes = nfsClient.GetAttr(mounted.Directory!);
        Assert.Equal(NfsFileType.Directory, rootAttributes.Attributes!.Type);
    }
}
