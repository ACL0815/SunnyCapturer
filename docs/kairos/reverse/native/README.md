# SunnyCapturer: native Codepfade

Die statische Decompilation belegt einen Qt-Grafikeditor mit eigenständigen Zeichenobjekten, einem `QUndoStack` für Objektbefehle und einem gemeinsamen Bitmap-Ausgabepfad. Text wird als `GraphicsTextItem`/`QGraphicsTextItem` angelegt. `snip` erzeugt das Ausgabe-Bitmap mit `QWidget::grab` aus der dargestellten Ansicht. OCR erhält dieses Bitmap und verzweigt anschließend zwischen Cloud- und nativen DLL-Pfaden.

Untersucht wurde ausschließlich `C:\Users\Axel_desktop\AppData\Local\Programs\SunnyCapturer\SunnyCapturer.exe`, x64 PE, 3.507.712 Byte, SHA-256 `e97cc2612b79ab154076844a13ec01cdeb70be0f14cf20675ce0e39dff81ecc8`. Die EXE wurde weder ausgeführt noch gepatcht. Datum: 18.10.2026. Die folgenden Aussagen beschreiben Code und statische Beziehungen, keine beobachtete Programmausführung.

**Evidence und Methode.** [seeds.json](seeds.json) leitet Qt-MOC-Einstiegspunkte aus dem bereitgestellten Parent-Inventar und den Originalbytes ab; dieser Schritt ist ausdrücklich gewöhnliche Byteanalyse, keine REA Evidence. Die Dispatcher wurden danach tatsächlich mit Ghidra dekompiliert: `ScreenShoter` bei `0x140004f10`, `Capturer` bei `0x140007790`, `EditingMenu` bei `0x140005fe0`. Die Zuordnung von Methodennamen erfolgt über die MOC-Methodenindizes und die dekompilierten Cases, nicht allein über Strings. [evidence-index.json](evidence-index.json) verbindet jeden Aufruf mit vollständiger Evidence-ID, Parametern, Datei-SHA-256 und Ergebnisdatei. [rea-evidence-bundle.json](rea-evidence-bundle.json) enthält alle 44 REA Records vollständig; die einzelnen Antworten einschließlich Kontext und Lifecycle liegen unter [evidence/](evidence/).

Die fünf vollständigen Dossiers enthalten Pseudocode, Assembly, Body-Ranges, Referenzen, Aufrufer/Unteraufrufe, CFG sowie native High-Pcode-Wertflussdaten:

- F1 [`createItem`, `0x1400bdc40`](evidence/012-createitem.json): `ev_3311e1568aa356689221e9f7c645ffb066711c30a635510603212db176db5e9c`.
- F2 [`finishItem`, `0x1400be6b0`](evidence/013-finishitem.json): `ev_5adeb3e0fdd0e60c4c2de36ff49ad8a669c87fa3d857b8efcdc81db4c5a56d97`.
- F3 [`snip`, `0x1400c3300`](evidence/014-snip.json): `ev_cd43692a968a9cd6d1069ddfb241fe61da37c14a0146965a1402aa6338e3b1fd`.
- F4 [`extractText`, `0x1400be5b0`](evidence/015-extracttext.json): `ev_d4b68cffb291f78db568f770c827967f5a006965097f1e03e3499f037bf02772`.
- F5 [`screenshot`, `0x1400c2e50`](evidence/016-screenshot.json): `ev_2f6583d6f4d5448ddffbeb00f324287988f21d1b1395a7fd014b29ba958ac23a`.

**Capture und Datenmodell.** [`Capturer::onCpature`](evidence/017-oncapture.json), MOC-Case 0, erstellt bei Bedarf über `0x1400bc0e0` einen ScreenShoter, setzt dessen Stil und ruft `start` bei `0x1400c3600`. [`start`](evidence/031-start.json) setzt Fenster-, Szenen- und Auswahlgeometrie, ruft F5 und zeigt die Auswahloberfläche. F5 ruft `QGuiApplication::primaryScreen` und `QScreen::grabWindow`, setzt das Ergebnis als `QGraphicsView::backgroundBrush` und aktiviert den Cache. Die exakte Argumentdarstellung von `grabWindow` im Pseudocode ist wegen der rekonstruierten Rückgabe-/Importsignatur nicht als originale C++-Signatur zu lesen.

Der [ScreenShoter-Konstruktor](evidence/032-screenshoter-constructor.json) und F1/F2 belegen relativ zu dessen `this`: `+0x30` ist die `QGraphicsScene`, `+0x38` der aktuelle `GraphicsItemWrapper`, `+0x90` der `QUndoStack`. Das sind analystisch benannte Feldrollen aus Nutzung und Typaufrufen; originale Feldnamen wurden nicht wiederhergestellt. Davon getrennt liest F1 den Werkzeugwert aus dem globalen EditingMenu-Objekt bei `DAT_14031a830 + 0x90`.

**Direkte Objekte und Text.** F1 beendet zunächst ein vorhandenes aktuelles Objekt. Werkzeugwert `1` erzeugt über [`0x1400a94c0`](evidence/026-shape-constructor.json) ein `GraphicsRectItem`; Wert `0x80` erzeugt über [`0x1400a95a0`](evidence/025-text-constructor.json) ein `GraphicsTextItem`. Dessen Konstruktor ruft `QGraphicsTextItem::QGraphicsTextItem`, setzt Position und Item-Flags und aktiviert Textinteraktion mit dem Rohwert `0x13`. F1 übergibt unter anderem QPen-/QFont-Werte über virtuelle Aufrufe und registriert Callbacks. [`0x1400a73a0`](evidence/028-scene-add-wrapper.json) castet den Wrapper zu `QGraphicsItem` und ruft `QGraphicsScene::addItem`; anschließend wird der Fokus gesetzt. Weitere Werkzeug-Cases sind vorhanden, ihre Namen werden hier nicht aus Zahlen oder Konstruktoradressen erfunden.

**Undo/Redo.** F2 prüft das aktuelle Objekt über einen nicht semantisch aufgelösten virtuellen Bool-Slot. Ein Zweig erstellt ein [`CreatedCommand`](evidence/027-add-command-constructor.json) mit Szene und Grafikobjekt und ruft `QUndoStack::push`; der andere ruft den Helfer `0x1400a7560` und den virtuellen Löschpfad auf. Beide setzen `this+0x38` auf null. Das Command speichert Szene bei `+0x10` und Item bei `+0x18`. Seine [Konstruktorreferenzen](evidence/034-created-command-references.json) und [Vtable-Bytes](evidence/037-created-command-vtable.json) führen zu [`0x1400a7df0`](evidence/039-created-command-slot1.json), das `removeItem` aufruft, und [`0x1400a7da0`](evidence/040-created-command-slot2.json), das das vorhandene Item bei fehlender Szene wieder hinzufügt und fokussiert. Die Einordnung als Undo-/Redo-Slots passt zur virtuellen Reihenfolge im [Qt-6.8.3-Header](https://raw.githubusercontent.com/qt/qtbase/v6.8.3/src/gui/util/qundostack.h). Der ScreenShoter-Konstruktor verbindet die EditingMenu-Signale mit `QUndoStack::undo/redo` und die Stack-Signale `canUndoChanged/canRedoChanged` mit den Menü-Callbacks. Damit ist Objekt-Undo über Szenenmitgliedschaft belegt; die vollständige Semantik sämtlicher Bearbeitungsbefehle wurde nicht untersucht.

**Flatten und Export.** F3 normalisiert das Auswahlrechteck, schließt das Auswahl-Widget, leert Szenenfokus und -selektion, verschiebt das Rechteck in die Ansichtskoordinaten und ruft `QWidget::grab`. Neben dem QPixmap wird die Auswahlposition in einem zusätzlichen Rückgabebereich gespeichert. [`copy`](evidence/019-copy.json), [`pin`](evidence/020-pin.json), [`onSave`](evidence/018-onsave.json) und F4 rufen denselben F3-Pfad. `onSave` bietet PNG/JPEG/BMP an und ruft `QPixmap::save` mit dem resultierenden Bitmap. `pin` reicht zusätzlich die Position weiter. Der nachgelagerte Clipboard-Helfer wurde nicht bis zur Betriebssystem-Zwischenablage verfolgt; ein editierbares Projektformat wird aus diesem Bitmap-Pfad nicht abgeleitet.

**OCR-Dispatch.** F4 versteckt Werkzeug-Widgets, verarbeitet ausstehende UI-Ereignisse, ruft F3 und zeigt über `Communication::onExtractText` (`0x14001ddb0`) die OCR-Oberfläche. Ein 10-ms-`QTimer::singleShotImpl` trägt eine QPixmap-Kopie. Sein [Callback](evidence/029-ocr-timer-callback.json) führt über [`0x14001dbc0`](evidence/033-ocr-dispatch.json), [`0x1400cb360`](evidence/036-ocrui-load-pixmap.json) zu [`0x1400c70b0`](evidence/041-imageview-ocr-input.json). Dieser Pfad speichert ein zeitgestempeltes PNG, zeigt ein Ladebild und startet nach erfolgreichem Speichern [`0x1400c6200`](evidence/042-ocr-engine-dispatch.json).

Dieser Dispatcher liest den Enginewert aus `DAT_140319d88`: `tencent_cloud → 0`, `baidu_cloud → 1`, `offline_ocr_cpu → 4`, `offline_ocr_gpu → 5`. Ein leerer Wert startet keinen Auftrag; ein anderer nichtleerer Wert behält den initialen Code 0. [`0x1400c46e0`](evidence/043-ocr-backend-job.json) verzweigt für 4/5 in den nativen Pfad, sonst zu `0x14008e300`; dessen Cloud-Transport und Zugangsdaten wurden nicht untersucht. Der native Pfad prüft die Dateipräsenz von [`sunny_ocr.dll`](evidence/044-offline-availability.json), erstellt Argumente für `infer/ch_PP-OCRv4_det_infer`, `infer/ch_PP-OCRv4_rec_infer`, Bild und Ausgabeordner `local_ocr`. CPU erzwingt `--use_gpu=false`; beim GPU-Code hängen GPU/TensorRT-Flags zusätzlich von `cudnn_cnn64_9.dll` ab. Das sind DLL-Argumente, kein belegter Start einer OCR-EXE. [`PaddleOCR`-Initialisierung](evidence/045-paddleocr-initialize.json) und [Loader](evidence/047-paddleocr-library-bindings.json) verwenden `QLibrary::load/resolve` für `ImageOcrProcess`, `ImageOcrProcessWithArgs`, `FreeReturnPtr`. [`0x140094360`](evidence/046-paddleocr-call.json) ruft den aufgelösten Funktionszeiger und gibt den Ergebnis-Pointer über den Free-Slot frei. DLL-Ladefähigkeit, Modellqualität und echte Cloud-Aufrufe bleiben ungetestet.

**Werkzeuge, Freigabe und Grenzen.** Verwendet wurden vorhandenes Node 24.18.0 und REA 6.4.0 sowie portable Ghidra 12.1.4 und Temurin JDK 21.0.12.1+1 x64. [setup-plan.json](setup-plan.json) und [acquisition.json](acquisition.json) enthalten offizielle Download-URLs, Versionen, Größen, SHA-256 und freien Speicher. Die [offizielle Ghidra-Anleitung](https://raw.githubusercontent.com/NationalSecurityAgency/ghidra/Ghidra_12.1.4_build/GhidraDocs/GettingStarted.md) und die entpackten Properties verlangen JDK 21, nicht zwingend 25. Archive/Engine liegen im ignorierten Worktree `.tools/reverse/`. Eine initial vorgeschlagene Kurznamen-Lösung wurde vor jedem REA/Ghidra-Start zurückgenommen. Kurznamen wurden nur lesend abgefragt, nie für den Provider eingesetzt. Die Root-Scopekorrektur erlaubte stattdessen den normalen Pfad `C:\Users\Axel_desktop\AppData\Local\KairosCapture\ReverseTools\97cc3309-5721-48c0-984b-21dc6de14109` für Laufzeit und Projekt. Keine Junctions, Mappings, globalen Installationen, Registry-/PATH- oder persistenten MCP-Änderungen. Root-MCP blieb unberührt. Eine eigene Sitzung und ein Import wurden für alle Aufrufe wiederverwendet und anschließend geschlossen.

REA bezeichnet Windows Ghidra P0 als experimentelle Grenze. Fehlende PDBs, nicht aufgelöste virtuelle Aufrufe, rekonstruiertes ABI und Ghidras Warnungen über als Aufruf behandelte indirekte Sprünge bleiben sichtbar. Ein wiederholter Autoanalyse-Warnhinweis bei `0x1400c9d40` betraf keine der fünf Hauptfunktionen. Kein eigener Analyseaufruf schlug fehl. Ein Ghidra-Download wurde einmal angehalten, um ein unnötiges 600-s-Limit zu entfernen, und als derselbe Transfer fortgesetzt; es gab keinen erneuten Voll-Download. Die Skill-Empfehlung optionaler Runtime-Beobachtungen wurde durch den ausdrücklichen Auftrag zur rein statischen Analyse begrenzt. Nach der Scopekorrektur bestand kein Setup-/Admission-Blocker. `unknowns: []` im Bundle bedeutet eine leere REA-Unknown-Registry, keine lückenlose Untersuchung aller Programmfunktionen.

**Prüfung.** [verification.json](verification.json) bestätigt 44 eindeutige Evidence-IDs in Bundle und Einzelantworten, identische Target-SHA und Analyseprofile, fünf vollständige Body-Ranges und fünf verfügbare Wertflussberichte ohne Trunkierung oder bekannte ausgelassene Inputs/Kanten. Der Target-Hash blieb nach `close_binary` identisch. Es verblieben weder private `rea-ghidra-*`-Projektverzeichnisse noch zugehörige Providerprozesse. Der Profil-Digest ist `249564812a8d83b9b3f461098fc485ccde60c516d4497547a124081c91d654bf`.

## Exakte Reproduktion

Die 49 tatsächlich ausgeführten Toolaufrufe stehen in [requests.jsonl](requests.jsonl), einschließlich `open_binary`, Export und `close_binary`; die Aufrufparameter sind keine nachträglich rekonstruierte Liste. Die Skripte stehen in [native_deep.ps1](../../../../scripts/reverse/native_deep.ps1) und [native_deep.mjs](../../../../scripts/reverse/native_deep.mjs). `native_deep.ps1` ohne Parameter zeigt nur den Plan. `-Acquire` lädt und entpackt die fest gepinnten Assets und verweigert bestehende Archive/Zielverzeichnisse; bei bereits vorhandenen verifizierten Werkzeugen wird dieser Schritt ausgelassen. Kein `rea setup/install` ist erforderlich.

Im Worktree, mit den bereits erworbenen Werkzeugen, startet die eigene Sitzung so:

```powershell
$node = 'C:\Users\Axel_desktop\.t3\projects\clockify\.rea-runtime\node-v24.18.0-win-x64\node.exe'
$env:GHIDRA_INSTALL_DIR = Join-Path (Get-Location) '.tools\reverse\ghidra\ghidra_12.1.4_PUBLIC'
$env:JAVA_HOME = Join-Path (Get-Location) '.tools\reverse\jdk\jdk-21.0.12.1+1'
$env:NATIVE_DEEP_PRIVATE_ROOT = 'C:\Users\Axel_desktop\AppData\Local\KairosCapture\ReverseTools\97cc3309-5721-48c0-984b-21dc6de14109'
& $node .\scripts\reverse\native_deep.mjs
```

Der Helfer liest nummerierte JSON-Aufträge `{name, arguments}` aus `inbox` unter dem explizit freigegebenen Laufzeitpfad. Er schreibt vollständige Antworten in `docs/kairos/reverse/native/evidence/`, speichert jede tatsächliche Anfrage in `requests.jsonl` und beendet sich nach `close_binary`. Für die erste Wiederholung können in einer zweiten PowerShell die gespeicherten Aufträge ohne Überschreiben unter neuen Nummern eingereiht werden:

```powershell
$saved = @(Get-Content .\docs\kairos\reverse\native\requests.jsonl |
    ForEach-Object { ConvertFrom-Json $_ })
$inbox = 'C:\Users\Axel_desktop\AppData\Local\KairosCapture\ReverseTools\97cc3309-5721-48c0-984b-21dc6de14109\inbox'
$reproBundle = Join-Path (Get-Location) 'docs\kairos\reverse\native\rea-evidence-bundle-repro.json'
if (Test-Path -LiteralPath $reproBundle) { throw "Existiert bereits: $reproBundle" }
foreach ($entry in $saved) {
    # Nur die ursprünglichen 001..049 übernehmen; spätere Wiederholungen ausschließen.
    if ($entry.filename -notmatch '^\d{3}-') { continue }
    $destination = Join-Path $inbox ('1' + $entry.filename)
    if (Test-Path -LiteralPath $destination) { throw "Existiert bereits: $destination" }
    if ($entry.request.name -eq 'export_evidence_bundle') {
        $entry.request.arguments.path = $reproBundle
    }
    $staging = $destination + '.tmp'
    if (Test-Path -LiteralPath $staging) { throw "Existiert bereits: $staging" }
    $entry.request | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $staging -Encoding utf8
    Move-Item -LiteralPath $staging -Destination $destination
}
```

Die Reproduktion importiert einmal neu; einzelne Funktionsabfragen importieren nicht erneut. Bei erneutem Auftreten desselben Problems abbrechen und melden. Für reine Nachprüfung ohne Engine/Import kann das gespeicherte Bundle mit dem öffentlichen REA-Befehl `inspect-evidence-bundle` gelesen werden. Der separate Seed-Schritt ist exakt `& $node scripts\reverse\native_deep.mjs seeds 'C:\Users\Axel_desktop\.t3\projects\kapture\docs\kairos\reverse\static\inventory.json'` und verweigert eine bereits vorhandene `seeds.json`.
