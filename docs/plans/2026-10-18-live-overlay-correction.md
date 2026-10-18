# Korrektur: Auf dem aufgenommenen Bildschirm bearbeiten

Der Nutzer hat den Kern ausdrücklich präzisiert: Auswahlrahmen **nach** der Aufnahme ändern; bestehende Markierungen **im Aufnahme-Overlay** weiter verändern. Ein separates Hauptfenster erfüllt diesen Kern nicht. Die bisherige Umsetzung ist in diesem Punkt falsch und nicht abgenommen. Die frühere Opus-Einschränkung, nachträgliche Bereichsänderung aus dem MVP zu nehmen, gilt nicht.

## Verbindlicher Ablauf

Strg+Alt+A oder Aufnahme → Bildschirm einfrieren → Bereich aufziehen → Overlay bleibt offen → Rahmen an acht Griffen verändern oder mit Leertaste ziehen → Werkzeuge direkt im Overlay → vorhandene Objekte auswählen, verschieben, skalieren, umstylen, Text erneut bearbeiten → denselben aktuellen Ausschnitt kopieren/speichern oder gezielt an OCR übergeben. Hauptfenster öffnet nur bei ausdrücklicher OCR-/Werkbankaktion.

## Ein Editor, physische Koordinaten

`AnnotationCanvas` verwendet das gesamte eingefrorene Desktopbild in physischen Pixelkoordinaten; im Overlay ist nur der aktuelle Auswahlbereich sichtbar. Der Auswahlrahmen verändert den Ausschnitt, ohne vorhandene Annotationen zu rastern oder neu anzulegen. `Flatten(Int32Rect crop)` rendert ausschließlich den aktuellen Ausschnitt, ohne Auswahlgriffe/Werkzeugleiste. Bestehendes `Flatten()` bleibt kompatibel. Die Editor-History bleibt bei Bereichsänderungen erhalten. Schwärzungen bilden die letzte, deckende Ebene.

## Integrationsvertrag

Capture-/Editor-Writer besitzt ausschließlich `src/KairosCapture/Capture/**` und `Editor/**`.

```csharp
public enum CaptureCompletion { Cancelled, Copied, Saved, Workbench, Ocr }
public sealed record CaptureSessionResult(CaptureCompletion Completion, BitmapSource? Image);
public static Task<CaptureSessionResult> CaptureService.CaptureEditedRegionAsync(Window owner, CancellationToken cancellationToken = default);
public BitmapSource AnnotationCanvas.Flatten(Int32Rect crop);
```

Bestehende rohe Capture-APIs bleiben kompatibel für Checks und vorhandene Aufrufer. Die neue Sitzung hält das Overlay offen; Copy/Save verwenden den Editor-Export und beenden die Sitzung. Abbruch stellt die vorherige Owner-Sichtbarkeit her; Copy/Save lassen das Hauptfenster im Tray. Workbench/Ocr liefern das fertige Bitmap zur ausdrücklichen Übergabe. Kein automatisches Öffnen der Werkbank nach Loslassen der Maus.

Shell-Writer besitzt ausschließlich `MainWindow.xaml*`, `App.xaml.cs`, `AppDiagnostics.cs`. Er verdrahtet die neue Sitzung und deren Status; kein eigener Editor/keine parallele Overlaylogik. Root besitzt Projekt-/Build-/Startdateien, Dokumentation und Integration. Jeder Writer arbeitet im eigenen Worktree. Services bleiben unverändert.

## Prüfung

- Auswahl nach MouseUp bleibt aktiv; acht Griffe und Bereichsverschiebung ändern den späteren Export.
- Markierung → Auswahl → Verschieben → Resize → Stiländerung → Text-Reedition → Undo/Redo im selben Overlay.
- Rahmenänderung erhält vorhandene Objekte und History; Export hat exakt die aktuellen physischen Maße und keine Handles.
- Escape und Rechtsklick brechen ab; TextBox erhält Textbearbeitungs-Tasten; Copy/Save/OCR schließen erst nach Erfolg.
- Offline-STA-Checks und echte offscreen gerenderte Overlay-Vorschau. Kein Benutzerkonto, kein Cloud-Upload und keine Clipboard-Modifikation in Offline-Checks.
- Computer Use wurde durch physisches Esc gestoppt; in diesem laufenden Turn keine erneute native Eingabesteuerung. Das ist kein Grund, die fehlende Bedienprüfung als bestanden auszugeben.

Beim zweiten Auftreten derselben Sollverletzung nach Korrektur stoppen und Root melden. Bereits mehrfach beschriebene unveränderte Symptome zählen einmal. Keine unbestätigten Fixschleifen.
