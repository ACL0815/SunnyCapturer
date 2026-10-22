# Haken mit Rücksprung und separatem Kopierbutton

Status: Spezifikation für eine spätere Umsetzung. Dieser PR enthält ausschließlich dieses Dokument.

## Gewünschtes Verhalten

Der Haken in der Menüleiste des Aufnahme-Overlays wird zur Aktion "Kopieren und zur letzten Anwendung zurückkehren". Direkt links daneben kommt ein Button mit Kopiersymbol hinzu. Dieser übernimmt das bisherige Verhalten des Hakens.

- **Haken** kopiert die aktuelle Auswahl einschließlich Markierungen in die Zwischenablage, schließt das Overlay und fordert anschließend die Aktivierung des Fensters der zuletzt verwendeten Anwendung an.
- **Kopierbutton** kopiert dasselbe Bild und schließt das Overlay wie der bisherige Haken. Er fordert keinen zusätzlichen Wechsel des Vordergrundfensters an.

"Letzte Anwendung" bezeichnet das Fenster, das unmittelbar vor Beginn der Aufnahme im Vordergrund war. Der Rücksprung öffnet keine neue Anwendung und fügt das Bild nicht automatisch ein.

Der Haken bleibt rechts außen und behält seine hervorgehobene Darstellung. Die rechte Aktionsgruppe lautet danach **Text erkennen, Trenner, Abbrechen, Anpinnen, Speichern, Kopieren, Haken**. Der neue Kopierbutton verwendet das vorhandene Kopiersymbol und denselben Buttonstil wie die übrigen Nebenaktionen.

Die Tooltips benennen beide Aktionen eindeutig:

- Haken: "Fertig · Bild kopieren und zur letzten Anwendung zurückkehren". Der Haken nennt kein Tastenkürzel.
- Kopierbutton: "Bild kopieren und schließen · Enter / Strg+C". Der Hinweis auf die Tastenkürzel wandert vom Haken hierher, weil Enter und Strg+C weiter nur kopieren und schließen.

## Bestehendes Verhalten

Die folgenden Beobachtungen beziehen sich auf den lokalen Kairos-Entwicklungsstand auf `dev` bei `4a888a07fd5bd7ab8cbe9e8f0529ae6e39ea7750`. Dieser Dokumentations-PR übernimmt keinen Produktcode aus diesem Entwicklungsstand.

- `RegionOverlay` verbindet den Haken mit `CompleteAsync(CaptureCompletion.Copied)`. Enter, Strg+C und der Abschluss per Doppelklick verwenden denselben Kopierpfad.
- `RegionOverlay.CompleteAsync` berücksichtigt die Einstellung `SaveOnCopy` und schreibt anschließend das Bild in die Zwischenablage. `ExecuteOutput` schließt das Overlay erst nach erfolgreicher Ausgabe. Bei einem Ausgabefehler bleibt die Sitzung geöffnet.
- `CaptureService` erfasst mit `GetForegroundWindow` das Fenster vor der Aufnahme. Nach dem Anpinnen prüft er den gespeicherten Handle mit `IsWindow` und versucht den Rücksprung mit `SetForegroundWindow`. Beim bisherigen Kopierabschluss gibt es diesen ausdrücklichen Rücksprung nicht.
- `Icons.xaml` enthält bereits `Glyph.Copy`.

Der gespeicherte Fenster-Handle bezeichnet das Vordergrundfenster beim Aufnahmebeginn. Beim Start aus einem sichtbaren Kairos-Hauptfenster kann dieses Fenster zu Kairos gehören. Die Spezifikation verspricht keinen Verlauf zuletzt verwendeter fremder Anwendungen.

## Grenzen der späteren Umsetzung

Die Änderung betrifft die beiden Buttons in der Menüleiste. Enter, Strg+C und Doppelklick behalten ihr bisheriges Verhalten. Die neue Rücksprungabsicht darf deshalb nicht pauschal an jeden Abschluss mit `CaptureCompletion.Copied` gekoppelt werden.

Beide Buttons verwenden weiterhin denselben bestehenden Kopier- und Fehlerpfad. Die Ausgabe bleibt ein Kopierabschluss. Falls für die Unterscheidung nötig, transportiert das Sitzungsergebnis nur die zusätzliche Rücksprungabsicht des Hakens.

Der Rücksprung verwendet den bereits erfassten Fenster-Handle, dieselbe Bedingung wie der bestehende Rücksprung nach dem Anpinnen und die vorhandene Win32-Anbindung. Der Handle muss ungleich null sein und `IsWindow` erfüllen. Das Ausblenden des Kairos-Hauptfensters nach einem Kopierabschluss bleibt unverändert. War das Hauptfenster selbst das gespeicherte Fenster, wird es für den Rücksprung nicht wieder eingeblendet.

Der Rücksprung erfolgt nach erfolgreichem Kopieren und nach dem Schließen des Overlays. Bei einem Ausgabefehler bleibt die Aufnahme geöffnet und der Fokus wechselt nicht.

Ist das frühere Fenster inzwischen geschlossen oder lehnt Windows die Aktivierung ab, bleibt das Bild kopiert und das Overlay geschlossen. Dafür sind weder Fokus-Erzwingung, künstliche Tastatureingaben noch ein neuer globaler Fensterverlauf vorgesehen.

Die vorhandene Einstellung "Beim Kopieren zusätzlich speichern" gilt weiterhin für beide Buttons. Speichern, Anpinnen, OCR, Abbrechen und das Fortsetzen der letzten Aufnahme behalten ihr Verhalten. Eine neue Einstellung oder ein Umbau der Aufnahmeverwaltung ist nicht vorgesehen.

Voraussichtlich betrifft die spätere Änderung `RegionOverlay.cs`, `CaptureService.cs` und bei Bedarf `CaptureSessionResult.cs` sowie gezielte bestehende Prüfungen. `Glyph.Copy` wird wiederverwendet. Vorhandene Bedienhinweise passt dieselbe spätere Änderung an, sofern es sie gibt.

## Abnahmekriterien für die spätere Umsetzung

1. Der Haken bleibt hervorgehoben rechts außen. Der Kopierbutton steht ohne weiteren Button dazwischen direkt links daneben. Beide Tooltips unterscheiden die Aktionen korrekt. Der Hinweis "Enter / Strg+C" steht nur im Tooltip des Kopierbuttons.
2. Beide Buttons kopieren dieselbe Auswahl einschließlich Markierungen. `SaveOnCopy` verhält sich bei beiden wie bisher.
3. Nur der Haken fordert nach erfolgreicher Ausgabe und geschlossenem Overlay die Aktivierung des vor der Aufnahme gespeicherten Fensters an.
4. Der Kopierbutton schließt nach erfolgreicher Ausgabe und löst keinen ausdrücklichen Rücksprung aus. Die anschließende normale Fokusvergabe von Windows bleibt davon unberührt.
5. Ein Fehler beim Speichern oder beim Schreiben in die Zwischenablage lässt das Overlay mit Auswahl und Markierungen geöffnet. Es erfolgt kein Rücksprung.
6. Ein nicht mehr vorhandenes Zielfenster oder eine verweigerte Aktivierung macht den erfolgreichen Kopiervorgang nicht rückgängig und löst keine Fokus-Schleife aus.
7. Enter, Strg+C und Doppelklick behalten ihr bisheriges Verhalten. Speichern, Anpinnen, OCR und Abbrechen bleiben unverändert.
8. Die um einen Button breitere Menüleiste bleibt innerhalb der Bildschirmgrenzen bedienbar. Die bestehende Positionierung und Größenberechnung wird dafür wiederverwendet.

Die späteren Prüfungen erweitern gezielt die vorhandenen Overlay- und Capture-Self-Checks. Die bestehende Toolbar-Prüfung schließt einen Kopierbutton derzeit ausdrücklich aus. Sie wird auf die neue Reihenfolge und die Nachbarschaft zum Haken angepasst. Beide tatsächlichen Button-Klicks müssen den gemeinsamen Kopierpfad und ihre unterschiedliche Rücksprungabsicht belegen. Der Fehlerfall und die unveränderte Zuordnung der Tastenkürzel werden ebenfalls geprüft. Der tatsächliche Fokuswechsel wird zusätzlich mit einer anderen geöffneten Anwendung geprüft. Statische Prüfungen gelten dafür nicht als Nachweis.

## Umfang dieses PRs

Dieser PR beschreibt die gewünschte Änderung, den kleinsten vorgesehenen Eingriff und die spätere Abnahme. Er ändert keinen Produktcode, keine Icons, keine Einstellungen und keine Tastenkürzel. Build-, Laufzeit- und Fokustests gehören zur späteren Umsetzung.
