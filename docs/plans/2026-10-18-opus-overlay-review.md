# Opus-Gegenprüfung: persistentes Capture-Overlay (Kairos 0.2)

Stand: Worktree `t3/c1fa96c3` @ `48c1ac6`, Quellen identisch mit Root `c709851` (`git diff -- src` leer). Reine Code-Lesung entlang RegionOverlay → AnnotationCanvas → CaptureSessionResult → MainWindow, dazu Root-Report `published/self-test.json` (sechs Suiten bestanden) und `overlay.png` angesehen. Keine eigene Gegenprobe, kein Window.Show, keine native Eingabe.

## Ergebnis der drei Prüfpunkte

1. **Physische Vollkoordinaten vs. Crop: kein Blocker.** Szene = Desktopraster, Editor ohne Offset (`RegionOverlay.cs:88-95`), Zeiger = physisch minus Bounds (`:478`), Deckung pixelgenau geprüft (`:196-207`). Export immer `Flatten(Selection)` (`:436`) bzw. `Flatten(_documentRegion)` (`MainWindow.xaml.cs:130,348,381,399,610`); Werkbank-Host mit −X/−Y (`:120-124`). Self-Checks nutzen 144/192-DPI-Bitmaps, also hängt nichts an DPI-Metadaten.
2. **Objekte/History bei Rahmenänderung: kein Blocker.** `SetRegion` ändert nur Clip/Toolbar (`RegionOverlay.cs:357-375`), Gesten-Start nur EndGesture/CommitText (`:310-311`); Handoff gibt dieselbe gelöste Instanz weiter (`:422-426`, `MainWindow.xaml.cs:112-124`). **Ausnahme siehe F1** (abgelehnte Übernahme).
3. **Text-/Toolbar-Eingaben nicht gestohlen: kein Blocker.** Ahnenprüfung Toolbar/TextBox vor jeder Rahmengeste (`RegionOverlay.cs:211-216,224`), Tasten bei offenem Text komplett an die TextBox (`:279-280`), Buttons `Focusable=false` (`:156`), TextBox selbst behandelt Esc/Strg+Enter (`AnnotationCanvas.cs:661-666`).

## Findings

**F1 – Mittel – Expliziter OCR-/Werkbank-Handoff kann Overlay-Arbeit verlieren.**
Soll (Plan „Prüfung“): OCR schließt erst nach Erfolg, Handoff erhält Objekte und History. Ist: `RegionOverlay.cs:422-427` schließt bedingungslos; danach verwirft `MainWindow.xaml.cs:264-267` `result.Editor`, wenn `PrepareReplacementAsync` (`:335-340`) `false` liefert.
Repro: Werkbank mit ungespeicherten Änderungen → aufnehmen, markieren → „OCR“/„Werkbank“ → im Prompt „Abbrechen“ (oder Speichern-Dialog abbrechen) → Aufnahme samt Objekten/History weg, Overlay geschlossen. Ungetestet (`AppDiagnostics.cs:202` prüft nur Cancelled/Copied/Saved).
Root hat F1 bestätigt (erstes Auftreten) und lässt eine asynchrone Shell-Freigabe vor dem Schließen umsetzen. Für diesen Fix aus dem bestehenden Code zu beachten: `ShowSavePrompt` holt das versteckte Hauptfenster per `ShowFromTray` nach vorn (`MainWindow.xaml.cs:355`); `SaveImage` öffnet den Datei-Dialog mit dem MainWindow als Owner (`:382`), sodass er hinter dem Topmost-Overlay landen kann; `Complete` ist während der Freigabe nicht gegen einen zweiten Aufruf bzw. Esc/Rechtsklick geschützt (`RegionOverlay.cs:415-428`).

**F2 – Niedrig – Werkbank begrenzt Eingaben auf den Desktop statt auf den Crop.**
`AnnotationCanvas.cs:512-519` (ClampPoint/ClampOffset), `:606-609` (Resize), `:637-640` (Text-Offset/-Breite) nutzen volle `Width/Height`. Mit gefangener Maus lässt sich ein Objekt in der Werkbank über den Ausschnitt hinaus ziehen; `CropCanvas ClipToBounds` (`MainWindow.xaml:70`) blendet es aus und macht es unanklickbar, nur Undo holt es zurück. Im Overlay ist das gewollt (Rahmen vergrößerbar), in der Werkbank ohne Rahmen eine Regression gegenüber Editor = Crop. Exporte bleiben WYSIWYG, also kein Datenverlust.

**F3 – Niedrig (Auslegung) – Esc während laufender Geste beendet die ganze Sitzung.**
Abnahme: „laufende Gesten werden nach ihrem aktuellen Kontext behandelt“. Ist: `HandleSessionKey` ruft bei Esc immer `Cancel()` (`RegionOverlay.cs:287`), auch mitten in Rahmen- oder Zeichengeste; `EndFrameGesture(true)` (`:340-344`) ist nur über Capture-Verlust erreichbar, `Editor.HandleKey(Escape)` (`AnnotationCanvas.cs:337-341`) im Overlay gar nicht. Esc als „diese Geste abbrechen“ verwirft alle Markierungen. Root entscheidet, ob das so gewollt ist.

## Beobachtungen ohne Finding-Status

- Rahmengriffe: fester Trefferradius 7 phys. px, gezeichnet 8 px (`RegionOverlay.cs:409,507`), ohne DisplayUnit-Skalierung; Vorrang vor Werkzeugen/Objektgriffen (`:305`). Bei 200 % klein, an Crop-Ecken/-Mitten startet Zeichnen als Rahmen-Resize. Gehört zur Mixed-DPI-Abnahme.
- Paletten/Dicken von Overlay und Werkbank weichen ab; `SyncDrawingSettings` fängt das ab (rein kosmetisch).

## Grenzen

Keine reale Maus-/Tastatur-, Mixed-DPI- oder OCR-Prüfung. Routing-Checks setzen `Source` synthetisch statt echter Hit-Tests; die Ahnenlogik trägt trotzdem. Kein Ersatz für die in `OVERLAY-ABNAHME.md` offene reale Bedienfolge. Keine wiederholte Sollverletzung, Stop-Regel nicht ausgelöst.
