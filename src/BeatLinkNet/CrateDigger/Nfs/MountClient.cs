using System.Net;
using System.Text;

namespace BeatLink.CrateDigger.Nfs;

/// <summary>
/// The result of asking a player to mount an exported file system (RFC 1094 appendix A
/// <c>fhstatus</c>). Corresponds to the generated <c>FHStatus</c> class in the Java crate-digger.
/// </summary>
/// <param name="Status">zero if the mount succeeded, or a Unix error number explaining why it failed</param>
/// <param name="Directory">the root file handle of the mounted file system, when the mount succeeded</param>
public sealed record MountResult(uint Status, FileHandle? Directory)
{
    /// <summary>
    /// Indicates whether the mount succeeded, in which case <see cref="Directory"/> holds the root
    /// file handle of the file system.
    /// </summary>
    public bool IsOk => Status == 0;
}

/// <summary>
/// One entry in a player's export list (RFC 1094 appendix A <c>exportlist</c>): a directory that
/// can be mounted, along with the groups allowed to mount it.
/// </summary>
/// <param name="Directory">the path at which the file system can be mounted</param>
/// <param name="Groups">the names of the groups which are allowed to mount it</param>
public sealed record ExportEntry(string Directory, IReadOnlyList<string> Groups);

/// <summary>
/// A client for the mount protocol (ONC RPC program 100005, version 1, RFC 1094 appendix A),
/// which translates the path of an exported file system into the NFS file handle of its root
/// directory. Since NFS is stateless and the players do not maintain a mount list, nothing is ever
/// unmounted.
/// </summary>
public sealed class MountClient : IDisposable
{
    /// <summary>
    /// The ONC RPC program number of the mount service.
    /// </summary>
    public const uint Program = 100005;

    /// <summary>
    /// The version of the mount protocol that we speak.
    /// </summary>
    public const uint Version = 1;

    /// <summary>
    /// The <c>MOUNTPROC_MNT</c> procedure number.
    /// </summary>
    private const uint ProcedureMount = 1;

    /// <summary>
    /// The <c>MOUNTPROC_EXPORT</c> procedure number.
    /// </summary>
    private const uint ProcedureExport = 5;

    private readonly OncRpcUdpClient _client;

    /// <summary>
    /// How long to wait, in milliseconds, for a response before retransmitting a request;
    /// see <see cref="OncRpcUdpClient.RetransmitTimeout"/>.
    /// </summary>
    public int RetransmitTimeout
    {
        get => _client.RetransmitTimeout;
        set => _client.RetransmitTimeout = value;
    }

    /// <summary>
    /// Create a client for talking to the mount service on the specified host.
    /// </summary>
    /// <param name="host">the address of the server whose file systems we want to mount</param>
    /// <param name="port">the UDP port on which the mount service is listening, as reported by
    /// the server's portmapper (see <see cref="PortmapperClient.GetPort"/>)</param>
    public MountClient(IPAddress host, int port)
    {
        _client = new OncRpcUdpClient(host, port, Program, Version);
    }

    /// <summary>
    /// Ask the server to mount the file system exported at the specified path, obtaining the file
    /// handle of its root directory.
    /// </summary>
    /// <param name="directoryPath">the bytes of the mount path; Pioneer players expect paths to be
    /// encoded as UTF-16 little-endian (see <see cref="FileFetcher.PathEncoding"/>)</param>
    /// <param name="cancellationToken">allows the call to be interrupted</param>
    /// <returns>an indication of whether the mount was successful, and, if so, the root file handle
    /// of that file system</returns>
    /// <exception cref="RpcException">if there is a problem in the remote procedure call layer</exception>
    public MountResult Mount(ReadOnlyMemory<byte> directoryPath, CancellationToken cancellationToken = default)
    {
        var reply = _client.Call(ProcedureMount, writer => writer.WriteOpaque(directoryPath.Span), cancellationToken);
        var status = reply.ReadUInt32();
        return new MountResult(status, status == 0 ? FileHandle.ReadFrom(reply) : null);
    }

    /// <summary>
    /// Retrieve the list of file systems the server exports.
    /// </summary>
    /// <param name="pathEncoding">the encoding with which the server represents paths and group
    /// names; defaults to the UTF-16 little-endian used by Pioneer players when <c>null</c></param>
    /// <param name="cancellationToken">allows the call to be interrupted</param>
    /// <returns>the exported directories, in the order the server listed them</returns>
    /// <exception cref="RpcException">if there is a problem in the remote procedure call layer</exception>
    public IReadOnlyList<ExportEntry> Export(Encoding? pathEncoding = null, CancellationToken cancellationToken = default)
    {
        pathEncoding ??= FileFetcher.PathEncoding;
        var reply = _client.Call(ProcedureExport, null, cancellationToken);
        var entries = new List<ExportEntry>();
        while (reply.ReadBool())
        {
            var directory = reply.ReadString(pathEncoding);
            var groups = new List<string>();
            while (reply.ReadBool())
            {
                groups.Add(reply.ReadString(pathEncoding));
            }
            entries.Add(new ExportEntry(directory, groups));
        }
        return entries;
    }

    /// <summary>
    /// Close the UDP socket used by this client.
    /// </summary>
    public void Dispose() => _client.Dispose();
}
