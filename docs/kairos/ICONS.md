# Kairos Capture: Icons und Glas

Produktstand: **2026-10-22**. Quellenabruf: **2026-10-19**. Originale WPF-Vektorzeichnungen, inspiriert von Apple Liquid Glass und der optischen Disziplin von SF Symbols. Keine Apple-Symbolpfade, Apple-Schriften oder Icon-Composer-Assets werden mitgeliefert.

## Entwurf und Raster

- Alle 27 `Glyph.*`-Schlüssel sind für `Stroke` gedacht; die bisherigen 25 bleiben bestehen, ergänzt um `Glyph.Pin` und `Glyph.Download`. `GlassIcon` setzt `Fill=null`, `StrokeThickness=1.75` DIP, runde Enden und runde Verbindungen. Standarddarstellung 20×20 DIP; für 16/24/32 einfach Width und Height gemeinsam setzen.
- Jede `GeometryGroup` enthält eine unsichtbare `PathGeometry` von (0,0) nach (24,24): `IsFilled=False` und `LineSegment.IsStroked=False`. Sie bestimmt die Bounds, ohne als Linie oder Fläche zu erscheinen. So skaliert `Stretch=Uniform` sämtliche Glyphen im selben 24×24-Raster. Sichtbare Striche inklusive halber Strichstärke bleiben innerhalb der Box; der Renderer prüft beides mit WPF.
- Optische Zeichnungsfläche meist 3–21 DIP, größere Rundformen mit etwas Überstand. Geometrien werden bewusst unterschiedlich proportioniert. Capture = vier Auswahlwinkel; Fullscreen = Monitor; OCR = Textrahmen; History = Rücklaufkreis mit Fortsetzen-Dreieck; Exit = Power. Pen und Highlight unterscheiden sich durch Spitze und Marker-Unterstrich. Keine gefüllt vorausgesetzten Flächen.
- `Glyph.Pin` zeigt eine aufrechte Reißzwecke mit querliegendem Kopf, schmalem Körper, breiter Schulter und nach unten gerichteter Nadel. `Glyph.Download` zeigt einen Abwärtspfeil über einer nach oben offenen Ablage. Beide nutzen dieselbe unsichtbare 24×24-Bounds-Geometrie und den unveränderten `GlassIcon`-Stil.
- `GlassIcon.Stroke` bindet an das geerbte `TextElement.Foreground` des Pfads. Dadurch funktionieren Button-, RadioButton- und ToggleButton-Farben ebenso wie ein direkt gesetzter `TextElement.Foreground` auf einem Container. Der Pfad ist nicht hit-testbar; zugängliche Namen und Tooltips gehören auf den aufrufenden Button.
- `AppIcon` und `AppIconSmall` sind `DrawingImage` mit exakt 0,0,256,256 als Designbox. Ein transparentes Rechteck hält die Box stabil. Eine kontinuierliche kubische Kontur nähert eine Superellipse an; sie ist keine behauptete Apple-Maskenformel. Violette Grundschicht, weicher innerer Radialglanz, transparente Innenplatte und gerichtete Oberkante tragen vier lavendelweiße Aufnahmewinkel und einen goldenen Mittelpunkt.
- **Bis einschließlich 32 px `AppIconSmall` verwenden:** 20 statt 12 Design-Einheiten Strichstärke, größerer Goldpunkt, weniger Materialschichten. Ab 40 px `AppIcon`. Diese Auswahl erfolgt im ICO-Renderer; ein WPF-`Image` wechselt seine Ressource nicht automatisch anhand seiner Größe.

Vollständiger Bestand der 27 Schlüssel (jeweils mit Präfix `Glyph.`): `Capture`, `Fullscreen`, `Timer`, `Ocr`, `Select`, `Rectangle`, `Ellipse`, `Arrow`, `Pen`, `Text`, `Highlight`, `Redact`, `Undo`, `Redo`, `Delete`, `Copy`, `Save`, `Close`, `Settings`, `Folder`, `History`, `Exit`, `Account`, `Check`, `Keyboard`, `Pin`, `Download`.

## Material und Zustände

Die Grundpalette bleibt #090E13 / #12171C, #7D4BE3 / #431B96, #B097E9, #D3B576 und #F4F2FA. Materialfarben sind transparente beziehungsweise aufgehellte oder abgedunkelte Ableitungen. `GlassFill` hat einen dunklen, leicht violetten Kontrastsockel mit ca. 86–92 % Alpha. Das erhält lesbare Inhalte über sehr hellen Screenshots; auf dunklem Mica entsteht mehr Materialtiefe durch Oberkante und Hintergrunddurchzeichnung. `GlassStroke` bündelt das Licht oben, `GlassHighlight` legt einen sanften Reflex über die Kontrollflächen.

Alle acht Vertragsschlüssel bleiben erhalten: `GlassFill`, `GlassStroke`, `GlassHighlight`, `GlassPanel`, `GlassIcon`, `GlassIconButton`, `GlassToolToggle`, `GlassSwatch`. Die Styles laden **ohne Theme.xaml**; keine Abhängigkeit von Theme-Styles. Die App darf Theme.xaml weiterhin zuerst mergen.

- `GlassPanel`: Border, Radius 22, Padding 10; einziger kleiner DropShadowEffect (16 Blur / 4 Tiefe / 28 %). Keine Schatten pro Icon-Button.
- `GlassIconButton`: Button, 36×36, Radius 13. Content ist ein Path mit `GlassIcon`.
- `GlassConfirmButton`: abgeleiteter Icon-Button für den rechten Fertig-Haken mit dezent violetter Füllung und hellem Lavendel-Glyph. Die Overlay-Aktionsgruppe enthält Ocr, Trenner, Close, Pin, Download (Tag `Save`), Copy, Check. Der Kopierbutton verwendet `Glyph.Copy` und den vorhandenen Nebenaktionsstil `GlassIconButton`.
- `GlassToolToggle`: TargetType ToggleButton; funktioniert ausdrücklich auch auf RadioButton. Checked färbt die Kapsel tiefviolett und den Glyphen in helles Lavendel (#D8C8FA) für Kontrast. Der normale Glyph bleibt #F4F2FA.
- Hover: weißer Reflex und 1 DIP angehoben. Pressed: abgedunkelt und 1 DIP abgesenkt. Disabled: echte Control-Opacity 0.35. `GlassFocus` liefert bei Tastaturfokus einen 2-DIP-Lavendelring außen; nicht durch einen gefärbten Glyph allein ersetzen.
- `GlassSwatch`: 28×28, Farbpunkt aus Background mit Glasring; Checked hat einen weißen 2-DIP-Außenring. Farben brauchen zusätzlich Namen/Tooltips im Aufrufer.
- Ergänzungen: `GlassPrimaryButton` (Text plus Icon, 40 hoch), `GlassDivider`, `GlassChip` und interne gemeinsam genutzte Brushes/Templates. Die Hauptaktion ist dunkler abgestimmt: selbst die konservative Kombination aus hellster Füllung, maximalem Reflex, Hover und reinweißem Hintergrund hält mit #F4F2FA mindestens ca. 4.54:1. Deaktivierte Inhalte sind absichtlich abgeschwächt.

Kein BlurEffect und keine Animationsuhren in den Styles. Der Aufrufer verwischt ausschließlich seinen Hintergrund. Der WPF-/ICO-Look ist eine statische Materialinterpretation: echte dynamische Lichtbrechung, Sensorlicht und Apples systemseitige Icon-Effekte sind nicht implementiert. Zusätzliche Verschachtelungen von GlassPanel vermeiden, damit sich Schatten und dunkle Füllungen nicht unnötig summieren.

## ICO und Kontaktbogen reproduzieren

Im Repository-Stamm unter Windows:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/render-icons.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -BuildOnly
```

`render-icons.ps1` benötigt ausschließlich vorhandenes Windows PowerShell/WPF, startet bei Bedarf selbst im STA-Modus und ist für Windows PowerShell 5.1 als UTF-8 mit BOM gespeichert. Es lädt Icons.xaml und Glass.xaml über XamlReader, rendert bei 96 DPI mit RenderTargetBitmap und schreibt das ICO direkt per BinaryWriter. Kein NuGet, kein Download, keine externen Grafiktools.

Ausgaben:

- `src/KairosCapture/KairosCapture.ico`: 16, 20, 24, 32, 40, 48, 64, 256 px; 32-bit-BGRA-DIB mit AND-Maske für 16–64, PNG-komprimierter 256er-Frame. Kleine Frames stammen aus `AppIconSmall`.
- `test-results/icons-preview.png`: 1280×1792. Alle 27 Glyphen in sechs Reihen nativ in 16/24/32 auf dunklem Glas und Glas über hellem Grund; alle acht App-Icon-Größen; echte Glass-Templates über hellen/dunklen synthetischen Screenshot-Fixtures mit Blur 10. Hover, Pressed und Fokus sind statisch simuliert und beschriftet; Checked und Disabled sind echte Zustände. Der Blur existiert nur in dieser Hintergrund-Fixture.

Der Renderer prüft alle Vertragsschlüssel, Geometrie- und Strichgrenzen, Zeichenflächen, ICO-Verzeichnis, PNG-Signatur und die WPF-Dekodierung sämtlicher acht Frames. Er prüft außerdem 35 % Disabled-Opacity, den RadioButton-Checked-Stil und den weißen Swatch-Ring. Ausgabe bei Erfolg endet mit PASS-Zeilen und den beiden Dateipfaden.

### Historischer Buildlauf vom 18.10.2026

Im damaligen isolierten Worktree fehlte der ignorierte SDK-Hinweis des Haupt-Repositories. Für diesen Build wurde die **bereits vorhandene** private SDK-Kopie verwendet, ohne Installation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -BuildOnly -DotnetPath 'C:/Users/Axel_desktop/.t3/projects/clockify/.dotnet/dotnet.exe'
```

Ergebnis dieses historischen Laufs: SDK 10.0.401; 0 Warnungen, 0 Fehler; sechs Offline-Prüfungen (`capture`, `editor`, `overlay`, `openai`, `shell`, `handoff`) bestanden. Der aktuelle Build findet den gemeinsamen privaten SDK-Hinweis selbst und führt neun Suiten aus; siehe [KAIROS.md](../../KAIROS.md). Live-Aufnahme, Hardware-DPI und echte Tastatur-/Tray-Interaktion sind damit nicht nachgewiesen.

## Visuelle Prüfung und Grenzen

Bei der Icon-Abnahme vom 19.10.2026 wurde der Kontaktbogen mit allen 27 Glyphen in Originalauflösung geöffnet. Pin und Download waren bei 16/24/32 px auf beiden Glas-Fixtures erkennbar; Kopf, Schulter und Nadel beziehungsweise Pfeil und Ablage blieben unterscheidbar. Der Renderer bestand seine Vertrags-, Bounds-, ICO- und Style-Prüfungen mit 27 Glyphen. Kontaktbogen und Abnahme-Kopien sind generierte lokale Ausgaben im ignorierten `test-results/`; `scripts/render-icons.ps1` erzeugt den aktuellen Kontaktbogen neu. Der alte getrackte Kontaktbogen mit 25 Glyphen wurde aus dem Index entfernt. Das für den Build benötigte `src/KairosCapture/KairosCapture.ico` bleibt versioniert.

Die unabhängige Gegenprüfung des bisherigen Bestands bestätigt dessen Glyphen auch bei 16 px als unterscheidbar und den Kontrast auf beiden Fixtures. Kleine verbleibende Schwächen: Capture/OCR und Pen/Highlight besitzen bei 16 px nur kleine Unterscheidungsmerkmale; OCR, Settings, History und Keyboard haben dort die dichtesten Details. Bei 24/32 px ist die Binnenzeichnung klarer. Der Goldpunkt des App-Icons bleibt bei 16/20 px sichtbar, seine goldene Farbe und Materialtiefe werden erst in größeren Größen deutlich. Die Flächen sind zugunsten des Kontrasts relativ deckend. Nach der Gegenprüfung wurde ausschließlich die zusätzliche Hauptaktionsfüllung für ihren Hover-Textkontrast abgedunkelt.

## Recherchebelege

Alle Links zuletzt abgerufen am **2026-10-19**; WWDC-Videos stammen aus den genannten Jahren. Regeln für die native Apple-Pipeline sind von dieser WPF-Übertragung zu unterscheiden.

- [Apple HIG: App icons](https://developer.apple.com/design/human-interface-guidelines/app-icons): klare Vordergrundkonturen, Hintergrund plus Vordergrundebenen, konsistente Erkennungsmerkmale in verschiedenen Darstellungen. Apple erwartet unmaskierte quadratische Quelldateien und fügt Systemmaske und Materialeffekte selbst hinzu. **Hier abweichend:** Windows-ICO benötigt die fertige Kontur und eingebrannte statische Lichteffekte.
- [WWDC25: Say hello to the new look of app icons](https://developer.apple.com/videos/play/wwdc2025/220/), besonders 0:53, 2:20, 5:18 und 9:23: Kantenglanz, Mattierung, Transluzenz und gestaffelte Ebenen; runde Konturen und kräftigere Striche erhalten Details im kleinen Maßstab. Apple nennt dort keine allgemeine 1.75-px-Regel.
- [Apple: Creating your app icon using Icon Composer](https://developer.apple.com/documentation/xcode/creating-your-app-icon-using-icon-composer): getrennte SVG-/PNG-Ebenen und systemseitige Materialkonfiguration. Icon Composer wird für diesen Windows-Build nicht benötigt.
- [Apple: Configuring and displaying symbol images](https://developer.apple.com/documentation/uikit/configuring-and-displaying-symbol-images-in-your-ui) und [WWDC19: Introducing SF Symbols](https://developer.apple.com/videos/play/wwdc2019/206/): neun Gewichte und drei optisch angepasste Skalen. Die konsistente Strichführung ist die Inspiration; 24×24 und 1.75 DIP sind der Kairos-Vertrag.
- [Apple: Creating custom symbol images](https://developer.apple.com/documentation/uikit/creating-custom-symbol-images-for-your-app): Apples eigene Symbol-Importpipeline verlangt gefüllte Konturen. Die hiesigen WPF-Glyphen bleiben gemäß Auftrag **Stroke-Geometrien** und sind keine SF-Symbol-Importdateien.
- [Apple: iOS 27](https://www.apple.com/de/os/ios/): beschreibt weiterentwickelte Lichtbrechung, Kontrast und schärfere App-Symbole. Keine öffentlich verifizierte neue numerische iOS-27-Geometrie- oder Strichvorschrift wurde daraus abgeleitet.
