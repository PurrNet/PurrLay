namespace PurrCommon;

public static class ContentType
{
    public const string JSON = "application/json";
    public const string TEXT = "text/plain";
    public const string HTML = "text/html";
    public const string JS = "application/javascript";
    public const string CSS = "text/css";

    // Images
    public const string PNG = "image/png";
    public const string JPEG = "image/jpeg";
    public const string GIF = "image/gif";
    public const string SVG = "image/svg+xml";
    public const string WEBP = "image/webp";
    public const string ICO = "image/x-icon";

    // Fonts
    public const string WOFF = "font/woff";
    public const string WOFF2 = "font/woff2";
    public const string TTF = "font/ttf";
    public const string OTF = "font/otf";

    // Media
    public const string MP4 = "video/mp4";
    public const string WEBM = "video/webm";
    public const string MP3 = "audio/mpeg";
    public const string WAV = "audio/wav";

    // Documents
    public const string PDF = "application/pdf";
    public const string XML = "application/xml";
    public const string ZIP = "application/zip";

    // Web
    public const string MANIFEST = "application/manifest+json";
    public const string WEBMANIFEST = "application/manifest+json";
    public const string WASM = "application/wasm";

    public static string FromExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return "application/octet-stream";

        extension = extension.TrimStart('.').ToLowerInvariant();

        return extension switch
        {
            "json" => JSON,
            "txt" => TEXT,
            "html" or "htm" => HTML,
            "js" => JS,
            "css" => CSS,
            "xml" => XML,
            "webmanifest" or "manifest" => WEBMANIFEST,
            "wasm" => WASM,

            // Images
            "png" => PNG,
            "jpg" or "jpeg" => JPEG,
            "gif" => GIF,
            "svg" => SVG,
            "webp" => WEBP,
            "ico" => ICO,

            // Fonts
            "woff" => WOFF,
            "woff2" => WOFF2,
            "ttf" => TTF,
            "otf" => OTF,

            // Media
            "mp4" => MP4,
            "webm" => WEBM,
            "mp3" => MP3,
            "wav" => WAV,

            // Documents
            "pdf" => PDF,
            "zip" => ZIP,

            _ => "application/octet-stream"
        };
    }
}
