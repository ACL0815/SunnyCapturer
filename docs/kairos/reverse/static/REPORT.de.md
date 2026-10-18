# SunnyCapturer: statisches Inventar der installierten Windows-Artefakte

Stand: 18.10.2026. Untersucht wurde ausschließlich die Installation unter
`C:\Users\Axel_desktop\AppData\Local\Programs\SunnyCapturer` sowie der lokale
Checkout zum Vergleich. Checkout-Ausgangscommit:
`42d627ed3b694b4d5c7c85cdcfe77e4bb2248762`.

Die Installation enthält belegbare Strukturen für Aufnahme, objektbasierte
Bildbearbeitung, Hotkeys und OCR. Die Qt-Metadaten trennen `Capturer`,
`ScreenShoter`, `Selector`, `EditingMenu`, `ImageView` und `OcrUI`.
Lokale PP-OCRv4-Modelle und eine Paddle/OpenCV-Bibliothekskette sind vorhanden.
Damit sind Komponenten und Schnittstellen belegt; ein ausgeführter Ablauf oder
eine konkrete Signalverbindung ist damit noch nicht nachgewiesen.

## Reproduktion und Belegkonvention

Das einzige Analysewerkzeug ist das stdlib-Skript
[static_inventory.py](../../../../scripts/reverse/static_inventory.py), ausgeführt
mit vorhandenem Python 3.12.4. Voraussetzung ist Python 3.11 oder neuer;
keine Installation, kein Zielprozess und kein REA-Aufruf sind erforderlich.
Im Repository-Wurzelverzeichnis unter PowerShell:

```powershell
python -X utf8 scripts/reverse/static_inventory.py --self-check
python -X utf8 scripts/reverse/static_inventory.py
```

Ergebnis ist [inventory.json](inventory.json). Das Skript bricht ab, wenn die
EXE nicht Größe und SHA-256 der vorgegebenen Identität hat. `--install` erlaubt
einen anderen Installationsordner mit exakt derselben Haupt-EXE; `--output`
bleibt auf `docs/kairos/reverse/static/` dieses Worktrees beschränkt.

Alle folgenden Hexadressen sind **Dateioffsets**, sofern ausdrücklich nicht
anders bezeichnet. Intervalle sind halboffen: `[Start, Ende)`. Die JSON enthält
Offset, Größe, Hash und Suchgrenze; Imports zusätzlich Descriptor-, Namens- und
Thunk-Offsets sowie IAT-RVAs. Die hier vergebenen Belegkürzel sind lokale
Verweise auf JSON-Felder, keine REA-Evidence-IDs:

- **E1**: `files[path="SunnyCapturer.exe"]`, PE-Header, Imports und Ressourcen.
- **E2**: `qt.objects[class_name=…]`, Metadatenkopf, Stringtabelle und Methoden.
- **E3**: `strings.groups[Gruppe][literal=…].hits`, vollständige Literal-Treffer.
- **E4**: `strings.simple_msvc_rtti_names`, einfache MSVC-Klassen-Namensstrings.
- **E5**: `files[path="sunny_ocr.dll"]` und weitere Abhängigkeitsdateien.
- **E6**: `checkout[path=…]`, Datei-SHA-256, Suchregex, Zeile und Byteoffset.
- **E7**: `files`, `file_count`, `total_bytes`, `top_level_bytes`, Größeninventar.

**Beobachtung** bezeichnet gelesene Bytes oder dekodierte Formatfelder.
**Inferenz** bezeichnet deren vorsichtige fachliche Zuordnung.
**Unbekannt** bezeichnet Aussagen, die dieser statische Durchlauf nicht belegt.

## Identität, PE und Abdeckung

**Beobachtung (E1):** `SunnyCapturer.exe` hat 3.507.712 Bytes und SHA-256
`e97cc2612b79ab154076844a13ec01cdeb70be0f14cf20675ce0e39dff81ecc8`.
PE-Maschine `0x8664` und Optional-Header-Magic `0x20b` belegen x64/PE32+.
Die sechs Sektionen heißen `.text`, `.rdata`, `.data`, `.pdata`, `.rsrc`,
`.reloc`. Die EXE hat keine PE-Exportdirectory und keine Delay-Importdirectory.
In ihren Win32-Ressourcen liegt keine VERSIONINFO vor; eine SunnyCapturer-
Versionsnummer wird deshalb nicht aus der EXE behauptet.

**Beobachtung (E1):** 43 normale Importmodule mit insgesamt 2.869
Importsymbolen. Der Importbereich liegt bei RVA `0x2e9f14`, Dateioffset
`0x2e8d14`, Größe 880 Bytes einschließlich Null-Descriptor. Vollständige
Symbolnamen der EXE stehen in der JSON. Darunter befinden sich:

- Qt: `Qt6Core`, `Qt6Gui`, `Qt6Widgets`, `Qt6Network`, `Qt6Pdf`, `Qt6PdfWidgets`.
- Medien/Grafik: `ass`, FFmpeg `avutil-59`, `avcodec-61`, `avfilter-10`,
  `avformat-61`, `d3d11`, `dwmapi`, Media Foundation.
- Windows und Laufzeit: u. a. `USER32`, `KERNEL32`, `ADVAPI32`, `SHELL32`,
  `ole32`, `OLEAUT32`, MSVC- und API-Set-Module.

**Beobachtung (E5):** Die installierte `Qt6Core.dll` enthält eine
VERSIONINFO mit fester Dateiversion `6.8.3.0`, Payload bei `0x5d86a0`,
860 Bytes. Das ist die Version dieser Bibliothek, kein Beleg einer
SunnyCapturer-Produktversion.

Alle 107 regulären Installationsdateien wurden gehasht und vermessen, davon
64 als PE erkannt und strukturell untersucht. EXE, `sunny_ocr.dll` und
`common.dll` enthalten vollständige Import-/Export-Symboldetails im Inventar.
Für die übrigen PE-Dateien enthält es alle statischen Importmodule samt
Symbolanzahl und die Exportanzahlen, jedoch keine Liste der exportierten Namen.
`common.dll` hat 966 benannte Exports; sieben Exportziele sind nicht
dateigestützt. Der Parser weist sie mit `file_offset: null` aus, statt einen
Dateioffset zu erfinden. Exportierte Daten dürfen im virtuellen Speicher liegen.

## Aufnahme und direkter Editor

**Beobachtung (E2):** Im Hauptprogramm wurden 51 plausibilisierte statische
Qt-Metaobjekte gefunden. Der Decoder prüft Pointer, Headerrevision 12,
zusammenhängende Stringtabellen, NUL-Abschluss, Methodenrecords und Signalanzahl.
Er scannt `.rdata` vollständig bei 8-Byte-Ausrichtung:
`[0x192e00, 0x30ea00)`. Er dekodiert ausschließlich dieses Layout, keine
allgemeine C++-Klassenhierarchie.

- `Capturer`: Metaobjekt `0x19c348`, Metadaten `0x19a7d0`, 19 Methoden,
  keine eigenen Signale. Slots u. a. `onCpature`, `PreviewClipboard`,
  `QuickLook`, `UpdateHotkeys`, `OpenSettingsDialog`, `RecordVideo`, `RecordGIF`.
  Die Schreibweise `onCpature` stammt unverändert aus dem Artefakt.
- `Selector`: Metaobjekt `0x19d958`, Metadaten `0x19ad60`, 13 Methoden,
  davon acht Signale: u. a. `selecting`, `captured`, `moved`, `resized`,
  `sigResizeCpaturedRect`; dazu Slots `start`, `setBorderStyle`, `setMaskStyle`.
- `ScreenShoter`: Metaobjekt `0x199458`, Metadaten `0x19b8a0`, 19 Methoden,
  davon `pinData` und `sigSaved` als Signale. Slots u. a. `start`, `repeat`,
  `screenshot`, `createItem`, `finishItem`, `copy`, `pin`, `onSave`, `extractText`.
  Der Methodenrecord `createItem` liegt bei `0x19ba70`, sein Namensstring
  bei `0x19b727`.
- `EditingMenu`: Metaobjekt `0x19a028`, Metadaten `0x19d620`, 18 Methoden,
  davon 15 Signale. Darunter `graphChanged`, `penChanged`, `fontChanged`,
  `fillChanged`, `undo`, `redo`, `sigSave`, `copy`, `sigExtractText`.
  Die Slots `canUndo`, `canRedo`, `paintGraph` sind separat markiert.
- `EditingSubmenu`: Metadaten `0x19c1a0`; Signale `penChanged`,
  `fontChanged`, `fillChanged`, `moved`.

**Beobachtung (E4):** Klassen-Namensstrings belegen u. a.
`canvas::Canvas` (`.?AVCanvas@canvas@@`, `0x315520`),
`GraphicsItemWrapper` (`0x312a30`) und `GraphicsTextItem` (`0x315978`).
Weitere Namen: Linie, Pfeil, Rechteck, Pixmap, Ellipse, Zähler, Pfad, Kurve,
Mosaik und Radierer. `CreatedCommand` (`0x315570`), `MoveCommand` (`0x315598`),
`ResizeCommand` (`0x3155c0`), `RotateCommand` (`0x3155e8`), `DeleteCommand`
(`0x315610`) sowie Änderungen an Stift, Füllung und Schrift sind ebenfalls
als RTTI-Namen vorhanden. Die Namen alleine belegen keine Vererbung.

**Beobachtung (E1/E3):** Dazu passen Imports von `QUndoStack::push`,
`undo`, `redo` (Namensoffsets `0x2f3728`, `0x2f3756`, `0x2f3772`) und
`QGraphicsTextItem::setTextInteractionFlags` (`0x30505c`). UI-Strings benennen
Rechteck `Ctrl+1` (`0x1ae0b0`), Ellipse `Ctrl+2`, Pfeil `Ctrl+3`, Stift
`Ctrl+4`/`B`, Mosaik `Ctrl+5`, Text `Ctrl+6`/`T`, Bild `Ctrl+7`, Zähler
`Ctrl+8`, Radierer `E` sowie Undo/Redo `Ctrl+Z`/`Ctrl+Y`.

**Inferenz:** Die Kombination aus Aufnahme-Slots, Erzeugen/Abschließen von
Grafikobjekten, Editor-Signalen, Grafikklassen und Undo-Stack spricht stark für
objektbasierte Annotation im Aufnahme-/Bildkontext. Direkte Texteingabe wird
durch interaktive Qt-Textobjekte gestützt. Konkrete Verbindungen zwischen diesen
Objekten sind ohne Code-Xrefs nicht bewiesen.

**Unbekannt:** Exakter Startpfad bis zum sichtbaren Editor, tatsächlicher
Single-Click-/Direkt-Editor-Ablauf, Fokus- und Texteingabeverhalten, Auswahl- und
Undo-Semantik sowie DPI-/Multimonitor-Verhalten. `WindowsGraphicsCapturer`
(`0x315cc8`) und `Windows.Graphics.Capture.h` sind zusätzliche Namensbelege;
welcher Aufnahme-Backend für einen bestimmten Screenshot benutzt wird, bleibt
offen. Ein Aufnahme-Backend für Video ist kein Beleg für denselben Screenshotpfad.

## OCR-Struktur und Bibliotheksgrenze

**Beobachtung (E2/E3):** `PaddleOCR` existiert als Qt-Klasse mit Metadaten bei
`0x19a750`, jedoch ohne eigene eingetragene Methoden. `ImageView` hat bei
`0x19b248` den Signalrecord `sigPaddleOCRFinish` mit zwei Parametern;
die Typdaten enthalten `ordered_json` und Qt-Typ-ID 10 (`QString`).
`OcrUI` hat bei `0x199390` den Slotrecord `onPaddleOCRFinish` mit denselben
Typbezeichnungen; weitere Slots sind `onOcrFinished`, `onEditEnable` und
`on_tbImportPdf_released`. `Communication::sigOcrFinished` und
`OcrUI::onOcrFinished` haben jeweils drei Parameter mit Qt-Typ-ID 10.
Gleiche Signaturen machen eine Verbindung plausibel, beweisen deren Existenz
aber nicht.

**Beobachtung (E3/E5):** Die EXE enthält `/sunny_ocr.dll` bei `0x1a2340`,
`ImageOcrProcess` bei `0x1a9fa8`, `ImageOcrProcessWithArgs` bei `0x1a9fb8`
und `FreeReturnPtr` bei `0x1a9fd0`. `sunny_ocr.dll` steht nicht in der
PE-Importliste der EXE. Imports für `QLibrary::load`/`resolve` und
`LoadLibraryW`/`GetProcAddress` sind vorhanden.

Die 755.552 Byte große x64-`sunny_ocr.dll` hat SHA-256
`fee6648ff1dee4865ff089595e08149cffbac039f7e978511c462d2b1cd9bc46`.
Ihre Exportdirectory liegt bei `0xaad00`; alle vier benannten Exports sind:

- Ordinal 1: `FreeReturnPtr`, Name `0xaad5e`, Code-RVA `0x20df0`.
- Ordinal 2: `ImageOcrProcess`, Name `0xaad6c`, Code-RVA `0x20e00`.
- Ordinal 3: `ImageOcrProcessWithArgs`, Name `0xaad7c`, Code-RVA `0x22b40`.
- Ordinal 4: `add`, Name `0xaad94`, Code-RVA `0x22bf0`.

`sunny_ocr.dll` importiert 27 Symbole aus `paddle_inference.dll` und 33
aus `opencv_world4100.dll`, außerdem Windows-Bibliotheken. Paddle importiert
u. a. `mkldnn.dll`, `libiomp5md.dll` und `common.dll`. Diese gerichteten
Abhängigkeiten sind PE-Beobachtungen, keine bloßen Dateinamenannahmen.

Die EXE enthält `ch_PP-OCRv4_det_infer` bei `0x1afa78` und
`ch_PP-OCRv4_rec_infer` bei `0x1afaa8`. Beide Ordner sind mit jeweils
`inference.pdmodel`, `inference.pdiparams` und `.pdiparams.info` installiert.
Dazu kommt `infer/ppocr_keys_v1.txt`; alle sieben Dateien zusammen haben
15.876.085 Bytes. Die beiden `.pdmodel`-SHA-256 lauten:

- Detektion: `2ad68ed2768fe6c41166a5bc64680cc9f445390acb6528da449a4db2f7b90e14`.
- Erkennung: `bf78f3898a004615e69c676259d8171bd7ae99000653b41fd72aaa36ae8bd304`.

**Inferenz:** Ein dynamisch angebundener lokaler Paddle/OpenCV-OCR-Pfad ist
stark gestützt. Modellnamen deuten auf Detektion und Erkennung mit PP-OCRv4.
`BaiduOcrChannel` und QtNetwork stützen zusätzlich einen Online-OCR-Bereich.

**Unbekannt:** Tatsächliche DLL-Ladestelle, Export-ABI/Argumentvertrag,
Speicherbesitz trotz `FreeReturnPtr`-Namen, Ausführung der Modelle,
Engine-Auswahl, CPU-/GPU-Verfügbarkeit, Fehlerpfade und aktuelles Netzwerkverhalten.
Mitgelieferte cuDNN-Dateien beweisen keine lauffähige GPU-Konfiguration.
Es wurden weder Modelle ausgeführt noch OCR-Anfragen gesendet.

## Hotkeys, Konfiguration und Export

**Hotkeys – Beobachtung (E1/E2):** Die EXE importiert `RegisterHotKey` bei
`0x30c012` und `UnregisterHotKey` bei `0x30c024`. `QHotkey` ist als Qt-Klasse
mit Metadaten `0x2920c0` und den Signalen `activated`, `released`,
`registeredChanged` vorhanden; Slots umfassen `setRegistered`, `setShortcut`,
`setNativeShortcut`. `HotkeyEdit::keySequenceChanged` ist ein Signal,
`Capturer::UpdateHotkeys` ein Slot. **Inferenz:** Globale Windows-Hotkeys
und eine Bearbeitungsoberfläche sind sehr gut gestützt.
**Unbekannt:** Aktive nutzerspezifische Kombinationen, Konfliktbehandlung und
tatsächlich registrierte Hotkeys. Tool-Shortcut-Labels sind keine Messung der
wirksamen Belegung.

**Konfiguration – Beobachtung (E2/E3):** `SettingWindow`, `config.cpp`
(`0x1a0a39` als Teil eines Buildpfads), `Settings File` (`0x1aba90`),
`capturer_run` (`0x1a0ad0` ASCII, `0x1a0b70` UTF-16LE),
`le_quick_save_path` und `cb_auto_save_enable` sind vorhanden.
Nlohmann-Namen sind ebenfalls nachweisbar. **Inferenz:** Es gibt eine
Konfigurationskomponente mit Speicher-/Startoptionen; Nlohmann deutet auf
JSON-Verarbeitung irgendwo im Programm. **Unbekannt:** Aktueller Dateipfad,
Schema, Persistenzmechanismus dieser Optionen und Nutzereinstellungen.
`config.json`, `settings.json`, `config.ini`, `ConfigJson` und `ConfigManager`
haben in der unten begrenzten Literalsuche keine Treffer. Das schließt anders
benannte, zusammengesetzte oder komprimierte Konfiguration nicht aus.

**Export – Beobachtung (E1/E2/E3):** `EditingMenu::sigSave`,
`ScreenShoter::onSave(bool)` und `ScreenShoter::sigSaved(QString)` sind
Metadatenbelege. Imports von `QPixmap::save` (`0x2f2454`), `QImage::save`
(`0x2f266c`) und `QClipboard::setMimeData` (`0x2efa48`) passen zu den Labels
`Save to file (Ctrl+S)` (`0x1ae2b0`) und `Copy to clipboard (Ctrl+C)`
(`0x1ae2d0`). `Manual Save`, `Quick Save`, `Auto Save`, `Ctrl + Shift + S`,
`Image quality` und `SunnyCapturer_$yyyy-MM-dd_HH-mm-ss$.png` sind vorhanden.
`.png`, `.jpg` und `.bmp` kommen als Teilstrings vor. Zehn installierte
Dateien unter `imageformats/` sind separat inventarisiert.
**Inferenz:** Datei-, Zwischenablage- und mehrere Speichermodi sind gestützt.
**Unbekannt:** Vollständige freigegebene Exportformate, Standardpfad,
Qualitätsvorgaben, Metadatenverhalten und Rasterisierung bearbeitbarer Objekte.
Das Vorhandensein eines Qt-Formatplugins beweist keinen sichtbaren Exportmodus.

## Installationsgröße und Abhängigkeiten

**Beobachtung (E7):** 107 Dateien, Summe der logischen Dateilängen
**3.489.366.381 Bytes = 3,2497 GiB**. Das ist weder die belegte Clustergröße
noch Installer-/Downloadgröße. In der Summe enthalten sind Uninstaller,
VC-Redistributable, Bibliotheken, Plugins, Übersetzungen und Modelle.

- `paddle_inference.dll`: 1.964.766.720 Bytes.
- Acht `cudnn*.dll`: zusammen 1.044.825.472 Bytes.
- Fünf `av*.dll`: zusammen 157.078.528 Bytes, ohne weitere FFmpeg-Hilfsdateien.
- `mkldnn.dll` und `mklml.dll`: zusammen 140.637.056 Bytes.
- `opencv_world4100.dll`: 64.653.312 Bytes.
- Sieben `Qt6*.dll` im Wurzelordner: zusammen 29.670.328 Bytes, ohne Qt-Plugins.
- `infer/`: 15.876.085 Bytes; Haupt-EXE: 3.507.712 Bytes.

Paddle und cuDNN machen zusammen rechnerisch 86,25 % der Dateisumme aus.
**Inferenz:** Der größte Teil der installierten Dateimenge ist dem
Inferenz-/Rechenbibliotheksbereich zuzuordnen. Welche Dateien für einen
konkreten Betriebsmodus tatsächlich nötig sind, wurde nicht durch Entfernen
oder Ausführen erprobt.

## Vergleich mit dem Checkout

**Beobachtung (E6):** Die README-Dateien der Generationen v5.x und v3.x
erklären jeweils in Zeile 1, dass deren aktueller Code nicht offenliegt.
Diese beiden Verzeichnisse enthalten im vorliegenden Checkout nur ihre README.
Die folgende Quellcodegegenüberstellung bezieht sich deshalb ausdrücklich auf
`history-version-code/2nd-SunnyCapturer-v1.x-Code/`:

- `src/ui/screenshot/screenshot.h:31`: `ScreenShot : public QWidget`;
  Zeilen 44–48: OCR-/Undo-/Redo-/Speicherfunktionen; Zeilen 145–146:
  `std::vector<PaintNode>` für Undo und Redo.
- `src/ui/screenshot/screenshot.cpp:879,887`: Aufnahme mit
  `QScreen::grabWindow`; Zeile 825: explizite Verbindung vom alten `PaintBar`
  zur Neuzeichnung des alten `ScreenShot`.
- `src/ui/paint_bar/toolbar_level/paintbarhelper.h:31`: `PaintType` mit
  Zeichenwerkzeugen; Zeile 71: `OcrChannel` mit Baidu-Varianten.
- `src/ui/commom/ocr/networkocr.h:22,37`: `NetworkOCR` und
  `sendBaiDuOcrRequest`.
- `src/ui/commom/communication/communication.cpp:23–27,46–50`:
  `QHotkey`-Erzeugung für verschiedene Aufnahmearten und explizite alte
  `activated`-Verbindungen.
- `src/data/configjson.h:78` und `configmanager.h:100`:
  alte `ConfigJson`-/`ConfigManager`-Deklarationen.

**Inferenz:** Aufnahme, Annotation, OCR, Hotkeys und Speichermodi sind
konzeptionell vergleichbar. Die installierte EXE hat andere belegte Namen und
eine deutlich andere Editor-/OCR-Struktur: `ScreenShoter`, Grafikobjekte und
Qt-Undo-Stack sowie ein lokales Paddle-Modellpaket. Die alte Implementierung
darf nicht als rekonstruierter aktueller Kontrollfluss behandelt werden.
`PaintBar`, `CaptureHelper`, `NetworkOCR`, `ConfigJson` und `ConfigManager`
haben in der begrenzten EXE-Literalsuche keine Treffer.
Der Suchterm `ScreenShot` trifft dagegen auch `ScreenShoter` als Teilstring;
er ist ausdrücklich kein Beleg für die alte Klasse `ScreenShot`.

## Grenzen und Prüfung

- Die vollständige EXE-Literalsuche umfasst `[0, 3507712)`, also
  `[0x0, 0x358600)`, ASCII und UTF-16LE, Groß-/Kleinschreibung beachtet,
  Teilstringvergleich, ohne Trefferlimit. Die exakte Liste der Suchwörter
  steht im Skript und in der JSON. Ein fehlendes Wort beweist keine fehlende
  Funktion. RTTI wird zusätzlich nur für einfache Klassen-Namensformen erfasst.
- Das Qt-Suchintervall und das unterstützte Layout stehen unter E2. Es werden
  keine Vererbung, tatsächlichen `QObject::connect`-Kanten, Properties oder
  vollständigen Enum-Werte rekonstruiert. `super_class_if_local: null` bedeutet
  lediglich, dass kein lokaler Super-Metaobjektname aufgelöst wurde.
- Ressourcen werden bei den drei detaillierten PE-Dateien und `Qt6Core.dll`
  nach Typ/Name/Sprache inventarisiert und gehasht. Komprimierte Qt-Ressourcen,
  Modelle, Icons und fremde Bibliotheken werden nicht dekompiliert.
- `installed_leaf_match` ist ein Dateinamensabgleich, keine Simulation der
  Windows-DLL-Suchreihenfolge. Windows-/API-Set-Imports wurden nicht außerhalb
  des Installationsordners aufgelöst. Dynamische Loads sind nur Hinweise.
- Nach Auftragskontext fehlen Ghidra-Konfiguration/geeignete Java-Runtime und
  IDA-Konfiguration. Diese Vorgabe wurde übernommen, nicht erneut getestet.
  Die aktive REA-Session des Root wurde weder geöffnet, geschlossen noch verändert.
- Es gab keine Programmausführung, Netzabfrage, Toolinstallation, Änderung der
  installierten Dateien oder Einsicht in Nutzerkonten, Tokens oder aktive
  Konfigurationswerte. Die Ausgabe enthält keine vollständigen Binärkopien.

Die mitgelieferte Selbstprüfung deckt PE64/RVA-Grenzen, benannte und ordinale
Imports, benannte und weitergeleitete Exports sowie Exportziele ohne Dateibytes
ab. Der echte Inventarlauf bestand mit allen 64 PE-Dateien. Hashes von EXE und
OCR-DLL, Dateianzahl und Gesamtgröße wurden unabhängig mit PowerShell
`Get-FileHash` und `Measure-Object` bestätigt. Ein zweiter identischer
Inventarlauf wurde auf bytegleiche JSON-Ausgabe geprüft. Der Installationshash
der EXE blieb unverändert.

Erledigt sind Identität, Größeninventar, PE-Abhängigkeiten und strukturelle
Capture-/Editor-/OCR-/Hotkey-Belege. Teilweise beantwortet bleiben Konfiguration
und Exportverhalten. Der tatsächliche Direkt-Editor-Ablauf, Runtime-Verbindungen,
OCR-ABI und aktive Nutzerkonfiguration bleiben ausdrücklich offen.
