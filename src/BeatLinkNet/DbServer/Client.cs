using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace BeatLink.DbServer;

/// <summary>
/// Manages a connection to the dbserver port on a particular player, allowing queries to be sent, and their
/// responses to be interpreted. Direct instantiation is not available outside the library; instead the
/// connection manager must be used.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>Client</c>, by James Elliott.
/// </remarks>
public sealed class Client : IDisposable
{
    private readonly ILogger _logger = BeatLinkLogging.CreateLogger<Client>();

    /// <summary>
    /// The socket on which we are communicating with the player's dbserver.
    /// </summary>
    private readonly Socket _socket;

    /// <summary>
    /// The stream used to read from and send messages to the dbserver.
    /// </summary>
    private readonly NetworkStream _stream;

    /// <summary>
    /// The player number we are communicating with.
    /// </summary>
    public int TargetPlayer { get; }

    /// <summary>
    /// The player we are pretending to be.
    /// </summary>
    public int PosingAsPlayer { get; }

    /// <summary>
    /// The greeting message exchanged over a new connection consists of a 4-byte number field containing the
    /// value 1.
    /// </summary>
    public static readonly NumberField GreetingField = new(1, 4);

    /// <summary>
    /// Used to assign unique numbers to each transaction. Only accessed while holding <see cref="_lock"/>.
    /// </summary>
    private long _transactionCounter;

    /// <summary>
    /// Serializes request/response cycles and transaction number assignment, replacing the Java
    /// <c>synchronized</c> methods. Like them, it is reentrant.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Used to ensure that only one thread at a time is attempting to perform menu operations, which require
    /// more than one request/response cycle. The <see cref="System.Threading.Monitor"/> it is used with has
    /// the same reentrant, owner-tracked semantics as the Java <c>ReentrantLock</c>.
    /// </summary>
    private readonly object _menuLock = new();

    /// <summary>
    /// Set once the connection has been closed, so that it will not be closed again.
    /// </summary>
    private volatile bool _closed;

    /// <summary>
    /// The dbserver client must be constructed with a freshly-opened socket to the dbserver port on the
    /// specified player number. It must be in charge of all communication with that socket. The connection
    /// manager is expected to be the only class that instantiates clients.
    /// </summary>
    /// <param name="socket">the newly opened network socket to the dbserver on a player</param>
    /// <param name="targetPlayer">the player number to which the socket was opened</param>
    /// <param name="posingAsPlayer">the player number that we are pretending to be</param>
    /// <exception cref="IOException">if there is a problem performing the greeting and setup exchanges</exception>
    internal Client(Socket socket, int targetPlayer, int posingAsPlayer)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        TargetPlayer = targetPlayer;
        PosingAsPlayer = posingAsPlayer;

        try
        {
            // Exchange the greeting message, which is a 4-byte number field containing the value 1.
            SendField(GreetingField);
            var response = Field.Read(_stream);
            if (response is NumberField { Size: 4, Value: 1 })
            {
                PerformSetupExchange();
            }
            else
            {
                throw new IOException($"Did not receive expected greeting response from dbserver, instead got: {response}");
            }
        }
        catch (IOException)
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Convenience constructor which opens a new socket connection to the dbserver at the specified host and
    /// port before performing the greeting and setup exchanges.
    /// </summary>
    /// <param name="host">the address on which the target player's dbserver is listening</param>
    /// <param name="port">the port on which the target player's dbserver is listening</param>
    /// <param name="targetPlayer">the player number to which the connection is being opened</param>
    /// <param name="posingAsPlayer">the player number that we are pretending to be</param>
    /// <exception cref="IOException">if there is a problem connecting or performing the greeting and setup
    /// exchanges</exception>
    internal Client(string host, int port, int targetPlayer, int posingAsPlayer) :
        this(OpenSocket(host, port), targetPlayer, posingAsPlayer)
    {
    }

    /// <summary>
    /// Opens a connected TCP socket to the specified host and port, for the convenience constructor.
    /// </summary>
    private static Socket OpenSocket(string host, int port)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Connect(host, port);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Exchanges the initial fully-formed messages which establishes the transaction context for queries to
    /// the dbserver.
    /// </summary>
    /// <exception cref="IOException">if there is a problem during the exchange</exception>
    private void PerformSetupExchange()
    {
        var setupRequest = new Message(0xfffffffeL, Message.KnownType.SetupReq, new NumberField(PosingAsPlayer, 4));
        SendMessage(setupRequest);
        var response = Message.Read(_stream);
        if (response.RecognizedType != Message.KnownType.MenuAvailable)
        {
            throw new IOException($"Did not receive message type 0x4000 in response to setup message, got: {response}");
        }
        if (response.Arguments.Count != 2)
        {
            throw new IOException($"Did not receive two arguments in response to setup message, got: {response}");
        }
        if (response.Arguments[1] is not NumberField player)
        {
            throw new IOException($"Second argument in response to setup message was not a number: {response}");
        }
        if (player.Value != TargetPlayer)
        {
            throw new IOException($"Expected to connect to player {TargetPlayer}, but welcome response identified itself as player {player.Value}");
        }
    }

    /// <summary>
    /// Exchanges the final messages which politely report our intention to disconnect from the dbserver.
    /// </summary>
    private void PerformTeardownExchange()
    {
        var teardownRequest = new Message(0xfffffffeL, Message.KnownType.TeardownReq);
        SendMessage(teardownRequest);
        // At this point, the server closes the connection from its end, so we can't read anymore.
    }

    /// <summary>
    /// Check whether our connection is still available for use. We will close it if there is ever a problem
    /// communicating with the dbserver.
    /// </summary>
    public bool IsConnected => !_closed && _socket.Connected;

    /// <summary>
    /// Closes the connection to the dbserver, first sending a polite teardown message. This instance can no
    /// longer be used after this action.
    /// </summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }
        try
        {
            PerformTeardownExchange();
        }
        catch (IOException e)
        {
            _logger.LogWarning(e, "Problem reporting our intention to close the dbserver connection");
        }
        _closed = true;
        try
        {
            _stream.Dispose();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Problem closing dbserver client stream");
        }
        try
        {
            _socket.Dispose();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Problem closing dbserver client socket");
        }
    }

    /// <summary>
    /// Attempt to send the specified field to the dbserver. This low-level method is used only in setting up
    /// the connection. It was previously also used for sending parts of larger-scale messages, but because
    /// that sometimes led to them being fragmented into multiple network packets, and Windows rekordbox cannot
    /// handle that, full message sending no longer uses this method.
    /// </summary>
    /// <param name="field">the field to be sent</param>
    /// <exception cref="IOException">if the field cannot be sent</exception>
    private void SendField(Field field)
    {
        if (IsConnected)
        {
            try
            {
                field.Write(_stream);
            }
            catch (IOException e)
            {
                _logger.LogWarning(e, "Problem trying to write field to dbserver, closing connection");
                Dispose();
                throw;
            }
            return;
        }
        throw new IOException("SendField() called after dbserver connection was closed");
    }

    /// <summary>
    /// Allocate a new transaction number for a message that is to be sent. Called only while holding
    /// <see cref="_lock"/> (or during single-threaded construction).
    /// </summary>
    /// <returns>the appropriate number field to use for the new message's transaction argument</returns>
    private NumberField AssignTransactionNumber() => new(++_transactionCounter, 4);

    /// <summary>
    /// Sends a message to the dbserver, first assembling it into a single byte buffer so that it can be sent
    /// as a single packet, because Windows rekordbox cannot handle fragmented dbserver messages.
    /// </summary>
    /// <param name="message">the message to be sent</param>
    /// <exception cref="IOException">if there is a problem sending it</exception>
    private void SendMessage(Message message)
    {
        var totalSize = 0;
        foreach (var field in message.Fields)
        {
            totalSize += field.GetBytes().Length;
        }
        var combined = new byte[totalSize];
        var offset = 0;
        foreach (var field in message.Fields)
        {
            var bytes = field.GetBytes();
            bytes.CopyTo(combined.AsMemory(offset));
            offset += bytes.Length;
        }
        _stream.Write(combined);
    }

    /// <summary>
    /// <para>Build the <em>R:M:S:T</em> parameter that begins many queries, when T=1.</para>
    ///
    /// <para>Many request messages take, as their first argument, a 4-byte value where each byte has a special
    /// meaning. The first byte is the player number of the requesting player. The second identifies the menu
    /// into which the response is being loaded, as described by <see cref="Message.MenuIdentifier"/>. The third
    /// specifies the media slot from which information is desired, as described by
    /// <see cref="CdjStatus.TrackSourceSlot"/>, and the fourth byte identifies the type of track about which
    /// information is being requested; in most requests this has the value 1, which corresponds to rekordbox
    /// tracks, and this version of the function assumes that.</para>
    /// </summary>
    /// <param name="requestingPlayer">the player number that is asking the question</param>
    /// <param name="targetMenu">the destination for the response to this query</param>
    /// <param name="slot">the media library of interest for this query</param>
    /// <returns>the first argument to send with the query in order to obtain the desired information</returns>
    public static NumberField BuildRmst(int requestingPlayer, Message.MenuIdentifier targetMenu,
        CdjStatus.TrackSourceSlot slot) =>
        BuildRmst(requestingPlayer, targetMenu, slot, CdjStatus.TrackType.Rekordbox);

    /// <summary>
    /// <para>Build the <em>R:M:S:T</em> parameter that begins many queries.</para>
    ///
    /// <para>Many request messages take, as their first argument, a 4-byte value where each byte has a special
    /// meaning. The first byte is the player number of the requesting player. The second identifies the menu
    /// into which the response is being loaded, as described by <see cref="Message.MenuIdentifier"/>. The third
    /// specifies the media slot from which information is desired, as described by
    /// <see cref="CdjStatus.TrackSourceSlot"/>, and the fourth byte identifies the type of track about which
    /// information is being requested; in most requests this has the value 1, which corresponds to rekordbox
    /// tracks.</para>
    /// </summary>
    /// <param name="requestingPlayer">the player number that is asking the question</param>
    /// <param name="targetMenu">the destination for the response to this query</param>
    /// <param name="slot">the media library of interest for this query</param>
    /// <param name="trackType">the type of track about which information is being requested</param>
    /// <returns>the first argument to send with the query in order to obtain the desired information</returns>
    public static NumberField BuildRmst(int requestingPlayer, Message.MenuIdentifier targetMenu,
        CdjStatus.TrackSourceSlot slot, CdjStatus.TrackType trackType)
    {
        ArgumentNullException.ThrowIfNull(targetMenu);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(trackType);
        return new NumberField(((long)(requestingPlayer & 0xff) << 24) |
            ((long)targetMenu.ProtocolValue << 16) |
            ((long)slot.ProtocolValue << 8) |
            trackType.ProtocolValue);
    }

    /// <summary>
    /// Build the <em>R:M:S:T</em> parameter that begins many queries, posing as ourselves and asking about
    /// rekordbox tracks. See
    /// <see cref="BuildRmst(int, Message.MenuIdentifier, CdjStatus.TrackSourceSlot, CdjStatus.TrackType)"/>
    /// for the details of the packed format.
    /// </summary>
    /// <param name="targetMenu">the destination for the response to this query</param>
    /// <param name="slot">the media library of interest for this query</param>
    /// <returns>the first argument to send with the query in order to obtain the desired information</returns>
    public NumberField BuildRmst(Message.MenuIdentifier targetMenu, CdjStatus.TrackSourceSlot slot) =>
        BuildRmst(PosingAsPlayer, targetMenu, slot, CdjStatus.TrackType.Rekordbox);

    /// <summary>
    /// Build the <em>R:M:S:T</em> parameter that begins many queries, posing as ourselves. See
    /// <see cref="BuildRmst(int, Message.MenuIdentifier, CdjStatus.TrackSourceSlot, CdjStatus.TrackType)"/>
    /// for the details of the packed format.
    /// </summary>
    /// <param name="targetMenu">the destination for the response to this query</param>
    /// <param name="slot">the media library of interest for this query</param>
    /// <param name="trackType">the type of track about which information is being requested</param>
    /// <returns>the first argument to send with the query in order to obtain the desired information</returns>
    public NumberField BuildRmst(Message.MenuIdentifier targetMenu, CdjStatus.TrackSourceSlot slot,
        CdjStatus.TrackType trackType) =>
        BuildRmst(PosingAsPlayer, targetMenu, slot, trackType);

    /// <summary>
    /// Send a request that expects a single message as its response, then read and return that response.
    /// </summary>
    /// <param name="requestType">identifies what kind of request to send</param>
    /// <param name="responseType">identifies the type of response we expect, or <c>null</c> if we'll accept
    /// anything</param>
    /// <param name="arguments">the argument fields to send in the request</param>
    /// <returns>the response from the player</returns>
    /// <exception cref="IOException">if there is a communication problem, or if the response does not have the
    /// same transaction ID as the request</exception>
    public Message SimpleRequest(Message.KnownType requestType, Message.KnownType? responseType,
        params Field[] arguments)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        lock (_lock)
        {
            var transaction = AssignTransactionNumber();
            var request = new Message(transaction, new NumberField(requestType.ProtocolValue, 2), arguments);
            SendMessage(request);
            var response = Message.Read(_stream);
            if (response.Transaction.Value != transaction.Value)
            {
                throw new IOException($"Received response with wrong transaction ID. Expected: {transaction.Value}, got: {response}");
            }
            if (responseType != null && response.RecognizedType != responseType)
            {
                throw new IOException($"Received response with wrong type. Expected: {responseType}, got: {response}");
            }
            return response;
        }
    }

    /// <summary>
    /// Send a request for a menu that we will retrieve items from in subsequent requests. This variant works
    /// for nearly all menus, but when you are trying to request metadata for an unanalyzed (non-rekordbox)
    /// track, you need to use
    /// <see cref="MenuRequestTyped(Message.KnownType, Message.MenuIdentifier, CdjStatus.TrackSourceSlot, CdjStatus.TrackType, Field[])"/>
    /// and specify the actual type of the track whose metadata you want to retrieve.
    /// </summary>
    /// <param name="requestType">identifies what kind of menu request to send</param>
    /// <param name="targetMenu">the destination for the response to this query</param>
    /// <param name="slot">the media library of interest for this query</param>
    /// <param name="arguments">the additional arguments needed, if any, to complete the request</param>
    /// <returns>the <see cref="Message.KnownType.MenuAvailable"/> response reporting how many items are
    /// available in the menu</returns>
    /// <exception cref="IOException">if there is a problem communicating, or if the requested menu is not
    /// available</exception>
    /// <exception cref="InvalidOperationException">if <see cref="TryLockingForMenuOperations"/> was not called
    /// successfully before attempting this call</exception>
    public Message MenuRequest(Message.KnownType requestType, Message.MenuIdentifier targetMenu,
        CdjStatus.TrackSourceSlot slot, params Field[] arguments) =>
        MenuRequestTyped(requestType, targetMenu, slot, CdjStatus.TrackType.Rekordbox, arguments);

    /// <summary>
    /// Send a request for a menu that we will retrieve items from in subsequent requests, when the request
    /// must reflect the actual type of track being asked about.
    /// </summary>
    /// <param name="requestType">identifies what kind of menu request to send</param>
    /// <param name="targetMenu">the destination for the response to this query</param>
    /// <param name="slot">the media library of interest for this query</param>
    /// <param name="trackType">the type of track for which metadata is being requested, since this affects the
    /// request format</param>
    /// <param name="arguments">the additional arguments needed, if any, to complete the request</param>
    /// <returns>the <see cref="Message.KnownType.MenuAvailable"/> response reporting how many items are
    /// available in the menu</returns>
    /// <exception cref="IOException">if there is a problem communicating, or if the requested menu is not
    /// available</exception>
    /// <exception cref="InvalidOperationException">if <see cref="TryLockingForMenuOperations"/> was not called
    /// successfully before attempting this call</exception>
    public Message MenuRequestTyped(Message.KnownType requestType, Message.MenuIdentifier targetMenu,
        CdjStatus.TrackSourceSlot slot, CdjStatus.TrackType trackType, params Field[] arguments)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(arguments);
        lock (_lock)
        {
            if (!Monitor.IsEntered(_menuLock))
            {
                throw new InvalidOperationException("MenuRequestTyped() cannot be called without first successfully calling TryLockingForMenuOperations()");
            }

            var combinedArguments = new Field[arguments.Length + 1];
            combinedArguments[0] = BuildRmst(targetMenu, slot, trackType);
            Array.Copy(arguments, 0, combinedArguments, 1, arguments.Length);
            var response = SimpleRequest(requestType, Message.KnownType.MenuAvailable, combinedArguments);
            var reportedRequestType = (NumberField)response.Arguments[0];
            if (reportedRequestType.Value != requestType.ProtocolValue)
            {
                throw new IOException($"Menu request did not return result for same type as request; sent type: {requestType.ProtocolValue}, received type: {reportedRequestType.Value}, response: {response}");
            }
            return response;
        }
    }

    /// <summary>
    /// The default maximum number of menu items we will request at a single time. We are not sure what the
    /// largest safe value to use is, but 64 seems to work well for CDJ-2000 nexus players.
    /// </summary>
    public const int DefaultMenuBatchSize = 64;

    private static volatile int _menuBatchSize = DefaultMenuBatchSize;

    /// <summary>
    /// The maximum number of menu items we will request at a single time. We are not sure what the largest
    /// safe value to use is, but 64 seems to work well for CDJ-2000 nexus players. Changing this will affect
    /// future calls to
    /// <see cref="RenderMenuItems(Message.MenuIdentifier, CdjStatus.TrackSourceSlot, CdjStatus.TrackType, Message)"/>.
    /// </summary>
    public static int MenuBatchSize
    {
        get => _menuBatchSize;
        set => _menuBatchSize = value;
    }

    /// <summary>
    /// Attempt to secure exclusive access to this player for performing a menu operation, which requires
    /// multiple request/response cycles. The caller <em>must</em> call <see cref="UnlockForMenuOperations"/>
    /// as soon as it is done (even if it is failing because of an exception), or no future menu operations
    /// will be possible by any other thread, unless <c>false</c> was returned, meaning the attempt failed.
    /// </summary>
    /// <param name="timeout">the time to wait for the lock</param>
    /// <returns><c>true</c> if the lock was acquired within the specified time</returns>
    public bool TryLockingForMenuOperations(TimeSpan timeout) => Monitor.TryEnter(_menuLock, timeout);

    /// <summary>
    /// Allow other threads to perform menu operations. This <em>must</em> be called promptly, once for each
    /// time that <see cref="TryLockingForMenuOperations"/> was called with a <c>true</c> return value.
    /// </summary>
    public void UnlockForMenuOperations() => Monitor.Exit(_menuLock);

    /// <summary>
    /// Gather all the responses that are available for a menu request. Will involve multiple requests if the
    /// number of responses available is larger than our maximum batch size (see <see cref="MenuBatchSize"/>).
    /// </summary>
    /// <param name="targetMenu">the destination for the response to this query</param>
    /// <param name="slot">the media library of interest for this query</param>
    /// <param name="trackType">the type of track about which information is being requested</param>
    /// <param name="availableResponse">the response to the initial menu setup request, reporting how many
    /// responses are available, as well as the target menu we are working with</param>
    /// <returns>all the response items, using as many queries as necessary to gather them safely, and omitting
    /// all the header and footer items</returns>
    /// <exception cref="IOException">if there is a problem reading the menu items</exception>
    /// <exception cref="InvalidOperationException">if <see cref="TryLockingForMenuOperations"/> was not called
    /// successfully before attempting this call</exception>
    public IReadOnlyList<Message> RenderMenuItems(Message.MenuIdentifier targetMenu,
        CdjStatus.TrackSourceSlot slot, CdjStatus.TrackType trackType, Message availableResponse)
    {
        ArgumentNullException.ThrowIfNull(availableResponse);
        lock (_lock)
        {
            var count = availableResponse.GetMenuResultsCount();
            if (count == Message.NoMenuResultsAvailable || count == 0)
            {
                return [];
            }
            return RenderMenuItems(targetMenu, slot, trackType, 0, (int)count);
        }
    }

    /// <summary>
    /// Gather the specified range of responses for a menu request. Will involve multiple requests if the
    /// number of responses requested is larger than our maximum batch size (see <see cref="MenuBatchSize"/>).
    /// It is the caller's responsibility to make sure that <paramref name="offset"/> and
    /// <paramref name="count"/> remain within the legal, available menu items based on the initial menu setup
    /// request. Most use cases will be best served by the simpler
    /// <see cref="RenderMenuItems(Message.MenuIdentifier, CdjStatus.TrackSourceSlot, CdjStatus.TrackType, Message)"/>.
    /// </summary>
    /// <param name="targetMenu">the destination for the response to this query</param>
    /// <param name="slot">the media library of interest for this query</param>
    /// <param name="trackType">the type of track about which information is being requested</param>
    /// <param name="offset">the first response desired (the first one available has offset 0)</param>
    /// <param name="count">the number of responses desired</param>
    /// <returns>the response items, using as many queries as necessary to gather them safely, and omitting all
    /// the header and footer items</returns>
    /// <exception cref="IOException">if there is a problem reading the menu items</exception>
    /// <exception cref="InvalidOperationException">if <see cref="TryLockingForMenuOperations"/> was not called
    /// successfully before attempting this call</exception>
    /// <exception cref="ArgumentOutOfRangeException">if <paramref name="offset"/> is negative or
    /// <paramref name="count"/> is not positive</exception>
    public IReadOnlyList<Message> RenderMenuItems(Message.MenuIdentifier targetMenu,
        CdjStatus.TrackSourceSlot slot, CdjStatus.TrackType trackType, int offset, int count)
    {
        lock (_lock)
        {
            if (!Monitor.IsEntered(_menuLock))
            {
                throw new InvalidOperationException("RenderMenuItems() cannot be called without first successfully calling TryLockingForMenuOperations()");
            }
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

            var results = new List<Message>(count);
            var gathered = 0;
            while (gathered < count)
            {
                var batchSize = Math.Min(count - gathered, MenuBatchSize);
                var transaction = AssignTransactionNumber();
                var limit = new NumberField(batchSize);
                var total = new NumberField(count);
                var request = new Message(transaction,
                    new NumberField(Message.KnownType.RenderMenuReq.ProtocolValue, 2),
                    BuildRmst(targetMenu, slot, trackType), new NumberField(offset), limit,
                    NumberField.Word0, total, NumberField.Word0);

                SendMessage(request);
                var response = Message.Read(_stream);

                if (response.Transaction.Value != transaction.Value)
                {
                    throw new IOException($"Received response with wrong transaction ID. Expected: {transaction.Value}, got: {response}");
                }
                if (response.RecognizedType != Message.KnownType.MenuHeader)
                {
                    throw new IOException($"Expecting MENU_HEADER, instead got: {response}");
                }
                response = Message.Read(_stream);

                while (response.RecognizedType == Message.KnownType.MenuItem)
                {
                    results.Add(response);
                    response = Message.Read(_stream);
                }

                if (response.RecognizedType != Message.KnownType.MenuFooter)
                {
                    throw new IOException($"Expecting MENU_ITEM or MENU_FOOTER, instead got: {response}");
                }

                offset += batchSize;
                gathered += batchSize;
            }
            return results;
        }
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"DBServer Client[targetPlayer: {TargetPlayer}, posingAsPlayer: {PosingAsPlayer}, transactionCounter: {Volatile.Read(ref _transactionCounter)}, menuBatchSize: {MenuBatchSize}]";
}
