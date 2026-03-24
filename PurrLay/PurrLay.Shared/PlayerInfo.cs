namespace PurrLay;

public record struct PlayerInfo(int connId, bool isUdp)
{
    public readonly bool isUdp = isUdp;
    public readonly int connId = connId;
}
