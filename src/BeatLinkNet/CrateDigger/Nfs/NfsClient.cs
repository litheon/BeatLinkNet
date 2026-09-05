using System.Net;

namespace BeatLink.CrateDigger.Nfs;

/// <summary>
/// The result of an NFS directory operation such as a lookup (RFC 1094 <c>diropres</c>).
/// Corresponds to the generated <c>DirOpRes</c> class in the Java crate-digger.
/// </summary>
/// <param name="Status">whether the operation succeeded</param>
/// <param name="File">the handle of the file that was found, when the operation succeeded</param>
/// <param name="Attributes">the attributes of the file that was found, when the operation succeeded</param>
public sealed record DirOpResult(NfsStatus Status, FileHandle? File, NfsFileAttributes? Attributes);

/// <summary>
/// The result of an NFS attribute query (RFC 1094 <c>attrstat</c>).
/// </summary>
/// <param name="Status">whether the query succeeded</param>
/// <param name="Attributes">the attributes of the file, when the query succeeded</param>
public sealed record GetAttrResult(NfsStatus Status, NfsFileAttributes? Attributes);

/// <summary>
/// The result of an NFS read operation (RFC 1094 <c>readres</c>). Corresponds to the generated
/// <c>ReadRes</c> class in the Java crate-digger.
/// </summary>
/// <param name="Status">whether the read succeeded</param>
/// <param name="Attributes">the attributes of the file, when the read succeeded</param>
/// <param name="Data">the bytes that were read, when the read succeeded</param>
public sealed record ReadResult(NfsStatus Status, NfsFileAttributes? Attributes, byte[]? Data);

/// <summary>
/// A client for the procedures of the NFS version 2 protocol (ONC RPC program 100003, version 2,
/// RFC 1094) which are needed to download files from Pioneer players: looking up files and reading
/// their contents.
/// </summary>
public sealed class NfsClient : IDisposable
{
    /// <summary>
    /// The ONC RPC program number of the NFS service.
    /// </summary>
    public const uint Program = 100003;

    /// <summary>
    /// The version of the NFS protocol that we speak.
    /// </summary>
    public const uint Version = 2;

    /// <summary>
    /// The maximum number of bytes of data which can be requested in a single read (RFC 1094
    /// <c>MAXDATA</c>). Corresponds to <c>nfs.MAXDATA</c> in the Java crate-digger.
    /// </summary>
    public const int MaxData = 8192;

    /// <summary>
    /// The <c>NFSPROC_GETATTR</c> procedure number.
    /// </summary>
    private const uint ProcedureGetAttr = 1;

    /// <summary>
    /// The <c>NFSPROC_LOOKUP</c> procedure number.
    /// </summary>
    private const uint ProcedureLookup = 4;

    /// <summary>
    /// The <c>NFSPROC_READ</c> procedure number.
    /// </summary>
    private const uint ProcedureRead = 6;

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
    /// Create a client for talking to the NFS service on the specified host.
    /// </summary>
    /// <param name="host">the address of the server from which we want to read files</param>
    /// <param name="port">the UDP port on which the NFS service is listening, as reported by the
    /// server's portmapper (see <see cref="PortmapperClient.GetPort"/>)</param>
    public NfsClient(IPAddress host, int port)
    {
        _client = new OncRpcUdpClient(host, port, Program, Version);
    }

    /// <summary>
    /// Retrieve the attributes of a file or directory.
    /// </summary>
    /// <param name="file">the handle of the file whose attributes are wanted</param>
    /// <param name="cancellationToken">allows the call to be interrupted</param>
    /// <returns>the status of the operation, and the attributes if it succeeded</returns>
    /// <exception cref="RpcException">if there is a problem in the remote procedure call layer</exception>
    public GetAttrResult GetAttr(FileHandle file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var reply = _client.Call(ProcedureGetAttr, file.WriteTo, cancellationToken);
        var status = (NfsStatus)reply.ReadUInt32();
        return new GetAttrResult(status, status == NfsStatus.Ok ? NfsFileAttributes.ReadFrom(reply) : null);
    }

    /// <summary>
    /// Look up a single name within a directory. The players cannot handle multipart path names, so
    /// a path must be traversed by looking up one component at a time, as
    /// <see cref="FileFetcher"/> does.
    /// </summary>
    /// <param name="directory">the handle of the directory in which to look</param>
    /// <param name="name">the bytes of the name to look up; Pioneer players expect names to be
    /// encoded as UTF-16 little-endian (see <see cref="FileFetcher.PathEncoding"/>)</param>
    /// <param name="cancellationToken">allows the call to be interrupted</param>
    /// <returns>the status of the operation, and the handle and attributes of the file if it succeeded</returns>
    /// <exception cref="RpcException">if there is a problem in the remote procedure call layer</exception>
    public DirOpResult Lookup(FileHandle directory, ReadOnlyMemory<byte> name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var reply = _client.Call(ProcedureLookup, writer =>
        {
            directory.WriteTo(writer);
            writer.WriteOpaque(name.Span);
        }, cancellationToken);
        var status = (NfsStatus)reply.ReadUInt32();
        if (status != NfsStatus.Ok)
        {
            return new DirOpResult(status, null, null);
        }
        return new DirOpResult(status, FileHandle.ReadFrom(reply), NfsFileAttributes.ReadFrom(reply));
    }

    /// <summary>
    /// Read a range of bytes from a file.
    /// </summary>
    /// <param name="file">the handle of the file to read</param>
    /// <param name="offset">the byte offset within the file at which the read should start</param>
    /// <param name="count">the number of bytes to read, at most <see cref="MaxData"/></param>
    /// <param name="cancellationToken">allows the call to be interrupted</param>
    /// <returns>the status of the operation, and the attributes and data if it succeeded</returns>
    /// <exception cref="RpcException">if there is a problem in the remote procedure call layer</exception>
    /// <exception cref="ArgumentOutOfRangeException">if <paramref name="count"/> is larger than <see cref="MaxData"/></exception>
    public ReadResult Read(FileHandle file, uint offset, uint count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (uint)MaxData);
        var reply = _client.Call(ProcedureRead, writer =>
        {
            file.WriteTo(writer);
            writer.WriteUInt32(offset);
            writer.WriteUInt32(count);
            writer.WriteUInt32(0);  // The totalcount argument is unused (and left zero by the Java crate-digger).
        }, cancellationToken);
        var status = (NfsStatus)reply.ReadUInt32();
        if (status != NfsStatus.Ok)
        {
            return new ReadResult(status, null, null);
        }
        return new ReadResult(status, NfsFileAttributes.ReadFrom(reply), reply.ReadOpaque());
    }

    /// <summary>
    /// Close the UDP socket used by this client.
    /// </summary>
    public void Dispose() => _client.Dispose();
}
