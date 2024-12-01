﻿using Newtonsoft.Json.Linq;
using WatsonWebserver.Core;

namespace PurrBalancer;

internal struct RelayServer
{
    public string host;
    public int port;
    public string region;
}

public static class HTTPRestAPI
{
    static readonly RelayServer[] _relayServers =
    [
        new() {
            host = "localhost",
            port = 8080,
            region = "eu"
        }
    ];
    
    public static JObject OnRequest(HttpRequestBase req)
    {
        if (req.Url == null)
            throw new Exception("Invalid URL");

        string path = req.Url.RawWithoutQuery;

        switch (path)
        {
            case "/ping": return new JObject();
            case "/servers": return new JObject { ["servers"] = JArray.FromObject(_relayServers) };
        }
        
        throw new Exception("Invalid path");
    }
}