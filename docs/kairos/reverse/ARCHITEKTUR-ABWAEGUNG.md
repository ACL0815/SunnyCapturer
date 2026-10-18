# Kairos Capture: Architektur-Abwägung

Stand 18.10.2026 · Lead-Architektur Claude Opus 5.5, Ponytail-Stufe *full* · Status: **Entscheidungsvorschlag, nicht implementiert**

Root-Endabnahme: Importbefunde sind unten auf statische Hinweise begrenzt; weder ausgeführte DPI-/Capture-Aufrufe noch die Zulassung einer privaten OpenAI-App werden daraus abgeleitet. Eine feste Null-Pakete-Regel ist kein Grund, eine eigene OIDC-Validierung zu bauen.

Im Checkout gibt es kein `src/`. `KAIROS.md` und `docs/kairos/ARCHITEKTUR.md` beschreiben ein geplantes WPF-Programm. Sie belegen weder einen Build noch eine Messung und wurden hier nicht geändert. Dieses Dokument trennt deshalb strikt zwischen **Beobachtung** (gelesene Bytes, Doku-Wortlaut, Befehlsausgabe), **Inferenz** und **offen**.

## 1. Ergebnis

1. **Empfehlung: C# + WPF auf .NET 10 (LTS)**, ein Projekt, ein Prozess. Zusätzliche UI-Frameworks sind für das MVP nicht erforderlich. Den Ausschlag gibt nicht gemessene Geschwindigkeit, sondern die erwartete geringe Integrationsarbeit: C# mit XAML, keine eigene Web-/Native-IPC-Grenze, direkter Zugang zu Windows. .NET liefert HTTP-, Krypto-, DPAPI- und JSON-Bausteine; ein vollständiger, sicher validierter OAuth-/OIDC-Client ist damit noch nicht implementiert. Eine gepflegte Validierungsbibliothek ist zulässig, wenn sie eigenen Sicherheitscode reduziert. Die verwendeten Bibliotheken behalten ihre Lizenzbedingungen.
2. **Ernsthafte Alternative: C++ + Qt 6.** SunnyCapturer selbst belegt statisch genau dieses Editor-Muster: Graphics-Items, `QUndoStack`, editierbare Textitems. Qt bringt für direktes Objekt-Editieren mehr fertige Bausteine mit als WPF. Bei den Kippkriterien in Abschnitt 4 wird gewechselt.
3. **Tauri und Electron werden für dieses erste Windows-Ziel zurückgestellt.** Beide bringen eine Web-Oberfläche und zusätzliche Prozess-/Integrationsgrenzen mit. Wie viele Bildkopien dabei tatsächlich anfallen, hängt vom gewählten Transferweg ab und ist nicht gemessen. Electron bündelt zusätzlich Chromium und Node.js.
4. **OCR soll über OpenAI „Sign in with ChatGPT“ laufen.** Das ist ein eigener, belegpflichtiger Vertrag (Abschnitt 6). Die Dokumentation beschreibt den öffentlichen Weg für quelloffene und lokal gehostete Apps; die konkrete Zulassung einer privaten Kairos-App ist daraus nicht geklärt. Private Codex-Tokens werden nie gelesen.
5. Einen Befund zur Dateimenge liefert SunnyCapturer: **Paddle und cuDNN machen allein 86,25 % der 3,25 GiB Installation aus.** Eine reine OpenAI-OCR benötigt diese lokalen Bibliotheken nicht. Kairos' gesamte Auslieferungsgröße muss trotzdem gemessen werden.

## 2. Belegbasis

### 2.1 SunnyCapturer, statisch (keine Laufzeitverbindungen behauptet)

Artefakt: `SunnyCapturer.exe`, x64 PE32+, 3.507.712 Bytes, SHA-256 `e97cc2612b79ab154076844a13ec01cdeb70be0f14cf20675ce0e39dff81ecc8`.
Quellen: eigene Prüfung von Opus (REA `inspect_pe_resources`, `pefile`-Importliste, String-Suche ASCII/UTF-16LE), der integrierte Root-Bericht [`static/REPORT.de.md`](static/REPORT.de.md) und die von Root gegen Originalbytes geprüften [Zusatzbefunde](architecture-extra-evidence.json). Letztere enthalten Manifest, Linker-Version sowie die begrenzten Literal-Suchbereiche und Offsets. Keine davon beweist Laufzeitaufrufe.

| Beobachtung | Bedeutung für Kairos |
| --- | --- |
| 107 Dateien, 3.489.366.381 Bytes. `paddle_inference.dll` 1.964.766.720 B + 8× `cudnn*` 1.044.825.472 B = 86,25 %. Qt6-DLLs im Wurzelordner 29.670.328 B. FFmpeg (5× `av*`) 157.078.528 B. PP-OCRv4-Modelle 15.876.085 B. | Lokale OCR ist der Größentreiber, nicht das UI-Framework. Video/GIF (FFmpeg, Media Foundation) gehört nicht ins MVP. |
| `Qt6Core.dll` VERSIONINFO `6.8.3.0`, in der EXE außerdem `qt_version`/`6.8.3`. | Referenz-App ist Qt 6.8, C++, Linker-Version 14.44 laut PE-Header. |
| Manifest: nur `asInvoker`, **kein** DPI-Awareness-Eintrag. Imports `SetThreadDpiAwarenessContext`, `GetDpiForWindow`, `GetSystemMetricsForDpi`, `EnumDisplayMonitors`, `GetMonitorInfoW`. String `QT_ENABLE_HIGHDPI_SCALING`. | Ein im Manifest deklarierter DPI-Modus ist hier nicht belegt. Die Imports ermöglichen Laufzeitsteuerung, beweisen ihren Aufruf aber nicht. Qt dokumentiert standardmäßig Per-Monitor V2; der tatsächliche Modus dieser Sitzung ist offen. |
| Imports `QScreen::grabWindow`, `d3d11!CreateDirect3D11DeviceFromDXGIDevice`, `RoGetActivationFactory`. Buildpfad `…cppwinrt\winrt/Windows.Graphics.Capture.h`, RTTI `WindowsGraphicsCapturer`. Quelldateinamen `screenshoter.cpp`, `screenrecorder.cpp`. | Zwei Capture-Wege sind kompiliert vorhanden. Welcher Weg Standbilder liefert, ist **offen**. Ein WGC-Pfad kann auch nur dem Recorder dienen. |
| `SetWindowDisplayAffinity`, `RegisterHotKey`, `SetWindowsHookExW`, `DwmGetWindowAttribute`, `EnumChildWindows`. | Diese Win32-Imports passen zu Fensterausschluss, globalen Hotkeys und Fenstererkennung. Die konkrete Nutzung und ein funktionierender Ausschluss sind ohne Aufruf- oder Laufzeitevidenz offen. |
| RTTI `canvas::Canvas`, `GraphicsItemWrapper`, `GraphicsTextItem`, `CreatedCommand`, `MoveCommand`, `ResizeCommand`, `RotateCommand`, `DeleteCommand`. Imports `QUndoStack::push/undo/redo`, `QGraphicsTextItem::setTextInteractionFlags`. 51 Qt-Metaobjekte (Root). | Starkes Indiz für objektbasierte, direkt editierbare Annotation mit Command-Undo. Der Ablauf selbst ist **nicht** bewiesen (keine Xrefs). |
| Label `Blur/Mosaic (Ctrl+5)`. v1.x-Quelle (`capturehelper.cpp:490–505`): Blur über `QGraphicsBlurEffect`, doppelt gerendert. | Weichzeichnen ist keine sichere Schwärzung. Kairos schwärzt deckend (Abschnitt 5.4). |
| `sunny_ocr.dll` fehlt in der Importliste, String `/sunny_ocr.dll` vorhanden, `QLibrary::load/resolve`. Exports `ImageOcrProcess`, `ImageOcrProcessWithArgs`, `FreeReturnPtr`, `add`. Cloud-OCR-Labels nennen Baidu/Tencent/Youdao. | Dynamisches Nachladen ist stark gestützt, aber hier noch kein nachgewiesener Codepfad. Eine vollständige Abwesenheit von OpenAI wird aus dieser begrenzten Suche nicht behauptet. Root-Prozessstichprobe: `sunny_ocr`/Paddle/cuDNN *nicht* geladen. |
| Strings `https://8.137.12.26:5000/add_data`, `wmic baseboard get serialnumber`, `application/json`. | **Ob und wann** gesendet wird, ist nicht belegt. Für Kairos gilt als Regel: keine Telemetrie, keine Hardware-IDs. |
| v1.x-Quelle: `std::vector<PaintNode> m_undo/m_redo` (`screenshot.h:145–146`), Capture `primaryScreen()->grabWindow(...)` mit *einer* globalen `devicePixelRatio` (`capturehelper.cpp:498–512`). v3/v5 im Checkout: nur README. | Die alte Generation zeigt die typische Mixed-DPI-Falle mit einem globalen Faktor. Die aktuelle Struktur ist eine andere, die alte Quelle ist kein Bauplan. |

### 2.2 Arbeitsrechner (Befehlsausgabe 18.10.2026)

| Werkzeug | Befund | Folge |
| --- | --- | --- |
| .NET | `dotnet --list-sdks` → nur **3.1.426**. Runtime `Microsoft.WindowsDesktop.App 10.0.12` ist vorhanden (ein Patch hinter 10.0.13). `ProtectedData`, `HttpListener`, `System.Text.Json` liegen im Shared Framework. | **.NET 10 SDK ist Voraussetzung, nicht vorhanden.** Die frameworkabhängige Ausführung läuft hier ohne Zusatzinstallation. |
| Qt | Nur Qt **5.15.2** über Anaconda (`qmake -v`). Kein Qt-6-SDK. VS Build Tools 2019/2022 als Ordner vorhanden, MSVC-Komponenten nicht geprüft. | Der Qt-Pfad braucht ein Qt-6-SDK und eine verifizierte MSVC-Toolchain. |
| Rust/Node | `rustc 1.80.0`, `node v20.16.0`. | Für Tauri/Electron vorhanden. Versionseignung ist nicht geprüft. |

### 2.3 Primärquellen (alle abgerufen am 18.10.2026)

| Fakt (Wortlaut sinngemäß, Kernsatz zitiert) | Quelle |
| --- | --- |
| .NET 10 erschienen 11.11.2025, LTS, Support bis **14.11.2028**; aktueller Patch 10.0.13 (13.10.2026). | <https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core> |
| WPF: „resolution-independent and uses a vector-based rendering engine“. Läuft nur auf Windows. | <https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/> |
| Trimming für WPF „is currently disabled in the .NET SDK“. Native AOT „Requires trimming“, also für WPF nicht verfügbar. | <https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities>, <https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/> |
| ReadyToRun verbessert Start und „first use of any code“. Assemblies werden „two to three times larger“. | <https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run> |
| Frameworkabhängig vs. self-contained, Single-File für beide Modi. | <https://learn.microsoft.com/en-us/dotnet/core/deploying/>, <https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview> |
| Per-Monitor-DPI je UI-Framework: WPF ab Windows 10 **1607**. Unaware-Apps werden bitmap-gestreckt („blurry“). | <https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows> |
| Virtueller Bildschirm: Der primäre Monitor hat den Ursprung (0,0), andere Bereiche können **negative Koordinaten** haben. | <https://learn.microsoft.com/en-us/windows/win32/gdi/the-virtual-screen> |
| `WDA_EXCLUDEFROMCAPTURE` ab Windows 10 2004. | <https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity> |
| Windows.Graphics.Capture: System-Picker, „yellow notification border“, `Direct3D11CaptureFramePool`. | <https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture> |
| WPF-Hit-Testing mit Punkt oder Geometrie über mehrere überlappende Visuals. Adorner in eigener `AdornerLayer` über dem Element. `RenderTargetBitmap` „Converts a Visual object into a bitmap“. | <https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/hit-testing-in-the-visual-layer>, <https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/adorners-overview>, <https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.rendertargetbitmap> |
| Windows OCR (`OcrEngine`), eingeführt in Windows 10 10.0.10240. | <https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine> |
| Qt Graphics View: BSP-Baum, „millions of items“, Auswahl/Fokus, Rubberband. | <https://doc.qt.io/qt-6/graphicsview.html> |
| Qt 6 ist „Per-Monitor DPI Aware V2 by default“. Unter Windows (physische Pixel) kann die Qt-Geometrie „gaps in virtual desktop geometry“ erzeugen. | <https://doc.qt.io/qt-6/highdpi.html> |
| Qt unter LGPLv3 bzw. GPL, einzelne Module nur GPL, sonst kommerziell. | <https://doc.qt.io/qt-6/licensing.html> |
| `windeployqt` sammelt Qt-Bibliotheken und Plugins. Drittbibliotheken werden nicht berücksichtigt. | <https://doc.qt.io/qt-6/windows-deployment.html> |
| Tauri: Rust + OS-Webview. „a minimal Tauri app can be less than 600KB“ (Herstellerangabe). | <https://v2.tauri.app/start/>, <https://v2.tauri.app/concept/architecture/> |
| WebView2 ist Teil von Windows 11. Prozessgruppe aus Browser-, Renderer-, GPU- und Hilfsprozessen. | <https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution>, <https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model> |
| Electron bündelt Chromium und Node.js, „most apps are >100MB“ (Herstellerangabe). Main- und Renderer-Prozesse. `desktopCapturer.getSources`. | <https://www.electronjs.org/docs/latest/why-electron>, <https://www.electronjs.org/docs/latest/tutorial/process-model>, <https://www.electronjs.org/docs/latest/api/desktop-capturer> |
| OpenAI SIWC: Quickstart, Open-Source-Überblick, Sign-in, Accounts/Sessions, Token-Referenz, Modelle/Inferenz, Preview-Grenzen, UI/UX, Bildeingabe. | Abschnitt 6 |

## 3. Abwägung

Bewertet wird nach Belegen und Strukturmerkmalen. Laufzeit- und Speicherwerte sind für **keine** Option gemessen. Wo die Tabelle „schneller“ oder „größer“ andeutet, folgt das aus Architekturmerkmalen und ist eine Hypothese, die Abschnitt 8 misst.

| Kriterium | C# WPF (.NET 10) | C++ Qt 6 | Rust Tauri 2 | Electron |
| --- | --- | --- | --- | --- |
| **Codeumfang / bewegliche Teile** | Eine Sprache (C# + XAML), ein Prozess, MVP ohne Drittpakete möglich (2.2). | Eine Sprache (C++), CMake, `windeployqt`. Sunny liefert zusätzlich OpenSSL-DLLs (5,3 MB) mit. | Zwei Sprachen (Rust + TS/HTML), IPC-Schicht, npm- und cargo-Toolchain. | Eine Sprache (TS/JS), aber Main-/Renderer-Grenze. Capture und Hotkeys brauchen Electron-APIs oder ein natives Addon. |
| **Direkt editierbares Objektmodell** | Hit-Testing (Geometrie), Adorner, `TextBox`, `RenderTargetBitmap` sind fertig. Auswahl-Handles und Undo sind eigener Code. | **Am meisten fertig:** `QGraphicsScene`/`Item` (Auswahl, Bewegen, Rubberband), `QGraphicsTextItem`, `QUndoStack`. Von Sunny statisch genutzt. | Canvas/SVG im Browser plus eigenes Modell in JS. Gut machbar, aber die Bitmap liegt jenseits der IPC-Grenze. | Wie Tauri, ohne Sprachwechsel. |
| **Capture / DPI / Monitore** | Win32 per P/Invoke (BitBlt, `EnumDisplayMonitors`, `WDA_EXCLUDEFROMCAPTURE`). Per-Monitor ab 1607. **Risiko:** WPF-Fenster über Monitore mit gemischter DPI (Spike S1). | Native Win32 plus Qt; PMv2 Standard. **Risiko:** Qt-Doku nennt Geometrielücken unter Windows. Sunny-Mixed-DPI ist hier ungetestet. | Capture in Rust (Win32/WGC). Unkomprimiertes 4K-BGRA hat 33.177.600 Bytes je Monitor. Format, Transferweg und Zahl der Kopien sind Implementierungsfragen. | `desktopCapturer` oder natives Addon. Pixelgenaue Vollauflösung ist erst nachzuweisen. |
| **Warmstart (Hotkey → Overlay)** | Tray-resident. JIT bei erster Nutzung, durch ReadyToRun milderbar (Doku). | Nativ, kein JIT. Hypothetisch der kleinste Kaltstart. | Webview-Prozessgruppe vorgewärmt. Bildtransfer über IPC ist der kritische Pfad. | Chromium vorgewärmt. Gleicher IPC-Vorbehalt. |
| **Speicher** | Ein Prozess plus CLR/WPF. Nicht gemessen. | Ein Prozess. Nicht gemessen. | Rust-Prozess plus WebView2-Prozessgruppe (Doku: mehrere Prozesse). | Main plus Renderer plus GPU usw. (Doku). |
| **Deployment** | Frameworkabhängig: kleine Ausgabe, braucht die Desktop Runtime 10 (hier vorhanden). Self-contained ohne Trimming → groß (Größe messen). | `windeployqt`: Qt-DLLs + Plugins (Sunny: ≈ 29,7 MB DLLs + Plugins). LGPLv3-Pflichten oder kommerzielle Lizenz. | Kleinstes Paket (Herstellerangabe), WebView2 in Windows 11 enthalten. | Chromium gebündelt, laut Hersteller meist > 100 MB. |
| **Stabilität** | Speichersicherer Code, ausgereiftes Framework, globale Exception-Hooks. | Speicherunsicheres C++; Sunny-Import `dbghelp` deutet auf Crashdump-Behandlung (Inferenz). | Rust speichersicher, Webview separat. Zwei Laufzeiten bedeuten zwei Fehlerklassen. | Ausgereift, aber die größte Angriffs- und Updatefläche. |
| **„Cooles Design“** | XAML-Styles, Verläufe, `DropShadowEffect` als Glow, Templates. | QSS + `QPainter`; Glow ist Handarbeit. | CSS, stärkste Gestaltungsfreiheit. | CSS. |

Qt bietet mehr fertige Editor-Bausteine. Für WPF sprechen hier das erwartete einfache Windows-Deployment, C# als überwiegende Logiksprache und die vorhandene Runtime. Wie viel eigener Editorcode nötig wird, entscheidet der Prototyp; „wenig Code“ ist noch kein gemessenes Ergebnis. Native P/Invoke-Grenzen brauchen auch in C# sorgfältige Prüfung.

## 4. Entscheidung, Alternative, Kippkriterien

**Entscheidungsvorschlag:** WPF/.NET 10, frameworkabhängig, `win-x64`, Per-Monitor V2 per Manifest. ReadyToRun wird nur geprüft, wenn A2 oder A4 (Abschnitt 9) verfehlt wird und Profiling JIT/ersten Codeaufruf als relevante Ursache zeigt.

**Alternative:** Qt 6 (C++), Muster wie Sunny: `QGraphicsScene` + `QUndoStack` + `QGraphicsTextItem`.

**Wechsel zu Qt, wenn eines davon eintritt:**

- **K1 DPI:** Spike S1 erreicht auf einem Aufbau mit 100 % + 150 % (oder 175 %) **keine** 1:1-Pixeltreue zwischen Overlay, Auswahl und Export, auch nicht mit einem Overlay-Fenster je Monitor.
- **K2 Latenz:** A2 wird verfehlt, und das Profiling zeigt WPF-Rendering oder -Komposition als Ursache, nicht Capture oder I/O.
- **K3 Plattform:** macOS/Linux wird Anforderung. WPF ist Windows-only (Doku), Sunny belegt Qt unter Windows und Linux (`DeepinDE`, `linux_script`).

**Kippen unabhängig vom Framework:**

- **K4 OAuth:** Der vorgesehene Veröffentlichungs-/Betriebsmodus oder das konkrete Konto erhält keine ChatGPT-Plan-Freigabe. Die Doku verweist bei bezahlten oder remote gehosteten Apps auf ein Interessenformular; die Berechtigung einer privaten lokalen App ist gesondert zu klären. Ein eigener API-Schlüssel oder Offline-OCR wäre eine bewusste alternative Produktentscheidung, kein stiller Ersatz.
- **K5 Offline-OCR:** wird Pflicht. Zuerst Windows `OcrEngine` (Systembestandteil, kein Bundle). Paddle (Sunny-Weg, 86 % der Installation) nur, wenn dessen Qualität messbar nicht reicht.

Tauri wird erst geprüft, wenn eine vorhandene Web-UI wiederverwendet werden muss. Electron wird nicht weiter verfolgt.

## 5. Minimale Architektur

### 5.1 Module (ein Projekt, fünf Verantwortungsbereiche)

| Modul | Aufgabe | Plattformmittel |
| --- | --- | --- |
| `Capture` | Hotkey, BitBlt des virtuellen Bildschirms in physischen Pixeln (BGRA), Monitorliste mit DPI | `RegisterHotKey`, `BitBlt`, `EnumDisplayMonitors`, `GetDpiForMonitor` (P/Invoke) |
| `Overlay` | Eingefrorenes Bild anzeigen, Bereich wählen, Esc bricht ab; eigene Fenster per `WDA_EXCLUDEFROMCAPTURE` ausschließen | WPF-Fenster (Anzahl entscheidet S1) |
| `Editor` | Dokumentmodell, Rendern, Hit-Test, Werkzeuge, Inline-Text, Undo, Flattening, Export | WPF `DrawingContext`, `Geometry`, `TextBox`, `RenderTargetBitmap`, `PngBitmapEncoder`, `Clipboard` |
| `Ocr` | OAuth-Loopback mit PKCE, Token-Speicher, Modelliste, Responses-Stream | `HttpListener`, `SHA256`, `RSA`, `HttpClient`, `System.Text.Json`, `ProtectedData` (alles im Shared Framework) |
| `App` | Tray, Fenster, Theme-Ressourcen | WPF + `NotifyIcon` aus WinForms (im WindowsDesktop-Framework) |

Ausgelassen: separates Settings-Fenster, Plugin-System, DI-Container, MVVM-Framework und Logging-Framework. Notwendige Kontoauswahl, Abmelden, Fehleranzeige und sichere Tokenverwaltung gehören trotzdem zum OCR-Ablauf. Eine etablierte OIDC-/JWT-Validierungsbibliothek darf eigenen Sicherheitscode ersetzen; ein Null-Pakete-Ziel hat hier keine Vorrangstellung.

### 5.2 Datenfluss

```text
Hotkey ─► Capture: BGRA(virtueller Bildschirm, physische px) + Monitore
        ─► Overlay: Auswahl-Rechteck (physische px, ggf. negativ)
        ─► Zuschnitt ─► Document { Base (unveränderlich), Shapes[], Undo[] }
                           │  direktes Editieren (5.3)
                           ▼
                       Flatten() ─► PNG-Bytes ─┬─► Zwischenablage
                                               ├─► Datei
                                               └─► OCR (nur diese Bytes) ─► Text (editierbar)
```

Invariante: **Alles verlässt das Programm nur über `Flatten()`.** Daraus folgt, dass OCR, Zwischenablage und Datei dieselben geschwärzten Pixel sehen.

### 5.3 Direktes Objekt-Editieren

- **Modell:** `record Shape(Kind, Rect/Points in Bildpixeln, Stroke, Fill, Text, FontSize)`. Für das MVP gibt es die Werkzeuge Rechteck, Pfeil, Stift, Text und Schwärzen.
- **Rendern:** *Eine* Funktion `Render(DrawingContext, Document)` dient Bildschirm und Export gleichermaßen. Was man sieht, ist damit das, was exportiert wird.
- **Treffer:** von oben nach unten über `Shapes`, `Geometry.FillContains`/`StrokeContains` mit Toleranz. Ein Klick wählt aus, Ziehen bewegt, acht Griffe ändern die Größe, `Entf` löscht, Esc hebt die Auswahl auf.
- **Text:** Klick mit dem Textwerkzeug oder Doppelklick auf Text legt eine `TextBox` exakt über das Objekt. Enter oder Fokusverlust übernimmt die Eingabe, Esc verwirft sie.
- **Undo:** Snapshot der unveränderlichen `Shapes`-Liste vor jeder abgeschlossenen Änderung. `ponytail:` Snapshot-Undo kostet O(n) je Schritt; Command-Objekte wie bei Sunny erst, wenn lange Stiftpfade oder mehr als 1.000 Objekte messbar bremsen.

### 5.4 Sicheres Flattening für Schwärzen

1. Die Schwärzung ist **deckend**. Weichzeichnen und Mosaik zählen nicht als Schwärzung und kommen nicht ins MVP.
2. Das Schwärzungsrechteck wird in Bildpixeln **nach außen gerundet** (`floor` für links/oben, `ceil` für rechts/unten). So entsteht keine geglättete Kante, die Restpixel durchlässt.
3. `Flatten()`: `Base` und alle Nicht-Schwärzungs-Shapes werden über `RenderTargetBitmap` bei 96 DPI in exakter Pixelgröße gerendert. Danach werden die Schwärzungsrechtecke **direkt in das BGRA-Array** geschrieben, als letzter Schritt und deshalb immer obenauf. Erst dann wird kodiert.
4. Das PNG enthält nur die neuen Pixel. Es gibt keine Ebenen, keine Metadaten und keine Projektdatei mit dem Original. Die Zwischenablage erhält nur das geflachte Bild.
5. Prüfung: Jedes Pixel in jedem Schwärzungsrechteck des exportierten PNG hat exakt die Füllfarbe. Der SHA-256 der OCR-Nutzlast ist gleich dem SHA-256 des exportierten PNG.

## 6. OCR-Vertrag: OpenAI Sign in with ChatGPT

Die vollständigen Implementierungsanforderungen und ihre einzelnen Quellen stehen im separat geprüften [OPENAI-OAUTH-VERTRAG.md](OPENAI-OAUTH-VERTRAG.md). Dieser Architekturvorschlag übernimmt dessen Grenzen; ein eigener Login wurde nicht ausgeführt.

Der offizielle Ablauf umfasst Systembrowser, Loopback-Callback, PKCE und eine bei der ersten Anmeldung ausgestellte Client-ID. Kairos braucht eigene geschützte Sitzungen und eine validierte Identität. Kontofreigabe und tatsächlich gewährter Direct-Scope sind vor Inferenz zu prüfen. Quelle: [Registrierung und Anmeldung](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), geprüft 18.10.2026.

Die öffentliche Dokumentation richtet sich an quelloffene und lokal gehostete Apps und verweist für bezahlte oder remote gehostete Apps auf einen gesonderten Kontaktweg. Daraus folgt weder eine automatische Freigabe jeder privaten lokalen App noch die Pflicht, dieses Repository jetzt zu veröffentlichen. Kairos-Lizenz und zulässiger Betriebsmodus bleiben offen. Quelle: [Übersicht](https://developers.openai.com/siwc/token-sharing-open-source), geprüft 18.10.2026.

OCR soll das bereits endgültig gerenderte Bild an die öffentliche Responses API senden. Bilder benötigen ein passendes kontospezifisches Modell; Streaming, Fehler und Abschluss müssen gemäß dem dokumentierten Vertrag behandelt werden. Die Anmeldung darf nur den freigegebenen OpenAI-Branding-Regeln folgen. Quellen: [Modelle und Inferenz](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), [Preview-Grenzen](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations), [UI/UX](https://developers.openai.com/siwc/ui-ux-guidelines), geprüft 18.10.2026.

Keine fremden Codex-Token übernehmen. Nicht geprüft sind Kontozulassung, echte Registrierung, Refresh/Widerruf, bildfähige Modelle und OCR-Qualität. Diese Abnahme folgt erst in der späteren Anwendung.

## 7. Design-Tokens (aus der Vorlage gesampelt)

Quelle: Nutzervorlage `97cc3309-…-22fe4b60-….png` (1801×1361), Pixelproben mit PIL.

| Token | Wert | Probe | Kontrast auf `#090E13` |
| --- | --- | --- | --- |
| `bg` | `#090E13` | (250,650); häufigste dunkle Farbe `#080D12` | – |
| `surface` | `#12171C` | Preiskarte (1020,280) | – |
| `violet` | `#7D4BE3` | „Upgrade to Pro“ (1115,365) | 3,68 : 1, nur für Flächen und Fokusrahmen |
| `violet-deep` | `#431B96` | Voice-Screen unten (1548,860) | – |
| `lavender` | `#B097E9` | (1115,375) | 7,80 : 1 |
| `lavender-frame` | `#CBC4DC` | Außenrahmen (30,30) | – |
| `gold` | `#D3B576` | häufigster Goldton | 9,82 : 1 |
| `gold-glow` | `#EBDF95` | hellster Glow am Eingabefeld (421,847) | 14,32 : 1 |
| `text` | `#F4F2FA` | **abgeleitet, nicht gesampelt** | 17,46 : 1 |

Regeln: Das Screenshot-Bild bleibt die hellste Fläche. Violett markiert aktive Werkzeuge und den Primärknopf (weißer Text darauf: 5,27 : 1). Gold erscheint sparsam als Glow an Auswahl und Fokus, wie in der Vorlage am Eingabefeld. Schwärzungen sind `#000000`, nicht `bg`, damit sie im Export eindeutig prüfbar bleiben.

## 8. MVP

**Enthalten:** Tray mit Hotkey, Bereichsaufnahme über alle Monitore, Editor mit Rechteck, Pfeil, Stift, Text und Schwärzen, Auswahl/Bewegen/Größe/Löschen, Undo/Redo, Kopieren, PNG speichern, OCR über SIWC mit editierbarem Ergebnis, Palette aus Abschnitt 7.

**Ausgelassen, nachrüsten wenn verlangt:** Fenster-Snapping (`DwmGetWindowAttribute`), Pin-to-Screen, Scroll-Capture, Video/GIF (der FFmpeg-Weg bei Sunny umfasst ≈ 157 MB), Blur/Mosaik, Übersetzung, Settings-UI (Hotkey zunächst fest, Konflikt wird gemeldet), Offline-OCR (K5).

**Ausgangspunkt, keine Abnahmegrenze:** ein `.csproj`, kein zusätzliches UI-Framework, wenige verständliche Module. Abhängigkeiten werden nach tatsächlichem Code- und Prüfaufwand gewählt. Insbesondere wird sichere OAuth-/OIDC-Validierung nicht für ein Paket- oder Zeilenbudget selbst nachgebaut.

## 9. Mess- und Abnahmekriterien

Die Zielwerte sind **Anforderungen**, keine Messergebnisse. Gemessen wird auf diesem Rechner, Release-Build, n ≥ 30, Angabe von Median und p95. SunnyCapturer wird mit derselben Methode als Referenz gemessen, sobald Root das Overlay steuern kann.

| ID | Kriterium | Methode | Ziel |
| --- | --- | --- | --- |
| A1 | Pixeltreue | Testbild mit 1-px-Raster auf jedem Monitor; Aufbau 100 % + 150 % (+ 175 %), ein Monitor mit negativen Koordinaten | Export = Quelle, Abweichung 0 px |
| A2 | Hotkey → Overlay sichtbar (warm) | `Stopwatch` von `WM_HOTKEY` bis zum ersten `CompositionTarget.Rendering` des Overlays, zusätzlich Kontrolle per 240-fps-Kameraaufnahme | p95 ≤ 150 ms |
| A3 | Auswahl → Editor bereit | `Stopwatch`, 3 × 4K | p95 ≤ 200 ms |
| A4 | Kaltstart → Tray bereit | Prozessstart bis Hotkey registriert | Median ≤ 1,5 s |
| A5 | Speicher | Private Bytes im Leerlauf und nach 50 Aufnahmen | Leerlauf ≤ Sunny-Leerlauf (gleiche Methode); kein Anstieg > 10 % nach 50 Zyklen |
| A6 | Stabilität | 500 automatisierte Zyklen Aufnahme → Editieren → Export | 0 Abstürze, 0 unbehandelte Exceptions |
| A7 | Schwärzung | Test aus 5.4, Punkt 5, inklusive Rechtecke mit Bruchteil-Koordinaten | 100 % Füllpixel; OCR-Hash = Export-Hash |
| A8 | Direktes Editieren | Manuelle Checkliste: Klick wählt das oberste Objekt, Ziehen, acht Griffe, Inline-Text öffnen/übernehmen/verwerfen, Undo/Redo über alle Aktionen | alle Punkte bestanden |
| A9 | OAuth/OCR | Manuell mit eigenem Konto: Registrierung, Neustart (Refresh), Abmelden, Ablehnen, Limitfehler simulieren | Ablauf gemäß Abschnitt 6; kein Token in Logs oder Dateien außerhalb des DPAPI-Speichers |
| A10 | Deployment | Größe der `publish`-Ausgabe frameworkabhängig und self-contained; Start auf sauberem Windows 11 | Werte dokumentiert; frameworkabhängig startet mit Desktop Runtime 10 |
| A11 | Kontrast | Berechnung wie in Abschnitt 7 | Text ≥ 4,5 : 1, UI-Grafik ≥ 3 : 1 |

**Spike S1 (vor allem anderen):** Capture + Overlay + Export ohne Editor, A1 und A2 auf dem Mixed-DPI-Aufbau. S1 entscheidet zwischen einem Fenster über den virtuellen Bildschirm und einem Fenster je Monitor. Scheitern beide → K1.

## 10. Fehlende Evidenz

| Offen | Wer / wie |
| --- | --- |
| Welche Capture-API Sunny für Standbilder nutzt (`grabWindow` oder WGC) und wie DPI tatsächlich gesetzt wird | Native-Deep-Analyse (Ghidra 12.1.4 + JDK 21 läuft bei Root), Xrefs auf `grabWindow` und `WindowsGraphicsCapturer` |
| Sunny-Editorablauf, Undo-Semantik, Textfokus, Mixed-DPI-Verhalten | Laufzeit; das Overlay ist derzeit nicht steuerbar |
| Ob `8.137.12.26:5000/add_data` und die Abfrage der Board-Seriennummer tatsächlich zur Laufzeit erfolgen | Netzwerk- und Prozessbeobachtung mit Einwilligung |
| Sunny-Referenzwerte für A2–A5 | Messung mit Methode aus Abschnitt 9 |
| Jeder Kairos-Messwert A1–A11 | Erst nach Installation des .NET 10 SDK und Spike S1 |
| WPF-Fensterverhalten über Monitore mit gemischter DPI | Nicht aus der Doku belegt → S1 |
| SIWC-Berechtigung für ein noch unveröffentlichtes Repository; Lizenz von Kairos | Repository-Status und Lizenz festlegen, Doku vor dem Bau erneut lesen |
| Bildfähige Modelle im Nutzerkatalog, OCR-Qualität und -Grenzen | A9 mit eigenem Konto |
| Ob die MSVC-Komponenten in VS Build Tools 2019/2022 installiert sind | Nur relevant bei K1–K3 |
