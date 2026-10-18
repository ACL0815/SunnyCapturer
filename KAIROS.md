# Kairos Capture

Native Screenshot-Werkbank für Windows 11, geschrieben in C# mit WPF und .NET 10. Die Oberfläche übernimmt die dunkle Violett-/Lavendel-/Gold-Palette der Nutzervorlage. Die ursprünglichen SunnyCapturer-Dokumente und historischen Quellen bleiben im Repository verfügbar.

## Starten

Nach dem Build `artifacts\KairosCapture-win-x64\KairosCapture.exe` öffnen. Alternativ:

```powershell
.\Start-KairosCapture.ps1
```

Die Anwendung benötigt die **.NET 10 Desktop Runtime** für Windows x64. Auf dem Entwicklungsrechner ist sie bereits vorhanden. Das SDK wird nur zum Bauen benötigt.

## Arbeiten

1. **Bereich aufnehmen** anklicken oder **Strg+Alt+A** drücken. Bereich aufziehen; mit Esc abbrechen.
2. Im Editor zeichnen, markieren oder Text einsetzen. Mit dem Auswahlwerkzeug lassen sich Markierungen verschieben. Doppelklick öffnet vorhandenen Text erneut.
3. Mit Rückgängig/Wiederholen Änderungen korrigieren. **Kopieren** überträgt das fertige Bild; **Speichern** erzeugt eine PNG-Datei.
4. Vorhandene Bilder lassen sich öffnen, hineinziehen oder aus der Zwischenablage einfügen.
5. Für OCR **Weiter mit ChatGPT** wählen und im Browser anmelden. Ein verfügbares Modell wählen und **Text erkennen** starten. Der erkannte Text bleibt bearbeitbar und kann separat kopiert oder gespeichert werden.

Aufnahmen und Bearbeitung bleiben lokal. Nur der ausdrücklich gestartete OCR-Vorgang sendet das bearbeitete Bild an OpenAI. Konto-Verfügbarkeit und Nutzungsgrenzen richten sich nach dem verbundenen ChatGPT-Konto. [Offizieller OAuth-Ablauf](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), [unterstützte Bildanfragen](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).

Das Schließen des Fensters lässt Kairos für den Hotkey im Infobereich weiterlaufen. Über das Tray-Menü **Beenden** wird es vollständig beendet. Ist Strg+Alt+A bereits vergeben, bleibt die Aufnahme über die Oberfläche möglich.

## Bauen und prüfen

Voraussetzung: .NET 10 SDK. Keine zusätzliche JavaScript-Toolchain erforderlich.

```powershell
.\scripts\build.ps1
```

Das Skript baut Release, führt die Offline-Prüfungen aus und erzeugt eine kleine, frameworkabhängige Windows-x64-Ausgabe. Ergebnisse und ein gerendertes Bild der Oberfläche landen in `test-results`. Der persönliche OAuth-Login und eine echte Cloud-OCR-Anfrage sind keine Offline-Prüfung und werden dadurch nicht als erfolgreich behauptet.

```powershell
# Nur bauen und prüfen
.\scripts\build.ps1 -BuildOnly

# Beispieldokument für die visuelle Prüfung
.\Start-KairosCapture.ps1 -Demo
```

Technologieabwägung und Grenzen stehen in [ARCHITEKTUR.md](docs/kairos/ARCHITEKTUR.md), die OAuth-Verträge in [OAUTH.md](docs/kairos/OAUTH.md).
