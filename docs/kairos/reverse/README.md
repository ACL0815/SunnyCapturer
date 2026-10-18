# SunnyCapturer als Grundlage für Kairos Capture

Untersuchung vom 18.10.2026. Der jüngste Nutzerauftrag priorisiert zunächst die installierte EXE mit `reverse-engineer-anything`. Dieser Ordner dokumentiert diese Untersuchung. **Ein gebautes Kairos Capture oder ein erfolgreicher OAuth-Login werden damit nicht behauptet.** Im Ausgangscheckout `42d627e` sind die Kairos-Start-/Architekturdokumente vorhanden, der darin beschriebene `src/KairosCapture`-Anwendungscode jedoch nicht.

Die zentrale Erkenntnis ist inzwischen durch native Decompilation belegt: Die installierte SunnyCapturer-Generation besitzt einen Editor mit eigenständigen Grafikobjekten, editierbaren Textknoten und einem Undo-Stack. Aufnahme und gemeinsamer Bitmap-Ausgabepfad sind bis zu ihren Qt-Aufrufen verfolgt. Die alten v1-Quellen bilden diese aktuelle Struktur nicht zuverlässig ab. Für die gewünschte direkte Bearbeitung ist deshalb ein eigenes Objektmodell die wichtige Rekonstruktionsvorgabe.

**Nachtrag zur Overlay-Korrektur:** Der [gezielte Overlay-Vertrag](overlay-native/README.md) ordnet die vorhandenen nativen Belege für vollständigen Hintergrund, separate Auswahlkoordinaten, Szenenobjekte und Objekt-Erzeugungs-Undo neu. Die zusätzliche Maus-/Statuspfad-Vertiefung scheiterte zweimal am nativen Provider-Timeout; danach wurde die Sitzung geschlossen. Es gibt daraus keine neue erfolgreiche Decompilation. Rahmenänderung nach Annotationen und Toolbar-Persistenz werden dort ausdrücklich als noch nicht vollständig nativ belegt geführt. Die eigene Kairos-Version 0.2 setzt den vom Nutzer verlangten Ablauf inzwischen um; ihre [sechs Offline-Suiten und WPF-Renderprüfung](../ABNAHME.md) sind getrennte Implementierungsbelege und kein Ersatz für die offene reale Windows-Bedienprüfung.

## Ziel und Identität

Unverändertes Ziel: `C:\Users\Axel_desktop\AppData\Local\Programs\SunnyCapturer\SunnyCapturer.exe`.

- 3.507.712 Bytes; PE32+, x86-64.
- SHA-256: `e97cc2612b79ab154076844a13ec01cdeb70be0f14cf20675ce0e39dff81ecc8`.
- Keine Produkt-Versionsressource in der EXE festgestellt. `Qt6Core.dll` ist separat als 6.8.3.0 identifiziert; das ist keine SunnyCapturer-Versionsnummer.

Die EXE wurde nicht gepatcht. Die Analyse läuft auf der explizit vorgegebenen Installation. Historischer Quellcode und offizielle Dokumentation sind Vergleichsquellen, keine Ersatzbeweise für ausgeführten aktuellen Code.

## Befunde und Belege

| Frage | Belegter Stand | Evidenz |
| --- | --- | --- |
| Woraus besteht die Installation? | 107 Dateien, 64 PE-Dateien, zusammen 3.489.366.381 logische Bytes = 3,2497 GiB. | [Statisches Inventar](static/inventory.json), E7 im Detailbericht |
| Welches UI-System ist vorhanden? | Haupt-EXE importiert Qt6Widgets/Gui/Core/Network/Pdf und enthält 51 dekodierte Qt-Metaobjekte. | [Statischer Bericht](static/REPORT.de.md), E1/E2 |
| Welche Editorstrukturen sind vorhanden? | `ScreenShoter`, `Selector`, `EditingMenu`, `canvas::Canvas`, Grafik-/Textobjekte und Create/Move/Resize/Rotate/Delete-Kommandonamen; Imports des Qt-Undo-Stacks. | Statischer Bericht, E1–E4 |
| Ist direkte Texteingabe gestützt? | `GraphicsTextItem` und Import von `QGraphicsTextItem::setTextInteractionFlags` sind nachgewiesen. Das konkrete Fokus-/Doppelklickverhalten ist damit noch nicht beobachtet. | Statischer Bericht, E1/E4 |
| Welche UI-Aktionen sind mitgeliefert? | 13 QM-Kataloge enthalten 3.855 Nachrichten, 385 unterschiedliche Quelltextschlüssel und 25 Kontexte; darunter neun Editorwerkzeuge und benannte Shortcuts. | [Qt-Bericht](qt/BERICHT.md), [Quellenindex](qt/inventory.json) |
| Was dominiert die Dateimenge? | Paddle: 1.964.766.720 Bytes; cuDNN: 1.044.825.472 Bytes. Zusammen 86,25 % der Installation. | Statischer Bericht, E5/E7 |
| Ist lokale OCR nur ein Dateinamenverdacht? | Nein: Die OCR-DLL exportiert vier Namen, importiert Paddle/OpenCV, und passende PP-OCRv4-Modelle sind installiert. Die tatsächliche Ausführung bleibt separat nachzuweisen. | Statischer Bericht, E3/E5 |
| Welche Windows-Ressourcen liegen vor? | Zehn Ressourcen: acht Icons, eine Icon-Gruppe und ein Manifest. Beide unabhängigen Parser stimmen in Nutzdatenoffset, Länge und SHA-256 überein. | [REA-Paket](rea-evidence.json), `ev_ecbb6e599964314b0f88aa71edd01b4da40896910de4725a7c3004bd9c0ddd23` |
| Ist die aktuelle Implementierung im GitHub-Checkout? | Die v5.x-/v3.x-Verzeichnisse enthalten hier nur Hinweise auf nicht offengelegten aktuellen Code; der verfügbare v1-Code ist historische Referenz. | Statischer Bericht, E6 |

Die ausführlichen Berichte kennzeichnen **Beobachtung**, **Inferenz** und **Unbekannt**. Lokale Belegkürzel wie E1 sind keine REA-Evidence-IDs. Eine Klasse, ein importiertes API oder ein übersetzter Buttontext beweist für sich allein keinen ausgeführten Ablauf.

## Native Codepfade: jetzt belegt

Die [native Untersuchung](native/README.md) verwendet Ghidra 12.1.4 mit REA 6.4.0 und Temurin JDK 21.0.12.1+1. Sie enthält [44 vollständige REA-Records](native/rea-evidence-bundle.json), einen [Index der einzelnen Aufrufe](native/evidence-index.json) und fünf vollständige Funktionsdossiers einschließlich Assembly, Referenzen und High-Pcode-Wertfluss. Die folgenden Aussagen sind statische Codebeobachtungen, keine Laufzeitmessung:

| Bereich | Verfolgter Zusammenhang | Hauptbeleg |
| --- | --- | --- |
| Aufnahme | `Capturer::onCpature` → `ScreenShoter::start` → `screenshot`; dort `QScreen::grabWindow` und Übernahme als Hintergrund der Grafikansicht. | `screenshot`, `0x1400c2e50`, `ev_2f6583d6f4d5448ddffbeb00f324287988f21d1b1395a7fd014b29ba958ac23a` |
| Objekte/Text | `createItem` erzeugt getrennte Objekte. Der Textkonstruktor erstellt `QGraphicsTextItem` und aktiviert Textinteraktion. | `createItem`, `0x1400bdc40`, `ev_3311e1568aa356689221e9f7c645ffb066711c30a635510603212db176db5e9c` |
| Undo/Redo | `finishItem` erstellt einen `CreatedCommand` für Szene und Item und ruft `QUndoStack::push`. Command-Slots entfernen das Item bzw. fügen dasselbe Item erneut hinzu. | `finishItem`, `0x1400be6b0`, `ev_5adeb3e0fdd0e60c4c2de36ff49ad8a669c87fa3d857b8efcdc81db4c5a56d97` |
| Gemeinsame Ausgabe | `snip` leert Fokus/Selektion und erzeugt das Bitmap über `QWidget::grab`. `copy`, `pin`, `onSave` und `extractText` rufen diesen Pfad auf. | `snip`, `0x1400c3300`, `ev_cd43692a968a9cd6d1069ddfb241fe61da37c14a0146965a1402aa6338e3b1fd` |
| OCR | `extractText` übergibt eine Bitmap-Kopie per Timer. Nach PNG-Zwischenspeicherung folgt ein Engine-Dispatch für Tencent, Baidu und Offline-CPU/GPU. Die native Variante lädt `sunny_ocr.dll` und löst ihre drei OCR-/Free-Exports auf. | `extractText`, `0x1400be5b0`, `ev_d4b68cffb291f78db568f770c827967f5a006965097f1e03e3499f037bf02772`; weitere Loader-Belege im nativen Index |

Methodennamen sind durch Qt-MOC-Indizes und dekompilierte Dispatcher verknüpft. Feldrollen und C++-Signaturen bleiben teilweise rekonstruiert; insbesondere die angezeigte ABI-Signatur von `grabWindow` darf nicht als Originalquellcode gelesen werden. Die Analyse beweist weder das Verhalten aller Bearbeitungsbefehle noch Pixeltreue, Schwärzungssicherheit, erfolgreiche GPU-/Cloud-OCR oder vollständige Betriebssystem-Zwischenablagefunktionen.

## Laufzeitgrenze dieser Untersuchung

Die später vom Nutzer gelieferten 13 Bilder sind in [UI-BELEGE.md](UI-BELEGE.md) mit einem [Hashverzeichnis](ui-evidence.json) ausgewertet. Elf Einstellungsbilder ergänzen die sichtbare Konfiguration; zwei Linux-/macOS-Bilder dienen als Editorreferenz. Sie schließen die offene Windows-Bediensequenz nicht automatisch.

Das normale Windows-Einstellungsfenster wurde danach erreichbar. Root las auf dessen Seite „Über“ **Version 5.3.0** am vorgegebenen laufenden Prozess; der EXE-Hash blieb unverändert. Der Befund ergänzt die Produktidentität als Laufzeit-UI-Angabe, während die EXE weiterhin keine PE-VERSIONINFO besitzt. Aufnahmeoverlay und vollständiger Editorablauf sind damit noch nicht geprüft.

Die vorhandene Sitzung war bereits vor der Untersuchung gestartet. [runtime-modules.json](runtime-modules.json) hält eine an EXE-Pfad und Dateihash gebundene Windows-Prozessstichprobe fest. Qt und FFmpeg sind darin geladen. `sunny_ocr.dll`, Paddle und cuDNN sind in dieser Stichprobe nicht geladen; sie können später dynamisch hinzukommen. Diese Aussage betrifft nur die Modulaufnahme, nicht alle bisherigen oder zukünftigen Programmzustände.

Ein erneuter Start über das Windows-Werkzeug meldete `launched app did not expose a targetable window`. Der dokumentierte F1-Weg erzeugte danach ein sichtbares Auswahl-/Lupenbild; Windows lieferte für den Zielprozess HWND `665704`. Eine auf dieses HWND begrenzte Aufnahme zeigte das Overlay. Das Windows-Automationswerkzeug bot dieses randlose Fenster trotzdem nicht als auswählbares Fenster an. Ein über das zuvor gebundene Explorer-Fenster gesendetes Escape ist deshalb kein verifizierter Abbruch. Die gezielte Nutzerübergabe zum Schließen/Öffnen der Einstellungen wurde angefragt.

**Nachtrag vom 18.10.2026, 13:49 UTC:** Der Nutzer berichtet eine manuell erstellte Testaufnahme, die noch nicht in die Zwischenablage gelegt wurde. Dies ist eine Nutzerbeobachtung, keine unabhängig geprüfte Export- oder Editorsequenz. Die erneute Windows-Fenstersuche liefert weiterhin kein SunnyCapturer-Fenster; der bestehende Zielprozess `72416` meldete bei dieser Stichprobe `MainWindowHandle = 0`. Ein erneuter Aufruf über Computer Use endet wieder mit `launched app did not expose a targetable window`. Eine temporäre Desktopaufnahme zeigt keine eindeutig erkennbare SunnyCapturer-Werkzeugleiste. Daraus folgt weder, dass die Aufnahme fehlgeschlagen ist, noch dass sie gespeichert wurde. Die Zwischenablage wurde durch die Prüfung weder gelesen noch überschrieben. Die vollständige Bedienprüfung bleibt offen; das neue Testbild liegt ausschließlich temporär unter `%LOCALAPPDATA%\Temp\codex-shot-2026-10-18_15-49-27.png`.

Die Aufnahmeszene enthielt den Cursor-Overlay der Automationshilfe. Daher wird daraus kein normaler Benutzerablauf, kein Geschwindigkeitswert und kein SunnyCapturer-Produktfehler abgeleitet. Testbilder liegen nur temporär unter `%LOCALAPPDATA%\Temp\codex-shot-2026-10-18_14-18-16.png` und `codex-shot-2026-10-18_14-21-26.png`; sie wurden nicht ins Repository übernommen.

Nicht ausgeführt: eine Zeichnen-/Verschieben-/Text-/Undo-/Export-Sequenz, eine echte OCR-Anfrage, GPU-Inferenz, Tests mit mehreren Monitoren oder ein Cold-Start-Benchmark. Die Speicherstichprobe wurde während dieser Sitzung aufgenommen und ist keine zugesicherte Leerlauf- oder Produktanforderung.

## REA-Abdeckung und zusätzliche Analysen

REA 6.4.0 öffnete das Originalartefakt und lieferte die vollständige portable PE-Ressourcenanalyse. [rea-environment.json](rea-environment.json) bewahrt die anfänglichen Providerdiagnosen. Ghidra war zunächst nicht konfiguriert, das im PATH gefundene Java war nur JRE 8; IDA war ebenfalls nicht konfiguriert. REAs native UI- und Prozessszenarien werden auf diesem Windows-Host nicht unterstützt. Android-, Firmware-, EVM- und Browseranalysen passen nicht zum gelieferten nativen Windows-Ziel und wurden nicht auf fremde Ziele angewendet.

Die [Qt-Kataloganalyse](qt/BERICHT.md) ist integriert und von Root erneut ausgeführt. Ihre Lookup-Hashes und 23.178 exportierten Bytebereiche stimmen mit den Originaldateien überein. Sie rekonstruiert keine kompilierten QRC-Baum-/Namens-/Datentabellen. Die begrenzte Suche fand keine separaten QRC/RCC-Dateien und keinen eigenständigen `qres`-Header in der EXE; eingebettete kompilierte Tabellen sind damit nicht ausgeschlossen.

Die [Architekturabwägung von Opus 5.5 mit Ponytail](ARCHITEKTUR-ABWAEGUNG.md) ist integriert und von Root fachlich nachgeprüft. Sie empfiehlt WPF/.NET 10 für den ersten Windows-Bau; Qt bleibt die Alternative, falls Pixel-/Mixed-DPI- oder gemessene Latenzkriterien nicht erfüllt werden oder weitere Desktop-Plattformen hinzukommen. Es gibt noch keine Messung, die WPF als schneller ausweist. Root hat zu starke Import-/DPI-/OAuth-Zulassungsbehauptungen auf ihre tatsächliche Beleggrenze korrigiert. Die Zusatzbefunde stehen in [architecture-extra-evidence.json](architecture-extra-evidence.json).

Die portable native Codeanalyse ist integriert. Der anfängliche Decompiler-Blocker wurde mit separat erworbenen, hashgeprüften portablen Werkzeugen und einem ausdrücklich freigegebenen normalen Laufzeitpfad behoben. Es gab keine globale Installation oder Änderung bestehender Java-/MCP-Konfigurationen. Die eigene Sitzung wurde geschlossen; Root prüfte die gespeicherte Schließantwort und fand keine privaten Ghidra-Projektverzeichnisse oder zugehörigen Providerprozesse mehr.

Die fehlende vollständige Windows-Editor-Bediensequenz bleibt im separaten Root-REA-Paket als offene Frage `unk_02c4fb520ff025f3c829f771142bb868bcd11b8b0e6a23f8cacf1ad70f8555f9` registriert. Die leere Unknown-Registry des nativen Teilbundles ist ausdrücklich keine Aussage über vollständige Programmanalyse.

## Konsequenzen für den späteren Bau

Eine schlanke Kairos-Fassung kann Aufnahme und Editor vom OCR-Betrieb trennen. Die Dateimengenmessung stützt die Entscheidung, eine reine OpenAI-OCR nicht mit Paddle/cuDNN auszuliefern. Sie beweist weder den späteren RAM-Bedarf noch, dass C# oder C++ auf diesem Rechner schneller wäre.

Für direktes Bearbeiten sollten Annotationen bis zum Export als auswählbare Objekte erhalten bleiben. Speichern, Kopieren und OCR benötigen denselben final gerenderten Bildstand. Eine Schwärzung muss dabei in exportierte Pixel eingehen. Das sind Kairos-Designvorgaben; eine entsprechende sichere SunnyCapturer-Implementierung wurde hier noch nicht nachgewiesen.

Die Farbreferenz ist unverändert die Nutzerdatei `97cc3309-5721-48c0-984b-21dc6de14109-22fe4b60-6f96-4e6c-b2cc-d81d3b216b33.png`: dunkler Grund, violette/lavendelfarbene Akzente und sparsames Gold. Eine einfache RGB-Häufigkeitsprüfung bestätigt diese Richtung; sie legt keine exakten Design-Tokens für die Anwendung fest.

Der dokumentierte spätere OpenAI-Weg steht in [OPENAI-OAUTH-VERTRAG.md](OPENAI-OAUTH-VERTRAG.md). Er benutzt eigene Registrierung und Anmeldung, öffentliche Responses API und kontospezifische Modelle. Eine konkrete Konto-/Workspace-Freigabe und echte OCR sind noch nicht getestet.

## Reproduktion und Root-Abnahme

Im Repository-Wurzelverzeichnis:

```powershell
python -X utf8 scripts/reverse/static_inventory.py --self-check
python -X utf8 scripts/reverse/static_inventory.py
python -B scripts/reverse/qt_resources.py --self-check
python -B scripts/reverse/qt_resources.py --root 'C:\Users\Axel_desktop\AppData\Local\Programs\SunnyCapturer' --out docs/kairos/reverse/qt
python -X utf8 scripts/reverse/verify_evidence.py
.\scripts\reverse\Observe-SunnyRuntime.ps1
git diff --check
```

Der Laufzeitbefehl benötigt genau einen bereits laufenden Prozess der ausgewählten EXE; er startet oder bedient die App nicht. Die statischen Skripte verwenden die vorhandene Python-Standardbibliothek. Das Inventar bleibt an die vorgegebene EXE-Identität gebunden. Ein späteres App-Update verlangt eine neue Untersuchung.

Von Root erneut bestanden: PE-Selbstprüfung, vollständiger Inventarlauf, 107-Dateien-Gesamtsumme, EXE-Hash, unabhängiger Vergleich aller zehn PE-Ressourcennutzdaten, QM-Selbstprüfung mit fünf beschädigten Gegenproben, erneute QM-Generierung, 23.178 Originalbyte-Abgleiche samt Quelltextindex, Identitätsabgleich der Prozessstichprobe und `git diff --check`. Zusätzlich: öffentliche REA-Bundle-Validierung, alle 49 nativen Antwortdatei-Hashes, unveränderte vollständige Gleichheit der 44 Records zwischen Einzelantworten und Bundle, fünf vollständige Dossiers ohne bekannte Wertfluss-Trunkierung und Syntaxprüfung beider nativen Helfer. Root hat die entscheidenden Pseudocodepfade selbst gelesen und die MOC-Zuordnung sowie Qt-Undo-Reihenfolge gegengeprüft. Die Aussage gilt für diese Prüfungen; sie ist keine Verhaltensgleichheit oder Abnahme einer Kairos-Anwendung.

## Abschluss der Frageliste

- **Beantwortet:** Artefaktidentität, native Plattform/Qt-Version, Dateimengen/Abhängigkeiten, verfügbare UI-Texte, ausgewählter Standbild-Codepfad, Anlage von Grafik-/Textobjekten, Objekt-Erzeugungs-Undo und gemeinsamer Bitmap-Ausgabepfad, Offline-OCR-Loader/Engine-Dispatch, Unterschied zu historischen Quellen.
- **Teilweise beantwortet:** direkte Bearbeitung (nicht jeder Command und jede Geste verfolgt), Export (gemeinsames Bitmap belegt, konkrete Pixel-/DPI-/Clipboard-Abnahme offen), Konfiguration (Symbole/Enginewerte belegt, aktive Nutzereinstellungen nicht untersucht), Frameworkwahl (begründeter Vorschlag, Prototypmessung offen).
- **Unaufgelöst:** vollständige Bediensequenz, Mixed-DPI und Multimonitore, Latenz-/Speichervergleich, echte OCR-/GPU-Lauffähigkeit, nachgelagerter Cloud-Transport und konkrete private OpenAI-Kontofreigabe. Diese Lücken werden nicht durch statische Strings oder ein grünes Parser-Ergebnis ersetzt.
