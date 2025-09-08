using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PurrCommon;

public struct ApiResponse(byte[] data, HttpStatusCode status = HttpStatusCode.OK, string contentType = ContentType.JSON)
{
    public readonly byte[] data = data;
    public readonly HttpStatusCode status = status;
    public readonly string contentType = contentType;

    public ApiResponse(HttpStatusCode status)
        : this([], status, ContentType.JSON)
    {}

    public ApiResponse(JObject obj, HttpStatusCode status = HttpStatusCode.OK)
        : this(Encoding.UTF8.GetBytes(obj.ToString(Formatting.None)), status, ContentType.JSON)
    {}

    public ApiResponse(JArray obj, HttpStatusCode status = HttpStatusCode.OK)
        : this(Encoding.UTF8.GetBytes(obj.ToString(Formatting.None)), status, ContentType.JSON)
    {}

    public ApiResponse(string data, HttpStatusCode status = HttpStatusCode.OK)
        : this(Encoding.UTF8.GetBytes(data), status, ContentType.TEXT)
    {}

    public static ApiResponse FromError(string message, HttpStatusCode status = HttpStatusCode.InternalServerError)
    {
        var errorResponse = new JObject
        {
            ["error"] = message
        };
        return new ApiResponse(errorResponse, status);
    }

    public static ApiResponse FromException(Exception e, HttpStatusCode status = HttpStatusCode.InternalServerError)
    {
        var errorResponse = new JObject
        {
            ["error"] = e.Message
        };
        return new ApiResponse(errorResponse, status);
    }
}
