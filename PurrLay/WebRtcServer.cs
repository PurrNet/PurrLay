using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace PurrLay;

/// <summary>
/// One authenticated loopback connection per browser peer, carrying UDP-compatible relay frames.
/// </summary>
public sealed class WebRtcServer : IUdpServer, IDisposable, IAsyncDisposable
{
    internal const int MaxPayload = ushort.MaxValue;
    internal const byte Hello = 0, Data = 1, Close = 2;
    readonly TcpListener _listener;
    readonly byte[] _token;
    readonly UdpServerCallbacks _callbacks;
    readonly CancellationTokenSource _stopping = new();
    readonly ConcurrentDictionary<long, Peer> _connections = new();
    readonly ConcurrentDictionary<int, Peer> _players = new();
    readonly int _maxConnections;
    readonly Task _acceptTask;
    long _nextConnection;
    int _disposed;

    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;
    internal bool IsRunning => Volatile.Read(ref _disposed) == 0;

    public WebRtcServer(IPEndPoint endpoint, string token, UdpServerCallbacks callbacks, int maxConnections = 1024)
    {
        if (!IPAddress.IsLoopback(endpoint.Address))
            throw new ArgumentException("WebRTC bridge must bind to loopback.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("WebRTC bridge requires an authentication token.", nameof(token));
        if (maxConnections < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        _token = Encoding.UTF8.GetBytes(token);
        _callbacks = callbacks;
        _maxConnections = maxConnections;
        _listener = new TcpListener(endpoint);
        _listener.Start();
        _acceptTask = AcceptAsync();
    }

    async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                if (_connections.Count >= _maxConnections || _stopping.IsCancellationRequested)
                {
                    client.Dispose();
                    continue;
                }
                client.NoDelay = true;
                var id = Interlocked.Increment(ref _nextConnection);
                var peer = new Peer(this, client, id);
                _connections[id] = peer;
                peer.Start();
            }
        }
        catch (Exception exception) when (_stopping.IsCancellationRequested &&
                                          exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"WebRTC bridge listener failed: {exception.Message}");
            Dispose();
        }
    }

    public void SendOne(int connId, ReadOnlySpan<byte> data, byte deliveryMethod)
    {
        if (_players.TryGetValue(connId, out var peer))
            peer.Send(data, deliveryMethod);
    }

    public void KickClient(int connId)
    {
        if (_players.TryGetValue(connId, out var peer))
            peer.Stop();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping.Cancel();
        _listener.Stop();
        foreach (var peer in _connections.Values)
            peer.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _acceptTask;
        await Task.WhenAll(_connections.Values.Select(peer => peer.Completion));
    }

    sealed class Peer(WebRtcServer owner, TcpClient client, long localId)
    {
        const int MaxQueuedBytes = 1024 * 1024;
        readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(owner._stopping.Token);
        readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        int _connId;
        int _queuedBytes;
        int _closed;

        public Task Completion { get; private set; } = Task.CompletedTask;
        public void Start() => Completion = RunAsync();

        public void Stop()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            _outgoing.Writer.TryComplete();
            _stop.Cancel();
            client.Dispose();
        }

        public void Send(ReadOnlySpan<byte> payload, byte method)
        {
            if (Volatile.Read(ref _closed) != 0)
                return;
            if (method > 4 || payload.Length == 0)
            {
                Stop();
                return;
            }
            // Sequenced data channels add a four-byte counter on the browser wire.
            if (payload.Length > MaxPayload || (method == 1 && payload.Length > MaxPayload - sizeof(uint)))
            {
                if (method is not (1 or 4)) Stop();
                return;
            }
            var size = payload.Length + 6;
            if (Interlocked.Add(ref _queuedBytes, size) > MaxQueuedBytes)
            {
                Interlocked.Add(ref _queuedBytes, -size);
                if (method is not (1 or 4)) Stop();
                return;
            }
            // Transport's packet writer is reused as soon as SendOne returns.
            var frame = new byte[size];
            frame[0] = Data;
            frame[1] = method;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)payload.Length);
            payload.CopyTo(frame.AsSpan(6));
            if (!_outgoing.Writer.TryWrite(frame))
            {
                Interlocked.Add(ref _queuedBytes, -size);
                if (method is not (1 or 4)) Stop();
            }
        }

        async Task RunAsync()
        {
            Task? writer = null;
            try
            {
                var stream = client.GetStream();
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(5));
                    var hello = await ReadFrameAsync(stream, handshake.Token);
                    if (hello.Type != Hello || hello.Method != 0 ||
                        !CryptographicOperations.FixedTimeEquals(hello.Payload, owner._token))
                        return;
                    _connId = owner._callbacks.ReserveConnId(true);
                    owner._players[_connId] = this;
                    await stream.WriteAsync(new byte[6], handshake.Token);
                }
                writer = WriteAsync(stream);
                bool firstMessage = true;
                while (!_stop.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(stream, _stop.Token);
                    if (frame.Type == Close && frame.Method == 0 && frame.Payload.Length == 0)
                        break;
                    if (frame.Type != Data || frame.Method > 4 || frame.Payload.Length == 0 ||
                        (firstMessage && frame.Method != Transport.RELIABLE_ORDERED))
                        throw new InvalidDataException("Invalid WebRTC bridge data frame.");
                    firstMessage = false;
                    // Only this reader invokes receive/leave callbacks for this peer. No map lock is held.
                    owner._callbacks.OnDataReceived(new PlayerInfo(_connId, true), frame.Payload);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or
                                               OperationCanceledException or ObjectDisposedException) { }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"WebRTC peer failed: {exception.Message}");
            }
            finally
            {
                Stop();
                if (writer != null) await writer;
                if (_connId != 0)
                {
                    owner._players.TryRemove(_connId, out _);
                    try { owner._callbacks.OnClientLeft(new PlayerInfo(_connId, true)); }
                    catch (Exception exception) { Console.Error.WriteLine($"WebRTC disconnect failed: {exception.Message}"); }
                }
                _stop.Dispose();
                owner._connections.TryRemove(localId, out _);
            }
        }

        async Task WriteAsync(NetworkStream stream)
        {
            try
            {
                await foreach (var frame in _outgoing.Reader.ReadAllAsync(_stop.Token))
                {
                    Interlocked.Add(ref _queuedBytes, -frame.Length);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await stream.WriteAsync(frame, timeout.Token);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or
                                               OperationCanceledException or ObjectDisposedException) { }
            finally { Stop(); }
        }
    }

    internal static async Task<(byte Type, byte Method, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[6];
        await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
        if (size > MaxPayload)
            throw new InvalidDataException("WebRTC bridge frame exceeds the payload limit.");
        var payload = new byte[(int)size];
        await stream.ReadExactlyAsync(payload, token);
        return (header[0], header[1], payload);
    }
}
