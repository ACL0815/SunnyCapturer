# WPF-Foreground: Ursachenbeleg und minimaler Fixvorschlag

Stand: 18.10.2026, Diagnose Eskalation1. Keine Unterdelegation. Ausschließlich lesender Zugriff auf den Shell-Worktree; alle eigenen Versuche liefen unter `test-results/foreground-diagnosis` im Diagnose-Worktree.

## Ergebnis und Übergabe an Root

**Der gestoppte Grid-Patch ist für die gemeldeten schwarzen Überschriften und Beschriftungen korrekt.** Er setzt einen gemeinsamen Vererbungsursprung und erhält gleichzeitig die schwarze Beschriftung des weißen ChatGPT-Buttons. Der vollständige Minimalvorschlag ab diesem Stand ist: Grid-Patch beibehalten, den entfernten pauschalen TextBlock-Foreground-Setter entfernt lassen und **genau ein `Foreground="{TemplateBinding Foreground}"` am internen ToggleButton des gemeinsamen ComboBox-Templates ergänzen**. Damit werden auch die zuvor weiterhin schwarzen Dropdown-Pfeile hell. Keine einzelnen Header-, Dateiname-, AccountStatus- oder Button-Text-Overrides erforderlich.

Die Ursache ist durch effektive DependencyProperty-Werte und deren `BaseValueSource` belegt. Der abschließende isolierte Lauf ergab **203 bestandene Assertions, acht vollständige XAML-Zustände und vier Mikro-Gegenproben**, Exitcode 0. Diese Zahl enthält bewusst erwartete Fehlzustände der rekonstruierten Baselines; sie behauptet keine Abnahme des ursprünglichen fehlerhaften Produkts.

Root meldete anschließend, den Grid-Patch beizubehalten, genau die nachgewiesene TemplateBinding-Weitergabe integriert und Farbassertionen für Dateiname, AccountStatus, erzeugten ChatGPT-Text und ComboBox-Pfeil ergänzt zu haben. Release-Build, Self-Test, neuer Render und anschließende Sichtprüfung liegen bei Root. Dieser Bericht bescheinigt die isolierten Diagnoseergebnisse; er greift deren Ergebnis nicht vor. Keine weiteren Produktänderungen oder Diagnoseausweitungen durch diesen Agenten.

## Quellenstand und Quellenrace

Gelesener Shell-Worktree: `C:/Users/Axel_desktop/.t3/worktrees/kapture/t3-65769d4f`.

Eingefrorener Shell-Commit während der Beweisführung: `5ccc738be2e8a0d91369133b65c2a63d85c1d6be` (`Complete WPF shell, offline diagnostics and private SDK build`). Zu diesem Zeitpunkt war der Shell-Worktree laut `git status --porcelain` sauber. Die vier Dateien wurden vor den Versuchen in den eigenen Harness kopiert. Deren Hashes wurden nach den Versuchen erneut am Shell-Quellstand gelesen und waren unverändert. Roots spätere Integration der TemplateBinding gehört nicht zu diesen Snapshots.

Originalbaseline aus Git: `d956eb81928168014e718c3196b3ce35138103ec`. `git show d956eb8:src/KairosCapture/Theme.xaml` enthält den ursprünglichen impliziten TextBlock-Foreground-Setter; `MainWindow.xaml` enthält dort den weißen ChatGPT-Button mit lokalem `Foreground="#101010"` sowie ein nacktes Inhalts-Grid ohne Background oder Text-Vorgaben.

Das zuerst gemeldete rote PNG mit 131.747 Byte und Zeit 16:04:39 war bei Prüfung bereits ersetzt. Tatsächlich unverändert betrachtet wurde `test-results/main-window.png` im Shell-Worktree mit **133.555 Byte, LastWriteTime 16:05:18**, SHA256 `56E8271EA38A0EBC2A314B17FB9C95437E065AA985FBF394AFA2B642298B461D`. Dort sind Kairos-Titel, OCR-Titel, Dateiname und AccountStatus hell und lesbar, der ChatGPT-Button schwarz auf weiß. Das Bild wurde weder bearbeitet noch erneut durch diesen Agenten erzeugt. Den alten fehlerhaften Zustand belegt deshalb die kontrollierte Rekonstruktion, keine behauptete Sichtprüfung des ersetzten PNG.

SHA256 der eingefrorenen Quellkopien:

```text
Theme.xaml
3BF56538444C9D2D0EA9B533A65568D275DE7D39F93DB9B6B58C9BA0B033F7E1
MainWindow.xaml
3C3B7020DDED81C187D1F2342324814DFC3BE69D9301B0FFBDDF3D0DE575D66C
App.xaml
6607DC896B6005681F30A365041C79D01B9BA2687D31E8A0D8B9222A79A72041
AppDiagnostics.cs
91A7E20CE247524D54C30F39862F771AE7895F00C2E32FAE094B5EE3C04440AC
```

## Ursache: vom Symptom zur konkreten WPF-Wertquelle

### 1. Der implizite Window-Style wurde auf MainWindow nicht angewendet

`Theme.xaml:17–20` definiert ausschließlich einen impliziten Style mit dem Ressourcenschlüssel `typeof(Window)`:

```xml
<Style TargetType="Window">
  <Setter Property="FontFamily" Value="Segoe UI Variable, Segoe UI" />
  <Setter Property="FontSize" Value="13" />
  <Setter Property="Foreground" Value="{StaticResource TextBrush}" />
  <Setter Property="Background" Value="{StaticResource BackgroundBrush}" />
</Style>
```

`App.xaml:6` lädt das Dictionary in die Application-Ressourcen. Das Laden fehlt also nicht. `MainWindow.xaml:1–5` hat `x:Class="KairosCapture.MainWindow"` ohne expliziten `Style`; `MainWindow.xaml.cs:26` deklariert `MainWindow : Window`. WPF sucht implizite Styles nach dem **exakten Laufzeittyp**. Die CLR-Vererbung von Window überträgt diesen impliziten Style nicht automatisch auf MainWindow. Das ist in Microsofts [Dependency property value precedence, Abschnitt „The Style property“](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/properties/dependency-property-value-precedence#the-style-property) ausdrücklich beschrieben.

Die Mikro-Gegenprobe mit demselben geladenen Dictionary bestätigt den Unterschied:

```text
[micro: plain Window / implicit style]
Window.Foreground=#FFF4F2FA; source=Style
Window.Style=System.Windows.Style; source=ImplicitStyleReference
defaultLabel/TextBlock=#FFF4F2FA; source=Inherited

[micro: DerivedWindow / implicit style]
Window.Foreground=#FF000000; source=DefaultStyle
Window.Style=<null>; source=Default
defaultLabel/TextBlock=#FF000000; source=Inherited

[micro: DerivedWindow / explicit style]
Window.Foreground=#FFF4F2FA; source=Style
Window.Style=System.Windows.Style; source=Local
defaultLabel/TextBlock=#FFF4F2FA; source=Inherited
```

Damit ist auch die Alternative `Style="{StaticResource {x:Type Window}}"` an einem abgeleiteten Fenster unabhängig nachgewiesen. Sie ist nach Roots Entscheidung zum funktionierenden Grid-Patch keine zusätzliche Integrationsanforderung.

### 2. Der ursprüngliche TextBlock-Setter verdeckte den fehlenden Ursprung und überstimmte den Button-Inhalt

In `d956eb8`, `Theme.xaml:21`, stand:

```xml
<Style TargetType="TextBlock">
  <Setter Property="Foreground" Value="{StaticResource TextBrush}" />
</Style>
```

Dieser Setter machte die Überschriften trotz schwarzer Window-Foreground hell. Er wirkte aber ebenso auf den vom ContentPresenter erzeugten TextBlock für `LoginButton.Content="Weiter mit ChatGPT"`. `Theme.xaml:26–40` definiert den Button-Style und dessen ContentPresenter; `MainWindow.xaml:121` im eingefrorenen Stand setzt lokal `Background="#F4F4F4"` und `Foreground="#101010"` am LoginButton.

Entscheidend ist, dass die Prioritäten **am jeweiligen Objekt** ausgewertet werden: Der lokale schwarze Wert gewinnt am Button. Am untergeordneten TextBlock hat dessen eigener Style-Setter jedoch Vorrang vor einem geerbten schwarzen Wert. Der lokale Button-Wert kann einen eigenen Style-Setter des Kindes nicht überstimmen. Die relevanten WPF-Prioritäten sind Style-Setter vor Default-Theme-Style vor Vererbung; lokale Werte stehen darüber. Quelle: Microsofts [DependencyProperty-Prioritätsliste](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/properties/dependency-property-value-precedence#dependency-property-precedence-list).

Die exakte Originalbaseline ergibt:

```text
[A-original-d956eb8]
Window.Foreground=#FF000000; source=DefaultStyle; local=Unset
Kairos Capture=#FFF4F1FF; source=Style; local=Unset
LoginButton.Foreground=#FF101010; source=Local; local=#FF101010
LoginButton.rendered.TextBlock=#FFF4F1FF; source=Style; local=Unset
LoginButton.Background=#FFF4F4F4; source=Local; local=#FFF4F4F4
```

Die isolierte Wiederaufnahme des ursprünglichen Setters auf der aktuellen Palette reproduziert den Buttonfehler sogar mit korrekt angewendetem Window-Style und hellem Grid:

```text
[E-counter-style-restored]
Window.Foreground=#FFF4F2FA; source=Style
Grid.Foreground=#FFF4F2FA; source=Local
LoginButton.Foreground=#FF101010; source=Local
LoginButton.rendered.TextBlock=#FFF4F2FA; source=Style
```

Die Ursache ist somit der Setter gegenüber Vererbung, unabhängig von den leicht geänderten Palettenfarben.

### 3. Entfernen des Setters allein erzeugte die zweite Sollverletzung

Im eingefrorenen `Theme.xaml` fehlt der implizite TextBlock-Foreground-Setter. `MutedText` (`:21`) setzt seinen Foreground bewusst selbst; `Glyph` (`:22`) lässt ihn erben. Die ungestylten Texte an `MainWindow.xaml:16` (Kairos), `:75` (Empty State), `:86` (Dateiname), `:100` (OCR-Titel) und `:119` (AccountStatus) haben keine lokale Foreground.

Für die Rekonstruktion B wurden ausschließlich die drei zuletzt hinzugefügten `TextElement.*`-Attribute aus einer Kopie des aktuellen Root-Grids entfernt. Dessen bereits dunkler Background blieb bestehen. B ist eine kontrollierte Quellenrekonstruktion des Vor-Textpatch-Verhaltens, keine bytegenaue Rekonstruktion des ersetzten alten PNG.

```text
[B-pre-grid-text-patch]
Window.Style=<null>; source=Default
Window.Foreground=#FF000000; source=DefaultStyle
Grid.Foreground=#FF000000; source=Inherited
Grid.Background=#FF090E13; source=Local
Kairos Capture=#FF000000; source=Inherited
Text aus Bild=#FF000000; source=Inherited
Noch kein Bild=#FF000000; source=Inherited
Konto verbinden=#FF000000; source=Inherited
Ein Ausschnitt. Alles im Blick.=#FF000000; source=Inherited
LoginButton.rendered.TextBlock=#FF101010; source=Inherited
```

Die Gegenprobe trifft genau das gemeldete Muster: Button wieder lesbar, gewöhnliche Beschriftungen schwarz auf dunklem Untergrund.

### 4. Warum der letzte Grid-Patch die genannten Texte korrekt behebt

Der gestoppte Stand `MainWindow.xaml:6–7` setzt einen gemeinsamen Ursprung:

```xml
<Grid Background="{StaticResource BackgroundBrush}"
      TextElement.Foreground="{StaticResource TextBrush}"
      TextElement.FontFamily="Segoe UI Variable, Segoe UI"
      TextElement.FontSize="13">
```

Der Harness prüft per `ReferenceEquals`, dass `Control.ForegroundProperty`, `TextBlock.ForegroundProperty` und `TextElement.ForegroundProperty` dieselbe DependencyProperty sind, und prüft die `Inherits`-Metadaten. Die Attached-Property am Grid ist deshalb ein wirksamer Vererbungsursprung. Microsoft dokumentiert die [Vererbung nur ohne höher priorisierten Kindwert](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/properties/property-value-inheritance) und die [Inherits-Metadaten von TextElement.Foreground](https://learn.microsoft.com/en-us/dotnet/api/system.windows.documents.textelement.foreground?view=windowsdesktop-10.0).

Ein lokaler Wert am Grid wird am gewöhnlichen TextBlock als `Inherited` wirksam. Am LoginButton gewinnt weiterhin dessen lokales Schwarz; dessen erzeugter TextBlock erbt dieses Schwarz. Gewollte MutedText-Styles und lokale Sonderfarben behalten ihre Priorität. Die Gegenprobe C ergibt:

```text
[C-latest-grid-patch]
Window.Foreground=#FF000000; source=DefaultStyle
Grid.Foreground=#FFF4F2FA; source=Local
Grid.FontFamily=Segoe UI Variable, Segoe UI; source=Local
Grid.FontSize=13; source=Local
Kairos Capture=#FFF4F2FA; source=Inherited; local=Unset
Text aus Bild=#FFF4F2FA; source=Inherited; local=Unset
Noch kein Bild=#FFF4F2FA; source=Inherited; local=Unset
Konto verbinden=#FFF4F2FA; source=Inherited; local=Unset
Ein Ausschnitt. Alles im Blick.=#FFF4F2FA; source=Inherited; local=Unset
LoginButton.Foreground=#FF101010; source=Local
LoginButton.rendered.TextBlock=#FF101010; source=Inherited; local=Unset
LoginButton.Background=#FFF4F4F4; source=Local
MutedText/Modell=#FFA4A0B2; source=Style
Glyph/RefreshModels=#FFF4F2FA; source=Inherited
```

Die weiterhin schwarze Foreground des Window-Objekts ist bei dieser bewusst gewählten Inhaltswurzel kein Gegenbeweis: Alle genannten Inhaltsbeschriftungen beziehen ihre Farbe vom Grid. Die Alternative D/H beweist zusätzlich, wie der vorhandene Window-Style explizit auf das abgeleitete Fenster angewendet werden könnte. Root benötigt dafür keinen weiteren Umbau des bereits funktionierenden Grid-Patches.

### 5. Verbliebene schwarze ComboBox-Pfeile und genau eine ursächliche Template-Korrektur

`Theme.xaml:75` setzt die ComboBox-Foreground hell. In deren Template (`:79–80`) erzeugt ein interner ToggleButton den Pfeil-TextBlock. Dieser ToggleButton hat einen eigenen System-Theme-Style mit schwarzer Foreground. Der Default-Style steht vor Vererbung. Deshalb reicht weder das helle Grid noch ein expliziter Window-Style über diese Grenze hinweg:

```text
[C-latest-grid-patch]
ComboBox.Foreground/ModelPicker=#FFF4F2FA; source=Style
ComboBox.ToggleButton/ModelPicker=#FF000000; source=DefaultStyle
ComboBox.chevron/ModelPicker=#FF000000; source=Inherited

[D-explicit-window-style]
ComboBox.ToggleButton/ModelPicker=#FF000000; source=DefaultStyle
ComboBox.chevron/ModelPicker=#FF000000; source=Inherited
```

Dasselbe wurde für `ThicknessPicker`, `ModelPicker` und `AccountPicker` gemessen. Der alte pauschale TextBlock-Setter hatte auch diese Template-Lücke verdeckt. Er darf wegen des nachgewiesenen ChatGPT-Rückfalls nicht als Lösung zurückkehren.

**Minimalvorschlag ab dem gestoppten Stand**, ausschließlich am gemeinsamen Template:

```diff
-        <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" ClickMode="Press">
+        <ToggleButton Focusable="False" Foreground="{TemplateBinding Foreground}" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" ClickMode="Press">
```

Der Binding-Ursprung ist die templated ComboBox. Das ist die Weitergabe einer Control-Eigenschaft innerhalb ihres Templates; keine hart kodierte Farbe und kein lokales Pflaster an einzelnen sichtbaren Beschriftungen. Microsoft beschreibt [TemplateBinding](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/templatebinding-markup-extension) als Bindung an eine Eigenschaft des templated Controls.

Genau diese einzelne Änderung am Dictionary-Snapshot wurde mit unverändertem letzten Grid-Patch als G geprüft:

```text
[G-grid-and-combo-foreground-contract]
Kairos Capture=#FFF4F2FA; source=Inherited
Text aus Bild=#FFF4F2FA; source=Inherited
Noch kein Bild=#FFF4F2FA; source=Inherited
Konto verbinden=#FFF4F2FA; source=Inherited
LoginButton.rendered.TextBlock=#FF101010; source=Inherited
ComboBox.ToggleButton/ModelPicker=#FFF4F2FA; source=ParentTemplate
ComboBox.chevron/ThicknessPicker=#FFF4F2FA; source=Inherited
ComboBox.chevron/ModelPicker=#FFF4F2FA; source=Inherited
ComboBox.chevron/AccountPicker=#FFF4F2FA; source=Inherited
Grid render corner ARGB=#FF090E13
```

### 6. Transparenter Preview-Hintergrund ist ein eigener, belegter Renderpfad

`AppDiagnostics.cs:109–111` rendert das von `LayoutPreview` gelieferte `window.Content`-Element. `:121` liefert das Grid, nicht die komplette Window-Oberfläche. Deshalb wird `Window.Background` beim Content-only-Render nicht mitgerendert. Background erbt hier nicht wie Foreground.

Die Originalbaseline A hat `Grid.Background=<null>` und einen transparenten Eckpixel `#00000000`. Gegenprobe F setzt den Window-Style korrekt, lässt aber ausschließlich den Grid-Background weg: `Window.Background=#FF090E13`, trotzdem Eckpixel `#00000000`. Mit explizitem Grid-Background in C/G ist derselbe Eckpixel `#FF090E13`. Deshalb den bereits hinzugefügten Grid-Background beibehalten. Für diese Messung wurde nur ein flüchtiges RenderTargetBitmap im Speicher ausgelesen; kein Bild bearbeitet oder gespeichert.

## Prüfumfang und Integrationsgrenze

Der Harness verwendet die tatsächlichen Dictionary- und MainWindow-XAML-Snapshots. Er lädt den Inhaltsbaum per `XamlReader`, entfernt ausschließlich Ereigniszuweisungen, hängt ihn an eine eigene `DerivedWindow : Window`-Klasse und misst/arrangiert den Inhalt wie AppDiagnostics. Das erhält die relevante abgeleitete Typidentität für die Style-Suche, die tatsächlichen Controls, benannten Texte und Templates. Es wird kein Produkt-Code-behind, keine Benutzer-App, kein Authentifizierungsablauf, keine Clipboard-/Tray-/Hotkey-Funktion und keine Netzwerkanfrage gestartet. Weder `Window.Show` noch `Application.Run` wird aufgerufen; jeder Fall prüft `!window.IsVisible`.

Erfasste Zustände: A Originalbaseline; B Textpatch rückgängig; C gestoppter Grid-Patch; D vorhandener Window-Style explizit zugewiesen statt Grid-Textvorgaben; E pauschaler TextBlock-Setter als Rückfallprobe; F korrekter Window-Style ohne Grid-Background; G übernommener Minimalvorschlag; H dieselbe Template-Weitergabe mit explizitem Window-Style. Dazu vier Mikro-Fälle für Window, DerivedWindow, expliziten Style und Grid-Vererbung mit gewöhnlichem TextBlock und explizitem TextBlock-Button-Inhalt.

`defaultLabel` im Mikrotest ist eine gewöhnliche TextBlock-Beschriftung, entsprechend den untersuchten MainWindow-Elementen. Zusätzlich wurde ein echter WPF-`Label` gemessen: Er behält hier mit `source=DefaultStyle` Schwarz, auch bei hellem Ursprung. Der Harness bestätigt, dass MainWindow selbst keine `Label`-Controls enthält. Eine pauschale Behauptung, jeder beliebige WPF-Control-Theme-Style erbe automatisch hell, wäre daher falsch; daraus wird kein weiterer Scope eröffnet.

Der eingefrorene `AppDiagnostics.CheckShell` (`:86–97`) prüfte STA und Layoutmaße, aber keine Foreground-Werte. Ein damaliges grünes Self-Test-Ergebnis konnte diese Farbfehler daher nicht ausschließen. Roots nun gemeldete Regressionserweiterung an den tatsächlich aufgelösten Texten/Pfeilen adressiert genau diese Prüflücke. Insbesondere reicht eine Assertion auf `LoginButton.Foreground` allein nicht, wie Fall A/E zeigt; maßgeblich ist der erzeugte TextBlock.

Der erste Harness-Build erfasste versehentlich die als Beleg kopierte `snapshots/AppDiagnostics.cs` über den SDK-Compile-Glob. Das wurde ausschließlich im temporären Harness durch explizites `Compile Include="Program.cs"` korrigiert. Danach: Build 0 Warnungen/0 Fehler; vollständiger Diagnose-Lauf Exitcode 0. Kein Produktfix wurde daraus abgeleitet. Es gab keine wiederholte gescheiterte Foreground-Nachbesserung in der Shell. Die erwarteten roten A/B/E/F-Gegenproben sind Beweisfälle. Die Diagnose endet mit diesem Bericht; keine weitere Fix-/Render-Schleife gegen den gestoppten Shell-Stand.

## Nachweispfade und reproduzierbarer Aufruf

Eigener Worktree: `C:/Users/Axel_desktop/.t3/worktrees/kapture/t3-8782429b`.

- Bericht (einzige commitbare Änderung): `docs/plans/2026-10-18-theme-foreground-diagnosis.md`.
- Temporärer Harness: `test-results/foreground-diagnosis/ForegroundProbe.csproj` und `Program.cs`.
- Eingefrorene Dateien: `test-results/foreground-diagnosis/snapshots/{Theme.xaml,MainWindow.xaml,App.xaml,AppDiagnostics.cs}`.
- Originalbaseline-Kopien: `test-results/foreground-diagnosis/snapshots/{Theme.d956eb8.xaml,MainWindow.d956eb8.xaml}`.
- Vollständiger Lauf: `test-results/foreground-diagnosis/probe-output.txt`.
- Ausgeführte Assembly: `test-results/foreground-diagnosis/bin/Release/net10.0-windows/ForegroundProbe.dll`.
- SDK ausschließlich gelesen/ausgeführt: `C:/Users/Axel_desktop/.t3/projects/clockify/.dotnet/dotnet.exe`, SDK **10.0.401**, WindowsDesktop-Runtime **10.0.12**.

`test-results/` ist im Repository bereits ignoriert. Keine Produktdatei und keine SDK-Datei wurde geändert. Projektdatei, vollständiger Harness-Quelltext und vollständige erfolgreiche Testausgabe sind unten eingebettet, damit der Beleg auch nach Entfernen der temporären Dateien erhalten bleibt.

```powershell
# Im Diagnose-Worktree, mit den oben beschriebenen Snapshots:
$taskHarness = Join-Path (Get-Location) 'test-results\foreground-diagnosis'
$taskSdk = 'C:\Users\Axel_desktop\.t3\projects\clockify\.dotnet\dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $taskHarness 'dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = Join-Path $taskHarness 'packages'
# empty-feed ist ein leerer lokaler Ordner; keine Fremdpakete notwendig.
& $taskSdk build (Join-Path $taskHarness 'ForegroundProbe.csproj') --configuration Release --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Harness build failed' }
& $taskSdk (Join-Path $taskHarness 'bin\Release\net10.0-windows\ForegroundProbe.dll') (Join-Path $taskHarness 'snapshots')
if ($LASTEXITCODE -ne 0) { throw 'Foreground diagnosis failed' }
```

Abschließende Ausgabe:

```text
Apartment=STA; Runtime=10.0.12; No Show/Run/Input/Auth
PASS: 203 assertions; eight full XAML states; four micro states; no visible windows.
```

SHA256 der temporären Nachweise:

```text
Program.cs           DE71A308CD0DA54B1656B29255C19A9EE69C7AFD7135901A7B4E6A24E05DFB6B
ForegroundProbe.csproj B92D5D64ACE43201E667E4EB9CDCFE930CDB0DA8A021503312362E86F0488B5B
probe-output.txt     7F0D1ADDDC063152C3E262AA42779D3DBC8FB04DE7C5D9918A4E93043121FEE9
```

<details>
<summary>Vollständige getestete Harness-Projektdatei</summary>

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>
    <EnableDefaultPageItems>false</EnableDefaultPageItems>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <NuGetAudit>false</NuGetAudit>
    <RestoreSources>$(MSBuildProjectDirectory)\empty-feed</RestoreSources>
  </PropertyGroup>
  <ItemGroup><Compile Include="Program.cs" /></ItemGroup>
</Project>

```
</details>

<details>
<summary>Vollständiger getesteter Harness-Quelltext</summary>

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal sealed class DerivedWindow : Window { }

internal static class Program
{
    private static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    {
        "Click", "Checked", "SelectionChanged", "SizeChanged", "PreviewMouseWheel",
        "ValueChanged", "TextChanged", "PreviewDragOver", "Drop", "PreviewKeyDown"
    };
    private static int _assertions;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.WriteLine($"Apartment={Thread.CurrentThread.GetApartmentState()}; Runtime={Environment.Version}; No Show/Run/Input/Auth");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            string snapshots = Path.GetFullPath(args[0]);
            string currentTheme = File.ReadAllText(Path.Combine(snapshots, "Theme.xaml"));
            string currentWindow = File.ReadAllText(Path.Combine(snapshots, "MainWindow.xaml"));
            string baselineTheme = File.ReadAllText(Path.Combine(snapshots, "Theme.d956eb8.xaml"));
            string baselineWindow = File.ReadAllText(Path.Combine(snapshots, "MainWindow.d956eb8.xaml"));

            Assert("shared Foreground DP", ReferenceEquals(Control.ForegroundProperty, TextBlock.ForegroundProperty)
                && ReferenceEquals(Control.ForegroundProperty, TextElement.ForegroundProperty));
            Assert("TextBlock Foreground inherits", ((FrameworkPropertyMetadata)TextBlock.ForegroundProperty.GetMetadata(typeof(TextBlock))).Inherits);
            Assert("Control Foreground inherits", ((FrameworkPropertyMetadata)Control.ForegroundProperty.GetMetadata(typeof(Control))).Inherits);

            Scene(app, "A-original-d956eb8", baselineTheme, baselineWindow, false, false, false, "#FFF4F1FF", "#FFF4F1FF", false, "#FFF4F1FF");
            Scene(app, "B-pre-grid-text-patch", currentTheme, currentWindow, true, false, false, "#FF000000", "#FF101010", true);
            Scene(app, "C-latest-grid-patch", currentTheme, currentWindow, false, false, false, "#FFF4F2FA", "#FF101010", true);
            Scene(app, "D-explicit-window-style", currentTheme, currentWindow, true, true, false, "#FFF4F2FA", "#FF101010", true);

            var counter = XDocument.Parse(currentTheme);
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            counter.Root!.Add(new XElement(ns + "Style", new XAttribute("TargetType", "TextBlock"),
                new XElement(ns + "Setter", new XAttribute("Property", "Foreground"), new XAttribute("Value", "{StaticResource TextBrush}"))));
            Scene(app, "E-counter-style-restored", counter.ToString(), currentWindow, false, true, false, "#FFF4F2FA", "#FFF4F2FA", true, "#FFF4F2FA");
            Scene(app, "F-style-without-grid-background", currentTheme, currentWindow, true, true, true, "#FFF4F2FA", "#FF101010", false);

            var boundToggle = XDocument.Parse(currentTheme);
            boundToggle.Descendants(ns + "ToggleButton").Single().SetAttributeValue("Foreground", "{TemplateBinding Foreground}");
            Scene(app, "G-grid-and-combo-foreground-contract", boundToggle.ToString(), currentWindow, false, false, false, "#FFF4F2FA", "#FF101010", true, "#FFF4F2FA");
            Scene(app, "H-window-style-and-combo-contract", boundToggle.ToString(), currentWindow, true, true, false, "#FFF4F2FA", "#FF101010", true, "#FFF4F2FA");

            Micro(app, currentTheme, false, false, false, "plain Window / implicit style", "#FFF4F2FA", "#FFF4F2FA");
            Micro(app, currentTheme, true, false, false, "DerivedWindow / implicit style", "#FF000000", "#FF000000");
            Micro(app, currentTheme, true, true, false, "DerivedWindow / explicit style", "#FFF4F2FA", "#FFF4F2FA");
            Micro(app, currentTheme, true, false, true, "DerivedWindow / Grid scope", "#FF000000", "#FFF4F2FA");
            Console.WriteLine($"PASS: {_assertions} assertions; eight full XAML states; four micro states; no visible windows.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static void Install(Application app, string theme)
    {
        app.Resources = new ResourceDictionary();
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(theme));
    }

    private static void Scene(Application app, string id, string theme, string windowXaml, bool removeTextScope,
        bool explicitStyle, bool removeBackground, string expectedDefault, string expectedLoginText, bool opaque, string expectedChevron = "#FF000000")
    {
        Console.WriteLine($"\n[{id}]");
        Install(app, theme);
        var xml = XDocument.Parse(windowXaml);
        var gridXml = new XElement(xml.Root!.Elements().Single(e => e.Name.LocalName == "Grid"));
        gridXml.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        foreach (var attribute in gridXml.DescendantsAndSelf().Attributes().Where(a => Events.Contains(a.Name.LocalName)).ToArray())
            attribute.Remove();
        if (removeTextScope)
            foreach (string name in new[] { "TextElement.Foreground", "TextElement.FontFamily", "TextElement.FontSize" })
                gridXml.Attribute(name)?.Remove();
        if (removeBackground) gridXml.Attribute("Background")?.Remove();
        var grid = (Grid)XamlReader.Parse(gridXml.ToString());
        var window = new DerivedWindow { Content = grid };
        if (explicitStyle) window.Style = (Style)app.FindResource(typeof(Window));
        Layout(grid);
        Dump("Window.Foreground", window, Control.ForegroundProperty);
        Dump("Window.Style", window, FrameworkElement.StyleProperty);
        Dump("Window.Background", window, Control.BackgroundProperty);
        Dump("Grid.Foreground", grid, TextElement.ForegroundProperty);
        Dump("Grid.FontFamily", grid, TextElement.FontFamilyProperty);
        Dump("Grid.FontSize", grid, TextElement.FontSizeProperty);
        Dump("Grid.Background", grid, Panel.BackgroundProperty);
        foreach (string name in new[] { "Kairos Capture", "Text aus Bild", "Noch kein Bild", "Konto verbinden", "Ein Ausschnitt. Alles im Blick." })
        {
            var block = Logicals(grid).OfType<TextBlock>().Single(t => t.Text == name);
            Dump(name, block, TextBlock.ForegroundProperty);
            Equal(id + ": " + name, Color(block.Foreground), expectedDefault);
            Assert(id + ": no per-element Foreground override on " + name, block.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue);
        }
        var login = Logicals(grid).OfType<Button>().Single(b => b.Name == "LoginButton");
        var loginText = Visuals(login).OfType<TextBlock>().Single(t => t.Text == "Weiter mit ChatGPT");
        Dump("LoginButton.Foreground", login, Control.ForegroundProperty);
        Dump("LoginButton.rendered.TextBlock", loginText, TextBlock.ForegroundProperty);
        Dump("LoginButton.Background", login, Control.BackgroundProperty);
        Equal(id + ": local button Foreground", Color(login.Foreground), "#FF101010");
        Equal(id + ": actual string content TextBlock", Color(loginText.Foreground), expectedLoginText);
        Equal(id + ": white login Background", Color(login.Background), "#FFF4F4F4");
        var muted = Logicals(grid).OfType<TextBlock>().Single(t => t.Text == "Modell");
        Dump("MutedText/Modell", muted, TextBlock.ForegroundProperty);
        Equal(id + ": MutedText", Color(muted.Foreground), Color((Brush)app.FindResource("MutedBrush")));
        var glyph = (TextBlock)Logicals(grid).OfType<Button>().Single(b => b.Name == "RefreshModelsButton").Content;
        Dump("Glyph/RefreshModels", glyph, TextBlock.ForegroundProperty);
        Equal(id + ": keyed Glyph inherits button", Color(glyph.Foreground), Color((Brush)app.FindResource("TextBrush")));
        foreach (var combo in Logicals(grid).OfType<ComboBox>())
        {
            Dump("ComboBox.Foreground/" + combo.Name, combo, Control.ForegroundProperty);
            Dump("ComboBox.ToggleButton/" + combo.Name, Visuals(combo).OfType<ToggleButton>().Single(), Control.ForegroundProperty);
            foreach (var chevron in Visuals(combo).OfType<TextBlock>().Where(t => t.Text == "\uE70D"))
            {
                Dump("ComboBox.chevron/" + combo.Name, chevron, TextBlock.ForegroundProperty);
                Equal(id + ": " + combo.Name + " chevron", Color(chevron.Foreground), expectedChevron);
            }
        }
        Assert(id + ": MainWindow labels are TextBlocks", !Logicals(grid).OfType<Label>().Any());
        if (id is "C-latest-grid-patch" or "D-explicit-window-style" or "G-grid-and-combo-foreground-contract" or "H-window-style-and-combo-contract")
        {
            Equal(id + ": default font family", TextElement.GetFontFamily(grid).Source, "Segoe UI Variable, Segoe UI");
            Equal(id + ": default font size", TextElement.GetFontSize(grid).ToString(System.Globalization.CultureInfo.InvariantCulture), "13");
            Equal(id + ": default text source", Source(Logicals(grid).OfType<TextBlock>().Single(t => t.Text == "Kairos Capture"), TextBlock.ForegroundProperty), "Inherited");
            Equal(id + ": login text source", Source(loginText, TextBlock.ForegroundProperty), "Inherited");
        }
        var bitmap = new RenderTargetBitmap(1440, 840, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(grid);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Console.WriteLine($"Grid render corner ARGB=#{pixel[3]:X2}{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}");
        Assert(id + ": content-only background", (pixel[3] == 255) == opaque);
        Assert(id + ": never visible", !window.IsVisible);
        window.Close();
    }

    private static void Micro(Application app, string theme, bool derived, bool explicitStyle, bool gridScope,
        string id, string expectedWindow, string expectedDefault)
    {
        Console.WriteLine($"\n[micro: {id}]");
        Install(app, theme);
        Window window = derived ? new DerivedWindow() : new Window();
        if (explicitStyle) window.Style = (Style)app.FindResource(typeof(Window));
        var panel = new StackPanel();
        if (gridScope)
        {
            TextElement.SetForeground(panel, (Brush)app.FindResource("TextBrush"));
            TextElement.SetFontFamily(panel, new FontFamily("Segoe UI Variable, Segoe UI"));
            TextElement.SetFontSize(panel, 13);
        }
        var defaultLabel = new TextBlock { Text = "defaultLabel" };
        var wpfLabel = new Label { Content = "actual WPF Label control" };
        var buttonText = new TextBlock { Text = "explicit button TextBlock content" };
        var button = new Button { Foreground = (Brush)new BrushConverter().ConvertFromString("#101010")!,
            Background = Brushes.White, Content = buttonText };
        panel.Children.Add(defaultLabel);
        panel.Children.Add(wpfLabel);
        panel.Children.Add(button);
        window.Content = panel;
        Layout(panel);
        Dump("Window.Foreground", window, Control.ForegroundProperty);
        Dump("Window.Style", window, FrameworkElement.StyleProperty);
        Dump("defaultLabel/TextBlock", defaultLabel, TextBlock.ForegroundProperty);
        Dump("actual WPF Label", wpfLabel, Control.ForegroundProperty);
        Dump("Button.Foreground", button, Control.ForegroundProperty);
        Dump("Button.TextBlock.Foreground", buttonText, TextBlock.ForegroundProperty);
        Equal(id + ": Window foreground", Color(window.Foreground), expectedWindow);
        Equal(id + ": default label text", Color(defaultLabel.Foreground), expectedDefault);
        Equal(id + ": explicit TextBlock inherits black button", Color(buttonText.Foreground), "#FF101010");
        Assert(id + ": never visible", !window.IsVisible);
        window.Close();
    }

    private static void Layout(FrameworkElement content)
    {
        content.Measure(new Size(1440, 840));
        content.Arrange(new Rect(0, 0, 1440, 840));
        content.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Logicals(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var descendant in Logicals(child)) yield return descendant;
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(node, i))) yield return child;
    }

    private static string Color(Brush? value) => value?.ToString() ?? "<null>";
    private static string Source(DependencyObject node, DependencyProperty property) => DependencyPropertyHelper.GetValueSource(node, property).BaseValueSource.ToString();

    private static void Dump(string label, DependencyObject node, DependencyProperty property)
    {
        var source = DependencyPropertyHelper.GetValueSource(node, property);
        Console.WriteLine($"{label}={node.GetValue(property) ?? "<null>"}; source={source.BaseValueSource}; expression={source.IsExpression}; local={(node.ReadLocalValue(property) == DependencyProperty.UnsetValue ? "Unset" : node.ReadLocalValue(property))}");
    }

    private static void Equal(string label, string actual, string expected) => Assert(label + $" expected {expected}, actual {actual}", actual == expected);
    private static void Assert(string label, bool condition)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("ASSERTION FAILED: " + label);
    }
}

```
</details>

<details>
<summary>Vollständige erfolgreiche Testausgabe (203 Assertions)</summary>

```text
Apartment=STA; Runtime=10.0.12; No Show/Run/Input/Auth

[A-original-d956eb8]
Window.Foreground=#FF000000; source=DefaultStyle; expression=False; local=Unset
Window.Style=<null>; source=Default; expression=False; local=Unset
Window.Background=#FFFFFFFF; source=DefaultStyle; expression=False; local=Unset
Grid.Foreground=#FF000000; source=Inherited; expression=False; local=Unset
Grid.FontFamily=Segoe UI; source=Default; expression=False; local=Unset
Grid.FontSize=12; source=Default; expression=False; local=Unset
Grid.Background=<null>; source=Default; expression=False; local=Unset
Kairos Capture=#FFF4F1FF; source=Style; expression=False; local=Unset
Text aus Bild=#FFF4F1FF; source=Style; expression=False; local=Unset
Noch kein Bild=#FFF4F1FF; source=Style; expression=False; local=Unset
Konto verbinden=#FFF4F1FF; source=Style; expression=False; local=Unset
Ein Ausschnitt. Alles im Blick.=#FFF4F1FF; source=Style; expression=False; local=Unset
LoginButton.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
LoginButton.rendered.TextBlock=#FFF4F1FF; source=Style; expression=False; local=Unset
LoginButton.Background=#FFF4F4F4; source=Local; expression=False; local=#FFF4F4F4
MutedText/Modell=#FF9693A9; source=Style; expression=False; local=Unset
Glyph/RefreshModels=#FFF4F1FF; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ThicknessPicker=#FFF4F1FF; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ThicknessPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ThicknessPicker=#FFF4F1FF; source=Style; expression=False; local=Unset
ComboBox.Foreground/ModelPicker=#FFF4F1FF; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ModelPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ModelPicker=#FFF4F1FF; source=Style; expression=False; local=Unset
ComboBox.Foreground/AccountPicker=#FFF4F1FF; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/AccountPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/AccountPicker=#FFF4F1FF; source=Style; expression=False; local=Unset
Grid render corner ARGB=#00000000

[B-pre-grid-text-patch]
Window.Foreground=#FF000000; source=DefaultStyle; expression=False; local=Unset
Window.Style=<null>; source=Default; expression=False; local=Unset
Window.Background=#FFFFFFFF; source=DefaultStyle; expression=False; local=Unset
Grid.Foreground=#FF000000; source=Inherited; expression=False; local=Unset
Grid.FontFamily=Segoe UI; source=Default; expression=False; local=Unset
Grid.FontSize=12; source=Default; expression=False; local=Unset
Grid.Background=#FF090E13; source=Local; expression=False; local=#FF090E13
Kairos Capture=#FF000000; source=Inherited; expression=False; local=Unset
Text aus Bild=#FF000000; source=Inherited; expression=False; local=Unset
Noch kein Bild=#FF000000; source=Inherited; expression=False; local=Unset
Konto verbinden=#FF000000; source=Inherited; expression=False; local=Unset
Ein Ausschnitt. Alles im Blick.=#FF000000; source=Inherited; expression=False; local=Unset
LoginButton.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
LoginButton.rendered.TextBlock=#FF101010; source=Inherited; expression=False; local=Unset
LoginButton.Background=#FFF4F4F4; source=Local; expression=False; local=#FFF4F4F4
MutedText/Modell=#FFA4A0B2; source=Style; expression=False; local=Unset
Glyph/RefreshModels=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ThicknessPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ThicknessPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ThicknessPicker=#FF000000; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ModelPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ModelPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ModelPicker=#FF000000; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/AccountPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/AccountPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/AccountPicker=#FF000000; source=Inherited; expression=False; local=Unset
Grid render corner ARGB=#FF090E13

[C-latest-grid-patch]
Window.Foreground=#FF000000; source=DefaultStyle; expression=False; local=Unset
Window.Style=<null>; source=Default; expression=False; local=Unset
Window.Background=#FFFFFFFF; source=DefaultStyle; expression=False; local=Unset
Grid.Foreground=#FFF4F2FA; source=Local; expression=False; local=#FFF4F2FA
Grid.FontFamily=Segoe UI Variable, Segoe UI; source=Local; expression=False; local=Segoe UI Variable, Segoe UI
Grid.FontSize=13; source=Local; expression=False; local=13
Grid.Background=#FF090E13; source=Local; expression=False; local=#FF090E13
Kairos Capture=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Text aus Bild=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Noch kein Bild=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Konto verbinden=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Ein Ausschnitt. Alles im Blick.=#FFF4F2FA; source=Inherited; expression=False; local=Unset
LoginButton.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
LoginButton.rendered.TextBlock=#FF101010; source=Inherited; expression=False; local=Unset
LoginButton.Background=#FFF4F4F4; source=Local; expression=False; local=#FFF4F4F4
MutedText/Modell=#FFA4A0B2; source=Style; expression=False; local=Unset
Glyph/RefreshModels=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ThicknessPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ThicknessPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ThicknessPicker=#FF000000; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ModelPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ModelPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ModelPicker=#FF000000; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/AccountPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/AccountPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/AccountPicker=#FF000000; source=Inherited; expression=False; local=Unset
Grid render corner ARGB=#FF090E13

[D-explicit-window-style]
Window.Foreground=#FFF4F2FA; source=Style; expression=False; local=Unset
Window.Style=System.Windows.Style; source=Local; expression=False; local=System.Windows.Style
Window.Background=#FF090E13; source=Style; expression=False; local=Unset
Grid.Foreground=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Grid.FontFamily=Segoe UI Variable, Segoe UI; source=Inherited; expression=False; local=Unset
Grid.FontSize=13; source=Inherited; expression=False; local=Unset
Grid.Background=#FF090E13; source=Local; expression=False; local=#FF090E13
Kairos Capture=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Text aus Bild=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Noch kein Bild=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Konto verbinden=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Ein Ausschnitt. Alles im Blick.=#FFF4F2FA; source=Inherited; expression=False; local=Unset
LoginButton.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
LoginButton.rendered.TextBlock=#FF101010; source=Inherited; expression=False; local=Unset
LoginButton.Background=#FFF4F4F4; source=Local; expression=False; local=#FFF4F4F4
MutedText/Modell=#FFA4A0B2; source=Style; expression=False; local=Unset
Glyph/RefreshModels=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ThicknessPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ThicknessPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ThicknessPicker=#FF000000; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ModelPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ModelPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ModelPicker=#FF000000; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/AccountPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/AccountPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/AccountPicker=#FF000000; source=Inherited; expression=False; local=Unset
Grid render corner ARGB=#FF090E13

[E-counter-style-restored]
Window.Foreground=#FFF4F2FA; source=Style; expression=False; local=Unset
Window.Style=System.Windows.Style; source=Local; expression=False; local=System.Windows.Style
Window.Background=#FF090E13; source=Style; expression=False; local=Unset
Grid.Foreground=#FFF4F2FA; source=Local; expression=False; local=#FFF4F2FA
Grid.FontFamily=Segoe UI Variable, Segoe UI; source=Local; expression=False; local=Segoe UI Variable, Segoe UI
Grid.FontSize=13; source=Local; expression=False; local=13
Grid.Background=#FF090E13; source=Local; expression=False; local=#FF090E13
Kairos Capture=#FFF4F2FA; source=Style; expression=False; local=Unset
Text aus Bild=#FFF4F2FA; source=Style; expression=False; local=Unset
Noch kein Bild=#FFF4F2FA; source=Style; expression=False; local=Unset
Konto verbinden=#FFF4F2FA; source=Style; expression=False; local=Unset
Ein Ausschnitt. Alles im Blick.=#FFF4F2FA; source=Style; expression=False; local=Unset
LoginButton.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
LoginButton.rendered.TextBlock=#FFF4F2FA; source=Style; expression=False; local=Unset
LoginButton.Background=#FFF4F4F4; source=Local; expression=False; local=#FFF4F4F4
MutedText/Modell=#FFA4A0B2; source=Style; expression=False; local=Unset
Glyph/RefreshModels=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ThicknessPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ThicknessPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ThicknessPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.Foreground/ModelPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ModelPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ModelPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.Foreground/AccountPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/AccountPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/AccountPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
Grid render corner ARGB=#FF090E13

[F-style-without-grid-background]
Window.Foreground=#FFF4F2FA; source=Style; expression=False; local=Unset
Window.Style=System.Windows.Style; source=Local; expression=False; local=System.Windows.Style
Window.Background=#FF090E13; source=Style; expression=False; local=Unset
Grid.Foreground=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Grid.FontFamily=Segoe UI Variable, Segoe UI; source=Inherited; expression=False; local=Unset
Grid.FontSize=13; source=Inherited; expression=False; local=Unset
Grid.Background=<null>; source=Default; expression=False; local=Unset
Kairos Capture=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Text aus Bild=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Noch kein Bild=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Konto verbinden=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Ein Ausschnitt. Alles im Blick.=#FFF4F2FA; source=Inherited; expression=False; local=Unset
LoginButton.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
LoginButton.rendered.TextBlock=#FF101010; source=Inherited; expression=False; local=Unset
LoginButton.Background=#FFF4F4F4; source=Local; expression=False; local=#FFF4F4F4
MutedText/Modell=#FFA4A0B2; source=Style; expression=False; local=Unset
Glyph/RefreshModels=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ThicknessPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ThicknessPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ThicknessPicker=#FF000000; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ModelPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ModelPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/ModelPicker=#FF000000; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/AccountPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/AccountPicker=#FF000000; source=DefaultStyle; expression=False; local=Unset
ComboBox.chevron/AccountPicker=#FF000000; source=Inherited; expression=False; local=Unset
Grid render corner ARGB=#00000000

[G-grid-and-combo-foreground-contract]
Window.Foreground=#FF000000; source=DefaultStyle; expression=False; local=Unset
Window.Style=<null>; source=Default; expression=False; local=Unset
Window.Background=#FFFFFFFF; source=DefaultStyle; expression=False; local=Unset
Grid.Foreground=#FFF4F2FA; source=Local; expression=False; local=#FFF4F2FA
Grid.FontFamily=Segoe UI Variable, Segoe UI; source=Local; expression=False; local=Segoe UI Variable, Segoe UI
Grid.FontSize=13; source=Local; expression=False; local=13
Grid.Background=#FF090E13; source=Local; expression=False; local=#FF090E13
Kairos Capture=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Text aus Bild=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Noch kein Bild=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Konto verbinden=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Ein Ausschnitt. Alles im Blick.=#FFF4F2FA; source=Inherited; expression=False; local=Unset
LoginButton.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
LoginButton.rendered.TextBlock=#FF101010; source=Inherited; expression=False; local=Unset
LoginButton.Background=#FFF4F4F4; source=Local; expression=False; local=#FFF4F4F4
MutedText/Modell=#FFA4A0B2; source=Style; expression=False; local=Unset
Glyph/RefreshModels=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ThicknessPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ThicknessPicker=#FFF4F2FA; source=ParentTemplate; expression=False; local=Unset
ComboBox.chevron/ThicknessPicker=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ModelPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ModelPicker=#FFF4F2FA; source=ParentTemplate; expression=False; local=Unset
ComboBox.chevron/ModelPicker=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/AccountPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/AccountPicker=#FFF4F2FA; source=ParentTemplate; expression=False; local=Unset
ComboBox.chevron/AccountPicker=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Grid render corner ARGB=#FF090E13

[H-window-style-and-combo-contract]
Window.Foreground=#FFF4F2FA; source=Style; expression=False; local=Unset
Window.Style=System.Windows.Style; source=Local; expression=False; local=System.Windows.Style
Window.Background=#FF090E13; source=Style; expression=False; local=Unset
Grid.Foreground=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Grid.FontFamily=Segoe UI Variable, Segoe UI; source=Inherited; expression=False; local=Unset
Grid.FontSize=13; source=Inherited; expression=False; local=Unset
Grid.Background=#FF090E13; source=Local; expression=False; local=#FF090E13
Kairos Capture=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Text aus Bild=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Noch kein Bild=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Konto verbinden=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Ein Ausschnitt. Alles im Blick.=#FFF4F2FA; source=Inherited; expression=False; local=Unset
LoginButton.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
LoginButton.rendered.TextBlock=#FF101010; source=Inherited; expression=False; local=Unset
LoginButton.Background=#FFF4F4F4; source=Local; expression=False; local=#FFF4F4F4
MutedText/Modell=#FFA4A0B2; source=Style; expression=False; local=Unset
Glyph/RefreshModels=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ThicknessPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ThicknessPicker=#FFF4F2FA; source=ParentTemplate; expression=False; local=Unset
ComboBox.chevron/ThicknessPicker=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/ModelPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/ModelPicker=#FFF4F2FA; source=ParentTemplate; expression=False; local=Unset
ComboBox.chevron/ModelPicker=#FFF4F2FA; source=Inherited; expression=False; local=Unset
ComboBox.Foreground/AccountPicker=#FFF4F2FA; source=Style; expression=False; local=Unset
ComboBox.ToggleButton/AccountPicker=#FFF4F2FA; source=ParentTemplate; expression=False; local=Unset
ComboBox.chevron/AccountPicker=#FFF4F2FA; source=Inherited; expression=False; local=Unset
Grid render corner ARGB=#FF090E13

[micro: plain Window / implicit style]
Window.Foreground=#FFF4F2FA; source=Style; expression=False; local=Unset
Window.Style=System.Windows.Style; source=ImplicitStyleReference; expression=False; local=Unset
defaultLabel/TextBlock=#FFF4F2FA; source=Inherited; expression=False; local=Unset
actual WPF Label=#FF000000; source=DefaultStyle; expression=False; local=Unset
Button.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
Button.TextBlock.Foreground=#FF101010; source=Inherited; expression=False; local=Unset

[micro: DerivedWindow / implicit style]
Window.Foreground=#FF000000; source=DefaultStyle; expression=False; local=Unset
Window.Style=<null>; source=Default; expression=False; local=Unset
defaultLabel/TextBlock=#FF000000; source=Inherited; expression=False; local=Unset
actual WPF Label=#FF000000; source=DefaultStyle; expression=False; local=Unset
Button.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
Button.TextBlock.Foreground=#FF101010; source=Inherited; expression=False; local=Unset

[micro: DerivedWindow / explicit style]
Window.Foreground=#FFF4F2FA; source=Style; expression=False; local=Unset
Window.Style=System.Windows.Style; source=Local; expression=False; local=System.Windows.Style
defaultLabel/TextBlock=#FFF4F2FA; source=Inherited; expression=False; local=Unset
actual WPF Label=#FF000000; source=DefaultStyle; expression=False; local=Unset
Button.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
Button.TextBlock.Foreground=#FF101010; source=Inherited; expression=False; local=Unset

[micro: DerivedWindow / Grid scope]
Window.Foreground=#FF000000; source=DefaultStyle; expression=False; local=Unset
Window.Style=<null>; source=Default; expression=False; local=Unset
defaultLabel/TextBlock=#FFF4F2FA; source=Inherited; expression=False; local=Unset
actual WPF Label=#FF000000; source=DefaultStyle; expression=False; local=Unset
Button.Foreground=#FF101010; source=Local; expression=False; local=#FF101010
Button.TextBlock.Foreground=#FF101010; source=Inherited; expression=False; local=Unset
PASS: 203 assertions; eight full XAML states; four micro states; no visible windows.

```
</details>
