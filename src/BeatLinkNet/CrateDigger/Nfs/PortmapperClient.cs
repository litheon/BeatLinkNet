using System.Net;

namespace BeatLink.CrateDigger.Nfs;

/// <summary>
/// A client for the portmapper service (ONC RPC program 100000, version 2, RFC 1057 appendix A),
/// which is used to discover the UDP ports on which a player's mount and NFS servers are listening.
/// </summary>
public sealed class PortmapperClient : IDisposable
{
    /// <summary>
    /// The ONC RPC program number of the portmapper service.
    /// </summary>
    public const uint Program = 100000;

    /// <summary>
    /// The version of the portmapper protocol that we speak.
    /// </summary>
    public const uint Version = 2;

    /// <summary>
    /// The well-known UDP port on which the portmapper listens.
    /// </summary>
    public const int DefaultPort = 111;

    /// <summary>
    /// The protocol number identifying UDP in portmapper requests.
    /// </summary>
    public const uint IpProtocolUdp = 17;

    /// <summary>
    /// The <c>PMAPPROC_GETPORT</c> procedure number.
    /// </summary>
    private const uint ProcedureGetPort = 3;

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
    /// Create a client for querying the portmapper on the specified host.
    /// </summary>
    /// <param name="host">the address of the server whose portmapper we will query</param>
    /// <param name="port">the UDP port on which the portmapper listens, usually <see cref="DefaultPort"/></param>
    public PortmapperClient(IPAddress host, int port = DefaultPort)
    {
        _client = new OncRpcUdpClient(host, port, Program, Version);
    }

    /// <summary>
    /// Look up the port on which the specified ONC RPC program is listening.
    /// </summary>
    /// <param name="program">the program number to look up, such as <see cref="MountClient.Program"/>
    /// or <see cref="NfsClient.Program"/></param>
    /// <param name="version">the desired version of that program</param>
    /// <param name="protocol">the transport protocol of interest, usually <see cref="IpProtocolUdp"/></param>
    /// <param name="cancellationToken">allows the call to be interrupted</param>
    /// <returns>the port on which the program is listening</returns>
    /// <exception cref="RpcException">if there is a problem in the remote procedure call layer</exception>
    /// <exception cref="IOException">if the program is not registered with the portmapper</exception>
    public int GetPort(uint program, uint version, uint protocol = IpProtocolUdp, CancellationToken cancellationToken = default)
    {
        var reply = _client.Call(ProcedureGetPort, writer =>
        {
            writer.WriteUInt32(program);
            writer.WriteUInt32(version);
            writer.WriteUInt32(protocol);
            writer.WriteUInt32(0);  // The port argument is ignored by GETPORT.
        }, cancellationToken);
        var port = reply.ReadUInt32();
        if (port == 0 || port > ushort.MaxValue)
        {
            throw new IOException($"Program {program} version {version} is not registered with the portmapper on {_client.Host}.");
        }
        return (int)port;
    }

    /// <summary>
    /// Close the UDP socket used by this client.
    /// </summary>
    public void Dispose() => _client.Dispose();
}
