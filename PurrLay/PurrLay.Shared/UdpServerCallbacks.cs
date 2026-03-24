namespace PurrLay;

/// <summary>
/// Callbacks that a UDP server implementation uses to communicate with the transport layer.
/// Avoids circular project references between UDP implementations and the main server.
/// </summary>
public class UdpServerCallbacks
{
    /// <summary>
    /// Reserve a global connection ID. Returns the assigned ID.
    /// </summary>
    public required Func<bool, int> ReserveConnId { get; init; }

    /// <summary>
    /// Called when a client disconnects.
    /// </summary>
    public required Action<PlayerInfo> OnClientLeft { get; init; }

    /// <summary>
    /// Called when data is received from a client.
    /// </summary>
    public required Action<PlayerInfo, ArraySegment<byte>> OnDataReceived { get; init; }
}
