#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KairosCapture.Services;

internal sealed record OpenAiProbeResult(bool Passed, IReadOnlyList<string> Lines);

public sealed partial class OpenAiService
{
    internal static Task<OpenAiProbeResult> ProbeAsync() => ProbeAsync(CreateHttpClient());

    internal static async Task<OpenAiProbeResult> ProbeAsync(HttpClient http, TimeSpan? jsonTimeout = null)
    {
        var lines = new List<string>();
        // Load() on this nonexistent path creates no file. Never read a product account/store.
        using var service = new OpenAiService(http, new OpenAiStorage(Path.Combine(Path.GetTempPath(),
            "KairosCapture-probe-" + Guid.NewGuid().ToString("N"))), _ => throw new InvalidOperationException(), true, lines.Add, jsonTimeout: jsonTimeout);
        bool passed = true;
        Exception? failure = null;
        foreach (OpenAiStep step in new[] { OpenAiStep.Discovery, OpenAiStep.Jwks, OpenAiStep.Token })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            try
            {
                if (step == OpenAiStep.Token)
                {
                    using JsonDocument unexpected = await service.ExchangeAuthorizationCodeAsync("oaiapp_kairos_probe_invalid_client",
                        "kairos-invalid-code", new string('p', 43), new Uri("http://127.0.0.1:42123/auth/callback"), timeout.Token).ConfigureAwait(false);
                    throw new InvalidOperationException("Der absichtlich ungültige Probe-Code wurde angenommen.");
                }
                else
                {
                    using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,
                        step == OpenAiStep.Discovery ? OpenAiProtocol.Issuer + "/.well-known/openid-configuration" : JwksEndpoint);
                    using JsonDocument json = await service.SendJsonAsync(request, timeout.Token).ConfigureAwait(false);
                    if (step == OpenAiStep.Discovery)
                    {
                        JsonElement root = json.RootElement;
                        if (OpenAiProtocol.Text(root, "issuer") != OpenAiProtocol.Issuer ||
                            OpenAiProtocol.Text(root, "authorization_endpoint") != AuthorizeEndpoint ||
                            OpenAiProtocol.Text(root, "token_endpoint") != TokenEndpoint ||
                            OpenAiProtocol.Text(root, "jwks_uri") != JwksEndpoint ||
                            OpenAiProtocol.Text(root, "revocation_endpoint") != OpenAiProtocol.Issuer + "/api/accounts/oauth/revoke")
                            throw new InvalidOperationException();
                    }
                    else if (!json.RootElement.TryGetProperty("keys", out JsonElement keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0)
                        throw new InvalidOperationException();
                }
            }
            catch (OpenAiRequestException ex) when (step == OpenAiStep.Token && ex.Status == 400 && ex.ErrorCode is "invalid_client" or "invalid_grant")
            {
                // Expected rejection proves the POST reached OpenAI.
            }
            catch (Exception ex)
            {
                passed = false;
                OpenAiDiagnostics.Mark(ex, step);
                failure ??= ex;
                if (OpenAiDiagnostics.IsOutboundBlocked(ex)) lines.Add("Hinweis: " + OpenAiDiagnostics.UserMessage(ex));
            }
        }
        if (failure is null) service.Report(OpenAiStep.Probe, OpenAiOutcome.Succeeded);
        else service.FinishFailure(failure, OpenAiStep.Probe, CancellationToken.None);
        return new OpenAiProbeResult(passed, lines.AsReadOnly());
    }
}
