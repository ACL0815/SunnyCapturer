# Kairos Capture: statischer Qt-Übersetzungsbefund

Untersuchungsstand: 18.10.2026. Untersucht wurden ausschließlich die mitgelieferten QM-Kataloge der Installation `C:\Users\Axel_desktop\AppData\Local\Programs\SunnyCapturer` sowie die EXE zur Identitätsprüfung und zur begrenzten Suche nach einem eigenständigen RCC-Header.

**Ergebnis:** 13 formal lesbare QM-Dateien enthalten 3.855 Nachrichten, 385 unterschiedliche Schlüssel `(Kontext, Quelltext, Kommentar)` und 25 Kontexte. Sie liefern direkte Textbelege für neun Editorwerkzeuge, benannte Aktionen und Shortcuts, Bildfenster, umfangreiche Einstellungen, OCR und Bildübersetzung. Alle 3.855 Einträge der QM-Hashverzeichnisse passen zu den Nachrichten und ihren Quelltext-/Kommentar-Hashes. **Das belegt mitgelieferte UI-Texte, keine ausführbare Funktion, Sichtbarkeit, Aktivierung oder erfolgreiche Bedienung.**

## Artefaktbindung und Abgrenzung

- Datei: `SunnyCapturer.exe`, 3.507.712 Byte.
- SHA-256: `e97cc2612b79ab154076844a13ec01cdeb70be0f14cf20675ce0e39dff81ecc8`; entspricht exakt dem Auftrag.
- QM-Dateien: zusammen 314.806 Byte, alle unter `translations/`. Relative Pfade, Dateihashes, Blockgrenzen und Nachrichtennachweise stehen in [inventory.json](inventory.json).
- Keine Zielprozesse oder UI gestartet; keine Konfiguration, Kontodaten oder Netzwerkfunktionen der App gelesen oder benutzt. Keine Installation, keine REA-Session geöffnet/geschlossen, keine Unterdelegation.
- PE-Imports, Exports, allgemeine EXE-Strings, Installationsgesamtinventar und historische Quellbäume wurden nicht untersucht. Die Verzeichnisabfrage war auf QM-/QRC-/RCC-Kandidaten begrenzt.
- Es wurden keine QM-Dateien, Bilder, Icons oder andere Originalressourcen kopiert. Übersetzungen werden zum Validieren dekodiert; exportiert werden nur Position, Länge, Hash und Nullstatus. Der Quellenindex enthält Kontext-/Quelltextschlüssel als Untersuchungsevidenz.

## Format, Provenienz und Reproduktion

[qt_resources.py](../../../../scripts/reverse/qt_resources.py) verwendet ausschließlich die Python-Standardbibliothek. Referenzen sind der [offizielle QM-Leser/-Schreiber](https://github.com/qt/qttools/blob/v6.8.3/src/linguist/shared/qm.cpp), der [Qt-Laufzeitleser](https://github.com/qt/qtbase/blob/v6.8.3/src/corelib/kernel/qtranslator.cpp) und die [Qt-Ressourcenimplementierung](https://github.com/qt/qtbase/blob/v6.8.3/src/corelib/io/qresource.cpp). `v6.8.3` bezeichnet die festgelegte Formatreferenz; die installierte Qt-Version wird damit nicht bestimmt.

Geprüft werden die 16 Magic-Bytes `3cb86418caef9c95cd211cbf60a1bddd`, Big-Endian-Blocklängen und Blockgrenzen, Nachrichtentags und Endmarker, striktes UTF-8/UTF-16BE, sortierte Hashverzeichnisse, Verweise auf tatsächliche Nachrichtenanfänge und der Qt-ELF-Hash von Quelltext plus Kommentar. Ein Übersetzungsfeld mit Längenwert `0xffffffff` wird als Nullstring ohne Nutzdaten behandelt. Es wird von einem nicht-null leeren String unterschieden.

Beobachtete Blöcke: Sprache `0xa7`, Hashes `0x42`, Nachrichten `0x69`, teilweise Numerusregeln `0x88`. Beobachtete Nachrichtentags: Ende `1`, Übersetzung `3`, Quelltext `6`, Kontext `7`, Kommentar `8`. Jeder Datensatz hat genau eine Übersetzungsform; alle Kommentare sind explizit vorhanden und leer. Kein Kontext-/Abhängigkeitenblock, kein unbekannter Block und kein unbekanntes Nachrichtentag wurde gefunden. Numerusregeln sind als Bereich mit Hash inventarisiert, ihre Ausführung wurde nicht interpretiert.

Alle Offsets sind **absolute, nullbasierte Bytepositionen** in der betreffenden QM-Datei. Ein Bereich ist `[offset, offset + length)`. Feldhashes beziehen sich auf die Nutzdaten in der ursprünglichen Kodierung; der Nachrichtenhash schließt Tags und Endmarker ein. `tag_offset` verweist auf das Feldtag, die vier Längenbytes folgen unmittelbar. Bei Nullstrings ist `length = 0`; der Nullmarker ist über `null = true` und den Nachrichtenhash nachvollziehbar. Die Hashverzeichniseinträge haben eigene Positionen und SHA-256-Werte. Keine Byteposition bezeichnet eine Adresse im laufenden Prozess.

`source_index` führt jeden Schlüssel einmal mit einer stabilen Kennung `K…` und allen Beleg-IDs. Eine Beleg-ID wie `translations/fr_fr.qm@0x17c2` bezeichnet den Nachrichtenanfang. `files[].records[]` verbindet sie mit den separaten Bereichen für `source`, `context`, `comment`, `translations[]` und `hash_entry`. Fehlende Felder würden nicht aus Nachbarn übernommen. Der Parser ist auf die hier beobachteten Tags beschränkt; alte Tags `2`, `4`, `9` werden abgelehnt. Bei kompaktierten QM-Dateien ohne explizite Schlüssel wäre keine vollständige Zuordnung möglich. Das betrifft keinen vorliegenden Datensatz.

Reproduktion aus dem Worktree, mit vorhandenem Python 3.12.4:

```powershell
python -B scripts/reverse/qt_resources.py --self-check
python -B scripts/reverse/qt_resources.py --root 'C:\Users\Axel_desktop\AppData\Local\Programs\SunnyCapturer' --out docs/kairos/reverse/qt
```

Der zweite Befehl prüft zuerst den EXE-Hash und alle Eingaben und schreibt anschließend nur `inventory.json` und `selfcheck.json` in den angegebenen, vom Skript fest begrenzten Ausgabepfad. Keine Zeitstempel im generierten JSON: identische Eingaben und Skriptversion ergeben identische Dateien. Der Bericht ist redaktionell aus diesem Inventar erstellt.

## Katalogabdeckung

Die Zahl der Schlüssel bezieht sich auf den vereinigten QM-Bestand von 385 Schlüsseln, nicht auf sämtliche Funktionen oder Texte der Anwendung. Vollständige Dateihashes sind im JSON enthalten.

| Datei | internes Sprachfeld | Nachrichten | fehlende Schlüssel gegenüber Union | Nullübersetzungen |
| --- | --- | ---: | ---: | ---: |
| `ar_sa.qm` | `ar_SA` | 385 | 0 | 0 |
| `de.qm` | `de` | 19 | 366 | 0 |
| `de_de.qm` | `de_DE` | 33 | 352 | 0 |
| `en_us.qm` | `en_US` | 63 | 322 | 49 |
| `es_es.qm` | `es_ES` | 385 | 0 | 0 |
| `fr_fr.qm` | `fr_FR` | 385 | 0 | 0 |
| `it_it.qm` | `it_IT` | 385 | 0 | 0 |
| `ja_jp.qm` | `ja_JP` | 385 | 0 | 0 |
| `ko_kr.qm` | `ko_KR` | 385 | 0 | 0 |
| `pt_br.qm` | `pt_BR` | 383 | 2 | 0 |
| `ru_ru.qm` | `ru_RU` | 385 | 0 | 0 |
| `zh_cn.qm` | `zh_CN` | 361 | 24 | 8 |
| `zh_tw.qm` | `zh_TW` | 301 | 84 | 8 |

Sieben Kataloge enthalten jeweils alle 385 Schlüssel. `de.qm` ist eine echte Schlüsselteilmenge von `de_de.qm`; Dateinamen und internes Sprachfeld belegen keine Ladereihenfolge oder Fallbackauswahl. `pt_br.qm` fehlen ausschließlich `HotkeyEdit / Press a key...` und `ImageView / Open Image`. Insgesamt liegen 65 Nullübersetzungen und keine nicht-null leeren Übersetzungen vor. Ein kleiner englischer oder deutscher Katalog ist deshalb keine geeignete alleinige Grundlage für eine Featureliste. Fehlende Übersetzungen belegen keine fehlenden Funktionen.

## Direkte Editorwerkzeuge und beschriftete Shortcuts

Diese neun Einträge stehen ausdrücklich im Kontext **`EditingMenu`**. Die Tastenkombinationen sind Bestandteil der Quelltexte. Die deutsche Benennung ist die Einordnung dieses Berichts, keine Behauptung über die tatsächlich angezeigte deutsche Oberfläche.

| Werkzeug | exakter Quelltext | Quellenkennung |
| --- | --- | --- |
| Rechteck | `Rectangle (Ctrl+1)` | `K2dc71414c2bb7cdf` |
| Ellipse | `Ellipse (Ctrl+2)` | `K11ff7771e68d38ea` |
| Pfeil | `Arrow (Ctrl+3)` | `K25c9392fb58864dc` |
| Stift | `Pencil (Ctrl+4) (B)` | `K70546537b0dcdf7a` |
| Weichzeichnung/Mosaik, gemeinsam beschriftet | `Blur/Mosaic (Ctrl+5)` | `K0157cd11f67f69e6` |
| Text | `Text (Ctrl+6) (T)` | `K8914df775640da23` |
| Bild | `Image (Ctrl+7)` | `Kb1f87e267a3f63b8` |
| Zähler/Nummerierung | `Counter (Ctrl+8)` | `K403ec14c85e52b3f` |
| Radierer | `Eraser (E)` | `Kd16d190196bd46eb` |

Der Quelltext allein trennt Blur und Mosaic nicht in zwei Werkzeuge. Er erklärt auch weder Nummerierungsregeln des Counters noch Auswahl-, Füll-, Strichbreiten- oder Ebenenverhalten. Für diese Details fehlt in diesem Teilauftrag ein Verhaltensnachweis.

Weitere `EditingMenu`-Aktionen und ihre beschrifteten Tasten:

- Abbrechen: `Cancel Capture (Esc)` — `K5b00c7dec40a3ef1`.
- Zwischenablage: `Copy to clipboard (Ctrl+C)` — `Kc4f6e047af65d887`.
- An Bildschirm heften: `Pin to screen (P)` — `Kf4e32e5a51565c44`.
- Rückgängig/Wiederholen: `Undo (Ctrl+Z)` / `Redo (Ctrl+Y)` — `K0d7b96e6102f2a11` / `Kc54238139fff9329`.
- Datei speichern: `Save to file (Ctrl+S)` — `Kbc3701c8371c0d2c`.
- Bild öffnen: `Open Image` — `Kbe7ed2e089be3f12`; dazu ein PNG/JPG/JPEG/BMP/SVG-Dialogfilter — `Kada408945744284a`.
- Text extrahieren: `Extract text` — `Kff37890b54168a2d`.
- Übersetzen: `Translate` — `K455fbc3cc0cb3f55`.

Zusammen sind das alle 19 Schlüssel von `EditingMenu`, davon 15 mit Tastendarstellung. Separat enthält `SubOutput` den Text **`Ctrl + Shift + S`** (`K9926da600717bf63`, z. B. `translations/fr_fr.qm@0x70ed`). Aus QM allein lässt er sich keinem benachbarten Steuerelement oder bestimmten Speicherablauf verbindlich zuordnen. `SettingWindow` nennt `Hotkeys` sowie unter anderem `Screenshot`, `Repeat Last Screenshot`, `Toggle Previews`, `Preview Clipboard`, `Transparent Input`; `HotkeyEdit` liefert `Press a key...`. Ihre gemeinsame Einordnung als Hotkey-Konfiguration ist eine plausible Inferenz, keine rekonstruierte Zuordnung zu Steuerelementen. **Globale Standardtasten, Gültigkeitsbereiche und die tatsächliche Registrierung sind unbekannt.** Eine Fehlermeldung zur Hotkeyregistrierung ist ebenfalls nur mitgelieferter Text.

## Bildfenster, Auswahlhilfe und Ausgabe

`ImageWindow` enthält 24 Schlüssel: Öffnen, Kopieren, Einfügen, Speichern und Speichern unter; `H Flip`, `V Flip`, `Rotate +90`, `Rotate -90`; `Grayscale`, `Inverse`, `Recover`; `Background` mit Schwarz/Grau/Weiß; `Opacity : `, `Zoom : `, `Transparent Input` und Schließen. Die Dialogfilter nennen PNG/JPEG/BMP für Speichern sowie zusätzlich SVG für Öffnen. Dialogfilter belegen angebotene Dateityptexte; sie verifizieren keinen Decoder/Encoder. Die genaue Wirkung von `Recover` oder `Transparent Input` lässt sich nicht aus dem Namen ableiten.

`GuideUI` enthält zehn inhaltliche Hilfetexte plus `Form` und `TextLabel`: Cursor um ein Pixel bewegen, Ausschnitt um ein Pixel bewegen, ausgewähltes Rechteck bewegen/vergrößern/verkleinern, aktiven Bildschirm/Vollbild auswählen, Aufnahme-Mauszeiger ein-/ausblenden, zwischen Fenster- und Elementerkennung umschalten, Lupenfarbe kopieren und deren Format RGB/HEX/FLT umschalten. Hier sind **keine konkreten zugehörigen Tasten** gespeichert. `Magnifier` selbst enthält ausschließlich `Form`; seine Kategorie stützt sich zusätzlich auf die expliziten Lupen-Hilfetexte von `GuideUI`. `ColorDialogButton` nennt die Farbauswahl; `ColorWindow` die Formen `Floating`, `Hybrid`, `Integral`, ohne sie zu erläutern.

`SubOutput` enthält 17 Schlüssel für manuelles, schnelles und automatisches Speichern, Aktivieren, Pfad, Dateiname, Ordner wechseln/öffnen, Vorschau, Bildqualität, Benachrichtigung und Merken der letzten Bildendung. Zwei Hilfetexte beschreiben automatisches Speichern jeder Aufnahme in einen Ordner sowie schnelles Speichern ohne Dialog im letzten Ordner. Der Qualitätshinweis nennt **0–100 oder −1**, beschreibt 0 als kleine komprimierte und 100 als große unkomprimierte Dateien und −1 als Entscheidung durch Sunny. Das ist die Beschreibung des Katalogs, keine geprüfte codecübergreifende Qualitätssemantik. `ScreenShoter` ergänzt Speicherdialog, PNG/JPEG/BMP-Filter sowie Erfolgs-/Fehler- und Speicherpfadmeldung. `PathEdit` enthält `Browse`.

## Einstellungen und OCR-UI

Die 83 Schlüssel von `SettingWindow` decken folgende Textkategorien ab; Mehrfachschreibungen wie `Private Account`/`Private account` und `GIF Recording`/`Gif Recording` sind eigene Schlüssel:

- **Allgemein und Darstellung:** General, About, Settings, Settings File, Language, Appearance, Theme mit Auto/Light/Dark, Run on Startup. Die Spracheinträge belegen keine verfügbare/aktive Sprachumschaltung im untersuchten Lauf, da kein Lauf stattfand.
- **Aufnahme und Vorschau:** Screenshot, Repeat Last Screenshot, Toggle Previews, Preview Clipboard, Quick Look, Hotkeys, Transparent Input, Floating Menu, Show Region, Mouse, Captured by Default; Randfarbe/-stil/-breite und Maskenfarbe. Ob etwa `Border Width` den Auswahlrahmen oder Zeichenwerkzeuge betrifft, ist ohne Layout/Code nicht bestimmt.
- **Ausgabe:** Image, File, Save Path, Manual Save, Quick Save, Auto Save und Auswahl für Auto-/Quick-Save-Verzeichnis. Weitere zugehörige Texte stehen in `SubOutput`.
- **OCR und Übersetzung:** OCR, Engine, Extract text, Image Translate, Source language, Target language; anbieterspezifische Sprachfelder `Tencent Cloud Language [%1]`, `BaiDu Cloud Language [%1]`, `YouDao Cloud Language [%1]`.
- **Zugangs- und Verbindungseinstellungen als Feldnamen:** Private Account/Private account, Secret ID, Secret Key, `If empty, use the default token.`, Proxy mit No Proxy/System Proxy. **Keine realen IDs, Schlüssel, Tokens, Konten, Proxyadressen oder Werte wurden gelesen.** Der Tokenhinweis belegt keinen tatsächlich vorhandenen Defaulttoken.
- **Video, GIF und Codecs:** Video, Video Recording, beide GIF-Schreibungen, Format, Framerate, Encoder, Params, Pixel Format, Color Range, Color Space, Dither, Max Colors, Preset, Profile, Tune, Rate Control; Software x264/x265 und Hardware NVENC jeweils mit H.264/AVC bzw. H.265/HEVC im Text. Diese Namen belegen weder integrierte Codec-Bibliotheken noch funktionierende Hardwareunterstützung.
- **Geräte und Audio:** Devices, Camera, Audio, Microphone, Speaker, System Audio, Sample Rate, Channel Layout.
- **Updates:** Update, Check update und Check for updates on startup; weitere Dialogtexte stehen in SubUpdate/UpdateUI/VersionUpdater.

Für OCR bestehen mehrere voneinander getrennte Textbelege:

1. `Capturer / OCR`, `EditingMenu / Extract text` und `EditingMenu / Translate` benennen Einstiege bzw. Aktionen.
2. `OcrUI` besitzt genau vier Schlüssel: **OCR, Import PDF, Open PDF file, PDF file (*.pdf)**. Der PDF-Import ist damit als UI-Text belegt. Seitenwahl, Stapelverarbeitung, Textergebnislayout und PDF-Rendering sind hieraus nicht bestimmt.
3. `ImageView` nennt Laden/Öffnen/Speichern von Bildern, Preset, Zoom in/out und Zoomanzeige. Ein eigener Filter nennt PNG/JPG/JPEG/BMP/GIF und alle Dateien. Ein Laufzeittext enthält einen Zeitmessungsplatzhalter für lokale Erkennung.
4. `QObject` enthält die Bezeichnungen **Offline Local CPU**, **Offline Local GPU**, **Baidu Cloud**, **Tencent Cloud**, **Youdao Cloud** und **Auto detect**. Die Zuordnung zu Auswahlmenüs ist plausibel, aber die QM-Datei enthält keine Widgetstruktur. `QObject` enthält zusätzlich 103 Sprachbezeichnungen, `CRASH` und zwei Updatehinweise. Die Sprachtexte sind keine Bestätigung, dass jeder Anbieter jede Sprache unterstützt.
5. `NetTencent` enthält trotz des Kontextnamens auch Baidu- und YouDao-Texte: Tencent/Baidu-Textextraktion, Bildübersetzung über Tencent/Baidu/YouDao, Cloud-/Zeit-/Status-/Netzwerkfehlertexte sowie einen Hinweis auf leere Baidu-API-Zugangsfelder. Ein gemeinsam benannter Kontext erlaubt keinen Schluss auf konkrete Requestimplementierungen, Endpunkte oder tatsächlichen Netzverkehr.
6. `ImageView` enthält eine bedingte Fehlermeldung, nach der die aktuelle Version keine Offline-OCR unterstützt und eine passende CPU-/GPU-DLL von der offiziellen Website nachgerüstet werden solle (`K721ac6f3f5327745`). **Die Existenz dieses Meldungstexts beweist weder fehlende noch vorhandene Offline-OCR in der Installation.** CPU/GPU-Einträge und dieser Fehlertext können gleichzeitig im selben Katalog stehen.

Damit ist die OCR-Frage auf Textebene beantwortet: Bild-/PDF-Einstiege, Extraktion, Übersetzung, Sprach-/Engine-/Zugangsfelder und bedingte Meldungen sind belegt. Erkennungsqualität, Engines im Betrieb, Offlineverfügbarkeit, verfügbare Kontingente und Ergebnisaktionen bleiben offen.

## Vollständige Kontext-/Kategorienmatrix

Die folgenden 25 Zeilen decken alle 385 unterschiedlichen Schlüssel ab. Die Zahlen zählen die Union der Schlüssel, nicht die Zahl von Widgets. Kontextnamen sind gespeicherte Übersetzungskontexte; eine Gleichsetzung mit aktuell instanziierten Klassen wäre ein zusätzlicher, hier nicht erbrachter Nachweis.

| Kontext | Schlüssel | belegte Textkategorie und Grenze |
| --- | ---: | --- |
| `CameraPlayer` | 4 | Kamera öffnen fehlgeschlagen, horizontal/vertikal spiegeln, schließen; kein Kameratest |
| `Capturer` | 13 | Aufnahme, OCR, Video/GIF, Kamera, Settings, Neustart/Beenden, Trayhinweis, Lauf-/Hotkey-/Kamerameldungen |
| `ColorDialogButton` | 1 | Farbauswahl |
| `ColorWindow` | 4 | Farbfenster und Floating/Hybrid/Integral; genaue Darstellung offen |
| `EditingMenu` | 19 | neun direkte Werkzeuge, Aktionen, Shortcuts, Bilddateifilter; oben vollständig aufgeführt |
| `GuideUI` | 12 | Auswahl-/Cursor-/Lupen-/Erkennungs-Hilfe und zwei generische Platzhaltertexte |
| `HotkeyEdit` | 1 | Aufforderung zur Tasteneingabe; keine gespeicherte Tastenbelegung |
| `ImageView` | 10 | Bild laden/öffnen/speichern, Preset, Zoom, Dateifilter, lokale OCR-Zeit-/Fehlermeldung |
| `ImageWindow` | 24 | Bildfenster, Bildtransformationen, Hintergrund/Deckkraft/Zoom, Zwischenablage und Dateidialoge |
| `Magnifier` | 1 | ausschließlich `Form`; Lupenfunktionstexte liegen unter GuideUI |
| `NetTencent` | 16 | Cloud-OCR/-Übersetzung und Meldungen mehrerer Anbieter |
| `OcrUI` | 4 | OCR und PDF-Importdialog |
| `PathEdit` | 1 | Browse |
| `PdfViewer` | 2 | PDF-Viewer und Schließen; kein Rendernachweis |
| `QObject` | 112 | 103 Sprachnamen, Auto detect, fünf Engine-/Anbieternamen, Crash und zwei Updatehinweise |
| `ScreenRecorder` | 3 | Fehlertexte zu Fensteraufnahme, Encoder und Recorderinitialisierung |
| `ScreenShoter` | 5 | Speicherdialog/-filter sowie Speichererfolg/-fehler/-pfad |
| `SettingWindow` | 83 | oben gegliederte Einstellungsbereiche |
| `SubMenuBar` | 1 | ausschließlich `Form`, daraus keine Menüstruktur rekonstruierbar |
| `SubOutput` | 17 | Speicherarten, Dateiname/Pfad, Bildqualität, Vorschau, Benachrichtigungen und Tastentext |
| `SubUpdate` | 9 | Updateprüfung mit Intervall/Tag, Insiderprogramm, manueller Download, Proxytyp/Server/Test, Form |
| `SystemInfo` | 8 | Bildschirmindex, Geometrien, logische/physische DPI, physische Größe, DPR, Bildwiederholrate; keine Gerätewerte |
| `UpdateUI` | 8 | eigene/aktuelle Version, letzter Check, erneute Prüfung, Downloadversion, Download/Abbruch, Form |
| `VersionUpdater` | 10 | Update verfügbar/kein Update, Installationsfrage/-aktion, URL-/GitHub-Netztest und Fehler/Limit |
| `VideoPlayer` | 17 | Video/Audio, Renderer/Properties, Audio-/Untertitelstrom, ASS/SSA/SRT-Filter, Spiegeln, Rotation 0/90/180/270, Decoder-/Audiofehler |

Eine Übersetzung mit `Form` oder `TextLabel` belegt nur diesen generischen Text. Video-/Kamera-/Updatekontexte sind mitgeliefert, obwohl der Schwerpunkt des Auftrags direkte Editor- und OCR-Texte war; sie sind deshalb vollständig erfasst, ohne daraus lauffähige Zusatzfunktionen abzuleiten.

## Nachweisbeispiel mit Bytebindung

`EditingMenu / Rectangle (Ctrl+1)` hat die Quellenkennung `K2dc71414c2bb7cdf`. Ein Beleg ist `translations/fr_fr.qm@0x17c2`, in der Datei mit SHA-256 `21542483d68b2f952a5d780e07e0c8cd558e02f4c0001151ec98c06a57733d7c`.

- Quelltextnutzdaten: Offset `0x17f5`, Länge 18 Byte, UTF-8, SHA-256 `04bc2cd2f7aa761e71577ef83486ddc87f111f82dd0bb6a7e3436cb28efcdca8`.
- Übersetzungsnutzdaten: Offset `0x17c7`, Länge 36 Byte, UTF-16BE; der separate SHA-256 steht im Datensatz.
- Kontext-/Kommentar-/Nachrichten-/Hashverzeichnisbereiche und weitere Sprachbelege sind unter derselben Quellenkennung im Inventar verknüpft.

`OcrUI / Import PDF` ist analog unter `K670a3f37f85465a2`, z. B. `translations/fr_fr.qm@0x3610`, gebunden. Damit kann ein anderer Worker die Textbelege direkt in den ursprünglichen Dateien überprüfen, ohne Übersetzungskataloge oder Icons aus diesem Worktree zu übernehmen.

## QRC-Grenze

Die begrenzte Suche fand **keine separate `.qrc`-/`.rcc`-Datei** im Installationsbaum und **keine exakte Bytefolge `qres`** in der identifizierten EXE. Diese Suche betrifft allein die EXE und separate Dateien; sie umfasst keine DLL-Inhalte.

Ein eigenständiger RCC-Datenblock verwendet einen `qres`-Header mit Version und Offsets. Kompilierte Qt-Ressourcen können dagegen durch getrennte Baum-, Namens- und Nutzdatenarrays mit Registrierungszeigern eingebunden werden. Der fehlende Header schließt solche Arrays nicht aus. Ohne verifizierte Basisadressen, Version und Zuordnung der drei Tabellen wäre eine Rekonstruktion mit Ressourcenpfaden und Bytebereichen hier nicht seriös. Es wurde daher **keine eingebettete QRC-Tabelle rekonstruiert und kein daraus abgeleiteter UI-Befund behauptet**. Die positive Featurematrix beruht ausschließlich auf QM.

Nicht ermittelt: Ressourcenbaum, originale `.qrc`-XML, Icon-/Assetnamen, Widgetanordnung, Reihenfolge/Abstände von Werkzeugen, Registrierungsaufrufe und Ressourcenverbraucher. Ebenso offen bleiben Texte, die nur im Binärcode, in Bibliotheken oder in nicht mitgelieferten Übersetzungen existieren.

## Selbstcheck und Abnahme

[selfcheck.json](selfcheck.json) dokumentiert einen bestandenen, ohne Fixturedateien ausführbaren Selbstcheck:

- Positiv: UTF-8-Schlüssel, UTF-16BE einschließlich Surrogatpaar, mehrere Übersetzungsformen, Nullstring gegenüber leerem String, Bereichs-/Hashbindung und Qt-Lookup-Hashes.
- Negativ: veränderte Magic; um ein Byte abgeschnittener Nachrichtenblock; übergroße Übersetzungsfeldlänge; Hashverweis mitten in einen Datensatz; ungerade UTF-16-Länge. Alle fünf Gegenproben werden mit dem erwarteten Fehler verworfen. Die beschädigten Daten entstehen ausschließlich im Speicher; die Installation wird nicht verändert.
- Reale Eingaben: 13/13 Kataloge formal akzeptiert, 3.855/3.855 Hashverweise und ELF-Hashes geprüft, 0 fehlende Quelltext-/Kontext-/Kommentarfelder und 0 unbekannte Blocktypen. Diese Prüfung interpretiert keine Numerusprogramme und ersetzt keinen Qt-Laufzeittest.
- Unabhängige Rückprüfung des exportierten JSON gegen die Originaldateien: 23.178 Block-/Nachrichten-/Hash-/Feldbereiche einschließlich ihrer SHA-256-Werte geprüft; alle Quelltext-/Kontext-/Kommentartexte und alle Quellenindex-Verweise stimmen. Eine erneute Generierung ergab identische Ausgabedateien.

SHA-256 der reproduzierten Ausgaben (UTF-8 mit LF-Zeilenenden): `inventory.json` = `4ae7686c1c9ec7d3f7dac14b86c5fa79273c16c214b1c181aa495501f478bb4e`; `selfcheck.json` = `791c20e2e7c435f2494e0a2be5aaa3a8c56326dc0eb017280097caf1b8560daa`.

Die erste kurze Explorationsprobe berücksichtigte Qt-Nullstrings noch nicht und wurde am ersten Nullstring abgebrochen. Nach Klärung des `0xffffffff`-Markers wurde der endgültige Parser mit expliziter Nullstringbehandlung und Selbstcheck erstellt; dieser Fehler trat nicht erneut auf. Es gab keinen zweiten gleichartigen Arbeitsfehler.

Abdeckung der Auftragsfragen: direkte Werkzeuge/Aktionen **auf Textebene beantwortet**; Einstellungen und OCR-UI **auf Textebene beantwortet**; explizit beschriftete Shortcuts **beantwortet**, globale Vorgaben und Wirkung **offen**; eingebettete QRC-Tabellen **nicht rekonstruiert, begrenzt auf QM**. Laufzeitverhalten wurde in keinem Fall behauptet.
