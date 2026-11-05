using System.Collections.Concurrent;
using System.Net;
using System.Text;
using BeatLink.CrateDigger.Nfs;

namespace BeatLink.CrateDigger;

/// <summary>
/// <para>Retrieves data files from a Pioneer player over NFS to enable use of track metadata even when four
/// players are using the same media.</para>
/// <para>The primary purpose of this class is provided through the
/// <see cref="Fetch(IPAddress, string, string, FileInfo, CancellationToken)"/> method.</para>
/// <para>This is a singleton, so the single instance is obtained through the <see cref="Instance"/> property.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's crate-digger <c>FileFetcher</c>, by James Elliott, replacing the
/// Remote Tea ONC RPC stack with the hand-written client in <see cref="BeatLink.CrateDigger.Nfs"/>.
/// </remarks>
public sealed class FileFetcher
{
    /// <summary>
    /// The character encoding with which paths are sent to the NFS servers running on players:
    /// UTF-16 little-endian. (Corresponds to the Java <c>CHARSET</c> constant.)
    /// </summary>
    public static readonly Encoding PathEncoding = Encoding.Unicode;

    /// <summary>
    /// The default number of bytes to read from the player in each request for file data. This is a
    /// trade-off between reducing the number of requests and reducing IP fragmentation and expensive
    /// retransmissions of already-sent fragments whenever one is lost.
    /// </summary>
    public const int DefaultReadSize = 2048;

    /// <summary>
    /// How long to wait, in milliseconds, for a response to our UDP RPC calls before retransmitting.
    /// The players respond within a few milliseconds if they are going to at all.
    /// </summary>
    public const int DefaultRpcRetransmitTimeout = 250;

    /// <summary>
    /// The singleton instance of this class.
    /// </summary>
    public static FileFetcher Instance { get; } = new();

    /// <summary>
    /// Create a file fetcher. Internal so tests can build isolated instances; everyone else uses
    /// <see cref="Instance"/>.
    /// </summary>
    internal FileFetcher()
    {
    }

    private int _readSize = DefaultReadSize;

    /// <summary>
    /// The number of bytes to read from the player in each request for file data. This is a trade-off
    /// between reducing the number of requests and reducing IP fragmentation and expensive
    /// retransmissions of already-sent fragments whenever one is lost. Changes do not affect
    /// operations already in progress.
    /// </summary>
    /// <exception cref="ArgumentException">if the value is less than 1024 or greater than
    /// <see cref="NfsClient.MaxData"/></exception>
    public int ReadSize
    {
        get => _readSize;
        set
        {
            if (value < 1024 || value > NfsClient.MaxData)
            {
                throw new ArgumentException($"ReadSize must be between 1024 and {NfsClient.MaxData}, inclusive.", nameof(value));
            }
            _readSize = value;
        }
    }

    private int _retransmitTimeout = DefaultRpcRetransmitTimeout;

    /// <summary>
    /// How long to wait, in milliseconds, for a response to our UDP RPC calls before retransmitting.
    /// The players respond within a few milliseconds if they are going to at all. Changes do not
    /// affect operations already in progress.
    /// </summary>
    /// <exception cref="ArgumentException">if the value is less than 1 or greater than 30000</exception>
    public int RetransmitTimeout
    {
        get => _retransmitTimeout;
        set
        {
            if (value is < 1 or > 30_000)
            {
                throw new ArgumentException("RetransmitTimeout must be between 1 and 30000, inclusive.", nameof(value));
            }
            _retransmitTimeout = value;
        }
    }

    /// <summary>
    /// The UDP port on which we expect to find the players' portmapper service. Only tests point
    /// this anywhere other than the standard port 111.
    /// </summary>
    internal int PortmapperPort { get; set; } = PortmapperClient.DefaultPort;

    /// <summary>
    /// Keeps track of the root file systems of the known players, so we don't have to mount them every
    /// time we want a file. Keys are the address of the player, values are a map from mount paths to
    /// the corresponding file handles.
    /// </summary>
    private readonly ConcurrentDictionary<IPAddress, ConcurrentDictionary<string, FileHandle>> _mounts = new();

    /// <summary>
    /// Holds an NFS client for talking to the player at each address, so we don't need to create a new
    /// one each time we retrieve another file from the player. The client is created the first time we
    /// need a file from a player, and removed when <see cref="RemovePlayer"/> is called.
    /// </summary>
    private readonly ConcurrentDictionary<IPAddress, NfsClient> _nfsClients = new();

    /// <summary>
    /// Mount a file system in preparation to retrieving files from it. Since NFS is a stateless
    /// protocol, and the players don't even maintain a mount list, there is no need to unmount it later.
    /// </summary>
    /// <param name="player">the address of the player from which we are going to retrieve a file</param>
    /// <param name="path">the mount path of the file system from which the file is to be retrieved</param>
    /// <param name="cancellationToken">allows the operation to be interrupted</param>
    /// <returns>an indication of whether the mount was successful, and, if so, the root file handle of
    /// that file system</returns>
    private MountResult Mount(IPAddress player, string path, CancellationToken cancellationToken)
    {
        using var portmapper = new PortmapperClient(player, PortmapperPort) { RetransmitTimeout = _retransmitTimeout };
        var port = portmapper.GetPort(MountClient.Program, MountClient.Version, cancellationToken: cancellationToken);
        using var client = new MountClient(player, port) { RetransmitTimeout = _retransmitTimeout };
        return client.Mount(PathEncoding.GetBytes(path), cancellationToken);
    }

    /// <summary>
    /// Looks up the root file handle for the file system exported by the specified player on the
    /// specified path. If we have already mounted the file system, return the cached file handle.
    /// Otherwise, try to mount it, cache it, and return it.
    /// </summary>
    /// <param name="player">the address of the player from which we are going to retrieve a file</param>
    /// <param name="path">the mount path of the file system from which the file is to be retrieved</param>
    /// <param name="cancellationToken">allows the operation to be interrupted</param>
    /// <returns>the file handle for the root of the file system exported at the specified path</returns>
    /// <exception cref="IOException">if there is a problem talking to the player, or it refuses the mount</exception>
    private FileHandle FindRoot(IPAddress player, string path, CancellationToken cancellationToken)
    {
        if (_mounts.TryGetValue(player, out var playerMap) && playerMap.TryGetValue(path, out var cached))
        {
            return cached;  // Found it, no need to mount again.
        }

        // Not found in our cache, mount the file system from the player.
        var mountResult = Mount(player, path, cancellationToken);
        if (!mountResult.IsOk)
        {
            throw new IOException($"Unable to mount path \"{path}\" on player {player}, mount command returned {mountResult.Status}");
        }

        // Create a cache for the player if one does not yet exist, and record the new file handle in it.
        playerMap = _mounts.GetOrAdd(player, static _ => new ConcurrentDictionary<string, FileHandle>());
        playerMap[path] = mountResult.Directory!;
        return mountResult.Directory!;
    }

    /// <summary>
    /// Clear any cached mount points for the player with the specified address. This should be called
    /// when the player drops off the network, in case a different player later appears on the same
    /// address, or if one of the media slots in the player unmounts, because that file handle will no
    /// longer be valid. Also clears our cached NFS client for the player.
    /// </summary>
    /// <param name="player">the player that has disappeared or unmounted a file system</param>
    public void RemovePlayer(IPAddress player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _mounts.TryRemove(player, out _);
        if (_nfsClients.TryRemove(player, out var client))
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Find or create the NFS client that can talk to a particular player.
    /// </summary>
    /// <param name="player">the address of the player from which we are going to retrieve files</param>
    /// <param name="cancellationToken">allows the operation to be interrupted</param>
    /// <returns>the client that can perform NFS calls on the specified player</returns>
    /// <exception cref="IOException">if there is a problem talking to the player</exception>
    private NfsClient GetNfsClient(IPAddress player, CancellationToken cancellationToken)
    {
        if (!_nfsClients.TryGetValue(player, out var client))
        {
            int port;
            using (var portmapper = new PortmapperClient(player, PortmapperPort) { RetransmitTimeout = _retransmitTimeout })
            {
                port = portmapper.GetPort(NfsClient.Program, NfsClient.Version, cancellationToken: cancellationToken);
            }
            var created = new NfsClient(player, port);
            client = _nfsClients.GetOrAdd(player, created);
            if (!ReferenceEquals(client, created))
            {
                created.Dispose();  // Another thread beat us to it; use theirs.
            }
        }
        client.RetransmitTimeout = _retransmitTimeout;  // In case the value has changed since the last invocation.
        return client;
    }

    /// <summary>
    /// Try to find a file on a player, using our cached mount point if possible, or a new mount.
    /// </summary>
    /// <param name="player">the address of the player from which we are going to retrieve a file</param>
    /// <param name="mountPath">the mount path of the file system from which the file is to be retrieved</param>
    /// <param name="filePath">the path to the file itself within the exported file system</param>
    /// <param name="cancellationToken">allows the operation to be interrupted</param>
    /// <returns>the result of looking up the last element in the path to the file</returns>
    /// <exception cref="FileNotFoundException">if an element of the path does not exist</exception>
    /// <exception cref="IOException">if there is any other problem finding the file</exception>
    private DirOpResult Find(IPAddress player, string mountPath, string filePath, CancellationToken cancellationToken)
    {
        var root = FindRoot(player, mountPath, cancellationToken);
        var client = GetNfsClient(player, cancellationToken);

        // Iterate over the elements of the path to the file we want to find (the players can't handle
        // multipart path names themselves).
        var fileHandle = root;
        DirOpResult? result = null;
        foreach (var element in filePath.Split('/'))
        {
            if (element.Length == 0)
            {
                continue;
            }
            result = client.Lookup(fileHandle, PathEncoding.GetBytes(element), cancellationToken);
            if (result.Status != NfsStatus.Ok)
            {
                var message = $"Unable to find file \"{filePath}\", lookup of element \"{element}\" returned status of {(uint)result.Status}";
                if (result.Status == NfsStatus.NoSuchEntity)
                {
                    throw new FileNotFoundException(message);
                }
                throw new IOException(message);
            }
            fileHandle = result.File!;
        }
        if (result == null)
        {
            throw new ArgumentException("Must supply at least one non-empty path element to look up.", nameof(filePath));
        }
        return result;
    }

    /// <summary>
    /// Download a file from a player, storing it locally.
    /// </summary>
    /// <param name="player">the address of the player from which we are to retrieve a file</param>
    /// <param name="mountPath">the mount path of the file system from which the file is to be retrieved</param>
    /// <param name="sourcePath">the path to the file itself within the exported file system</param>
    /// <param name="destination">the local file to which the remote contents should be saved</param>
    /// <exception cref="FileNotFoundException">if the file does not exist on the player</exception>
    /// <exception cref="IOException">if there is a problem retrieving the file</exception>
    public void Fetch(IPAddress player, string mountPath, string sourcePath, FileInfo destination) =>
        Fetch(player, mountPath, sourcePath, destination, CancellationToken.None);

    /// <summary>
    /// Download a file from a player, storing it locally.
    /// </summary>
    /// <param name="player">the address of the player from which we are to retrieve a file</param>
    /// <param name="mountPath">the mount path of the file system from which the file is to be retrieved</param>
    /// <param name="sourcePath">the path to the file itself within the exported file system</param>
    /// <param name="destination">the local file to which the remote contents should be saved</param>
    /// <param name="cancellationToken">allows the download to be interrupted</param>
    /// <exception cref="FileNotFoundException">if the file does not exist on the player</exception>
    /// <exception cref="IOException">if there is a problem retrieving the file</exception>
    /// <exception cref="OperationCanceledException">if the download is cancelled</exception>
    public void Fetch(IPAddress player, string mountPath, string sourcePath, FileInfo destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(mountPath);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destination);

        try
        {
            using var outputStream = destination.Create();

            // Make sure the file exists on the player, and find its file handle.
            var found = Find(player, mountPath, sourcePath, cancellationToken);
            if (found.Attributes!.Type != NfsFileType.Regular)
            {
                throw new IOException($"Path \"{sourcePath}\" is not a normal file.");
            }

            // Read the contents of the remote file to the local file.
            var client = GetNfsClient(player, cancellationToken);
            var readSize = _readSize;  // Snapshot so a concurrent change cannot affect this operation.
            var offset = 0u;
            long bytesLeft = found.Attributes.Size;
            while (bytesLeft > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = bytesLeft < readSize ? (uint)bytesLeft : (uint)readSize;
                var result = client.Read(found.File!, offset, count, cancellationToken);
                if (result.Status != NfsStatus.Ok)
                {
                    throw new IOException($"Problem reading \"{sourcePath}\": NFS read call returned status: {(uint)result.Status}");
                }
                var data = result.Data!;
                if (data.Length != count)
                {
                    throw new IOException($"Problem reading \"{sourcePath}\": tried to read {count} bytes but only received {data.Length}");
                }
                outputStream.Write(data);
                offset += (uint)data.Length;
                bytesLeft -= data.Length;
            }
        }
        catch (RpcException e)
        {
            throw new IOException($"Unable to download file \"{sourcePath}\", caught ONC RPC exception.", e);
        }
    }
}
