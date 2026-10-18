# Kairos Capture: Bau und Abnahme

Stand 18.10.2026. Status: **Integration läuft; noch keine Gesamtfreigabe.**

## Prüfgrenzen

Die SunnyCapturer-Untersuchung und die Nutzerbilder sind die fachliche Referenz. Kairos ist eine eigene Implementierung. Ein grüner Parser- oder Offline-Test ersetzt weder eine erfolgreiche Aufnahme unter Windows noch einen realen OpenAI-Kontozugriff.

| Prüfschicht | Zweck | Stand |
| --- | --- | --- |
| Release-Build | Das integrierte WPF-Programm vollständig übersetzen und veröffentlichen | Root ausstehend |
| CaptureSelfChecks | Pixelraster, negative Ursprünge, DPI-Abbildung, PNG-Roundtrip, Abbruch-/Hotkey-Schutz | Teilprüfung beim Writer grün; Root ausstehend |
| EditorSelfChecks | Getrennte Objekte, Move/Resize, Text-Reedition, Undo/Redo, Export ohne Griffe, deckende Schwärzung | Teilprüfung beim Writer grün; Root ausstehend |
| OpenAiSelfChecks | Negative OAuth-/Token-/Streamfälle, sichere Rotation, lokale DPAPI-Ablage mit Testdaten | In Arbeit |
| Shell/Render | Start, Bedienelemente, Diagnoseisolation und echtes WPF-Rendering | Teilprüfung beim Writer; Root ausstehend |
| Echte Windows-Bedienung | Aufnahme → Editor → Bearbeitung → Ausgabe; Abbruch und Tastatur | Root ausstehend |
| Echter OpenAI-Login/OCR | Konto-Freigabe, bildfähiges Modell, OCR-Ergebnis | Nutzeranmeldung erforderlich; nicht als Offline-Erfolg behauptet |
| Hardware-Mixed-DPI | Auswahl und Ausgabe bei unterschiedlichen Monitor-Skalierungen | Nicht abgenommen |

## Werkzeug und Reproduktion

Für den aktuellen Bau wird das vorhandene private .NET-SDK `10.0.401` unter `C:/Users/Axel_desktop/.t3/projects/clockify/.dotnet/dotnet.exe` ausschließlich ausgeführt und gelesen. Es wird dort nichts verändert. Die globale SDK-/Runtime-Konfiguration bleibt unverändert. Zwei Versuche, eine neue SDK-Archivkopie zu übertragen, lieferten keine Nutzdaten; der Übertragungsweg wurde beendet. Das vorhandene SDK genügt für den Bau.

Die Builddateien werden vom Shell-Writer fertiggestellt. Erwarteter Root-Aufruf:

```powershell
.\scripts\build.ps1 -DotnetPath 'C:\Users\Axel_desktop\.t3\projects\clockify\.dotnet\dotnet.exe'
```

Die Veröffentlichung liegt anschließend unter `artifacts/KairosCapture-win-x64/`; maschinenlesbare Prüfberichte und die echte WPF-Vorschau unter `test-results/`. Die erzeugten Binärdateien und Diagnosebilder sind bewusst nicht versioniert.

## Fehlerzählung

Capture-Baseline: Hotkey im gesperrten Fenster, Hotkey während paralleler Aufnahme und fehlender CancellationToken-Einstieg jeweils einmal reproduziert; erster Ursachenfix laut Writer mit 7/7 Checks bestanden. OAuth-Baseline: Callback mit UserInfo, abgebrochener SSE-Stream vor einem späteren Completed, Tokenrotation bei Aufrufer-Abbruch und Guthabenfehlermeldung jeweils einmal reproduziert; Korrektur in Arbeit. Diese Befunde werden erst nach Root-Prüfung als abgeschlossen bewertet. Kein wiederholt fehlgeschlagener Ursachenfix wurde bisher gemeldet.
