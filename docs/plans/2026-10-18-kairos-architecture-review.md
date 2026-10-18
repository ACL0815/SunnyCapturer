# Kairos Capture: Architekturprüfung des Baustands

Stand 18.10.2026 · Design-Subagent Claude Opus 5.5, Ponytail *full* · Status: **Review, nichts gebaut, nichts ausgeführt**

Geprüft wurde der Root-Stand `d956eb8` („assemble prior app drafts as unverified build baseline“). Jede der 16 Dateien unter `src/KairosCapture/` ist per `diff` byte-identisch mit den Entwürfen in `t3-3693b06b` (Services), `t3-7585bde9` (Editor), `t3-9a755c10` (Shell) und `t3-e603acab` (Capture). Zeilenangaben beziehen sich auf `d956eb8`. Weitere Grundlagen: `docs/kairos/reverse/ARCHITEKTUR-ABWAEGUNG.md`, `OPENAI-OAUTH-VERTRAG.md`, `UI-BELEGE.md` (`a26bcfa`) und `docs/plans/2026-10-18-kairos-capture-build.md`.

Nicht getan: kein Build, kein Start, keine Selbstprüfung, kein Login, keine Mixed-DPI-Messung. Jede Aussage über Verhalten ist deshalb eine **statische Lesart des Codes**. Was erst eine Ausführung zeigt, steht unter „Methode“ und hat den Status *nicht ausgeführt*.

Übernommene Root-Beschlüsse: WPF/.NET 10, SDK 10.0.401 aus `clockify/.dotnet`, `Microsoft.IdentityModel.JsonWebTokens` 8.23.0 statt eigener JWT-Prüfung (Paket gehört der Shell), Hotkey Strg+Alt+A statt Sunnys F1, keine Einstellungsoberfläche im MVP.

## 1. Ergebnis

1. **Wiederverwenden statt neu schreiben.** Die vier Entwürfe passen in den Signaturen zusammen. Jeder Member, den `MainWindow.xaml.cs` aufruft, existiert in Capture, Editor und Services. Die Architektur bleibt schmal: ein Objekteditor (`AnnotationCanvas`), ein gemeinsamer Exportpfad (`Flatten()` → `ImageOutput.EncodePng`), ein OAuth-Dienst, eine Shell. Ein zusätzliches Framework ist nicht nötig.
2. **Der Stand baut nicht und startet nicht.** `App.xaml.cs` fehlt, und das Build-Skript findet das vorhandene SDK nicht (P0).
3. **Vier funktionale Lücken gegenüber Auftrag und Plan:** keine Größenänderung von Objekten, unsichtbarer Editor nach Hotkey aus dem Tray, verschluckte OAuth-Fehlermeldungen, Schwärzung nicht als letzte Ebene (P1).
4. **Produktentscheidung offen: Wo wird direkt editiert?** Die Referenzbilder zeigen Bearbeitung im eingefrorenen Overlay. Der Stand bearbeitet in einem separaten Hauptfenster. Der Umbau ist klein, wenn `AnnotationCanvas` unverändert im Overlay gehostet wird (Vertrag in 4.2).

## 2. Wiederverwendung je Modul

| Modul (Zeilen) | Urteil | Begründung |
| --- | --- | --- |
| Capture (721) | **behalten** | Physische Pixel unter PerMonitorV2: `GetSystemMetrics(76–79)` und `CopyFromScreen` (`CaptureService.cs:87–118`), Zeiger über `GetCursorPos`/`ScreenToClient` (`RegionOverlay.cs:163–175`), Prüfung negativer Koordinaten (`CaptureSelfChecks.cs`). PNG/Clipboard/Speichern hängen an einem Encoder (`ImageOutput.cs`). Kleine Korrekturen: P1-1, P2-1, P2-2, P3-3. |
| Editor (666) | **behalten, ergänzen** | Objektmodell mit Command-Undo (`AnnotationCanvas.cs:29`, `493–498`), Verschieben, erneut editierbarer Inline-Text, deckende Schwärzung mit `floor`/`ceil`. Es fehlen Größenänderung (P1-2) und Schwärzung als letzte Ebene (P1-4). Die Public API bleibt gleich. |
| Services (987) | **behalten, JWT ersetzen** | Loopback, PKCE, state/nonce, ausgestellte Client-ID, Rotation, DPAPI-Snapshot, SSE bis `response.completed`: alles vertragsnah. Die eigene RS256/JWKS-Prüfung (`OpenAiProtocol.cs:81–153`) fällt nach Root-Beschluss weg (P0-3). |
| Shell (824, `App.xaml.cs` fehlt) | **behalten, vervollständigen** | Codebehind ohne MVVM, Theme, Manifest PerMonitorV2. Es fehlen App/Tray/Argumente (P0-1). Kleine Korrekturen: P1-1, P1-3. |

Nach den Korrekturen bleiben es etwa 3.300 Zeilen in einem Projekt.

## 3. Befunde, priorisiert

### P0: Build oder Start blockiert

**P0-1 `App.xaml.cs` fehlt.** `App.xaml:1–4` deklariert `x:Class="KairosCapture.App"` mit `ShutdownMode="OnExplicitShutdown"`, aber ohne `StartupUri` und ohne Codebehind. `MainWindow.xaml.cs:524/532/536` ruft `ExitApplication()`, `IsExiting` und `NotifyHidden()` auf. Erwartet: Compilerfehler CS1061. Ohne Codebehind wäre die App ein Prozess ohne Fenster. Verantwortlich: Shell. Vertrag in 4.5.

**P0-2 Build-Skript.** `scripts/build.ps1:7–12` sucht nur `%LOCALAPPDATA%\KairosCapture\dotnet\dotnet.exe` (Ordner vorhanden, kein `sdk`) oder das `dotnet` aus PATH (nur 3.1.426) und bricht ab. Lösung: Parameter `-Dotnet <Pfad>` oder `$env:KAIROS_DOTNET`, Wert `C:\Users\Axel_desktop\.t3\projects\clockify\.dotnet\dotnet.exe`. Außerdem ruft `build.ps1:21` `--self-check` auf, der Plan nennt `--self-test`. Ein Name gilt für beide, empfohlen `--self-test`. `global.json` aus dem Shell-Entwurf wurde nicht übernommen und ist für 10.0.401 nicht nötig.

**P0-3 JWT-Tausch quer über zwei Writer.** Services kompiliert erst, wenn die Shell `<PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" Version="8.23.0" />` in `KairosCapture.csproj` gesetzt hat. Root merged beides gemeinsam, oder Shell zuerst.
- Zu löschen: `OpenAiProtocol.JwtKeyId/HasKey/ValidateIdToken` (`OpenAiProtocol.cs:81–153`) und der private Base64/JSON-Doppelschlüssel-Code, soweit nur dort benutzt.
- Aufrufstellen: `OpenAiService.cs:153–155` (Anmeldung, nonce Pflicht) und `326–328` (Refresh, nonce optional, `sub` muss gleich bleiben).
- JWKS-Quelle: `OpenAiService.cs:27` legt `https://auth.openai.com/.well-known/jwks.json` fest. Der Vertrag verlangt „veröffentlichte Schlüssel“; die Discovery wird in `RevokeAsync` (`OpenAiService.cs:388`) bereits geladen und auf Issuer geprüft. `jwks_uri` von dort übernehmen und nicht raten.

### P1: Funktion falsch oder fehlend

**P1-1 Hotkey aus dem Tray zeigt nichts.** `OnClosing` versteckt das Hauptfenster (`MainWindow.xaml.cs:530–537`). `CaptureService.cs:65–76` stellt nach der Aufnahme den vorherigen, versteckten Zustand wieder her. `MainWindow.CaptureAsync` lädt das Bild (`:157`) und ruft kein `ShowFromTray()` auf. Statische Folge: Fenster schließen → Strg+Alt+A → Bereich ziehen → kein Editor sichtbar. Lösung: eine Zeile `ShowFromTray()` nach `LoadImage` (entfällt bei Overlay-Editing für Kopieren/Speichern). Zu prüfen ist zusätzlich, ob der Speichern-Dialog aus `PrepareReplacementAsync` (`:210–220`) bei verstecktem Owner sichtbar erscheint.

**P1-2 Keine Größenänderung.** `AnnotationCanvas` verändert nur `Offset` (`:310–315`, `:412–414`). Es gibt keine Griffe und keinen Resize-Zweig in `BeginGesture` (`:271–305`). Plan §2 und ABWAEGUNG A8 verlangen beides. Minimal:
- Kastenformen (Rechteck, Ellipse, Markieren, Schwärzen): acht Griffe auf `WorldBounds`. Die Geometrie wird aus dem neuen Rechteck neu erzeugt (normalisiert, mindestens 2 px).
- Pfeil: zwei Endpunkt-Griffe. Dafür Start- und Endpunkt im `Annotation` speichern.
- Stift und Text: nur Verschieben. `ponytail:` Stift skalieren bzw. Text-Schriftgröße nachrüsten, wenn verlangt.
- Ein Resize ist genau ein `Record(undo, redo)`. Griffe des ausgewählten Objekts haben beim Treffertest Vorrang.
- Public API unverändert. Etwa 80 Zeilen.

**P1-3 OAuth-Fehlertexte werden verworfen.** `ReportError` (`MainWindow.xaml.cs:137–144`) ersetzt jede Ausnahme durch einen von drei Standardtexten. Die Services liefern bereits geprüfte deutsche Meldungen ohne Geheimnisse: `OpenAiRequestException` mit `Safe()`-Filter (`OpenAiProtocol.cs:240–273`) für Limit, fehlende Berechtigung und ungültige Sitzung, dazu `TimeoutException` und `InvalidOperationException`. Der OAuth-Vertrag („Offene Abnahme“) behandelt fehlenden Direct-Scope, Kontosperre und Nutzungslimit als eigene externe Zustände, und A9 prüft Limit und Ablehnung getrennt. Mit einem Einheitstext kann der Nutzer sie nicht unterscheiden. Lösung: für diese drei Typen aus `KairosCapture.Services` die `Message` anzeigen, für alle übrigen beim Standardtext bleiben.

**P1-4 Schwärzung nicht als letzte Ebene.** `DrawImageAndAnnotations` zeichnet alle Objekte in Listenreihenfolge (`AnnotationCanvas.cs:190–192`), die Schwärzung dabei an ihrer Stelle (`:198–205`). Ein später gezeichneter Pfeil liegt über dem Schwarz. Das ist kein Leck von Originalpixeln, verletzt aber ABWAEGUNG 5.4.3 und lässt A7 („jedes Pixel im Schwärzungsrechteck = `#000000`“) scheitern. Lösung: erst alle Nicht-Schwärzungen zeichnen, dann alle Schwärzungen. Weil die Kanten ganzzahlig sind (`floor`/`ceil`), ist die Fläche bei 96 DPI deckend. Etwa fünf Zeilen.

**P1-5 Selbstprüfungen sind kein Testnachweis.** `CaptureSelfChecks.Run` und `EditorSelfChecks.Run` hat noch niemand aufgerufen, weil es keinen Runner gibt (P0-1). Sie prüfen weder Größenänderung noch Textposition noch die Schwärzung als letzte Ebene. Bis `--self-test` grün läuft, gelten sie als ungeprüfter Code.

**P1-6 Direktes Editieren im Overlay (Root-Entscheidung).** UI-BELEGE beschreibt aus beiden Referenzbildern eine helle Auswahl mit Griffen, Größenanzeige und Werkzeugleiste darunter, also Bearbeitung an Ort und Stelle. `RegionOverlay` schließt beim Loslassen der Maus (`:107–119`) und die Shell lädt das Bild in ein separates Fenster. Beides ist „Aufnahme → direktes Editieren“, nur der zweite Weg ist gebaut. Empfehlung: zuerst den vorhandenen Ablauf grün bauen, dann `AnnotationCanvas` unverändert im Overlay hosten (4.2). Andernfalls ist die Abweichung von den Referenzen ausdrücklich festzuhalten.

### P2: messen oder klein korrigieren

**P2-1 Overlay-Abbildung ist nicht explizit 1:1.** `OverlaySurface` zeichnet das 96-DPI-Bild auf `ActualWidth × ActualHeight` (`RegionOverlay.cs:216–217`), mit `UseLayoutRounding` (`:44`) und ohne festen `BitmapScalingMode`. Ein Fenster über den ganzen virtuellen Bildschirm hat unter PerMonitorV2 *einen* DPI-Wert, die Abbildung ist also gleichmäßig. Rundet WPF die DIP-Größe aber um Bruchteile, wird neu abgetastet (Unschärfe oder 1 px Versatz). Nicht gemessen. Günstige Absicherung: `RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.NearestNeighbor)` und das Bild unter `ScaleTransform(1/DpiScaleX, 1/DpiScaleY)` in Pixelgröße zeichnen. Beleg nur über O1.

**P2-2 Feste 100 ms im Latenzpfad.** `CaptureService.cs:37–38` wartet immer 100 ms, auch wenn das Hauptfenster schon versteckt ist (Tray-Fall). Das kostet 100 ms vom A2-Budget (p95 ≤ 150 ms). Nur warten, wenn das Fenster sichtbar war. Ob 100 ms für die Ausblendanimation reichen, zeigt O5.

**P2-3 „100 %“ zeigt nicht pixelgenau an.** `SetZoom(1)` (`MainWindow.xaml.cs:306`, `315–323`) bedeutet 1 DIP je Bildpixel, bei 150 % Skalierung also 1,5 Gerätepixel je Bildpixel. Der Export ist nicht betroffen. Korrektur: Faktor `1 / VisualTreeHelper.GetDpi(this).DpiScaleX`.

**P2-4 Textsprung zwischen Bearbeiten und Export ist möglich.** Die Inline-`TextBox` (`AnnotationCanvas.cs:435–444`) setzt Padding, Rahmen und Hintergrund lokal. Die implizite `TextBox`-Style (`Theme.xaml:69–73`) wirkt weiter (FocusVisualStyle, Trigger), und das Standardtemplate hat einen internen Textrand. Die übernommenen Glyphen entstehen über `FormattedText.BuildGeometry` an `Offset` (`:472–484`). Eine Abweichung von wenigen Pixeln ist plausibel, aber nicht gemessen → T5. Bei Bedarf in der TextBox `Style = null` setzen und den gemessenen Innenrand per `Margin` ausgleichen.

**P2-5 Mit aktivem Zeichenwerkzeug lassen sich vorhandene Objekte nicht greifen.** In jedem Zeichenwerkzeug startet ein Klick ein neues Objekt (`AnnotationCanvas.cs:287–303`). Der Auswahlrahmen erscheint nur im Auswahlwerkzeug (`:177`). Nach dem Zeichnen muss der Nutzer also erst V drücken. Für direktes Editieren: Ein Klick auf einen Griff oder auf die Kontur eines vorhandenen Objekts wählt aus und verschiebt, ein Klick auf freie Fläche zeichnet.

**P2-6 OCR-Feld `instructions`.** `OpenAiService.cs:266` sendet `instructions`. Der Vertrag schließt `max_output_tokens`/`temperature` aus, `instructions` ist dort weder erlaubt noch verboten. Prüfen in A9. Lehnt der Server das Feld ab, kommt der Text als `input_text` in das `input`-Array.

### P3: Aufräumen

- **P3-1** Zeitüberschreitung bei der Anmeldung wird als Abbruch gemeldet: `attempt.CancelAfter(5 min)` (`OpenAiService.cs:90`) und der Filter `!attempt.IsCancellationRequested` (`:172`) schließen sich gegenseitig aus. Ein eigener Timeout-CTS behebt das.
- **P3-2** Die Theme-Farben (`Theme.xaml:2–10`: `#0B1015`, `#8B5CF6`, `#C4B5FD`, `#E2B866`, `#F4F1FF`) weichen von den gesampelten Tokens in ABWAEGUNG §7 ab (`#090E13`, `#7D4BE3`, `#B097E9`, `#D3B576`, `#F4F2FA`). Fünf Werte tauschen, danach A11 neu rechnen.
- **P3-3** `CaptureScreenAsync` bzw. `entireScreen` ist ein toter Pfad: Es gibt nur Aufrufe mit Standardwert (`MainWindow.xaml.cs:63/146/155/165`). Löschen.
- **P3-4** `Changed` feuert bei jeder Mausbewegung (`AnnotationCanvas.cs:340`), dadurch läuft `UpdateControls` je Bewegung (`MainWindow.xaml.cs:95`). Erst messen, wenn A3 knapp wird.

## 4. Minimale Integrationsverträge

Die Verträge beschreiben den vorhandenen Code. Neu sind nur 4.2 (wenn Root P1-6 wählt) und 4.5.

### 4.1 Capture → Shell (bestehend)

```csharp
// null = abgebrochen. Ergebnis: eingefroren, 96 DPI, Pixel = physische Bildschirmpixel, Bgr32.
public static Task<BitmapSource?> CaptureService.CaptureRegionAsync(Window owner);
public sealed class GlobalHotkey(Window owner, Action onPressed) : IDisposable { bool Register(out string? error); }
```

Invarianten:
- Auswahl in physischen Pixeln relativ zu `SM_X/YVIRTUALSCREEN`, Ende exklusiv (`CaptureGeometry.Select`).
- Nur eine Aufnahme gleichzeitig (`_capturing`).
- Esc und Rechtsklick brechen ab.

### 4.2 Overlay-Editing (nur bei Entscheidung für P1-6)

```csharp
public sealed record OverlayHandoff(AnnotationCanvas Editor, bool StartOcr);
// null = abgebrochen oder im Overlay kopiert/gespeichert. Sonst übernimmt die Werkbank den lebenden Editor.
public static Task<OverlayHandoff?> CaptureService.CaptureAsync(Window owner);
public void MainWindow.LoadEditor(AnnotationCanvas editor, string name); // LoadImage(img) = LoadEditor(new AnnotationCanvas(img))
```

- Das Overlay hostet `new AnnotationCanvas(cropped)` in einer `Viewbox` mit `Left/Top/Width/Height = Auswahl_px / DpiScale` des Overlay-Fensters. Damit ist eine Canvas-Einheit genau ein Gerätepixel.
- Werkzeugleiste unter der Auswahl. Die Styles kommen aus `Theme.xaml` und sind an `RegionOverlay` explizit zu setzen, weil implizite `Window`-Styles nicht für abgeleitete Typen gelten.
- Strg+C oder Enter: `ImageOutput.Copy(editor.Flatten())` und schließen. Strg+S: `ImageOutput.Save`. „Werkbank“ und „OCR“: Canvas aus dem Overlay lösen und als `OverlayHandoff` zurückgeben. Esc: verwerfen.
- Nicht im MVP: nachträgliches Ändern der Auswahlgröße. `ponytail:` Griffe aus P1-2 wiederverwenden, wenn verlangt.

### 4.3 Editor → alle (Public API einfrieren)

```csharp
public sealed class AnnotationCanvas : Canvas {
  public AnnotationCanvas(BitmapSource image);       // Width/Height = PixelWidth/PixelHeight; Basisbild wird nie verändert
  public EditorTool Tool { get; set; }               // Select, Arrow, Rectangle, Ellipse, Pen, Text, Highlight, Redact
  public Brush Stroke { get; set; }  public double Thickness { get; set; } // 1..24 Bildpixel
  public bool CanUndo { get; }  public bool CanRedo { get; }  public bool HasChanges { get; }
  public event EventHandler? Changed;
  public void Undo(); public void Redo(); public void DeleteSelected(); public void CommitText();
  public BitmapSource Flatten();                     // eingefroren, PixelWidth×PixelHeight, 96 DPI, Pbgra32, Schwärzungen zuletzt
}
```

Größenänderung, Schwärzung zuletzt und Greifen aus Zeichenwerkzeugen bleiben intern. Damit muss kein anderer Writer seinen Code anpassen.

### 4.4 Export (eine Quelle für alles)

```csharp
byte[] ImageOutput.EncodePng(BitmapSource);   // einziger Encoder
void ImageOutput.Copy(BitmapSource);           // Clipboard: Bitmap + "PNG" aus denselben Bytes
string? ImageOutput.Save(BitmapSource, Window);// atomar über .tmp + Move
```

Invariante: Kopieren, Speichern und OCR gehen von `editor.Flatten()` derselben Dokumentrevision aus. Alle Stellen sind bereits so gebaut (`MainWindow.xaml.cs:249–250`, `267`, `461–462`). Für denselben Stand gilt: `EncodePng(Flatten())` liefert byte-identische Ausgaben (prüfbar in `--self-test`).

### 4.5 Shell/App (fehlend, vom Shell-Writer zu liefern)

```csharp
public partial class App : Application {
  public bool IsExiting { get; }
  public void ExitApplication();   // IsExiting = true, Tray entfernen, Shutdown()
  public void NotifyHidden();      // einmaliger Tray-Hinweis „läuft weiter“
  // OnStartup(args):
  //   --self-test <dir>      STA: CaptureSelfChecks.Run, EditorSelfChecks.Run, Offline-Protokollchecks
  //                          → <dir>/self-test.json, ExitCode 0/1, kein Fenster, kein Netz, kein Hotkey
  //   --render-preview <png> MainWindow(diagnosticsMode: true) → RenderTargetBitmap → PNG
  //   --demo                 MainWindow mit lokalem Beispielbild
  //   (ohne)                 Tray (WinForms NotifyIcon: Aufnehmen, Öffnen, Beenden) + MainWindow
}
```

### 4.6 Services → Shell (bestehend, JWT intern getauscht)

```csharp
OpenAiService : IDisposable
  IReadOnlyList<OpenAiAccount> Accounts; OpenAiAccount? ActiveAccount; void SelectAccount(string key);
  Task SignInAsync(string? key, CancellationToken); Task<string?> SignOutAsync(CancellationToken); // Rückgabe = Warnung
  Task<IReadOnlyList<OpenAiModel>> GetModelsAsync(CancellationToken);
  Task<string> ExtractTextAsync(byte[] png, string slug, IProgress<string>?, CancellationToken);
// Ausnahmen mit nutzertauglicher Message: OpenAiRequestException, TimeoutException, InvalidOperationException (P1-3)
```

Intern ersetzt: `ValidateIdToken` → `JsonWebTokenHandler.ValidateTokenAsync` mit:
- `ValidIssuer = "https://auth.openai.com"`
- `ValidAudience = <ausgestellte Client-ID>`
- `ValidAlgorithms = { "RS256" }`
- `IssuerSigningKeys` aus `JsonWebKeySet(jwks_uri-Antwort)`
- `RequireSignedTokens`, `RequireExpirationTime`

Eigener Code bleibt nur für drei Prüfungen: nonce, Gleichheit von `sub`, `azp` bei mehreren Audiences. Ist der Schlüssel unbekannt (`SecurityTokenSignatureKeyNotFoundException`), wird die JWKS einmal neu geladen.

## 5. Abnahmekriterien

Status aller Punkte: **nicht ausgeführt**. Die Selbstprüfungen in `--self-test` sind die einzigen automatisierbaren Teile. O-, E- und alle UI-Punkte brauchen das laufende Programm. Für Mixed-DPI ist ein Aufbau mit 100 % + 150 % (+175 %) und einem Monitor mit negativen Koordinaten nötig. Ob dieser Aufbau am Arbeitsrechner vorhanden ist, wurde nicht geprüft. Fehlt er, bleibt K1 offen und wird nicht als bestanden gemeldet.

### 5.1 Overlay, Pixel, DPI

| ID | Soll | Methode |
| --- | --- | --- |
| O1 | Das Overlay zeigt das eingefrorene Bild deckungsgleich. | Testbild mit 1-px-Schachbrett und Koordinatengitter vollflächig auf jedem Monitor. Overlay öffnen, Auswahl = ganzer Monitor. Ein zweiter Prozess macht `CopyFromScreen`. Diff zum eingefrorenen Bild außerhalb des 2-px-Rahmenbands und der Beschriftung: **0 Pixel**, je Monitor. |
| O2 | Auswahl → Export pixelgenau. | `SetCursorPos`/`SendInput`: Ziehen von (x1,y1) nach (x2,y2) in physischen Pixeln, je einmal auf jedem Monitor, über eine Monitorgrenze und auf dem Monitor mit negativen Koordinaten. Export-PNG hat genau `|x2−x1| × |y2−y1|` und ist pixelgleich mit dem Ausschnitt einer Referenzaufnahme. |
| O3 | Das Overlay deckt nach dem DPI-Wechsel weiter den virtuellen Bildschirm. | `GetWindowRect(overlay)` gleich den `SM_*VIRTUALSCREEN`-Werten, auch wenn das Overlay auf einem Monitor mit anderer DPI als dem primären startet. |
| O4 | Abbruch ist zuverlässig. | Esc und Rechtsklick vor, während und nach dem Ziehen → keine Aufnahme, Hauptfenster in vorherigem Zustand. Fokus nach dem Hotkey: Esc wirkt ohne vorherigen Klick. |
| O5 | Kairos nimmt sich nicht selbst auf. | Hauptfenster sichtbar → Hotkey → Export enthält keine Kairos-Fensterpixel (Vergleich gegen eine Aufnahme ohne Kairos). |
| O6 | Tray-Fall. | Fenster geschlossen → Hotkey → Auswahl → Editor (Werkbank oder Overlay) ist sichtbar und hat den Fokus (P1-1). |
| E1 | Editor im Overlay, falls P1-6 gewählt: 1 Canvas-Einheit = 1 Gerätepixel. | Schwärzung von Bildschirm (a,b) nach (c,d) ziehen → Schwarz im Export exakt `[a−x0, c−x0) × [b−y0, d−y0)`. Nach innen 0 px, nach außen höchstens 1 px. Auf jedem Monitor. |

### 5.2 Wiedereditierbarer Text

| ID | Soll | Methode |
| --- | --- | --- |
| T1 | Übernahme auf allen Wegen. | Strg+Enter, Klick außerhalb, Werkzeugwechsel, Undo-Taste und `Flatten()` übernehmen den Text. Esc verwirft ihn, danach ist der Export byte-identisch mit dem Stand davor. |
| T2 | Wieder öffnen. | Doppelklick im Auswahlwerkzeug und Klick mit dem Textwerkzeug auf vorhandenen Text öffnen ihn mit gleichem Inhalt, Cursor am Ende. |
| T3 | Bearbeiten als ein Undo-Schritt. | Text ändern → Undo → Export byte-identisch mit vorher, Redo → mit nachher. Ist der Text unverändert, entsteht kein Undo-Schritt. |
| T4 | Leeren = Löschen. | Vorhandenen Text leeren und übernehmen → Objekt entfernt, ein Undo-Schritt stellt es wieder her. |
| T5 | Kein Positionssprung. | Canvas einmal mit offener TextBox, einmal nach Übernahme per `RenderTargetBitmap` rendern. Bounding-Box der Glyphenpixel weicht um **≤ 1 px** ab (P2-4). |
| T6 | Verschoben, dann bearbeitet. | Text verschieben → wieder öffnen → TextBox steht an der neuen Position, Übernahme ohne Rücksprung. |
| T7 | Zoom in der Werkbank. | Bei 50 % und 200 % öffnet die TextBox deckungsgleich über dem Text (Sichtprüfung plus Screenshot). |

`EditorSelfChecks.cs:76–98` deckt Teile von T1–T4 ab, T5 und T6 noch nicht.

### 5.3 Auswählen, Verschieben, Größe, Undo

| ID | Soll | Methode |
| --- | --- | --- |
| M1 | Ein Klick wählt das oberste Objekt. | Zwei überlappende Rechtecke, Klick in die Schnittfläche → das zuletzt gezeichnete Objekt bewegt sich. |
| M2 | Verschieben exakt. | Ganzzahliges Delta (dx,dy) → Rechteck im Export genau um (dx,dy) versetzt, Schwärzung höchstens 1 px außen. |
| M3 | Esc während des Ziehens stellt den Ursprung wieder her, ohne Undo-Schritt. | Vorhandene Prüfung in `EditorSelfChecks.cs:61–72` erweitern. |
| M4 | Begrenzung auf das Bild. | Über den Rand ziehen → Objekt bleibt im Bild (vorhanden: `EditorSelfChecks.cs:50–53`). |
| R1 | Acht Griffe für Kastenformen. | Jeder Griff verändert nur seine Kanten. Ziehen über die Gegenkante normalisiert, Mindestgröße 2 px. Für Rechteck, Ellipse, Markieren und Schwärzen. |
| R2 | Pfeil-Endpunkte. | Zwei Griffe, Pfeilspitze folgt dem Endpunkt. |
| R3 | Grenzen sind dokumentiert. | Stift und Text haben keine Größengriffe. |
| U1 | Ein Schritt je Aktion. | Hinzufügen, Verschieben, Größe, Text, Löschen sind je genau ein Undo-Schritt. Klick ohne Bewegung und Miniziehen erzeugen keinen. |
| U2 | Rundlauf. | Feste Folge aus N ≥ 10 gemischten Aktionen → N × Undo → `Flatten()` byte-identisch mit dem unbearbeiteten Bild → N × Redo → byte-identisch mit dem Endstand. |
| U3 | Redo wird verworfen. | Nach Undo eine neue Aktion → `CanRedo == false`. |
| S1 | Schwärzung deckt immer. | Schwärzung, darüber ein Pfeil → jedes Pixel im Schwärzungsrechteck `#000000` mit Alpha 255 (P1-4). Ebenfalls mit Bruchteil-Koordinaten und nach Verschieben/Größenänderung. |
| X1 | Gemeinsamer Export. | Für denselben Stand byte-identische `EncodePng(Flatten())` bei zwei Aufrufen. Datei und Clipboard-„PNG“ haben dieselben Bytes. Die OCR-Nutzlast ist dieselbe Funktion. |

R1, R2, U2, S1 und X1 gehören als Offline-Fälle in `--self-test`. Die übrigen ABWAEGUNG-Kriterien A2–A6, A9–A11 gelten unverändert.

## 6. Risiken

| Risiko | Lesart | Wirkung | Absicherung |
| --- | --- | --- | --- |
| WPF-Fenster über Monitore mit gemischter DPI | WPF wählt *eine* DPI je Fenster; der Code rechnet in physischen Pixeln. Theoretisch korrekt, nicht gemessen. | Versatz oder Unschärfe im Overlay | O1–O3, P2-1. Scheitert O1/O2 auch mit einem Overlay je Monitor → K1 (Qt) gemäß ABWAEGUNG. |
| Riesige Overlay-Bitmap (3 × 4K ≈ 11520 × 2160) | Ein neues Overlay je Aufnahme, keine Vorwärmung, zwei Vollkopien (`Bitmap` + `BitmapSource`) | A2/A3/A5 verfehlt | Messen, erst dann Overlay wiederverwenden oder vorwärmen. |
| HDR-/geschützte Inhalte | GDI `CopyFromScreen` liefert bei HDR möglicherweise falsche Farben, geschützte Inhalte schwarz | Falsche Aufnahme | Als Grenze dokumentieren, kein MVP-Umbau auf WGC. |
| Parallele Writer | Shell besitzt csproj, Services braucht das Paket. Overlay-Editing (4.2) berührt Capture und Shell. | Merge- und Build-Bruch | Root merged in der Reihenfolge Shell → Services → Editor → Capture. Die Editor-API (4.3) bleibt eingefroren. |
| SIWC-Zulassung und Modellkatalog | Nicht geprüft (OAuth-Vertrag „Offene Abnahme“) | Keine OCR trotz korrektem Code | A9 mit eigenem Konto. Keine Ersatzabrechnung per API-Schlüssel. |
| Doppelte Instanz | Kein Single-Instance-Schutz. Die zweite Instanz bekommt keinen Hotkey, der Token-Speicher ist prozessübergreifend gesperrt. | Verwirrung, kein Datenverlust | Die vorhandene Meldung „Hotkey belegt“ reicht für das MVP. |

## 7. Bewusst nicht gefordert

Einstellungsoberfläche, Speicherpfad- und Dateinamensvorlagen, Quick-/Auto-Save, Video/GIF, Geräte, Übersetzung, Pin, Mosaik, Nummerierung und Schriftart-/Schriftstilleiste. Die Einstellungsbilder belegen den Funktionsumfang der Referenz, nicht den Auftrag (UI-BELEGE, „Folgen für Kairos“). Nachrüsten, wenn der Nutzer es verlangt.

## 8. Stop-Regel

Beim Lesen trat kein Problem zweimal auf. Die Stop-Regel wurde nicht ausgelöst.
