namespace PurrLay;

public interface IUdpServer
{
    void SendOne(int connId, ReadOnlySpan<byte> data, byte deliveryMethod);
    void KickClient(int connId);
}
