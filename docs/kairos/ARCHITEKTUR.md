# Kairos Capture

> Stand 18.10.2026: C#/WPF ist implementiert. Die ursprüngliche Beschränkung auf Bearbeitung im Hauptfenster wurde durch den verbindlichen [Overlay-Ablauf](../plans/2026-10-18-live-overlay-correction.md) ersetzt. Für die nach Untersuchung der installierten SunnyCapturer-Version geprüfte Technologieabwägung siehe [ARCHITEKTUR-ABWAEGUNG.md](reverse/ARCHITEKTUR-ABWAEGUNG.md) und [Untersuchungsbericht](reverse/README.md). Praktische Windows-/Mixed-DPI-Abnahme bleibt offen.

Entscheidung vom 18.10.2026, Architekturprüfung durch Claude Opus 5.5 mit Ponytail.

Kairos Capture ist eine eigene Windows-Anwendung. Die öffentlich vorliegenden SunnyCapturer-Quellen sind ältere Generationen; die aktuelle Version liegt hier nicht vollständig vor. Aus SunnyCapturer wird kein Programmcode übernommen.

| Möglichkeit | Abwägung für diesen Auftrag |
| --- | --- |
| **C# / WPF / .NET 10** | Gewählt. Windows-Bildaufnahme, Zwischenablage, Dialoge und Tray sind direkt erreichbar. WPF liefert Zeichenobjekte, Texteingabe, Treffertests und Bitmap-Export. Ein Projekt und eine Sprache genügen. |
| C++ / Qt | Native und plattformübergreifend; hier würde die Wiederverwendung älterer Quellen zusätzlichen Build- und Wartungsaufwand verursachen. Die aktuelle SunnyCapturer-App kann aus diesem Checkout nicht gebaut werden. |
| Rust / Tauri | Kleine native Hülle möglich. Für diesen Editor wären zusätzlich eine Web-Oberfläche und die Verbindung zu nativen Aufnahmefunktionen nötig. |
| TypeScript / Electron | Gute Web-Werkzeuge und viel verfügbare UI-Technik. Die zusätzliche Browser-Runtime passt weniger gut zum gewünschten schlanken Windows-Werkzeug. |

Die Wahl ist auf Windows optimiert. Sie ist keine Zusage für macOS/Linux und keine gemessene Performance-Rangliste. Geschwindigkeit und Stabilität werden am gebauten Programm geprüft; konkrete Latenzwerte ergeben sich aus dem jeweiligen Rechner.

## Aufbau

- `Capture`: vollständigen virtuellen Desktop einfrieren, Auswahlrahmen im offenen Overlay verschieben/verändern, Werkzeuge und Bildausgabe direkt dort verwenden; Hotkey und native Bildaufnahme.
- `Editor`: ein gemeinsamer Canvas für das volle Pixelraster mit eigenständigen Zeichenobjekten, Auswahl, Move/Resize, Stiländerung, direkter Texteingabe und Rückgängig/Wiederholen. Der veränderbare Rahmen ist ein Crop; Export rendert direkt dessen aktuelle Originalpixel.
- `Services`: direkter OpenAI-OAuth-Login, geschützte lokale Sitzung und OCR über die Responses API.
- `MainWindow`: ausdrücklich gewählte Werkbank mit demselben lebenden Editor und bearbeitbarem OCR-Text; kleine WPF-Codebehind ohne zusätzliches UI-Framework. Kein automatischer Wechsel nach dem Aufziehen eines Ausschnitts.

Die Farben folgen der Nutzervorlage: fast schwarzer Grund, violette und lavendelfarbene Akzente, sparsame goldene Konturen. Das Screenshot-Bild bleibt der Mittelpunkt der Oberfläche.

## Datenschutz und Grenzen

Aufnahme und Bearbeitung laufen lokal. Erst die ausdrücklich gestartete Texterkennung sendet das bearbeitete Bild an OpenAI. Die Einwilligung zum Kontozugriff erfolgt im Systembrowser. Kairos liest keine Zugangsdaten anderer Anwendungen.

Schwarze Abdeckungen werden beim PNG-Export in die Bildpixel geschrieben. Einzelne markierte Objekte bleiben bis zum Export bearbeitbar. Geschützte Bildschirminhalte können durch Windows schwarz erscheinen. Unterschiedliche Monitor-Skalierungen und Remote Desktop brauchen eine Prüfung auf dem jeweiligen Aufbau.

Scroll-Aufnahmen, Video, Cloud-Synchronisierung und Übersetzung gehören nicht zur ersten Fassung.

## Quellen

- [SunnyCapturer-Quellenübersicht](https://github.com/XMuli/SunnyCapturer/tree/master/history-version-code)
- [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- [Virtueller Windows-Bildschirm](https://learn.microsoft.com/en-us/windows/win32/gdi/the-virtual-screen)
- [OpenAI: Registrierung und Login lokaler Apps](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
- [OpenAI: Modelle und Inferenz](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)

Quellen geprüft am 18.10.2026.
