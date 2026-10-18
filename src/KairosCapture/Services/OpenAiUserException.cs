#nullable enable
using System;

namespace KairosCapture.Services;

internal enum OpenAiUserError
{
    SignedOut, SigningOut, SessionExpired, SessionBusy, CredentialsUnreadable,
    PlanPermissionMissing, PlanPermissionRevoked, RefreshTooEarly, IncompleteTokenSet
}

// Only fixed, application-owned messages cross the UI boundary. No provider/IO/library text.
internal sealed class OpenAiUserException(OpenAiUserError error) : InvalidOperationException(error switch
{
    OpenAiUserError.SignedOut => "Bitte zuerst mit ChatGPT anmelden.",
    OpenAiUserError.SigningOut => "Die OpenAI-Abmeldung läuft noch.",
    OpenAiUserError.SessionExpired => "Die OpenAI-Sitzung kann nicht verwendet oder erneuert werden. Bitte dieses Konto erneut anmelden.",
    OpenAiUserError.SessionBusy => "Eine andere Kairos-Instanz bearbeitet die OpenAI-Sitzung. Bitte später erneut versuchen.",
    OpenAiUserError.CredentialsUnreadable => "Die geschützten OpenAI-Kontodaten sind nicht lesbar. Die Datei wurde nicht verändert.",
    OpenAiUserError.PlanPermissionMissing => "Das Konto ist angemeldet, aber die Freigabe zur Nutzung des ChatGPT-Plans fehlt. Für OCR bitte dieses Konto erneut anmelden und die Plannutzung erlauben.",
    OpenAiUserError.PlanPermissionRevoked => "OpenAI hat die Freigabe zur Nutzung des ChatGPT-Plans zurückgenommen. Bitte dieses Konto erneut anmelden.",
    OpenAiUserError.RefreshTooEarly => "OpenAI erlaubt die Sitzungserneuerung noch nicht. Bitte später erneut versuchen.",
    OpenAiUserError.IncompleteTokenSet => "OpenAI hat keinen vollständigen oder rotierten Token-Satz geliefert. Bitte dieses Konto erneut anmelden.",
    _ => "Die OpenAI-Verbindung ist nicht verfügbar."
})
{
    internal OpenAiUserError Error { get; } = error;
}
