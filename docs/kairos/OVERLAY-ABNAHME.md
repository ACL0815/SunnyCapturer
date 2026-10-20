# Kernabnahme: Screenshot bleibt direkt bearbeitbar

Stand 19.10.2026, Version 0.4.0. Kairos startet im Tray; MainWindow enthält die Einstellungen. Die Bedienfolge verlangt Bearbeitung direkt auf dem aufgenommenen Bildschirm, nachträgliche Rahmenänderung und weiter editierbare Markierungen. Die frühere Werkbank und ihre Handoff-Rückfragen sind entfallen.

## Reproduzierbare Bedienfolge

| Schritt | Mussverhalten |
| --- | --- |
| Aufnahme starten, Bereich aufziehen, Maus loslassen | Eingefrorener Bildschirm und Auswahl bleiben sichtbar. Die Werkzeugleiste erscheint am Ausschnitt; es erfolgt kein automatischer Wechsel ins Hauptfenster. |
| Jeden der acht Rahmengriffe ziehen | Ausschnitt lässt sich auch nach dem Loslassen verkleinern und vergrößern. Die Abmessungen entsprechen dem aktuellen Rahmen. |
| Rahmen über seine ursprünglichen Grenzen vergrößern | Zusätzliche Pixel stammen aus derselben vollständigen Bildschirmaufnahme. Die Auswahl ist nicht auf das erste Crop-Bitmap beschränkt. |
| Leertaste gedrückt halten und Rahmen ziehen | Der Ausschnitt lässt sich innerhalb des aufgenommenen Desktops verschieben. |
| Rechteck, Pfeil, Text und Schwärzung setzen | Objekte erscheinen unmittelbar an der gewählten Stelle im Aufnahme-Overlay. |
| Auswahlwerkzeug: vorhandenes Objekt greifen und verschieben; anschließend skalieren | Dasselbe Objekt wird verändert. Formen haben Griffe, Text bleibt proportional skalierbar. |
| Bestehenden Text doppelklicken; Inhalt ändern; Farbe/Strichstärke ändern | Die Markierung bleibt als eigenes Objekt bearbeitbar. TextBox-Tasten und normale Leerzeichen bleiben Texteingaben. |
| Rahmen verkleinern und wieder vergrößern | Zuvor angelegte Objekte und ihre History bleiben erhalten, auch wenn sie zeitweise außerhalb des Ausschnitts lagen. |
| Mehrfach Undo und Redo | Objektänderungen werden nachvollziehbar zurückgenommen und wiederhergestellt; kein stilles Rastern oder Verlust der einzelnen Objekte. |
| Kopieren bzw. PNG speichern | Exakt der aktuelle Ausschnitt mit den aktuellen Markierungen wird ausgegeben. Maske, Rahmen, Griffe und Werkzeugleiste erscheinen nicht im Bild. Das Hauptfenster wird dadurch nicht geöffnet. |
| Speichern oder Strg+S wählen und den Dialog abbrechen | Das Overlay bleibt mit demselben Editor, Ausschnitt, ausgewählten Objekt und Undo/Redo offen. Direktspeichern erfolgt nur bei aktiviertem `SaveWithoutDialog`; Speichern beim Kopieren bleibt direkt. |
| OCR wählen oder die OCR-Aufnahme aus dem Tray starten | Der aktuelle Crop wird mit Markierungen exportiert; das Overlay schließt und ein separates Ergebnisfenster öffnet. Der Text bleibt bearbeitbar, die feste Sprachauswahl erlaubt Übersetzung. |
| Anpinnen oder I wählen | Der markierte Ausschnitt erscheint rahmenlos im Vordergrund. Mehrere Pins sind möglich; sie lassen sich bewegen und proportional skalieren. P bleibt das Stiftwerkzeug. |
| Letzte Aufnahme fortsetzen | Derselbe eingefrorene Bildschirm, Rahmen, Editor und beide History-Stacks werden wieder geöffnet. |
| Escape bei aktiver Text-, Zeichen- oder Rahmengeste | Nur die aktuelle Eingabe/Geste wird verworfen; der vorherige Ausschnitt und ältere Markierungen bleiben erhalten. Ohne aktive Geste wird zunächst ein ausgewähltes Objekt abgewählt, danach beendet Escape die Sitzung. |
| Deaktivierung oder Capture-Verlust bei losgelassener linker Taste | Geste an der aktuellen bzw. letzten bekannten Cursorposition abschließen. Auswahl und Rahmenbedienung bleiben erhalten. OCR startet wie bei normaler Freigabe genau einmal; Raw nimmt die Auswahl an. |
| Deaktivierung bei gedrückter linker Taste und gehaltener Capture | Geste weiterführen und das spätere MouseUp normal verarbeiten. |
| Deaktivierung oder Capture-Verlust bei gedrückter linker Taste ohne Capture | Geste abbrechen und die vorherige Auswahl mit konsistenter Rahmenbedienung wiederherstellen. Keine OCR starten. |
| Abbrechen / Rechtsklick oder Escape ohne aktive Geste und Objektauswahl | Sitzung endet ohne Ausgabe. Keine Eingabe wird versehentlich als neuer Auswahlrahmen aufgefasst. |

## Aktuelle Offline-Prüfung

`scripts/build.ps1` baut Release, führt neun Suiten (`capture`, `editor`, `overlay`, `openai`, `shell`, `settings`, `hotkey`, `ocr-result`, `tray`) aus und rendert `overlay.png`, `settings-window.png`, `ocr-result.png` sowie `tray-menu.png` unter `test-results`. Die Aufruf- und Startdetails stehen in [KAIROS.md](../../KAIROS.md). Offline-Prüfungen und Offscreen-Vorschauen belegen keine reale Maus-/Tastaturbedienung, Mixed-DPI-Hardware oder einen echten OpenAI-Login.

Die Ergänzung vom 20.10.2026 prüft die echten MouseMove- und MouseUp-Handler mit synthetischen WPF-Events. Injizierte Cursor-, Tasten- und Capture-Funktionen prüfen auch eine synchrone Capture-Freigabe sowie OCR- und Raw-Abschlüsse. Beim Schließen wird die Geste bereits in `OnClosing` inaktiv. Capture-Verlust zwischen `Closing` und `Closed` darf keinen OCR-Abschluss auslösen. Bei ReleasedMove wird die erste danach bekannte Cursorposition verwendet, weil die ursprüngliche Loslassmeldung fehlt.

Die Spur ist ein Ringpuffer mit höchstens 20 Zeilen. Normale Aufnahmen schreiben nichts. Auffällige Gestenenden, Schließen mit aktiver Geste, unerwartet fehlende Rahmenbedienung sowie Capture- und Cursorfehler führen zu genau einem Flush beim Schließen. `Closing` bewahrt den vorherigen Zustand, `Closed` zeigt geschlossene Bedienelemente und die Geste samt Auswahl vor dem Zurücksetzen. Ein Test prüft den echten App-Log-Hook mit umgeleitetem Ziel und erkennt einen entfernten Diagnose-Guard. Für Gegenproben ist `KAIROS_DIAGNOSTIC_LOG_DIRECTORY` gesetzt; das persönliche Nutzer-Log wird dabei nicht beschrieben.

## Historische Belegarten: Version 0.2.0 vom 18.10.2026

Die folgenden Protokolle halten den damaligen Stand mit sechs Suiten und Werkbank-Handoff fest. Sie sind keine Beschreibung der aktuellen Oberfläche oder des heutigen Prüfregisters.

Root hat den integrierten Stand am 18.10.2026 mit SDK 10.0.401 gebaut: **0 Warnungen, 0 Fehler; sechs Suiten bestanden**, einschließlich `OverlaySelfChecks` und der Shell-Handoff-Prüfungen. Die Pixelgegenproben vergleichen den direkt gerenderten Crop mit den entsprechenden Pixeln des vollständigen Editors. Sie prüfen alle acht Rahmengriffe, Vergrößerung über den ersten Ausschnitt hinaus, Objektidentität trotz zeitweiliger Ausblendung, Move/Resize, Text-Reedition, Stiländerungen und Undo/Redo. Handoff-Prüfungen bestätigen dieselbe Editorinstanz und denselben Crop sowie den Erhalt des bisherigen Dokuments bei Copy/Save/Cancel.

`--render-overlay-preview` rendert die echte WPF-Oberfläche mit einem lokalen synthetischen Testbild. Root hat `test-results/overlay.png` (1280 × 800) selbst visuell geprüft: Maskierung, acht goldene Rahmengriffe, gesonderte Objektgriffe und lesbare dunkle Werkzeugleiste. Die Werkbank-Vorschau (`main-window.png`, 1440 × 840) wurde ebenfalls geprüft. Das ist ein echtes Offscreen-WPF-Rendering; es ist kein Nachweis physischer Mausbedienung.

Die physische Computer-Use-Steuerung wurde durch Esc beendet und wird in diesem laufenden Turn nicht erneut benutzt. Die obige vollständige reale Maus-/Tastaturfolge gilt deshalb bis zu einer tatsächlichen Ausführung als **offen**, auch wenn Offline-Prüfungen und Vorschau grün sind. Mixed-DPI und echte OpenAI-OCR bleiben separate Laufzeitprüfungen.

### Historische Ergänzung aus dem Opus-Review

Der [Opus/Ponytail-Gegencheck](../plans/2026-10-18-opus-overlay-review.md) bestätigte die Koordinaten-, Objekt-/History- und Routingstruktur. Er fand jedoch den möglichen Verlust der neuen Aufnahme bei abgelehnter Handoff-Rückfrage sowie zwei kleinere Grenzfälle bei Werkbank-Eingabegrenzen und Escape während einer Geste. Die [Root-Entscheidung für diese drei Korrekturen](../plans/2026-10-18-overlay-review-fixes.md) wurde in Code-Commit `67711de` umgesetzt und anschließend von Root geprüft.

Der erneute integrierte Release-/Publish-Build sowie die veröffentlichte EXE bestehen alle sechs Suiten einschließlich der neuen Gegenproben (18.10.2026, `published/self-test.json` um 14:49:52 UTC, Exit 0). Kontrollierte sofortige und verzögerte Freigaben prüfen Ablehnung, Ausnahme, Wiederholung, doppelte Übergabe, gesperrte Ausgaben/Gesten und externe Schließung mit späterer Zustimmung. Der Editor bleibt nach Ablehnung derselbe; Crop, Pixel und beide History-Stacks bleiben erhalten. Die Shell nimmt eine bestätigte Übergabe ohne erneute Rückfrage an.

Weitere Gegenproben bestehen für alle vier Werkbankgrenzen, Resize, neue Texte am Croprand und unveränderte ältere Außenobjekte. Escape ist für Zeichenentwurf, Objekt-Move/Resize, Rahmen-Move/Resize und Text geprüft; die Raw-Capture-API behält ihren bisherigen unmittelbaren Escape-Abbruch. Root hat das neue WPF-Overlay-Rendering selbst angesehen. Diese zusätzlichen Offline-Prüfungen ersetzen weiterhin keine reale Dialog-/Maus-/Tastaturabnahme.
