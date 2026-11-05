using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BeatLink.CrateDigger.Nfs;

namespace BeatLink.Tests.Nfs;

/// <summary>
/// A node in the virtual file tree served by <see cref="FakeRpcServer"/>.
/// </summary>
internal sealed class FakeNode
{
    public required byte[] Handle { get; init; }
    public required bool IsDirectory { get; init; }
    public byte[] Content { get; set; } = [];
    public Dictionary<string, FakeNode> Children { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// An in-process fake ONC RPC server which answers portmapper, mount, and NFS version 2 requests
/// on a single loopback UDP port, serving a small virtual file tree. GETPORT always reports the
/// server's own port, so a client bootstrapped through it will send every protocol here. Provides
/// scripting knobs for dropping datagrams and forcing error statuses, and records the requests it
/// has seen so tests can verify chunking, caching, and retransmission behavior.
/// </summary>
internal sealed class FakeRpcServer : IDisposable
{
    private readonly Socket _socket;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private int _nextHandle;
    private readonly Dictionary<string, FakeNode> _mountRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeNode> _nodesByHandle = new(StringComparer.Ordinal);

    /// <summary>The ephemeral loopback port on which the server is listening.</summary>
    public int Port { get; }

    /// <summary>When positive, that many incoming datagrams are silently discarded (no reply).</summary>
    public int DropNextDatagrams;

    /// <summary>The total number of datagrams received, including dropped ones.</summary>
    public int DatagramsReceived;

    /// <summary>How many MOUNTPROC_MNT calls have been answered.</summary>
    public int MountCallCount;

    /// <summary>When nonzero, MOUNTPROC_MNT fails with this status instead of mounting.</summary>
    public uint MountStatus;

    /// <summary>When set, looking up this name fails with <see cref="LookupStatus"/>.</summary>
    public string? LookupFailName;

    /// <summary>The NFS status returned when <see cref="LookupFailName"/> is looked up.</summary>
    public uint LookupStatus = 2;  // NFSERR_NOENT

    /// <summary>When nonzero, NFSPROC_READ fails with this status instead of returning data.</summary>
    public uint ReadStatus;

    /// <summary>The offset and count of every NFSPROC_READ call received, in order.</summary>
    public List<(uint Offset, uint Count)> ReadRequests { get; } = [];

    public FakeRpcServer()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Poll rather than block forever: disposing a socket on which another thread is blocked in a
        // synchronous ReceiveFrom can hang indefinitely on Unix, so the loop must notice _running.
        _socket.ReceiveTimeout = 100;
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "fake-rpc-server" };
        _thread.Start();
    }

    /// <summary>
    /// Export a file system at the given mount path, returning its root directory node.
    /// </summary>
    public FakeNode AddMount(string path)
    {
        var root = NewNode(isDirectory: true);
        lock (_mountRoots)
        {
            _mountRoots[path] = root;
        }
        return root;
    }

    /// <summary>
    /// Add a subdirectory to a directory in the virtual tree.
    /// </summary>
    public FakeNode AddDirectory(FakeNode parent, string name)
    {
        var directory = NewNode(isDirectory: true);
        parent.Children[name] = directory;
        return directory;
    }

    /// <summary>
    /// Add a regular file with the given content to a directory in the virtual tree.
    /// </summary>
    public FakeNode AddFile(FakeNode parent, string name, byte[] content)
    {
        var file = NewNode(isDirectory: false);
        file.Content = content;
        parent.Children[name] = file;
        return file;
    }

    private FakeNode NewNode(bool isDirectory)
    {
        var handle = new byte[FileHandle.Size];
        BinaryPrimitives.WriteInt32BigEndian(handle, Interlocked.Increment(ref _nextHandle));
        Random.Shared.NextBytes(handle.AsSpan(4));  // Make handles opaque-looking.
        var node = new FakeNode { Handle = handle, IsDirectory = isDirectory };
        lock (_nodesByHandle)
        {
            _nodesByHandle[Convert.ToHexString(handle)] = node;
        }
        return node;
    }

    private FakeNode? FindByHandle(byte[] handle)
    {
        lock (_nodesByHandle)
        {
            return _nodesByHandle.GetValueOrDefault(Convert.ToHexString(handle));
        }
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[ushort.MaxValue];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            int received;
            try
            {
                received = _socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                continue;  // Just a poll interval expiring; check whether we are still running.
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref DatagramsReceived);
            if (Volatile.Read(ref DropNextDatagrams) > 0)
            {
                Interlocked.Decrement(ref DropNextDatagrams);
                continue;
            }
            byte[]? reply;
            try
            {
                reply = HandleCall(new XdrReader(buffer.AsMemory(0, received).ToArray()));
            }
            catch (IOException)
            {
                continue;  // Ignore malformed datagrams.
            }
            if (reply != null)
            {
                try
                {
                    _socket.SendTo(reply, remote);
                }
                catch (SocketException)
                {
                    // The client may have gone away; keep serving.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    private byte[]? HandleCall(XdrReader call)
    {
        var transactionId = call.ReadUInt32();
        if (call.ReadUInt32() != 0 || call.ReadUInt32() != 2)
        {
            return null;  // Not an RPC version 2 call.
        }
        var program = call.ReadUInt32();
        call.ReadUInt32();  // Program version, which the fake trusts.
        var procedure = call.ReadUInt32();
        call.ReadUInt32();
        call.ReadOpaque();  // Credentials, ignored.
        call.ReadUInt32();
        call.ReadOpaque();  // Verifier, ignored.

        var reply = new XdrWriter();
        reply.WriteUInt32(transactionId);
        reply.WriteUInt32(1);  // REPLY
        reply.WriteUInt32(0);  // MSG_ACCEPTED
        reply.WriteUInt32(0);  // Verifier flavor AUTH_NONE.
        reply.WriteUInt32(0);  // Verifier body length zero.

        switch (program, procedure)
        {
            case (100000, 3):  // PMAPPROC_GETPORT
                reply.WriteUInt32(0);  // SUCCESS
                reply.WriteUInt32((uint)Port);  // Everything is served from this one socket.
                break;
            case (100005, 1):  // MOUNTPROC_MNT
                reply.WriteUInt32(0);  // SUCCESS
                HandleMount(call, reply);
                break;
            case (100005, 5):  // MOUNTPROC_EXPORT
                reply.WriteUInt32(0);  // SUCCESS
                HandleExport(reply);
                break;
            case (100003, 1):  // NFSPROC_GETATTR
                reply.WriteUInt32(0);  // SUCCESS
                HandleGetAttr(call, reply);
                break;
            case (100003, 4):  // NFSPROC_LOOKUP
                reply.WriteUInt32(0);  // SUCCESS
                HandleLookup(call, reply);
                break;
            case (100003, 6):  // NFSPROC_READ
                reply.WriteUInt32(0);  // SUCCESS
                HandleRead(call, reply);
                break;
            default:
                reply.WriteUInt32(3);  // PROC_UNAVAIL
                break;
        }
        return reply.ToArray();
    }

    private void HandleMount(XdrReader call, XdrWriter reply)
    {
        Interlocked.Increment(ref MountCallCount);
        var path = Encoding.Unicode.GetString(call.ReadOpaque());
        var forcedStatus = Volatile.Read(ref MountStatus);
        if (forcedStatus != 0)
        {
            reply.WriteUInt32(forcedStatus);
            return;
        }
        FakeNode? root;
        lock (_mountRoots)
        {
            root = _mountRoots.GetValueOrDefault(path);
        }
        if (root == null)
        {
            reply.WriteUInt32(2);  // ENOENT
            return;
        }
        reply.WriteUInt32(0);
        reply.WriteFixedOpaque(root.Handle);
    }

    private void HandleExport(XdrWriter reply)
    {
        lock (_mountRoots)
        {
            foreach (var path in _mountRoots.Keys)
            {
                reply.WriteBool(true);
                reply.WriteOpaque(Encoding.Unicode.GetBytes(path));
                reply.WriteBool(false);  // No groups.
            }
        }
        reply.WriteBool(false);  // End of the export list.
    }

    private void HandleGetAttr(XdrReader call, XdrWriter reply)
    {
        var node = FindByHandle(call.ReadFixedOpaque(FileHandle.Size));
        if (node == null)
        {
            reply.WriteUInt32(70);  // NFSERR_STALE
            return;
        }
        reply.WriteUInt32(0);  // NFS_OK
        WriteAttributes(reply, node);
    }

    private void HandleLookup(XdrReader call, XdrWriter reply)
    {
        var directory = FindByHandle(call.ReadFixedOpaque(FileHandle.Size));
        var name = Encoding.Unicode.GetString(call.ReadOpaque());
        if (directory == null)
        {
            reply.WriteUInt32(70);  // NFSERR_STALE
            return;
        }
        if (name == LookupFailName)
        {
            reply.WriteUInt32(LookupStatus);
            return;
        }
        if (!directory.Children.TryGetValue(name, out var child))
        {
            reply.WriteUInt32(2);  // NFSERR_NOENT
            return;
        }
        reply.WriteUInt32(0);  // NFS_OK
        reply.WriteFixedOpaque(child.Handle);
        WriteAttributes(reply, child);
    }

    private void HandleRead(XdrReader call, XdrWriter reply)
    {
        var node = FindByHandle(call.ReadFixedOpaque(FileHandle.Size));
        var offset = call.ReadUInt32();
        var count = call.ReadUInt32();
        call.ReadUInt32();  // totalcount, unused.
        lock (ReadRequests)
        {
            ReadRequests.Add((offset, count));
        }
        var forcedStatus = Volatile.Read(ref ReadStatus);
        if (forcedStatus != 0)
        {
            reply.WriteUInt32(forcedStatus);
            return;
        }
        if (node == null)
        {
            reply.WriteUInt32(70);  // NFSERR_STALE
            return;
        }
        reply.WriteUInt32(0);  // NFS_OK
        WriteAttributes(reply, node);
        var available = Math.Max(0, node.Content.Length - (int)offset);
        var length = Math.Min((int)count, available);
        reply.WriteOpaque(node.Content.AsSpan((int)offset, length));
    }

    private static void WriteAttributes(XdrWriter reply, FakeNode node)
    {
        reply.WriteUInt32(node.IsDirectory ? 2u : 1u);  // NFDIR or NFREG.
        reply.WriteUInt32(node.IsDirectory ? 0x41edu : 0x81a4u);  // Mode bits.
        reply.WriteUInt32(1);  // Link count.
        reply.WriteUInt32(0);  // User id.
        reply.WriteUInt32(0);  // Group id.
        reply.WriteUInt32((uint)node.Content.Length);  // Size.
        reply.WriteUInt32(8192);  // Block size.
        reply.WriteUInt32(0);  // Device number.
        reply.WriteUInt32((uint)((node.Content.Length + 8191) / 8192));  // Blocks.
        reply.WriteUInt32(1);  // File system id.
        reply.WriteUInt32(BinaryPrimitives.ReadUInt32BigEndian(node.Handle));  // File id.
        reply.WriteUInt32(0);
        reply.WriteUInt32(0);  // Access time.
        reply.WriteUInt32(0);
        reply.WriteUInt32(0);  // Modification time.
        reply.WriteUInt32(0);
        reply.WriteUInt32(0);  // Status change time.
    }

    public void Dispose()
    {
        _running = false;
        // Let the receive loop notice the flag and exit before closing the socket: disposing a socket
        // out from under a thread blocked in a synchronous ReceiveFrom can hang forever on Unix.
        _thread.Join(TimeSpan.FromSeconds(5));
        _socket.Dispose();
    }
}
