#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KairosCapture.Services;

internal sealed class OpenAiOcrTrace
{
    internal const string SyntheticText = "Kairos Test 123";
    internal List<string> Lines { get; } = new();

    internal void Response(HttpResponseMessage response)
    {
        Lines.Add($"http.status={(int)response.StatusCode}");
        Lines.Add("contentType.mediaType=" + Metadata(response.Content.Headers.ContentType?.MediaType));
        Lines.Add("contentType.charset=" + Metadata(response.Content.Headers.ContentType?.CharSet));
    }

    // Only metadata leaves this boundary. Response bodies and arbitrary text never enter Lines.
    private static string Metadata(string? value) => value is null ? "<missing>" :
        value.Length is > 0 and <= 96 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/') ? value : "<redacted>";

    private static string FieldName(string name) =>
        new[] { "msg_", "resp_", "req_", "item_", "call_" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) ||
        name.Split('.', '_', '-', '/').Any(segment =>
            (segment.Length >= 16 && segment.All(char.IsAsciiHexDigit)) ||
            (segment.Length >= 20 && segment.All(char.IsAsciiLetterOrDigit))) ? "<id>" : Metadata(name);

    internal void Body(byte[] body, string? mediaType)
    {
        if (mediaType != "text/event-stream" && Encoding.UTF8.GetString(body).TrimStart().StartsWith('{'))
        {
            Inspect(null, Encoding.UTF8.GetString(body), 1, out string fields);
            Lines.Add("first.fields=" + fields);
            Lines.Add("last.fields=" + fields);
            return;
        }
        using var reader = new StringReader(Encoding.UTF8.GetString(body));
        var data = new StringBuilder();
        string? name = null;
        string? first = null, last = null;
        int count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    Inspect(name, data.ToString(), ++count, out string fields);
                    first ??= fields;
                    last = fields;
                }
                data.Clear(); name = null;
                continue;
            }
            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "event") name = value;
            else if (field == "data") data.Append(value).Append('\n');
        }
        Lines.Add("first.fields=" + (first ?? "<none>"));
        Lines.Add("last.fields=" + (last ?? "<none>"));
    }

    private void Inspect(string? name, string json, int index, out string fields)
    {
        fields = "<none>";
        if (json.Trim() == "[DONE]")
        {
            Lines.Add($"sse[{index}].event={Metadata(name)} json.type=<done>");
            return;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            Lines.Add($"sse[{index}].event={Metadata(name)} json.type={Metadata(OpenAiProtocol.Text(root, "type"))}");
            var paths = new List<string>();
            CollectFields(root, "", paths);
            fields = string.Join(',', paths);
            CollectErrors(root);
        }
        catch (JsonException) { Lines.Add($"sse[{index}].event={Metadata(name)} json.type=<invalid-json>"); }
    }

    private static void CollectFields(JsonElement value, string prefix, List<string> paths)
    {
        if (paths.Count >= 256) return;
        if (value.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty field in value.EnumerateObject())
            {
                if (paths.Count >= 256) break;
                string path = prefix + FieldName(field.Name);
                if (!paths.Contains(path)) paths.Add(path);
                CollectFields(field.Value, path + ".", paths);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in value.EnumerateArray().Take(8)) CollectFields(child, prefix + "[].", paths);
    }

    private void CollectErrors(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty field in value.EnumerateObject())
        {
            if (field.Name == "error" && field.Value.ValueKind == JsonValueKind.Object)
                Lines.Add("error.code=" + OpenAiDiagnostics.Code(OpenAiProtocol.Text(field.Value, "code")) +
                    " error.param=" + Metadata(OpenAiProtocol.Text(field.Value, "param")));
            if (field.Value.ValueKind == JsonValueKind.Object) CollectErrors(field.Value);
        }
        if (OpenAiProtocol.Text(value, "type") == "error")
            Lines.Add("error.code=" + OpenAiDiagnostics.Code(OpenAiProtocol.Text(value, "code")) +
                " error.param=" + Metadata(OpenAiProtocol.Text(value, "param")));
    }
}

public sealed partial class OpenAiService
{
    internal static async Task<OpenAiProbeResult> ProbeOcrAsync(byte[] syntheticPng, string accountKey, string model)
    {
        var trace = new OpenAiOcrTrace();
        try
        {
            using var service = new OpenAiService(CreateHttpClient(), new OpenAiStorage(AppSettings.DirectoryPath),
                _ => throw new InvalidOperationException(), true);
            await service.EnsureLoadedAsync(CancellationToken.None).ConfigureAwait(false);
            if (service.Accounts.Any(a => a.Key == accountKey)) await service.SelectAccountAsync(accountKey, CancellationToken.None).ConfigureAwait(false);
            string text = await service.ExtractTextCoreAsync(syntheticPng, model, null, CancellationToken.None, null, trace).ConfigureAwait(false);
            bool passed = text.Trim() == OpenAiOcrTrace.SyntheticText;
            trace.Lines.Add(passed ? "result=" + OpenAiOcrTrace.SyntheticText : "result=<unexpected-text>");
            trace.Lines.Add("check=" + (passed ? "Passed" : "SyntheticTextMismatch"));
            return new OpenAiProbeResult(passed, trace.Lines.AsReadOnly());
        }
        catch (Exception ex)
        {
            trace.Lines.Add("check=" + (ex is OpenAiOcrProtocolException protocol ? protocol.Reason.ToString() : OpenAiDiagnostics.Classify(ex).ToString()));
            trace.Lines.Add(OpenAiDiagnostics.Describe(ex));
            return new OpenAiProbeResult(false, trace.Lines.AsReadOnly());
        }
    }
}
