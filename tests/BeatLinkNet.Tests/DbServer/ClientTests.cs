using System.Net;
using System.Net.Sockets;
using BeatLink.DbServer;

namespace BeatLink.Tests;

public class ClientTests
{
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Records what a scripted fake dbserver observed while serving a client.
    /// </summary>
    private sealed class ServerLog
    {
        public List<Message> ReceivedMessages { get; } = [];
        public long GreetingValue { get; set; } = -1;
    }

    /// <summary>
    /// Starts a fake dbserver on an ephemeral loopback port which accepts a single connection and runs
    /// the supplied script against it, recording everything it reads.
    /// </summary>
    private static (int Port, Task<ServerLog> ServerTask) StartFakeDbServer(Action<Stream, ServerLog> script)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(() =>
        {
            var log = new ServerLog();
            try
            {
                using var socket = listener.AcceptSocket();
                using var stream = new NetworkStream(socket, ownsSocket: false);
                script(stream, log);
            }
            finally
            {
                listener.Stop();
            }
            return log;
        });
        return (port, task);
    }

    /// <summary>
    /// Serves the greeting and setup exchanges the client constructor performs.
    /// </summary>
    private static void ServeHandshake(Stream stream, ServerLog log, int targetPlayer)
    {
        var greeting = Assert.IsType<NumberField>(Field.Read(stream));
        log.GreetingValue = greeting.Value;
        new NumberField(1, 4).Write(stream);

        var setup = Message.Read(stream);
        log.ReceivedMessages.Add(setup);
        new Message(0xfffffffeL, Message.KnownType.MenuAvailable,
            new NumberField(Message.KnownType.SetupReq.ProtocolValue),
            new NumberField(targetPlayer)).Write(stream);
    }

    /// <summary>
    /// Reads a message and records it, so a script can respond to it.
    /// </summary>
    private static Message ReadAndLog(Stream stream, ServerLog log)
    {
        var message = Message.Read(stream);
        log.ReceivedMessages.Add(message);
        return message;
    }

    private static Client ConnectClient(int port, int targetPlayer, int posingAsPlayer)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Connect(IPAddress.Loopback, port);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new Client(socket, targetPlayer, posingAsPlayer);
    }

    private static ServerLog AwaitLog(Task<ServerLog> serverTask)
    {
        Assert.True(serverTask.Wait(ServerTimeout), "Fake dbserver script did not complete in time");
        return serverTask.Result;
    }

    /// <summary>
    /// Blocks until a background task finishes; deliberate because the menu lock has Monitor
    /// thread-affinity, so these tests cannot be written with await without changing which thread
    /// runs the assertions.
    /// </summary>
    private static void AwaitCompletion(Task task) =>
        Assert.True(task.Wait(ServerTimeout), "Background task did not complete in time");

    [Fact]
    public void HandshakeSucceedsAndTeardownIsSentOnDispose()
    {
        var (port, serverTask) = StartFakeDbServer((stream, log) =>
        {
            ServeHandshake(stream, log, targetPlayer: 3);
            ReadAndLog(stream, log);  // The teardown message sent by Dispose.
        });

        using (var client = ConnectClient(port, targetPlayer: 3, posingAsPlayer: 5))
        {
            Assert.True(client.IsConnected);
            Assert.Equal(3, client.TargetPlayer);
            Assert.Equal(5, client.PosingAsPlayer);
        }

        var log = AwaitLog(serverTask);
        Assert.Equal(1, log.GreetingValue);  // The greeting is a bare 4-byte number field of value 1.

        var setup = log.ReceivedMessages[0];
        Assert.Equal(Message.KnownType.SetupReq, setup.RecognizedType);
        Assert.Equal(0xfffffffeL, setup.Transaction.Value);
        Assert.Equal(5, Assert.IsType<NumberField>(Assert.Single(setup.Arguments)).Value);

        var teardown = log.ReceivedMessages[1];
        Assert.Equal(Message.KnownType.TeardownReq, teardown.RecognizedType);
        Assert.Equal(0xfffffffeL, teardown.Transaction.Value);
        Assert.Empty(teardown.Arguments);
    }

    [Fact]
    public void HandshakeFailsWhenGreetingResponseIsWrong()
    {
        var (port, serverTask) = StartFakeDbServer((stream, log) =>
        {
            var greeting = Assert.IsType<NumberField>(Field.Read(stream));
            log.GreetingValue = greeting.Value;
            new NumberField(2, 4).Write(stream);  // Wrong value; the client must reject it.
        });

        Assert.Throws<IOException>(() => ConnectClient(port, targetPlayer: 3, posingAsPlayer: 5));
        AwaitLog(serverTask);
    }

    [Fact]
    public void HandshakeFailsWhenSetupResponseIdentifiesWrongPlayer()
    {
        var (port, serverTask) = StartFakeDbServer((stream, log) =>
        {
            ServeHandshake(stream, log, targetPlayer: 4);  // Claims to be player 4, not 3.
        });

        var e = Assert.Throws<IOException>(() => ConnectClient(port, targetPlayer: 3, posingAsPlayer: 5));
        Assert.Contains("identified itself as player 4", e.Message);
        AwaitLog(serverTask);
    }

    [Fact]
    public void SimpleRequestsIncrementAndValidateTransactionIds()
    {
        var (port, serverTask) = StartFakeDbServer((stream, log) =>
        {
            ServeHandshake(stream, log, targetPlayer: 2);
            for (var i = 0; i < 2; i++)
            {
                var request = ReadAndLog(stream, log);
                new Message(request.Transaction.Value, Message.KnownType.CueList,
                    new NumberField(request.MessageType.Value)).Write(stream);
            }
            ReadAndLog(stream, log);  // Teardown.
        });

        using (var client = ConnectClient(port, targetPlayer: 2, posingAsPlayer: 7))
        {
            var first = client.SimpleRequest(Message.KnownType.CueListReq, Message.KnownType.CueList,
                new NumberField(0x07030301L), new NumberField(17));
            Assert.Equal(1, first.Transaction.Value);
            var second = client.SimpleRequest(Message.KnownType.CueListReq, Message.KnownType.CueList,
                new NumberField(0x07030301L), new NumberField(18));
            Assert.Equal(2, second.Transaction.Value);
        }

        var log = AwaitLog(serverTask);
        Assert.Equal(Message.KnownType.CueListReq, log.ReceivedMessages[1].RecognizedType);
        Assert.Equal(1, log.ReceivedMessages[1].Transaction.Value);
        Assert.Equal(Message.KnownType.CueListReq, log.ReceivedMessages[2].RecognizedType);
        Assert.Equal(2, log.ReceivedMessages[2].Transaction.Value);
        Assert.Equal(Message.KnownType.TeardownReq, log.ReceivedMessages[3].RecognizedType);
    }

    [Fact]
    public void SimpleRequestRejectsMismatchedTransactionId()
    {
        var (port, serverTask) = StartFakeDbServer((stream, log) =>
        {
            ServeHandshake(stream, log, targetPlayer: 2);
            var request = ReadAndLog(stream, log);
            new Message(request.Transaction.Value + 100, Message.KnownType.CueList).Write(stream);
            try
            {
                ReadAndLog(stream, log);  // Teardown (may not arrive if the client tears down abruptly).
            }
            catch (IOException)
            {
            }
        });

        using (var client = ConnectClient(port, targetPlayer: 2, posingAsPlayer: 7))
        {
            var e = Assert.Throws<IOException>(() =>
                client.SimpleRequest(Message.KnownType.CueListReq, null, new NumberField(1)));
            Assert.Contains("wrong transaction ID", e.Message);
        }

        AwaitLog(serverTask);
    }

    [Fact]
    public void SimpleRequestRejectsMismatchedResponseType()
    {
        var (port, serverTask) = StartFakeDbServer((stream, log) =>
        {
            ServeHandshake(stream, log, targetPlayer: 2);
            var request = ReadAndLog(stream, log);
            new Message(request.Transaction.Value, Message.KnownType.Unavailable,
                new NumberField(request.MessageType.Value)).Write(stream);
            try
            {
                ReadAndLog(stream, log);  // Teardown.
            }
            catch (IOException)
            {
            }
        });

        using (var client = ConnectClient(port, targetPlayer: 2, posingAsPlayer: 7))
        {
            var e = Assert.Throws<IOException>(() =>
                client.SimpleRequest(Message.KnownType.CueListReq, Message.KnownType.CueList, new NumberField(1)));
            Assert.Contains("wrong type", e.Message);
        }

        AwaitLog(serverTask);
    }

    [Fact]
    public void RenderMenuItemsGathersMultipleBatches()
    {
        const int totalItems = 100;  // More than one 64-item batch.
        var (port, serverTask) = StartFakeDbServer((stream, log) =>
        {
            ServeHandshake(stream, log, targetPlayer: 2);

            // Serve the initial menu request.
            var menuRequest = ReadAndLog(stream, log);
            new Message(menuRequest.Transaction.Value, Message.KnownType.MenuAvailable,
                new NumberField(menuRequest.MessageType.Value), new NumberField(totalItems)).Write(stream);

            // Serve two render batches.
            var served = 0;
            while (served < totalItems)
            {
                var render = ReadAndLog(stream, log);
                var limit = (int)((NumberField)render.Arguments[2]).Value;
                new Message(render.Transaction.Value, Message.KnownType.MenuHeader).Write(stream);
                for (var i = 0; i < limit; i++)
                {
                    new Message(render.Transaction.Value, Message.KnownType.MenuItem,
                        new NumberField(0), new NumberField(served + i), new NumberField(0),
                        new StringField($"Track {served + i}"), new NumberField(0), new StringField(""),
                        new NumberField(0x0004)).Write(stream);
                }
                new Message(render.Transaction.Value, Message.KnownType.MenuFooter).Write(stream);
                served += limit;
            }

            ReadAndLog(stream, log);  // Teardown.
        });

        IReadOnlyList<Message> items;
        ServerLog log;
        using (var client = ConnectClient(port, targetPlayer: 2, posingAsPlayer: 5))
        {
            Assert.True(client.TryLockingForMenuOperations(TimeSpan.FromSeconds(5)));
            try
            {
                var available = client.MenuRequest(Message.KnownType.TrackMenuReq,
                    Message.MenuIdentifier.MainMenu, CdjStatus.TrackSourceSlot.UsbSlot, NumberField.Word0);
                Assert.Equal(totalItems, available.GetMenuResultsCount());
                items = client.RenderMenuItems(Message.MenuIdentifier.MainMenu,
                    CdjStatus.TrackSourceSlot.UsbSlot, CdjStatus.TrackType.Rekordbox, available);
            }
            finally
            {
                client.UnlockForMenuOperations();
            }
        }
        log = AwaitLog(serverTask);

        Assert.Equal(totalItems, items.Count);
        Assert.All(items, item => Assert.Equal(Message.KnownType.MenuItem, item.RecognizedType));
        Assert.Equal("Track 0", Assert.IsType<StringField>(items[0].Arguments[3]).Value);
        Assert.Equal("Track 99", Assert.IsType<StringField>(items[99].Arguments[3]).Value);

        // The server saw the setup message, the menu request, then two batched render requests.
        var menu = log.ReceivedMessages[1];
        Assert.Equal(Message.KnownType.TrackMenuReq, menu.RecognizedType);
        Assert.Equal(1, menu.Transaction.Value);
        Assert.Equal(0x05010301L, ((NumberField)menu.Arguments[0]).Value);  // R:M:S:T from posing player 5.

        var firstBatch = log.ReceivedMessages[2];
        Assert.Equal(Message.KnownType.RenderMenuReq, firstBatch.RecognizedType);
        Assert.Equal(2, firstBatch.Transaction.Value);
        Assert.Equal(0, ((NumberField)firstBatch.Arguments[1]).Value);       // Offset.
        Assert.Equal(64, ((NumberField)firstBatch.Arguments[2]).Value);      // Limit (default batch size).
        Assert.Equal(totalItems, ((NumberField)firstBatch.Arguments[4]).Value);

        var secondBatch = log.ReceivedMessages[3];
        Assert.Equal(Message.KnownType.RenderMenuReq, secondBatch.RecognizedType);
        Assert.Equal(3, secondBatch.Transaction.Value);
        Assert.Equal(64, ((NumberField)secondBatch.Arguments[1]).Value);     // Offset.
        Assert.Equal(36, ((NumberField)secondBatch.Arguments[2]).Value);     // Remaining items.

        Assert.Equal(Message.KnownType.TeardownReq, log.ReceivedMessages[4].RecognizedType);
    }

    [Fact]
    public void MenuMethodsThrowWhenLockIsNotHeld()
    {
        var (port, serverTask) = StartFakeDbServer((stream, log) =>
        {
            ServeHandshake(stream, log, targetPlayer: 2);
            ReadAndLog(stream, log);  // Teardown.
        });

        using (var client = ConnectClient(port, targetPlayer: 2, posingAsPlayer: 5))
        {
            Assert.Throws<InvalidOperationException>(() =>
                client.MenuRequest(Message.KnownType.TrackMenuReq, Message.MenuIdentifier.MainMenu,
                    CdjStatus.TrackSourceSlot.UsbSlot, NumberField.Word0));
            Assert.Throws<InvalidOperationException>(() =>
                client.RenderMenuItems(Message.MenuIdentifier.MainMenu, CdjStatus.TrackSourceSlot.UsbSlot,
                    CdjStatus.TrackType.Rekordbox, 0, 10));
        }

        AwaitLog(serverTask);
    }

    [Fact]
    public void MenuLockIsHeldPerThread()
    {
        var (port, serverTask) = StartFakeDbServer((stream, log) =>
        {
            ServeHandshake(stream, log, targetPlayer: 2);
            ReadAndLog(stream, log);  // Teardown.
        });

        using (var client = ConnectClient(port, targetPlayer: 2, posingAsPlayer: 5))
        {
            using var acquired = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = Task.Run(() =>
            {
                Assert.True(client.TryLockingForMenuOperations(TimeSpan.FromSeconds(5)));
                acquired.Set();
                try
                {
                    Assert.True(release.Wait(ServerTimeout));
                }
                finally
                {
                    client.UnlockForMenuOperations();
                }
            });
            Assert.True(acquired.Wait(ServerTimeout));

            // Another thread holding the lock does not satisfy the current thread's check...
            Assert.Throws<InvalidOperationException>(() =>
                client.RenderMenuItems(Message.MenuIdentifier.MainMenu,
                    CdjStatus.TrackSourceSlot.UsbSlot, CdjStatus.TrackType.Rekordbox, 0, 10));
            // ...and the lock cannot be acquired while the other thread holds it.
            Assert.False(client.TryLockingForMenuOperations(TimeSpan.FromMilliseconds(50)));

            release.Set();
            AwaitCompletion(holder);
        }

        AwaitLog(serverTask);
    }

    [Fact]
    public void MenuBatchSizeCanBeAdjusted()
    {
        Assert.Equal(64, Client.DefaultMenuBatchSize);
        Assert.Equal(Client.DefaultMenuBatchSize, Client.MenuBatchSize);
        try
        {
            Client.MenuBatchSize = 16;
            Assert.Equal(16, Client.MenuBatchSize);
        }
        finally
        {
            Client.MenuBatchSize = Client.DefaultMenuBatchSize;
        }
    }
}
