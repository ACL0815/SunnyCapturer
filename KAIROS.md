# Kairos Capture

> Stand 18.10.2026, Version 0.2.0: Die Aufnahme bleibt nach der Auswahl zum direkten Bearbeiten im Bildschirm-Overlay offen. Rahmen und Markierungen lassen sich anschließend weiter verändern. Der erste Stand mit automatischem Wechsel ins Hauptfenster verfehlte diesen Kern und wurde ersetzt. Der korrigierte Gesamtbuild, sechs Offline-Prüfsuiten und beide WPF-Vorschauen sind von Root geprüft; die reale Maus-/Tastaturfolge und Mixed-DPI sind noch offen. Siehe [Kernabnahme](docs/kairos/OVERLAY-ABNAHME.md) und [Untersuchungsbericht](docs/kairos/reverse/README.md).

Native Screenshot-Anwendung für Windows 11, geschrieben in C# mit WPF und .NET 10. Die Oberfläche übernimmt die dunkle Violett-/Lavendel-/Gold-Palette der Nutzervorlage. Die ursprünglichen SunnyCapturer-Dokumente und historischen Quellen bleiben im Repository verfügbar.

## Starten

`artifacts\KairosCapture-0.2.0-win-x64\KairosCapture.exe` öffnen. Alternativ:

```powershell
.\Start-KairosCapture.ps1
```

Die Anwendung benötigt die **.NET 10 Desktop Runtime** für Windows x64. Auf dem Entwicklungsrechner ist sie bereits vorhanden. Das SDK wird nur zum Bauen benötigt.

## Arbeiten

1. **Bereich aufnehmen** anklicken oder **Strg+Alt+A** drücken. Bereich aufziehen und die Maus loslassen: Der eingefrorene Bildschirm bleibt mit Rahmen und schwebender Werkzeugleiste offen.
2. Den Rahmen an seinen **acht Griffen** verändern. **Leertaste + Ziehen** verschiebt den Ausschnitt; mit dem Auswahlwerkzeug geht das auch auf freier Bildfläche. Er lässt sich über die erste Auswahl hinaus vergrößern, weil die vollständige Bildschirmaufnahme erhalten bleibt.
3. Direkt im Overlay zeichnen, markieren oder Text einsetzen. **Auswahl** aktiviert bestehende Objekte zum Verschieben und Skalieren. Doppelklick öffnet vorhandenen Text erneut; Farbe und Dicke ändern auch ein ausgewähltes Objekt. Undo/Redo bleibt bei Rahmenänderungen erhalten.
4. **Kopieren** oder **Speichern** gibt den aktuellen Ausschnitt samt Markierungen ohne Griffe oder Werkzeugleiste aus. Die Sitzung endet erst nach erfolgreicher Ausgabe. Ein abgebrochener Speicherdialog lässt sie offen. **Esc** verwirft zuerst eine laufende Text-, Zeichen- oder Rahmengeste; ohne aktive Geste beendet es die Sitzung. Rechtsklick oder **Abbrechen** beendet sie ohne Ausgabe.
5. **Werkbank** oder **OCR** öffnet ausdrücklich das Hauptfenster und übernimmt denselben Editor samt Ausschnitt, Objekten und History. Muss vorher ein altes Dokument gesichert werden, bleibt bei abgebrochener Rückfrage die neue Aufnahme im Overlay erhalten. In der Werkbank bleiben neue Gesten im sichtbaren Ausschnitt. Vorhandene Bilder lassen sich dort auch öffnen, hineinziehen oder aus der Zwischenablage einfügen.
6. Für OCR **Weiter mit ChatGPT** wählen und im Browser anmelden. Ein verfügbares Modell wählen und **Text erkennen** starten. Der erkannte Text bleibt bearbeitbar und kann separat kopiert oder gespeichert werden. Der OCR-Knopf im Overlay startet die Erkennung bei bereits verfügbarem Konto und Modell direkt nach der Übergabe; andernfalls zeigt die Werkbank den nötigen nächsten Schritt.

Aufnahmen und Bearbeitung bleiben lokal. Nur der ausdrücklich gestartete OCR-Vorgang sendet das bearbeitete Bild an OpenAI. Konto-Verfügbarkeit und Nutzungsgrenzen richten sich nach dem verbundenen ChatGPT-Konto. [Offizieller OAuth-Ablauf](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), [unterstützte Bildanfragen](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).

Das Schließen des Fensters lässt Kairos für den Hotkey im Infobereich weiterlaufen. Über das Tray-Menü **Beenden** wird es vollständig beendet. Ist Strg+Alt+A bereits vergeben, bleibt die Aufnahme über die Oberfläche möglich.

## Bauen und prüfen

Voraussetzung: .NET 10 SDK. Keine zusätzliche JavaScript-Toolchain erforderlich.

```powershell
.\scripts\build.ps1
```

Das Skript baut Release, führt sechs Offline-Prüfsuiten aus und erzeugt eine kleine, frameworkabhängige Windows-x64-Ausgabe. Ergebnisse (`self-test.json`) und die tatsächlich gerenderten WPF-Ansichten (`overlay.png`, `main-window.png`) landen in `test-results`. Der persönliche OAuth-Login und eine echte Cloud-OCR-Anfrage sind keine Offline-Prüfung und werden dadurch nicht als erfolgreich behauptet.

```powershell
# Nur bauen und prüfen
.\scripts\build.ps1 -BuildOnly

# Beispieldokument für die visuelle Prüfung
.\Start-KairosCapture.ps1 -Demo
```

Technologieabwägung und Grenzen stehen in [ARCHITEKTUR.md](docs/kairos/ARCHITEKTUR.md), die OAuth-Verträge in [OAUTH.md](docs/kairos/OAUTH.md).
