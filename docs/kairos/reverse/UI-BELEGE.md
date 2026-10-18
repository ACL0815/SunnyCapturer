# Sichtbare Oberfläche aus den Nutzerbildern

Stand 18.10.2026. Quelle sind die 13 Anhänge der Nutzernachricht „Weiter“. [ui-evidence.json](ui-evidence.json) bindet jedes Bild an Größe, Abmessungen und SHA-256. Die Originalbilder verbleiben im Anhangsverzeichnis und werden nicht ins Repository kopiert.

Die elf Einstellungsbilder sind vom Nutzer bereitgestellte Windows-UI-Belege. Sie sind nicht an einen bestimmten Prozess oder den untersuchten EXE-Hash gebunden. Die zwei Editorbilder zeigen erkennbar Deepin/Linux beziehungsweise macOS und sind Referenzen für die Gestaltung und Werkzeuganordnung. Sie ersetzen keine Windows-Bedienprüfung. Ein sichtbarer Schalterwert ist weder ein Herstellerstandard noch ein Test seiner Funktion.

## Abgelesene Einstellungen

| Seite / Bild-ID | Sichtbarer Zustand |
| --- | --- |
| `settings-general` | Autostart aus, Sprache Deutsch, Einstellungsdateipfad angezeigt, Theme Auto. Die Oberfläche mischt deutsche und englische Texte. |
| `settings-hotkeys` | Screenshot F1; letzte Aufnahme Strg+Bild↑; Clipboard-Vorschau F3; Vorschauen umschalten Umschalt+F3; Quick Look Umschalt+F6; Transparent Input Strg+T; Video Strg+Alt+V; GIF Strg+Alt+G. |
| `settings-screenshot-appearance` | Randbreite 2, blaue Randfarbe, SolidLine, graue Maskenfarbe; Speicherordner Desktop; Bildqualität −1. Bedeutung von −1 hier nicht durch Bedienung geprüft. |
| `settings-screenshot-manual-quick-save` | Datums-/Zeitvorlage für PNG, Vorschau des Dateinamens; letzte Dateiendung merken an; Speicherdialog überspringen aus. Quick Save hat einen eigenen Pfad und Benachrichtigung an. |
| `settings-screenshot-quick-auto-save` | Quick-Save-Benachrichtigung an. Auto Save zeigt eigenen Bilderpfad und ist ausgeschaltet. |
| `settings-video-appearance` | Orangefarbener Rand, Breite 2, SolidLine, graue Maske; Show Region und Floating Menu an; MP4 als Ausgabeformat. |
| `settings-video-default-capture` | Maus an, Mikrofon aus, Systemaudio an; Software x264, 30 fps, CRF 23, Preset medium, Profile none. |
| `settings-video-encoding` | Tune none, YUV420P, BT.709, Limited; AAC, Stereo, 48 kHz. |
| `settings-devices` | Separate Auswahlfelder für Mikrofon, Lautsprecher und Kamera. Die konkreten persönlichen Gerätenamen werden hier nicht erneut erfasst. |
| `settings-ocr` | Tencent Cloud ausgewählt; Hinweis auf Default-Token bei leeren privaten Feldern. Secret-ID und Secret-Key sind im bereitgestellten Bild leer. Kein OCR-Erfolg belegt. |
| `settings-translation` | Tencent Cloud ausgewählt, private Felder leer; Quellsprache Auto detect, Zielsprache Simplified Chinese. Kein Übersetzungserfolg belegt. |

## Editorreferenzen

Beide Bilder zeigen einen hellen ausgewählten Bildbereich, abgedunkelten Außenbereich, blauen Auswahlrand, Griffe an Ecken/Kanten, Koordinaten-/Größenanzeige und eine kompakte Werkzeugleiste unterhalb der Auswahl. Sichtbar sind unter anderem Form-, Pfeil-, Stift-, Text- und Bildwerkzeuge sowie Undo/Redo und Ausgabeaktionen. Icons allein belegen ihre konkrete Funktion oder Tastenkürzel nicht.

Im Deepin-Bild ist das Textwerkzeug aktiv. Die zweite Werkzeugzeile zeigt Schriftstil, Schriftfamilie, Schriftgröße und Farben. Im macOS-Bild ist ein Rechteckwerkzeug aktiv; die Eigenschaftenzeile zeigt Kontur/Füllung, Strichstärke und Farbpalette. Damit ist eine kontextabhängige Eigenschaftenleiste visuell belegt. Wiedereditieren, Verschieben, Größenänderung bereits angelegter Objekte und Exporttreue sind aus diesen Standbildern nicht nachgewiesen.

## Folgen für Kairos

Der Kernablauf lautet Aufnahme → direktes Editieren → gemeinsame Bitmap für Kopieren, Speichern und explizit ausgelöste OCR. Die eigene erste Windows-Fassung verwendet Strg+Alt+A statt Sunnys bereits belegtem F1. Annotationen bleiben bis zur Ausgabe getrennte, auswählbare und veränderbare Objekte. Eine deckende Schwärzung erhält einen eigenen Werkzeugtyp. Farbschema und Gestaltung kommen aus der ursprünglichen dunklen Violett-/Lavendel-/Gold-Vorlage.

Die Video-, Geräte- und Übersetzungseinstellungen dokumentieren den Funktionsumfang der Referenz. Sie sind kein Auftrag, alle Recorder-/Codecoptionen vor dem ersten funktionsfähigen Screenshot-Editor nachzubauen. Die vorgesehenen Aufnahme-/Editor-/OCR-Funktionen werden in `docs/plans/2026-10-18-kairos-capture-build.md` umgesetzt und eigenständig geprüft.
