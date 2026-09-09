using System.Collections.Specialized;
using System.Text;
using WatsonWebserver.Core;

namespace PurrLay.Tests;

internal sealed class TestHttpRequest : HttpRequestBase
{
    public TestHttpRequest(string path, params (string Name, string? Value)[] headers)
    {
        Method = WatsonWebserver.Core.HttpMethod.GET;
        Url = new UrlDetails("http://localhost" + path, path);
        Headers = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
            if (value != null)
                Headers[name] = value;
    }

    public override Stream Data { get; set; } = new MemoryStream();
    public override byte[] DataAsBytes => ((MemoryStream)Data).ToArray();
    public override string DataAsString => Encoding.UTF8.GetString(DataAsBytes);
    public override Task<Chunk> ReadChunk(CancellationToken token = default) => throw new NotSupportedException();
    public override bool HeaderExists(string key) => Headers[key] != null;
    public override bool QuerystringExists(string key) => false;
    public override string RetrieveHeaderValue(string key) => Headers[key]!;
    public override string RetrieveQueryValue(string key) => null!;
}
