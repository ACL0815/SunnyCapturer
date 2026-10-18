# Kairos Capture: Bau und Abnahme

Stand 18.10.2026. Status: **Der erste Release bestand seine Offline-Prüfungen, verfehlte aber den Produktkern und ist nicht abgenommen.** Nach Nutzerpräzisierung wird die [Overlay-Korrektur](../plans/2026-10-18-live-overlay-correction.md) umgesetzt. Die nachstehende Tabelle beschreibt die bisherigen Prüfungen, nicht die noch ausstehende Abnahme des korrigierten Kerns.

## Prüfgrenzen

Die SunnyCapturer-Untersuchung und die Nutzerbilder sind die fachliche Referenz. Kairos ist eine eigene Implementierung. Ein grüner Parser- oder Offline-Test ersetzt weder eine erfolgreiche Aufnahme unter Windows noch einen realen OpenAI-Kontozugriff.

| Prüfschicht | Zweck | Stand |
| --- | --- | --- |
| Release-Build | Das integrierte WPF-Programm vollständig übersetzen und veröffentlichen | Root bestanden, 0 Fehler / 0 Warnungen |
| CaptureSelfChecks | Pixelraster, negative Ursprünge, DPI-Abbildung, PNG-Roundtrip, Abbruch-/Hotkey-Schutz | Root bestanden |
| EditorSelfChecks | Getrennte Objekte, Move/Resize, Text-Reedition, Undo/Redo, Export ohne Griffe, deckende Schwärzung | Root bestanden; Schwärzungen auch über späteren Annotationen geprüft |
| OpenAiSelfChecks | Negative OAuth-/Token-/Streamfälle, sichere Rotation, lokale DPAPI-Ablage mit Testdaten | Root bestanden; nur synthetische Konten/Fake-HTTP |
| Shell/Render | Start, Bedienelemente, Diagnoseisolation und echtes WPF-Rendering | Root bestanden; Textfarben-Gegenproben und sichtbare Renderprüfung bestanden |
| Echte Windows-Bedienung | Aufnahme → Editor → Bearbeitung → Ausgabe; Abbruch und Tastatur | App gestartet (PID 21396, Titel Kairos Capture), weitere Computer-Use-Prüfung durch physisches Esc gestoppt; offen |
| Echter OpenAI-Login/OCR | Konto-Freigabe, bildfähiges Modell, OCR-Ergebnis | Nutzeranmeldung erforderlich; nicht als Offline-Erfolg behauptet |
| Hardware-Mixed-DPI | Auswahl und Ausgabe bei unterschiedlichen Monitor-Skalierungen | Nicht abgenommen |
| Korrigierter Produktkern | Nach MouseUp offenes Overlay, Rahmen-Resize/-Move, weiter editierbare Objekte, gemeinsamer Crop-Export | Neue Implementierung und Root-Abnahme in Arbeit |

## Werkzeug und Reproduktion

Für den aktuellen Bau wird das vorhandene private .NET-SDK `10.0.401` unter `C:/Users/Axel_desktop/.t3/projects/clockify/.dotnet/dotnet.exe` ausschließlich ausgeführt und gelesen. Es wird dort nichts verändert. Die globale SDK-/Runtime-Konfiguration bleibt unverändert. Zwei Versuche, eine neue SDK-Archivkopie zu übertragen, lieferten keine Nutzdaten; der Übertragungsweg wurde beendet. Das vorhandene SDK genügt für den Bau.

Der folgende Root-Aufruf wurde erfolgreich ausgeführt:

```powershell
.\scripts\build.ps1 -DotnetPath 'C:\Users\Axel_desktop\.t3\projects\clockify\.dotnet\dotnet.exe'
```

Die Veröffentlichung liegt unter `artifacts/KairosCapture-win-x64/`; maschinenlesbare Prüfberichte (`self-test.json`, `passed=true`, Exit 0) und die echte WPF-Vorschau (`main-window.png`, 1440 × 840) unter `test-results/`. Die erzeugten Binärdateien und Diagnosebilder sind bewusst nicht versioniert. Der geprüfte Stand bearbeitet im Hauptfenster nach der Auswahl; Sunnys Bearbeitung im eingefrorenen Auswahl-Overlay ist nicht nachgebaut und die Anwendung nicht als verhaltensgleich abgenommen.

## Fehlerzählung

Capture- und OAuth-Baselinefälle wurden nach den jeweiligen ersten Ursachenfixes beim Writer und anschließend in den integrierten Root-Suiten bestanden. Hinzu kam ein einmal reproduzierter OAuth-A→B→A-Abbruchfall, ebenfalls korrigiert und bestanden. Beim Theme-Textkontrast trat die Sollverletzung zweimal auf; die Patches wurden gestoppt und ein frischer Sol-max-Diagnoseagent beauftragt. Sein isolierter WPF-Harness belegte die fehlende implizite Style-Anwendung auf MainWindow sowie den schwarzen Systemstyle eines internen ComboBox-ToggleButtons. Der Grid-Vererbungspatch und die Foreground-Weitergabe im ComboBox-Template bestehen die Root-Gegenproben. Keine weitere Eskalationsstufe erforderlich.
