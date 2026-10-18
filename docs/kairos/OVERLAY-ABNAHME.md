# Kernabnahme: Screenshot bleibt direkt bearbeitbar

Diese Abnahme ersetzt die unzureichende Prüfung des ersten separaten Werkbank-Ablaufs. Der Nutzer verlangt ausdrücklich Bearbeitung direkt auf dem aufgenommenen Bildschirm, nachträgliche Rahmenänderung und weiter editierbare Markierungen.

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
| Explizit OCR oder Werkbank wählen | Derselbe Editor samt Objekten, Crop und History wird übergeben. Kopieren, Speichern und OCR verwenden denselben aktuellen Crop-Export. |
| OCR/Werkbank wählen, während ein altes Werkbankdokument ungesichert ist; Rückfrage oder Speicherdialog abbrechen | Das aktuelle Overlay bleibt mit demselben Editor, Ausschnitt und History offen. Dialoge gehören zum Overlay; die neue Aufnahme wird nicht verworfen. |
| Verzögerte Handoff-Freigabe, erneuter Klick, externe Schließung | Nur ein Freigabeversuch läuft. Währenddessen entstehen keine parallelen Ausgaben oder Gesten. Ein nach Schließung zurückkehrender Callback aktiviert kein Dokument. |
| In der Werkbank ein Objekt am Ausschnittrand ziehen/skalieren oder neuen Text setzen | Neue Eingaben bleiben im sichtbaren Ausschnitt. Die vollständige Aufnahme und bereits außerhalb liegende alte Objekte bleiben unverändert erhalten. |
| Escape bei aktiver Text-, Zeichen- oder Rahmengeste | Nur die aktuelle Eingabe/Geste wird verworfen; der vorherige Ausschnitt und ältere Markierungen bleiben erhalten. Ein weiteres Escape im Idle beendet die Sitzung. |
| Abbrechen / Rechtsklick oder Escape ohne aktive Geste | Sitzung endet ohne Ausgabe. Keine Eingabe wird versehentlich als neuer Auswahlrahmen aufgefasst. |

## Belegarten

Root hat den integrierten Stand am 18.10.2026 mit SDK 10.0.401 gebaut: **0 Warnungen, 0 Fehler; sechs Suiten bestanden**, einschließlich `OverlaySelfChecks` und der Shell-Handoff-Prüfungen. Die Pixelgegenproben vergleichen den direkt gerenderten Crop mit den entsprechenden Pixeln des vollständigen Editors. Sie prüfen alle acht Rahmengriffe, Vergrößerung über den ersten Ausschnitt hinaus, Objektidentität trotz zeitweiliger Ausblendung, Move/Resize, Text-Reedition, Stiländerungen und Undo/Redo. Handoff-Prüfungen bestätigen dieselbe Editorinstanz und denselben Crop sowie den Erhalt des bisherigen Dokuments bei Copy/Save/Cancel.

`--render-overlay-preview` rendert die echte WPF-Oberfläche mit einem lokalen synthetischen Testbild. Root hat `test-results/overlay.png` (1280 × 800) selbst visuell geprüft: Maskierung, acht goldene Rahmengriffe, gesonderte Objektgriffe und lesbare dunkle Werkzeugleiste. Die Werkbank-Vorschau (`main-window.png`, 1440 × 840) wurde ebenfalls geprüft. Das ist ein echtes Offscreen-WPF-Rendering; es ist kein Nachweis physischer Mausbedienung.

Die physische Computer-Use-Steuerung wurde durch Esc beendet und wird in diesem laufenden Turn nicht erneut benutzt. Die obige vollständige reale Maus-/Tastaturfolge gilt deshalb bis zu einer tatsächlichen Ausführung als **offen**, auch wenn Offline-Prüfungen und Vorschau grün sind. Mixed-DPI und echte OpenAI-OCR bleiben separate Laufzeitprüfungen.

## Ergänzung aus dem Opus-Review

Der [Opus/Ponytail-Gegencheck](../plans/2026-10-18-opus-overlay-review.md) bestätigte die Koordinaten-, Objekt-/History- und Routingstruktur. Er fand jedoch den möglichen Verlust der neuen Aufnahme bei abgelehnter Handoff-Rückfrage sowie zwei kleinere Grenzfälle bei Werkbank-Eingabegrenzen und Escape während einer Geste. Die [Root-Entscheidung für diese drei Korrekturen](../plans/2026-10-18-overlay-review-fixes.md) wurde in Code-Commit `67711de` umgesetzt und anschließend von Root geprüft.

Der erneute integrierte Release-/Publish-Build sowie die veröffentlichte EXE bestehen alle sechs Suiten einschließlich der neuen Gegenproben (18.10.2026, `published/self-test.json` um 14:49:52 UTC, Exit 0). Kontrollierte sofortige und verzögerte Freigaben prüfen Ablehnung, Ausnahme, Wiederholung, doppelte Übergabe, gesperrte Ausgaben/Gesten und externe Schließung mit späterer Zustimmung. Der Editor bleibt nach Ablehnung derselbe; Crop, Pixel und beide History-Stacks bleiben erhalten. Die Shell nimmt eine bestätigte Übergabe ohne erneute Rückfrage an.

Weitere Gegenproben bestehen für alle vier Werkbankgrenzen, Resize, neue Texte am Croprand und unveränderte ältere Außenobjekte. Escape ist für Zeichenentwurf, Objekt-Move/Resize, Rahmen-Move/Resize und Text geprüft; die Raw-Capture-API behält ihren bisherigen unmittelbaren Escape-Abbruch. Root hat das neue WPF-Overlay-Rendering selbst angesehen. Diese zusätzlichen Offline-Prüfungen ersetzen weiterhin keine reale Dialog-/Maus-/Tastaturabnahme.
