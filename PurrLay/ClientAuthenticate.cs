namespace PurrLay;

public struct ClientAuthenticate(string roomName, string clientSecret, bool pipe = false, bool nat = false)
{
    public readonly string roomName = roomName;
    public readonly string clientSecret = clientSecret;
    public readonly bool pipe = pipe;

    /// <summary>
    /// When true, this peer supports NAT hole-punching and wants the relay to act as a
    /// mediator. The relay introduces it to the other peer for a direct P2P link.
    /// Defaults to false so older clients (which never send this field) keep relay-only behaviour.
    /// </summary>
    public readonly bool nat = nat;
}