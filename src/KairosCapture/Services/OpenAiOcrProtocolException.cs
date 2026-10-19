#nullable enable
using System;

namespace KairosCapture.Services;

internal enum OpenAiOcrReason
{
    ContentType, UnexpectedEnd, EventTooLarge, UnconfirmedDone, InvalidJson, MissingType,
    EventMismatch, InvalidDelta, TextTooLarge, InvalidCompletion, Incomplete, Cancelled, Refusal, ConnectionInterrupted, InvalidEncoding, NoEvents
}

internal sealed class OpenAiOcrProtocolException(OpenAiOcrReason reason, Exception? inner = null)
    : InvalidOperationException(reason switch
    {
        OpenAiOcrReason.ContentType => "OpenAI hat keine lesbare Antwort zur Texterkennung geliefert. Bitte erneut versuchen.",
        OpenAiOcrReason.NoEvents => "OpenAI hat keine Antwort zur Texterkennung geliefert. Bitte erneut versuchen.",
        OpenAiOcrReason.UnexpectedEnd or OpenAiOcrReason.UnconfirmedDone => "Die OCR-Verbindung endete ohne bestätigten Abschluss. Der Teiltext ist unvollständig. Bitte erneut versuchen.",
        OpenAiOcrReason.EventTooLarge => "Die Antwort zur Texterkennung ist zu groß. Bitte einen kleineren Ausschnitt versuchen.",
        OpenAiOcrReason.InvalidJson => "Die Antwort zur Texterkennung ist beschädigt. Bitte erneut versuchen.",
        OpenAiOcrReason.InvalidEncoding => "Die Antwort zur Texterkennung enthält unlesbare Zeichen. Bitte erneut versuchen.",
        OpenAiOcrReason.MissingType => "Die Antwort zur Texterkennung ist unvollständig. Bitte erneut versuchen.",
        OpenAiOcrReason.EventMismatch => "Die Antwort zur Texterkennung ist widersprüchlich. Bitte erneut versuchen.",
        OpenAiOcrReason.InvalidDelta => "Ein erkannter Textabschnitt ist beschädigt. Bitte erneut versuchen.",
        OpenAiOcrReason.TextTooLarge => "Der erkannte Text ist zu lang. Bitte einen kleineren Ausschnitt versuchen.",
        OpenAiOcrReason.InvalidCompletion => "OpenAI hat den Abschluss der Texterkennung nicht bestätigt. Bitte erneut versuchen.",
        OpenAiOcrReason.Incomplete => "OpenAI hat die Texterkennung unvollständig beendet. Bitte einen kleineren Ausschnitt versuchen.",
        OpenAiOcrReason.Cancelled => "OpenAI hat die Texterkennung abgebrochen. Der Teiltext ist unvollständig. Bitte erneut versuchen.",
        OpenAiOcrReason.Refusal => "Das gewählte Modell hat die Texterkennung abgelehnt. Bitte ein anderes Modell oder einen anderen Ausschnitt versuchen.",
        OpenAiOcrReason.ConnectionInterrupted => "Die OCR-Verbindung wurde unterbrochen. Der Teiltext ist unvollständig. Bitte erneut versuchen.",
        _ => "OpenAI hat eine unlesbare Antwort zur Texterkennung geliefert. Bitte erneut versuchen."
    }, inner)
{
    internal OpenAiOcrReason Reason { get; } = reason;
}
