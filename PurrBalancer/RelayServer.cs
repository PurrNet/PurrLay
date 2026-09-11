namespace PurrBalancer;

public struct RelayServer
{
    public string apiEndpoint;
    public string? instanceId;
    public string? deploymentId;
    public bool draining;
    public string host;
    public int udpPort;
    public int udpPortV2;
    public int webSocketsPort;
    public string region;
}
