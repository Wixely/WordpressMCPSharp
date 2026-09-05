using System.Text.Json;
using System.Text.Json.Serialization;

namespace WordpressMCPSharp.Tools;

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        // Keeps typed results (diagnostics) consistent with the anonymous objects the tools return.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
