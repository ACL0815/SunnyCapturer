# Kairos Capture: Bau und Abnahme

Stand 18.10.2026, Version 0.2.0. **Der korrigierte Overlay-Kern ist implementiert, integriert und von Root offline geprüft.** Der erste Release verfehlte den Produktkern durch den automatischen Wechsel ins Hauptfenster. Die [Korrektur](../plans/2026-10-18-live-overlay-correction.md) hält die Bearbeitung im offenen Aufnahme-Overlay und erhält nachträgliche Rahmenänderungen sowie einzelne Markierungsobjekte. Praktische Windows-Bedienung, Mixed-DPI und echter OpenAI-Zugriff bleiben offen.

## Prüfgrenzen

Die SunnyCapturer-Untersuchung und die Nutzerbilder sind die fachliche Referenz. Kairos ist eine eigene Implementierung. Ein grüner Parser- oder Offline-Test ersetzt weder eine erfolgreiche Aufnahme unter Windows noch einen realen OpenAI-Kontozugriff.

| Prüfschicht | Zweck | Stand |
| --- | --- | --- |
| Release-Build | Das integrierte WPF-Programm vollständig übersetzen und veröffentlichen | Root bestanden, 0 Fehler / 0 Warnungen |
| CaptureSelfChecks | Pixelraster, negative Ursprünge, DPI-Abbildung, PNG-Roundtrip, Abbruch-/Hotkey-Schutz | Root bestanden |
| EditorSelfChecks | Getrennte Objekte, Move/Resize, Text-Reedition, Undo/Redo, Export ohne Griffe, deckende Schwärzung | Root bestanden; Schwärzungen auch über späteren Annotationen geprüft |
| OverlaySelfChecks | Nach MouseUp offen bleiben, acht Rahmengriffe und Move, Crop vergrößern, Objekte/History erhalten, Stiländerungen, Control-Routing, Ausgabegrenzen | Root bestanden; physische Pixelgegenproben mit synthetischem Testbild |
| OpenAiSelfChecks | Negative OAuth-/Token-/Streamfälle, sichere Rotation, lokale DPAPI-Ablage mit Testdaten | Root bestanden; nur synthetische Konten/Fake-HTTP |
| Shell/Render | Start, Bedienelemente, Diagnoseisolation und echtes WPF-Rendering | Root bestanden; Textfarben-Gegenproben und sichtbare Renderprüfung bestanden |
| Handoff | Gleiche Editorinstanz mit Crop und History, Export/Anzeige stimmen überein, vorhandenes Dokument bei Copy/Save/Cancel erhalten | Root bestanden; kein Rasterersatz des Editors |
| Echte Windows-Bedienung | Aufnahme → offenes Overlay → Rahmen-/Objektbearbeitung → Ausgabe; Abbruch und Tastatur | Frühere App gestartet (PID 21396), Computer-Use-Prüfung durch physisches Esc gestoppt. Neue native Bedienprüfung offen; in diesem Turn keine erneute Eingabesteuerung |
| Echter OpenAI-Login/OCR | Konto-Freigabe, bildfähiges Modell, OCR-Ergebnis | Nutzeranmeldung erforderlich; nicht als Offline-Erfolg behauptet |
| Hardware-Mixed-DPI | Auswahl und Ausgabe bei unterschiedlichen Monitor-Skalierungen | Nicht abgenommen |
| Korrigierter Produktkern | Nach MouseUp offenes Overlay, Rahmen-Resize/-Move, weiter editierbare Objekte, gemeinsamer Crop-Export | Implementiert, Root-Diffprüfung, sechs Offline-Suiten und echte WPF-Sichtprüfung bestanden; reale Bedienabnahme offen |

## Werkzeug und Reproduktion

Für den aktuellen Bau wird das vorhandene private .NET-SDK `10.0.401` unter `C:/Users/Axel_desktop/.t3/projects/clockify/.dotnet/dotnet.exe` ausschließlich ausgeführt und gelesen. Es wird dort nichts verändert. Die globale SDK-/Runtime-Konfiguration bleibt unverändert. Zwei Versuche, eine neue SDK-Archivkopie zu übertragen, lieferten keine Nutzdaten; der Übertragungsweg wurde beendet. Das vorhandene SDK genügt für den Bau.

Der folgende Root-Aufruf wurde erfolgreich ausgeführt:

```powershell
.\scripts\build.ps1 -DotnetPath 'C:\Users\Axel_desktop\.t3\projects\clockify\.dotnet\dotnet.exe'
```

Die korrigierte Veröffentlichung liegt unter `artifacts/KairosCapture-0.2.0-win-x64/`. Der ältere Ausgabeordner `KairosCapture-win-x64` gehört zu Version 0.1 und ist nicht die korrigierte Fassung. Der maschinenlesbare Prüfbericht (`test-results/self-test.json`, sechs Suiten, `passed=true`, Exit 0) und die echten WPF-Vorschauen (`overlay.png`, 1280 × 800; `main-window.png`, 1440 × 840) wurden von Root erzeugt und angesehen. Binärdateien und Diagnosebilder sind bewusst nicht versioniert.

Beim Crop-Wechsel bleiben das vollständige eingefrorene Pixelraster, dieselben Markierungsobjekte und ihre History erhalten. Copy/Save exportieren nur den aktuellen Ausschnitt. Das Hauptfenster öffnet nur nach ausdrücklicher Werkbank-/OCR-Aktion. Die genaue reale Bedienfolge und ihre noch offenen Prüfschritte stehen in [OVERLAY-ABNAHME.md](OVERLAY-ABNAHME.md); eine vollständige Verhaltensgleichheit mit SunnyCapturer wird nicht behauptet.

## Fehlerzählung

Capture- und OAuth-Baselinefälle wurden nach den jeweiligen ersten Ursachenfixes beim Writer und anschließend in den integrierten Root-Suiten bestanden. Hinzu kam ein einmal reproduzierter OAuth-A→B→A-Abbruchfall, ebenfalls korrigiert und bestanden. Beim Theme-Textkontrast trat die Sollverletzung zweimal auf; die Patches wurden gestoppt und ein frischer Sol-max-Diagnoseagent beauftragt. Sein isolierter WPF-Harness belegte die fehlende implizite Style-Anwendung auf MainWindow sowie den schwarzen Systemstyle eines internen ComboBox-ToggleButtons. Der Grid-Vererbungspatch und die Foreground-Weitergabe im ComboBox-Template bestehen die Root-Gegenproben. Keine weitere Eskalationsstufe erforderlich.

Die fehlende Änderung des Stils bereits ausgewählter Objekte wurde vor dem Overlay-Fix einmal rot reproduziert, korrigiert und anschließend von Root bestanden. Der erste Overlay-Harness verglich eine berechnete Formbreite von `110.00000000000001` mit exakt `110`; die Behauptung wurde auf eine kleine numerische Toleranz korrigiert. Der Shell-Writer hatte denselben Zwischenstand eingefroren und meldete denselben Befund. Das sind keine zwei Produktfehler oder unabhängigen Wiederholungen. Im integrierten Root-Lauf besteht die Resize-Gegenprobe gemeinsam mit den exakten Pixel-/History-Gegenproben.
