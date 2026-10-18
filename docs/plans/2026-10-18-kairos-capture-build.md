# Kairos Capture Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Eine startbare Windows-App für Bereichsaufnahme, direktes Objekt-Editieren, PNG/Clipboard-Ausgabe und explizit gestartete OpenAI-OAuth-OCR bauen.

**Architecture:** Ein WPF-Projekt, ein Prozess, C#/.NET 10. Capture liefert physische Bildpixel; der Editor hält getrennte Annotationen und einen gemeinsamen pixelgenauen Export. Die OCR verarbeitet ausschließlich diesen Export nach Nutzeraktion und nutzt eine eigene, geschützte OAuth-Sitzung.

**Tech Stack:** C#, WPF, .NET 10, Win32-Aufnahme/Hotkey, Systembrowser-OAuth, HTTP/JSON; keine zusätzliche Web-UI.

---

Die ursprüngliche Bauanweisung, die vorgelegte Architekturabwägung und das anschließende „Weiter“ autorisieren die Umsetzung. Die neuen Einstellungsbilder sind visuelle Belege; die Linux-/macOS-Bilder sind Editorreferenzen und keine Windows-Laufzeitabnahme. Frühere Arbeiten in separaten Worktrees werden als ungeprüfter Ausgangscode übernommen, danach eigenständig geprüft und korrigiert. Keine SunnyCapturer-Quellcodeübernahme.

### 1. Belege und Baubasis (Root)
- Neue Bildbelege mit Hash, sichtbaren Werten und begrenzter Aussagekraft dokumentieren.
- Vorhandene eigene C#-Entwürfe in `src/KairosCapture/` zusammenführen; private .NET-10-SDK-Kopie unter `.tools/` anhand Microsoft-Metadaten hashprüfen. Keine globale Installation.
- Projekt-/Paketdateien gehören dem Shell-Writer; jeder parallele Writer erhält einen eigenen T3-Worktree.

### 2. Editor (`src/KairosCapture/Editor/`)
- Vorhandenes Pixel-Canvas prüfen; Auswahl, Verschieben, Größenänderung, erneut editierbarer Text, Pfeil/Form/Stift/Marker/deckende Schwärzung, Undo/Redo durchgängig machen.
- Offline-STA-Checks müssen objektweise Änderungen, Rücknahme/Wiederholung, Exportmaße und deckende Schwärzung prüfen.

### 3. Aufnahme und Ausgabe (`src/KairosCapture/Capture/`)
- Hotkey, Auswahl/Abbruch, negative Desktopkoordinaten und DPI-Umrechnung prüfen; laufende Aufnahme vor Doppelstart schützen.
- PNG-Datei und Clipboard aus demselben Bitmap erzeugen. Reale Mixed-DPI-Sichtprüfung offen kennzeichnen, sofern keine passende Hardwarekonfiguration prüfbar ist.

### 4. OAuth/OCR (`src/KairosCapture/Services/`)
- Implementierung am Vertrag `docs/kairos/reverse/OPENAI-OAUTH-VERTRAG.md` prüfen; aktuelle offizielle Dokumentation gegenprüfen.
- State/Nonce/PKCE/ausgestellte Client-ID, Signaturprüfung, Tokenrotation und geschützte lokale Ablage absichern; keine fremden Tokens lesen.
- Kleine Offline-Protokollprüfungen mit falscher Signatur/State, Streamfehlern und abgeschlossenem Response. Echter Login bleibt Nutzerhandlung und ist kein Offline-Test.

### 5. Oberfläche, Start und Integration (Shell-Writer, Root nimmt ab)
- `App.xaml.cs`, `MainWindow.xaml*`, `Theme.xaml`, Projekt, Manifest und `scripts/build.ps1`: dunkle Vorlage mit Violett/Lavendel und sparsamen Goldakzenten, Bild im Mittelpunkt, OCR-Nebenbereich, sichtbare Zustände.
- Frameworkabhängig veröffentlichen. `--self-test` erzeugt maschinenlesbare Offline-Ergebnisse, `--render-preview` eine echte WPF-Vorschau, `--demo` ein lokales Beispieldokument.
- Root liest Diffs, führt Release-Build und Offline-Prüfungen aus, prüft gerenderte Oberfläche und bedient die eigene App. Erst danach Abschluss mit ausführbarem Pfad und präzisen verbleibenden Grenzen.

### Stop-Regel
Root zählt gleiches verletztes Sollverhalten. Beim zweiten Auftreten Patches des Falls stoppen, Stand sichern und frische Diagnose der nächsten vereinbarten Modellstufe beauftragen. Externe Login-/Netz-/Rechteblocker erhalten höchstens einen Retry, keine Modelleskalation.
