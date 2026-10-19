#nullable enable
using System;

namespace KairosCapture.Services;

internal enum OpenAiOcrReason
{
    ContentType, UnexpectedEnd, EventTooLarge, UnconfirmedDone, InvalidJson, MissingType,
    EventMismatch, InvalidDelta, TextTooLarge, InvalidCompletion, Incomplete, Cancelled, Refusal, ConnectionInterrupted, InvalidEncoding
}

internal sealed class OpenAiOcrProtocolException(OpenAiOcrReason reason, Exception? inner = null)
    : InvalidOperationException(reason switch
    {
        OpenAiOcrReason.ContentType => "OpenAI hat keinen OCR-Ereignisstream geliefert.",
        OpenAiOcrReason.UnexpectedEnd or OpenAiOcrReason.UnconfirmedDone => "Die OCR-Verbindung endete ohne bestätigten Abschluss. Der Teiltext ist unvollständig. Bitte erneut versuchen.",
        OpenAiOcrReason.EventTooLarge => "Ein OCR-Stream-Ereignis überschreitet die zulässige Größe.",
        OpenAiOcrReason.InvalidJson => "OpenAI hat ein ungültiges JSON-Ereignis im OCR-Stream geliefert.",
        OpenAiOcrReason.InvalidEncoding => "OpenAI hat ungültiges UTF-8 im OCR-Stream geliefert.",
        OpenAiOcrReason.MissingType => "Ein OCR-Stream-Ereignis enthält keinen gültigen Ereignistyp.",
        OpenAiOcrReason.EventMismatch => "Der Ereignisname und der JSON-Typ im OCR-Stream widersprechen sich.",
        OpenAiOcrReason.InvalidDelta => "OpenAI hat einen ungültigen OCR-Textabschnitt geliefert.",
        OpenAiOcrReason.TextTooLarge => "Der erkannte Text überschreitet die zulässige Größe.",
        OpenAiOcrReason.InvalidCompletion => "OpenAI hat keinen vollständigen OCR-Abschluss bestätigt.",
        OpenAiOcrReason.Incomplete => "OpenAI hat die Texterkennung unvollständig beendet. Bitte einen kleineren Ausschnitt versuchen.",
        OpenAiOcrReason.Cancelled => "OpenAI hat die Texterkennung abgebrochen. Der Teiltext ist unvollständig.",
        OpenAiOcrReason.Refusal => "Das gewählte Modell hat die Texterkennung abgelehnt.",
        OpenAiOcrReason.ConnectionInterrupted => "Die OCR-Verbindung wurde unterbrochen. Der Teiltext ist unvollständig. Bitte erneut versuchen.",
        _ => "OpenAI hat einen ungültigen OCR-Stream geliefert."
    }, inner)
{
    internal OpenAiOcrReason Reason { get; } = reason;
}
