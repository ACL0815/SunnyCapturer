# Kairos Capture

> Stand 19.10.2026, Version 0.4.0: Speichern öffnet wieder den Dialog, rechts in der Glasleiste stehen Schließen, Anpinnen, Speichern und Fertig; angepinnte Bilder schweben rahmenlos im Vordergrund. Markierungen zeigen direkt nach dem Zeichnen Anfasser. Der OCR-Dialog kann übersetzen, die OpenAI-Anmeldung protokolliert Fehler jetzt schrittgenau. Build und neun Offline-Prüfsuiten sind grün; echte Bedienung, Mixed-DPI und echter OpenAI-Login sind weiterhin offen. Siehe [Kernabnahme](docs/kairos/OVERLAY-ABNAHME.md), [Icons](docs/kairos/ICONS.md), [OAuth](docs/kairos/OAUTH.md).

Native Screenshot-Anwendung für Windows 11, geschrieben in C# mit WPF und .NET 10, in der dunklen Violett-/Lavendel-/Gold-Palette der Nutzervorlage.

## Starten

`artifacts\KairosCapture-win-x64\KairosCapture.exe` öffnen oder `.\Start-KairosCapture.ps1`. Kairos erscheint ohne Fenster im Infobereich. Voraussetzung ist die **.NET 10 Desktop Runtime** für Windows x64. Der Publish-Ordner bleibt bei Versionswechseln gleich, damit Firewall-Regeln und Autostart ihren Programmpfad behalten. Ein bereits aktivierter Autostart wird beim Start aus einem Ordner `KairosCapture-win-x64` auf dessen Pfad aktualisiert, sofern dieser nicht in einem Git-Worktree liegt; Starts aus `bin` oder alten Versionsordnern ändern ihn nicht, und ausgeschalteter Autostart bleibt aus.

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
3. Mit **Auswahl** bestehende Objekte verschieben und skalieren. Ein Doppelklick auf eine freie Stelle innerhalb des Bereichs kopiert und schließt wie **Fertig**. Auf Rahmengriffen, mit gedrückter Leertaste oder ohne aufgezogenen Bereich wirkt der zweite Druck wie ein Einzelklick; außerhalb des Bereichs tut er nichts. Ein Doppelklick auf Text bearbeitet ihn weiterhin erneut. Farbe und Dicke wirken auch auf das ausgewählte Objekt.
4. **Fertig (Enter / Strg+C)** legt das Bild in die Zwischenablage. **Speichern (Strg+S)** öffnet den Windows-Dialog mit Speicherordner (Standard `Bilder\Kairos`) und `Kairos JJJJ-MM-TT HHmmss.png`. Abbrechen erhält Auswahl, Markierungen und Undo/Redo. Direktspeichern ist ein Opt-in (`SaveWithoutDialog`, Standard `false`); das alte JSON-Feld `AskBeforeSaving` wird ignoriert. „Beim Kopieren zusätzlich speichern“ legt weiterhin direkt im Ordner ab. **Esc** verwirft zuerst eine laufende Geste und beendet sonst die Sitzung.
5. **OCR** schließt das Overlay und öffnet ein kleines Ergebnisfenster mit bearbeitbarem Text. Der Text wird standardmäßig sofort kopiert. Ohne verbundenes Konto bietet das Fenster direkt **Mit ChatGPT verbinden** und danach **Erneut versuchen**.
6. **Anpinnen (I)** zeigt die Auswahl inklusive Markierungen als rahmenloses Bild im Vordergrund. **P** bleibt für den Stift reserviert. Links ziehen verschiebt; Ränder/Ecken skalieren mit festem Seitenverhältnis (mindestens 48 px je Seite), das Mausrad zoomt um den Cursor. Doppelklick oder Esc im fokussierten Pin schließt. Rechtsklick bietet Kopieren, Speichern… (immer mit Dialog), Originalgröße und Schließen. Mehrere Pins und „Letzte Aufnahme fortsetzen“ sind gleichzeitig möglich.

Pins erscheinen ohne Aktivierung (`ShowActivated=false`, `SWP_NOACTIVATE`); nach dem Overlay versucht Kairos, den vorherigen Vordergrund wiederherzustellen. Ein bewusster Klick darf den Pin aktivieren, damit Esc lokal funktioniert. Es gibt keinen globalen Tastatur-Hook. Bildposition und Originalgröße sind physische Pixel; eine Viewbox gleicht den WPF-DPI-Maßstab aus. Der Schatten liegt als separate Fläche dahinter, damit die Bildpixel bei 100 % keinen Effekt-Zwischenpuffer durchlaufen; skalierte Ansichten verwenden hochwertige Interpolation. `WM_GETMINMAXINFO` hebt die Desktop-Standardgrenze für Pin-Größen bis 32768 px je Seite inklusive Schatten an; die tatsächliche Clientfläche wird nach jeder Platzierung geprüft. Native Fokus- und Mixed-DPI-Hardwaretests stehen noch aus. Siehe [Microsoft: SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos).

Bei Deaktivierung oder Capture-Verlust prüft das Overlay die linke Maustaste. Ist sie losgelassen, endet die Rahmengeste an der aktuellen Cursorposition, ersatzweise an der zuletzt bekannten Position. Die Auswahl bleibt erhalten; im OCR-Modus startet die Erkennung genau einmal. Bei gedrückter Taste und gehaltener Capture läuft die Geste bis MouseUp weiter. Bei gedrückter Taste ohne Capture stellt das Overlay die vorherige Auswahl wieder her. Escape verwirft die Geste weiterhin.

Eine fehlende MouseUp-Meldung wird auch beim nächsten Move mit losgelassener Taste aufgefangen. Dabei gilt die dann gelesene Cursorposition. Die genaue Loslassposition ist ohne die fehlende Meldung nicht bekannt.

## Einstellungen

Tastenkürzel (Kombination direkt drücken; ist sie belegt, bleibt das alte aktiv), Speicherordner, „Ohne Nachfrage direkt im Ordner speichern“, „Beim Kopieren zusätzlich speichern“, ChatGPT-Konten und OCR-Modell, „Erkannten Text automatisch kopieren“, „Mit Windows starten“. Änderungen gelten sofort und landen in `%LOCALAPPDATA%\KairosCapture\settings.json`.

Aufnahmen bleiben lokal. Nur eine ausdrücklich gestartete Texterkennung sendet das bearbeitete Bild an OpenAI. [Offizieller OAuth-Ablauf](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), [unterstützte Bildanfragen](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).

Bei blockierten ausgehenden Verbindungen (`AccessDenied` oder `NetworkUnreachable`) nennen Anmeldung und `--openai-probe` die konkrete Abhilfe: `KairosCapture.exe` für ausgehende Verbindungen in der Firewall freigeben, z. B. in Windows Firewall Control. Diese Verbindungsfehler lösen keinen automatischen Token-POST-Retry aus. Kairos ändert keine Firewall-Regeln.

## Bauen und prüfen

Voraussetzung: .NET 10 SDK. Keine zusätzliche JavaScript-Toolchain erforderlich.

```powershell
.\scripts\build.ps1
```

Das Skript baut Release, führt neun Offline-Prüfsuiten aus und erzeugt eine kleine, frameworkabhängige Windows-x64-Ausgabe in `artifacts\KairosCapture-win-x64`. Ist die dortige EXE gesperrt, bricht das Skript vor dem Build mit „Kairos läuft noch – bitte über das Tray-Menü beenden“ ab; es beendet keinen Prozess. Ergebnisse (`self-test.json`) und die tatsächlich gerenderten WPF-Ansichten (`overlay.png`, `settings-window.png`, `ocr-result.png`, `tray-menu.png`) landen in `test-results`. Der persönliche OAuth-Login und eine echte Cloud-OCR-Anfrage sind keine Offline-Prüfung und werden dadurch nicht als erfolgreich behauptet.

Die Overlay-Suite prüft außerdem die vier rechten Aktionen, Dialogabbruch, den Pin-Handoff samt Flatten-Bild und fortsetzbarer Sitzung sowie Resize-/Zoom-Verhältnis, Mindestgröße und DPI-Layout (100–300 %). Unsichtbare HWNDs prüfen die nativen Positionen und Größen bei hohen und breiten Bildern; ein 20000-px-Original prüft die Zoomrichtung oberhalb der früheren 16384-px-Grenze. Die Settings-Suite ersetzt den Speicherdialog durch eine injizierte Funktion. `KairosCapture --render-pin test-results/pin.png` rendert den echten Pin-Inhalt mit Schatten auf neutralem Hintergrund, ohne ein Fenster anzuzeigen.

Doppelklick-Regressionen verwenden dieselbe Gestenroute wie der Fenster-Handler (`ClickCount=2`) und eine injizierte Kopierfunktion ohne Zugriff auf die echte Zwischenablage. Sie prüfen Copy-Completion, Textbearbeitung, Klicks außerhalb, fehlgeschlagene Ausgabe und erneute Toolbar-Vermessung mit Blur-Hintergrund. Das Hintergrundbild liegt in einem Canvas und bestimmt dadurch nicht die automatische Toolbar-Höhe.

Die Overlay-Suite prüft MouseMove und MouseUp über echte WPF-Handler mit injiziertem Cursor, Tastenstatus und Capture. Ein synchrones LostMouseCapture während der Freigabe darf die endgültige Auswahl nicht verändern. OCR- und Raw-Abschlüsse, alle Deaktivierungsfälle und der Schutz des Log-Hooks in Diagnoseläufen sind ebenfalls geprüft.

Die Zustandsspur hält die letzten höchstens 20 Zeilen pro Overlay im Speicher. Eine normale Aufnahme schreibt keine Spur. Nur bei auffälligem Gestenende durch Deactivated, CaptureLost oder ReleasedMove, Schließen mit aktiver Geste, unerwartet fehlender Leiste oder fehlendem Chip sowie Capture- oder Cursorfehlern wird beim Schließen einmal geschrieben. `Closing` hält den Zustand vor dem Schließen fest. `Closed` behält Geste und Auswahl vor dem Zurücksetzen bei und zeigt Leiste, Chip und Chrome konsistent als geschlossen. Diagnoseläufe unterdrücken diesen Log-Hook. Für isolierte Gegenproben leitet `KAIROS_DIAGNOSTIC_LOG_DIRECTORY` sämtliche App-Logs in das angegebene Verzeichnis um.

```powershell
# Nur bauen und prüfen
.\scripts\build.ps1 -BuildOnly

# Mit geöffneten Einstellungen starten
.\Start-KairosCapture.ps1 -Demo
```

Technologieabwägung und Grenzen stehen in [ARCHITEKTUR.md](docs/kairos/ARCHITEKTUR.md), die OAuth-Verträge in [OAUTH.md](docs/kairos/OAUTH.md).
