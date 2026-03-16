namespace PurrLay;

public struct ClientAuthenticate(string roomName, string clientSecret, bool pipe = false)
{
    public readonly string roomName = roomName;
    public readonly string clientSecret = clientSecret;
    public readonly bool pipe = pipe;
}