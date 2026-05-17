namespace PurrLay;

public enum SERVER_PACKET_TYPE : byte
{
    SERVER_CLIENT_CONNECTED = 0,
    SERVER_CLIENT_DISCONNECTED = 1,
    SERVER_CLIENT_DATA = 2,
    SERVER_AUTHENTICATED = 3,
    SERVER_AUTHENTICATION_FAILED = 4,
    SERVER_PIPE_AUTHENTICATED = 5,
    /// <summary>
    /// Tells a peer to begin a NAT hole-punch attempt with another peer.
    /// Payload: [token(UTF8 string, remaining bytes)].
    /// Only sent to peers that authenticated with `nat = true` on UDP V2.
    /// </summary>
    SERVER_NAT_INTRODUCE = 6
}
