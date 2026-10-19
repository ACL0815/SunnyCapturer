# Kairos Capture

> Stand 19.10.2026, Version 0.4.0: Speichern öffnet wieder den Dialog, rechts in der Glasleiste stehen Schließen, Anpinnen, Speichern und Fertig; angepinnte Bilder schweben rahmenlos im Vordergrund. Markierungen zeigen direkt nach dem Zeichnen Anfasser. Der OCR-Dialog kann übersetzen, die OpenAI-Anmeldung protokolliert Fehler jetzt schrittgenau. Build und neun Offline-Prüfsuiten sind grün; echte Bedienung, Mixed-DPI und echter OpenAI-Login sind weiterhin offen. Siehe [Kernabnahme](docs/kairos/OVERLAY-ABNAHME.md), [Icons](docs/kairos/ICONS.md), [OAuth](docs/kairos/OAUTH.md).

Native Screenshot-Anwendung für Windows 11, geschrieben in C# mit WPF und .NET 10, in der dunklen Violett-/Lavendel-/Gold-Palette der Nutzervorlage.

## Starten

`artifacts\KairosCapture-0.4.0-win-x64\KairosCapture.exe` öffnen oder `.\Start-KairosCapture.ps1`. Kairos erscheint ohne Fenster im Infobereich. Voraussetzung ist die **.NET 10 Desktop Runtime** für Windows x64.

## Tray

- **Linksklick:** Bereich aufnehmen (wie das Tastenkürzel, Standard **Strg+Alt+A**).
- **Rechtsklick:**
  - Vollbild aufnehmen (Monitor unter dem Mauszeiger vorausgewählt)
  - Verzögert aufnehmen (3/5/10 s; ein Linksklick bricht den Countdown ab)
  - Text erkennen… (die Erkennung startet direkt nach dem Aufziehen)
  - Letzte Aufnahme fortsetzen (derselbe eingefrorene Bildschirm samt Rahmen, Objekten und Undo/Redo)
  - Angepinnte Bilder schließen (mit Anzahl, nur sichtbar solange Pins geöffnet sind)
  - Zuletzt gespeichert
  - OCR-Kontostatus
  - Einstellungen
  - Beenden

## Arbeiten im Overlay

1. Bereich aufziehen. Der eingefrorene Bildschirm bleibt offen. Die Glasleiste enthält Werkzeuge, Farben, Strichstärke, Rückgängig/Wiederholen/Löschen und OCR. Rechts folgen **Abbrechen · Anpinnen · Speichern · Fertig (✓)**; der lavendelfarbene Haken kopiert und schließt.
2. Den Rahmen über acht Griffe verändern. **Leertaste + Ziehen** verschiebt ihn, auch über die erste Auswahl hinaus.
3. Mit **Auswahl** bestehende Objekte verschieben und skalieren. Ein Doppelklick bearbeitet Text erneut. Farbe und Dicke wirken auch auf das ausgewählte Objekt.
4. **Fertig (Enter / Strg+C)** legt das Bild in die Zwischenablage. **Speichern (Strg+S)** öffnet den Windows-Dialog mit Speicherordner (Standard `Bilder\Kairos`) und `Kairos JJJJ-MM-TT HHmmss.png`. Abbrechen erhält Auswahl, Markierungen und Undo/Redo. Direktspeichern ist ein Opt-in (`SaveWithoutDialog`, Standard `false`); das alte JSON-Feld `AskBeforeSaving` wird ignoriert. „Beim Kopieren zusätzlich speichern“ legt weiterhin direkt im Ordner ab. **Esc** verwirft zuerst eine laufende Geste und beendet sonst die Sitzung.
5. **OCR** schließt das Overlay und öffnet ein kleines Ergebnisfenster mit bearbeitbarem Text. Der Text wird standardmäßig sofort kopiert. Ohne verbundenes Konto bietet das Fenster direkt **Mit ChatGPT verbinden** und danach **Erneut versuchen**.
6. **Anpinnen (I)** zeigt die Auswahl inklusive Markierungen als rahmenloses Bild im Vordergrund. **P** bleibt für den Stift reserviert. Links ziehen verschiebt; Ränder/Ecken skalieren mit festem Seitenverhältnis (mindestens 48 px je Seite), das Mausrad zoomt um den Cursor. Doppelklick oder Esc im fokussierten Pin schließt. Rechtsklick bietet Kopieren, Speichern… (immer mit Dialog), Originalgröße und Schließen. Mehrere Pins und „Letzte Aufnahme fortsetzen“ sind gleichzeitig möglich.

Pins erscheinen ohne Aktivierung (`ShowActivated=false`, `SWP_NOACTIVATE`); nach dem Overlay versucht Kairos, den vorherigen Vordergrund wiederherzustellen. Ein bewusster Klick darf den Pin aktivieren, damit Esc lokal funktioniert. Es gibt keinen globalen Tastatur-Hook. Bildposition und Originalgröße sind physische Pixel; eine Viewbox gleicht den WPF-DPI-Maßstab aus. Der Schatten liegt als separate Fläche dahinter, damit die Bildpixel bei 100 % keinen Effekt-Zwischenpuffer durchlaufen; skalierte Ansichten verwenden hochwertige Interpolation. `WM_GETMINMAXINFO` hebt die Desktop-Standardgrenze für Pin-Größen bis 32768 px je Seite inklusive Schatten an; die tatsächliche Clientfläche wird nach jeder Platzierung geprüft. Native Fokus- und Mixed-DPI-Hardwaretests stehen noch aus. Siehe [Microsoft: SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos).

## Einstellungen

Tastenkürzel (Kombination direkt drücken; ist sie belegt, bleibt das alte aktiv), Speicherordner, „Ohne Nachfrage direkt im Ordner speichern“, „Beim Kopieren zusätzlich speichern“, ChatGPT-Konten und OCR-Modell, „Erkannten Text automatisch kopieren“, „Mit Windows starten“. Änderungen gelten sofort und landen in `%LOCALAPPDATA%\KairosCapture\settings.json`.

Aufnahmen bleiben lokal. Nur eine ausdrücklich gestartete Texterkennung sendet das bearbeitete Bild an OpenAI. [Offizieller OAuth-Ablauf](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), [unterstützte Bildanfragen](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).

## Bauen und prüfen

Voraussetzung: .NET 10 SDK. Keine zusätzliche JavaScript-Toolchain erforderlich.

```powershell
.\scripts\build.ps1
```

Das Skript baut Release, führt neun Offline-Prüfsuiten aus und erzeugt eine kleine, frameworkabhängige Windows-x64-Ausgabe. Ergebnisse (`self-test.json`) und die tatsächlich gerenderten WPF-Ansichten (`overlay.png`, `settings-window.png`, `ocr-result.png`, `tray-menu.png`) landen in `test-results`. Der persönliche OAuth-Login und eine echte Cloud-OCR-Anfrage sind keine Offline-Prüfung und werden dadurch nicht als erfolgreich behauptet.

Die Overlay-Suite prüft außerdem die vier rechten Aktionen, Dialogabbruch, den Pin-Handoff samt Flatten-Bild und fortsetzbarer Sitzung sowie Resize-/Zoom-Verhältnis, Mindestgröße und DPI-Layout (100–300 %). Unsichtbare HWNDs prüfen die nativen Positionen und Größen bei hohen und breiten Bildern; ein 20000-px-Original prüft die Zoomrichtung oberhalb der früheren 16384-px-Grenze. Die Settings-Suite ersetzt den Speicherdialog durch eine injizierte Funktion. `KairosCapture --render-pin test-results/pin.png` rendert den echten Pin-Inhalt mit Schatten auf neutralem Hintergrund, ohne ein Fenster anzuzeigen.

```powershell
# Nur bauen und prüfen
.\scripts\build.ps1 -BuildOnly

# Mit geöffneten Einstellungen starten
.\Start-KairosCapture.ps1 -Demo
```

Technologieabwägung und Grenzen stehen in [ARCHITEKTUR.md](docs/kairos/ARCHITEKTUR.md), die OAuth-Verträge in [OAUTH.md](docs/kairos/OAUTH.md).
