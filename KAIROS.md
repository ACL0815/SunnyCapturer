# Kairos Capture

> Stand 19.10.2026, Version 0.3.0: Kairos läuft nur noch im Infobereich. Die frühere Werkbank ist entfallen; das Fenster dient nur noch den Einstellungen. Overlay, Tray-Menü und Icons haben einen Liquid-Glass-Look. Build, neun Offline-Prüfsuiten und alle WPF-Vorschauen sind geprüft. Echte Maus-/Tastaturbedienung, Mixed-DPI und echter OpenAI-Zugriff sind weiterhin offen. Siehe [Kernabnahme](docs/kairos/OVERLAY-ABNAHME.md), [Icons](docs/kairos/ICONS.md).

Native Screenshot-Anwendung für Windows 11, geschrieben in C# mit WPF und .NET 10, in der dunklen Violett-/Lavendel-/Gold-Palette der Nutzervorlage.

## Starten

`artifacts\KairosCapture-0.3.0-win-x64\KairosCapture.exe` öffnen oder `.\Start-KairosCapture.ps1`. Kairos erscheint ohne Fenster im Infobereich. Voraussetzung ist die **.NET 10 Desktop Runtime** für Windows x64.

## Tray

- **Linksklick:** Bereich aufnehmen (wie das Tastenkürzel, Standard **Strg+Alt+A**).
- **Rechtsklick:**
  - Vollbild aufnehmen (Monitor unter dem Mauszeiger vorausgewählt)
  - Verzögert aufnehmen (3/5/10 s; ein Linksklick bricht den Countdown ab)
  - Text erkennen… (die Erkennung startet direkt nach dem Aufziehen)
  - Letzte Aufnahme fortsetzen (derselbe eingefrorene Bildschirm samt Rahmen, Objekten und Undo/Redo)
  - Zuletzt gespeichert
  - OCR-Kontostatus
  - Einstellungen
  - Beenden

## Arbeiten im Overlay

1. Bereich aufziehen. Der eingefrorene Bildschirm bleibt offen. Eine schwebende Glasleiste enthält Werkzeuge, Farben, Strichstärke, Rückgängig/Wiederholen/Löschen, OCR, Kopieren, Speichern und Schließen.
2. Den Rahmen über acht Griffe verändern. **Leertaste + Ziehen** verschiebt ihn, auch über die erste Auswahl hinaus.
3. Mit **Auswahl** bestehende Objekte verschieben und skalieren. Ein Doppelklick bearbeitet Text erneut. Farbe und Dicke wirken auch auf das ausgewählte Objekt.
4. **Kopieren** legt das Bild in die Zwischenablage. **Speichern** legt `Kairos JJJJ-MM-TT HHmmss.png` direkt im Speicherordner ab (Standard `Bilder\Kairos`). Den Dialog kannst du in den Einstellungen wieder einschalten. **Esc** verwirft zuerst eine laufende Geste und beendet sonst die Sitzung.
5. **OCR** schließt das Overlay und öffnet ein kleines Ergebnisfenster mit bearbeitbarem Text. Der Text wird standardmäßig sofort kopiert. Ohne verbundenes Konto bietet das Fenster direkt **Mit ChatGPT verbinden** und danach **Erneut versuchen**.

## Einstellungen

Tastenkürzel (Kombination direkt drücken; ist sie belegt, bleibt das alte aktiv), Speicherordner, „Beim Speichern jedes Mal fragen“, „Beim Kopieren zusätzlich speichern“, ChatGPT-Konten und OCR-Modell, „Erkannten Text automatisch kopieren“, „Mit Windows starten“. Änderungen gelten sofort und landen in `%LOCALAPPDATA%\KairosCapture\settings.json`.

Aufnahmen bleiben lokal. Nur eine ausdrücklich gestartete Texterkennung sendet das bearbeitete Bild an OpenAI. [Offizieller OAuth-Ablauf](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), [unterstützte Bildanfragen](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).

## Bauen und prüfen

Voraussetzung: .NET 10 SDK. Keine zusätzliche JavaScript-Toolchain erforderlich.

```powershell
.\scripts\build.ps1
```

Das Skript baut Release, führt neun Offline-Prüfsuiten aus und erzeugt eine kleine, frameworkabhängige Windows-x64-Ausgabe. Ergebnisse (`self-test.json`) und die tatsächlich gerenderten WPF-Ansichten (`overlay.png`, `settings-window.png`, `ocr-result.png`, `tray-menu.png`) landen in `test-results`. Der persönliche OAuth-Login und eine echte Cloud-OCR-Anfrage sind keine Offline-Prüfung und werden dadurch nicht als erfolgreich behauptet.

```powershell
# Nur bauen und prüfen
.\scripts\build.ps1 -BuildOnly

# Mit geöffneten Einstellungen starten
.\Start-KairosCapture.ps1 -Demo
```

Technologieabwägung und Grenzen stehen in [ARCHITEKTUR.md](docs/kairos/ARCHITEKTUR.md), die OAuth-Verträge in [OAUTH.md](docs/kairos/OAUTH.md).
