using System.Security.Authentication;
using JamesFrowen.SimpleWeb;

namespace PurrLay;

public class WebSockets : IDisposable
{
    private SimpleWebServer? _server;

    readonly TcpConfig _tcpConfig = new (noDelay: true, sendTimeout: 0, receiveTimeout: 0);

    static readonly Dictionary<int, int> _localConnToGlobal = new();
    static readonly Dictionary<int, int> _globalConnToLocal = new();
    static readonly object _wsConnLock = new();

    public int port { get; }

    private bool _disposed;

    public WebSockets(int port)
    {
        this.port = port;
        var thread = new Thread(Start);
        thread.Start();
    }

    private void Start()
    {
        var sslConfig = new SslConfig(false, null!, null!, SslProtocols.None);

        _server = new SimpleWebServer(int.MaxValue, _tcpConfig, ushort.MaxValue, 5000, sslConfig);
        _server.Start((ushort)port);
        _server.onConnect += OnClientConnected;
        _server.onDisconnect += OnClientDisconnectedFromServer;
        _server.onData += OnServerReceivedData;

        while (!_disposed)
        {
            try
            {
                Thread.Sleep(10);
                try
                {
                    _server.ProcessMessageQueue();
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Error processing message queue: {e.Message}\n{e.StackTrace}");
                }
            }
            catch
            {
                break;
            }
        }

        Dispose();
    }

    private static void OnClientConnected(int conn)
    {
        var global = Transport.ReserveConnId(false);
        lock (_wsConnLock)
        {
            _localConnToGlobal[conn] = global;
            _globalConnToLocal[global] = conn;
        }
    }

    private static void OnClientDisconnectedFromServer(int connId)
    {
        try
        {
            int global;
            lock (_wsConnLock)
            {
                if (!_localConnToGlobal.Remove(connId, out global))
                    return;
                _globalConnToLocal.Remove(global);
            }
            Transport.OnClientLeft(new PlayerInfo(global, false));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error handling disconnect: {e.Message}\n{e.StackTrace}");
        }
    }

    public void KickClient(int connId)
    {
        int localId;
        lock (_wsConnLock)
        {
            if (!_globalConnToLocal.Remove(connId, out localId))
                return;
            _localConnToGlobal.Remove(localId);
        }
        _server?.KickClient(localId);
    }

    private static void OnServerReceivedData(int connId, ArraySegment<byte> data)
    {
        try
        {
            int globalId;
            lock (_wsConnLock)
            {
                if (!_localConnToGlobal.TryGetValue(connId, out globalId))
                    return;
            }
            Transport.OnServerReceivedData(new PlayerInfo(globalId, false), data);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error handling data: {e.Message}\n{e.StackTrace}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_server?.Active == false)
            return;

        _server?.Stop();
    }

    public void SendOne(int connId, ReadOnlySpan<byte> segment)
    {
        if (segment.IsEmpty)
        {
            Console.Error.WriteLine($"Trying to send empty segment?\n{Environment.StackTrace}");
            return;
        }

        int localId;
        lock (_wsConnLock)
        {
            if (!_globalConnToLocal.TryGetValue(connId, out localId))
                return;
        }
        _server?.SendOne(localId, segment);
    }
}
