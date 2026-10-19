#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;

namespace KairosCapture.Services;

internal enum OpenAiStep { Unknown, Callback, Token, Jwks, Validate, Persist, Models, Discovery, Refresh, Revoke, Ocr, Session }
internal enum OpenAiOutcome { Started, Succeeded, Failed, Retrying, Response, Cancelled }
internal enum OpenAiFailure { Unknown, Dns, Connect, Tls, Proxy, Timeout, Cancelled, Transport, Http, Permission, Validation, Protocol, Storage }
// Provider strings are never log fields unless they match one of these fixed enum values.
internal enum OpenAiErrorCode
{
    None, Unknown, invalid_client, invalid_grant, invalid_refresh_token, token_expired,
    refresh_token_expired, refresh_token_invalidated, refresh_token_reused, invalid_request,
    insufficient_scope, insufficient_quota, billing_hard_limit_reached, rate_limit_exceeded,
    chatpass_v2_scope_not_authorized, chatpass_v2_invalid_authorization_context,
    subscription_sharing_usage_limit_exceeded, subscription_sharing_usage_unavailable,
    subscription_sharing_user_unavailable, subscription_sharing_route_not_supported,
    subscription_sharing_invalid_user, subscription_sharing_user_not_eligible,
    subscription_sharing_unsupported_capability
}

internal static class OpenAiDiagnostics
{
    private const string StepKey = "Kairos.OpenAi.Step";
    private const string StatusKey = "Kairos.OpenAi.Status";
    internal static void Mark(Exception ex, OpenAiStep step, int? status = null)
    {
        if (ex.Data[StepKey] is not OpenAiStep) ex.Data[StepKey] = step;
        if (status.HasValue) ex.Data[StatusKey] = status.Value;
    }
    internal static OpenAiStep Step(Exception ex) => ex.Data[StepKey] is OpenAiStep step ? step : OpenAiStep.Unknown;
    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (int depth = 0; depth < 16; depth++)
        {
            yield return ex;
            if (ex.InnerException is not { } inner) yield break;
            ex = inner;
        }
    }
    internal static OpenAiFailure Classify(Exception ex)
    {
        HttpRequestException? http = Chain(ex).OfType<HttpRequestException>().FirstOrDefault();
        if (http is not null) return http.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => OpenAiFailure.Dns,
            HttpRequestError.ConnectionError => OpenAiFailure.Connect,
            HttpRequestError.SecureConnectionError => OpenAiFailure.Tls,
            HttpRequestError.ProxyTunnelError => OpenAiFailure.Proxy,
            _ => OpenAiFailure.Transport
        };
        return ex switch
        {
            TimeoutException => OpenAiFailure.Timeout,
            OperationCanceledException => OpenAiFailure.Cancelled,
            System.IO.IOException when Step(ex) == OpenAiStep.Persist => OpenAiFailure.Storage,
            OpenAiRequestException => OpenAiFailure.Http,
            OpenAiUserException { Error: OpenAiUserError.PlanPermissionMissing or OpenAiUserError.PlanPermissionRevoked } => OpenAiFailure.Permission,
            _ when Step(ex) == OpenAiStep.Validate => OpenAiFailure.Validation,
            InvalidOperationException => OpenAiFailure.Protocol,
            _ => OpenAiFailure.Unknown
        };
    }
    // A code may have been consumed after any response, timeout, reset or TLS failure.
    // Retry only DNS failure or a socket error proving connection establishment failed.
    internal static bool MayRetryToken(Exception ex, int completedAttempts) => completedAttempts == 1 && ex is HttpRequestException http &&
        !IsOutboundBlocked(ex) &&
        (http.HttpRequestError == HttpRequestError.NameResolutionError ||
         (http.HttpRequestError == HttpRequestError.ConnectionError && Chain(http).OfType<SocketException>().Any(s =>
             s.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkDown)));

    internal static bool IsOutboundBlocked(Exception ex) => Classify(ex) == OpenAiFailure.Connect &&
        Chain(ex).OfType<SocketException>().Any(s => s.SocketErrorCode is SocketError.AccessDenied or SocketError.NetworkUnreachable);

    internal static OpenAiErrorCode Code(string? code) => code is null ? OpenAiErrorCode.None :
        Enum.TryParse(code, false, out OpenAiErrorCode known) && Enum.IsDefined(known) && known.ToString() == code ? known : OpenAiErrorCode.Unknown;

    internal static string Describe(Exception ex)
    {
        Exception[] chain = Chain(ex).ToArray();
        HttpRequestException? http = chain.OfType<HttpRequestException>().FirstOrDefault();
        SocketException? socket = chain.OfType<SocketException>().FirstOrDefault();
        OpenAiRequestException? api = chain.OfType<OpenAiRequestException>().FirstOrDefault();
        return $"step={Step(ex)} class={Classify(ex)} types={string.Join("<-", chain.Select(e => e.GetType().FullName))}" +
            $" httpRequestError={http?.HttpRequestError.ToString() ?? "None"} socketError={socket?.SocketErrorCode.ToString() ?? "None"}" +
            $" status={api?.Status ?? (int?)http?.StatusCode ?? (ex.Data[StatusKey] is int status ? status : (int?)null)} code={Code(api?.ErrorCode)}" +
            (ex is OpenAiOcrProtocolException protocol ? $" reason={protocol.Reason}" : "") +
            (ex is OpenAiUserException user ? $" userError={user.Error}" : "");
    }
    internal static string Event(OpenAiStep step, OpenAiOutcome outcome, Exception? ex = null, int? status = null) =>
        ex is null ? $"step={step} outcome={outcome} status={status}" : $"outcome={outcome} {Describe(ex)}";

    internal static string UserMessage(Exception ex)
    {
        if (ex is OpenAiUserException or OpenAiOcrProtocolException) return ex.Message;
        string context = Step(ex) switch
        {
            OpenAiStep.Token => "Der Browser-Login ist angekommen. Der Tokenaustausch mit OpenAI ist fehlgeschlagen. ",
            OpenAiStep.Jwks => "Die OpenAI-Signaturschlüssel konnten nicht geladen werden. ",
            OpenAiStep.Validate => "Die OpenAI-Identität konnte nicht sicher bestätigt werden. Bitte erneut anmelden. ",
            OpenAiStep.Models => "Die OpenAI-Modellliste konnte nicht geladen werden. ",
            OpenAiStep.Callback => "Die Rückkehr vom Browser konnte nicht bestätigt werden. ",
            OpenAiStep.Persist => "Die OpenAI-Anmeldung konnte nicht lokal gespeichert werden. ",
            OpenAiStep.Session => "Die OpenAI-Anmeldung ist noch beschäftigt. ",
            OpenAiStep.Refresh => "Die OpenAI-Anmeldung konnte nicht erneuert werden. ",
            OpenAiStep.Ocr => "Die Texterkennung ist fehlgeschlagen. ",
            _ => ""
        };
        if (ex is OpenAiRequestException) return context + ex.Message;
        return context + (Classify(ex) switch
        {
            OpenAiFailure.Dns => "Die OpenAI-Adresse konnte nicht aufgelöst werden. DNS und Internetverbindung prüfen und erneut versuchen.",
            // Firewall rules are per path; name the app host the user starts, not the process image of a test run.
            OpenAiFailure.Connect when IsOutboundBlocked(ex) => "Kairos darf keine Internetverbindung aufbauen. Bitte „" +
                System.IO.Path.Combine(AppContext.BaseDirectory, "KairosCapture.exe") +
                "“ in der Firewall für ausgehende Verbindungen freigeben (z. B. Windows Firewall Control).",
            OpenAiFailure.Connect => "Die Verbindung zu OpenAI konnte nicht aufgebaut werden. Netzwerk, Firewall und Proxy-Einstellungen prüfen.",
            OpenAiFailure.Tls => "Die verschlüsselte Verbindung zu OpenAI konnte nicht hergestellt werden. Systemzeit, Zertifikate und HTTPS-Filter prüfen.",
            OpenAiFailure.Proxy => "Der konfigurierte Proxy konnte die Verbindung zu OpenAI nicht herstellen. Proxy-Einstellungen prüfen.",
            OpenAiFailure.Timeout when Step(ex) == OpenAiStep.Ocr => "OpenAI hat nicht rechtzeitig geantwortet. Bitte einen kleineren Ausschnitt versuchen.",
            OpenAiFailure.Timeout when Step(ex) == OpenAiStep.Refresh => "Bitte erneut versuchen oder die Anmeldung erneuern.",
            OpenAiFailure.Timeout when Step(ex) == OpenAiStep.Session => "Bitte die laufende Anmeldung abschließen und erneut versuchen.",
            OpenAiFailure.Timeout => "OpenAI hat nicht rechtzeitig geantwortet. Bitte erneut versuchen; eine Anmeldung beginnt dabei mit einem neuen Code.",
            OpenAiFailure.Storage => "Bitte den Zugriff auf den lokalen Sitzungsspeicher prüfen und erneut versuchen.",
            OpenAiFailure.Transport => "Die Netzwerkverbindung zu OpenAI wurde unterbrochen. Bitte erneut versuchen. Technische Fehlerklasse steht im Protokoll.",
            _ => "Die OpenAI-Aktion konnte nicht abgeschlossen werden. Technischer Schritt und Fehlertyp stehen im Protokoll."
        });
    }
}
