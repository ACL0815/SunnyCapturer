# Aufnahme-Overlay protokollieren

Aktiviere in den Einstellungen unter **System** die Option **Diagnose: Aufnahme-Overlay protokollieren**. Sie ist standardmäßig aus und gilt ab der nächsten Aufnahme. Lass sie für den nächsten problematischen Aufnahmevorgang eingeschaltet und drücke bei noch offenem Overlay **Strg+Umschalt+F12**, um einen zusätzlichen aktuellen Zustand zu speichern. Öffne anschließend über **Protokollordner öffnen** die Datei `overlay-diagnostics.log` und gegebenenfalls `overlay-diagnostics.log.previous`. Schalte die Option nach der Diagnose wieder aus, um die zusätzliche synchrone Protokollierung zu beenden.

## Dateien und Erfassung

Bei ausgeschalteter Option bleibt die bisherige Anomalieprotokollierung in `application.log` erhalten. Sie schreibt beim Schließen eines auffälligen Overlays höchstens die letzten 20 Einträge. Bei eingeschalteter Option beginnt jede neue Aufnahme synchron mit `Created`, auch ohne Auswahl oder Präsentationsquelle. Regions-, Gesten-, Sichtbarkeits-, Layout-, Render- und Ausgabestatusänderungen erreichen den getrennten On-Stream bereits vor dem Schließen. Unveränderte Pointer- und Layoutcallbacks erzeugen keine zusätzlichen vollständigen Snapshots.

Das Ziel ist `%LOCALAPPDATA%\KairosCapture\logs`. Ein gesetzter `KAIROS_DIAGNOSTIC_LOG_DIRECTORY` gilt auch für den Ordnerbutton und den On-Writer. Ein ungültiges explizites Ziel wird nicht durch den persönlichen Logordner ersetzt. Jede Zeile bleibt einschließlich UTF-8-Zeilenende innerhalb des Segmentlimits von 1 MiB. Vor einem überschreitenden Append rotiert der Writer; er behält genau eine Vorgängerdatei. Beide Segmente zusammen benötigen höchstens 2 MiB, sofern keine fremde Anwendung diese Dateien verändert. Größenprüfung, Rotation und Append verwenden gemeinsam eine benannte Prozesssperre mit höchstens 100 ms Wartezeit pro Zeile.

## Feldreferenz

Jede JSONL-Zeile besitzt eine feste Hülle und ein skalares `value`-Objekt. Ein Snapshot besteht aus mehreren Zeilen. `part` beginnt bei 0; `parts` enthält die Zahl einschließlich der abschließenden `Complete`-Zeile. Ein Monitor und ein Griff erhalten jeweils eigene Zeilen. Jeder Snapshot enthält seine Monitor- und Zustandswerte erneut.

| Felder oder Zeilentyp | Bedeutung |
|---|---|
| `schema`, `run`, `build`, `process`, `processStartUtc`, `capture` | Schema 1, erzeugte Laufkennung, Assembly-MVID als Buildidentität, Prozess und Aufnahme. Die Capture-ID ist nur zusammen mit der Laufkennung eindeutig. |
| `seq`, `ticks`, `frequency`, `utc`, `event`, `source`, `data` | Fortlaufende Snapshotnummer pro Aufnahme, monotone Uhr samt Frequenz, UTC, feste Ereignis- und Herkunftswerte sowie ein numerischer Ereigniscode. |
| `part`, `parts`, `writeFailures`, `lastWriteCode` | Teilezuordnung und kumulative Schreibfehler mit letztem numerischem Fehlercode. Fehlende Teile und Rotationslücken bleiben anhand der Nummern erkennbar. |
| `State` | Chrome-Sollzustand, Rahmengeste, Griff, Endgrund, Werkzeug, Visualrevision, letzter Szenenpunkt, Aufnahme-, Editor-, Aktivierungs-, Capture- und Ausgabestatus. `selection*` ist die aktuelle Auswahl. |
| `renderNumber`, `renderTicks`, `renderRevision`, `renderChrome`, `renderSelection*`, `submittedHandles` | Zuletzt tatsächlich ausgeführter `FrameChrome.OnRender` samt damaliger Auswahl und Anzahl eingereichter Griffrechtecke. Eine spätere Auswahländerung überschreibt diese Beobachtung nicht. |
| `Element` | Szene, Toolbar, Chip und FrameChrome. `wanted` bezeichnet das Sichtbarkeitssoll. `visibility`, `isVisible`, `loaded`, `hitTestVisible`, `effectiveOpacity`, `measureValid`, `arrangeValid`, `actual*`, `desired*` und `render*` sind getrennte WPF-Messwerte. |
| `canvas*`, `calculated*`, `calculatedRevision`, `calculatedCurrent`, `observedScene*` | Canvas-Position, zuletzt berechnete Szenengeometrie mit Aktualität und unabhängig beobachtete Geometrie nach visueller Transformation. Alte Platzierungen werden nicht als aktuelle Berechnung ausgegeben. |
| `expectedScreen*`, `measuredScreen*`, `toClient*` | Sollabbildung aus Desktopursprung plus Szene. Die gemessene Abbildung verwendet unabhängig Element-zu-Client-Matrix, `TransformToDevice` und nativen Clientursprung. Screenwerte sind physische Pixel; Szenenwerte sind Rastereinheiten. |
| `Dpi` | WPF-DPI für Fenster und Szene, `sceneToClient*`, `toDevice*` sowie beim DPI-Ereignis die alten und neuen WPF-Skalen in `previousWidth/Height` und `currentWidth/Height`. |
| `NativeWindow` | Fensterrechteck, lokales Clientrechteck, absolute Client-Ecken, native Sichtbarkeit, Iconic-Status, Fenster-DPI, Prozess- und Thread-Awareness, PMv2-Status und Monitorabfragestatus. Keine HWND-Werte. |
| `Handle` | Acht aktuelle Sollgriffe und davon getrennt die zuletzt tatsächlich eingereichten Rechtecke. Enthält Renderzuordnung, Elternsichtbarkeit, Opazität sowie Szenenclip- und Client-Schnittrechtecke. |
| `Monitor` | Numerischer Index, Primary-Flag, physische Monitorfläche, unabhängige Workarea und effektive X-/Y-DPI. Negative Ursprünge bleiben erhalten. Monitor-DPI müssen im Kontext der aufgezeichneten Awareness gelesen werden. |
| `Intersection` | Je Element oder Griff und Monitor getrennte Schnittrechtecke mit Monitorfläche und Workarea, jeweils für Soll und gemessenen Stand. Keine neue Platzierungsregel. |
| `Clip` | Bekannte Vorfahren mit Tiefe, Sichtbarkeit, Opazität, ClipToBounds, Bounds und explizitem Clip. Tiefe -1 bezeichnet den Toolbarinhalt. `BoundsOnly` kennzeichnet insbesondere gerundete oder andere nicht exakt ausgewertete Clips. |
| `*Status`, `*Code` | `Known`, `Empty`, `NotObserved`, `NoPresentationSource`, `TransformFailure` oder `NativeFailure`. Fehlende Messungen erhalten keine erfundenen Nullkoordinaten. Legitime gemessene Nullgrößen bleiben `Known`. |

## Aussagegrenzen

`OnRender` belegt eingereichte WPF-Zeichenbefehle, keine sichtbaren Compositorpixel. Ein aktueller allgemeiner Revisionszähler allein beweist keine veralteten Griffrechtecke; vergleiche außerdem Auswahl, Renderzustand und konkrete Geometrie. Clip-Bounds sind bei `BoundsOnly` nur eine Eingrenzung und kein Nachweis vollständiger Sichtbarkeit. Offscreen-Selbsttests belegen weder reale Mixed-DPI-Fensterplatzierung noch physische Eingaben, Aktivierung, Überdeckung oder Desktopkomposition.

Rotation kann bei langen Sitzungen ältere Teile entfernen. Ein Schreibfehler, ein Sperrtimeout, ein blockierter Dispatcher oder ein Prozessabbruch verhindert gegebenenfalls weitere Zeilen. `Complete` beendet die erzeugte Teilserie; die tatsächliche Vollständigkeit ergibt sich erst aus vorhandenen Teilen und Fehlerzähler. Synchrone Ausgabe kann das Timing eines sporadischen Fehlers beeinflussen.

Die Diagnose liest keine Annotationen, OCR-Texte, Titel, Pfade, Tokens, Bilddaten oder Exception-Messages. Sie erzwingt kein Layout und erzeugt kein HWND. Die Option repariert weder Gesten noch Layout oder Platzierung. Der reale Fall und die Belegkette stehen im [Ursachen-Ledger](OVERLAY-URSACHEN-LEDGER.md).

## Designauswahl

Die verbindliche Auswahl steht unter `C:\Users\AXEL_D~1\AppData\Local\Temp\kairos-overlay-r12-20261021\synthesis.md`. Basis ist `design-sol.md`; `design-judge.md` bewertet dessen Vertrag mit 14/15 gegenüber 9/15 für den anderen Entwurf. Übernommen wurde der getrennte injizierbare On-Sink. Die Umsetzung verwendet kleine konkrete Records ohne generisches Probe-Framework; der unveränderte Off-Puffer verbleibt im Overlay.
