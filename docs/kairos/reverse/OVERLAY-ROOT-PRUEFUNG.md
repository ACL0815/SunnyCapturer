# Root-Prüfung des ergänzten Overlay-Berichts

Am 18.10.2026 prüfte Root den integrierten [Overlay-Bericht](overlay-native/README.md), die vollständigen fünf gespeicherten Toolantworten und deren Hashverzeichnis unabhängig. Alle 15 neu manifestierten Dateien stimmen bytegenau mit den dort notierten SHA-256 und Längen überein. Die Reihenfolge ist `open_binary`, `binary_session`, zwei mit `provider_timeout` beendete native Aufrufe, `close_binary`. Die letzte Antwort bestätigt erfolgreiches Schließen. Die Ziel-EXE besitzt weiterhin SHA-256 `e97cc2612b79ab154076844a13ec01cdeb70be0f14cf20675ce0e39dff81ecc8`.

Von den 56 wiederverwendeten Dateien stimmen 55 bytegenau mit dem Kind-Worktree-Verzeichnis überein. Bei `qt/inventory.json` hat Git im Kind-Checkout CRLF verwendet; Root und der Git-Blob enthalten LF. Das wurde unabhängig durch Bytevergleich, JSON-Vergleich und Rekonstruktion der CRLF-Bytes belegt:

| Variante | Bytes | SHA-256 |
| --- | --- | --- |
| Root und Git-Blob, LF | 6.560.820 | `4ae7686c1c9ec7d3f7dac14b86c5fa79273c16c214b1c181aa495501f478bb4e` |
| Kind-Checkout, CRLF | 6.754.485 | `9be6fdea29866103e3956a04067e5404060975666bb571a585c99a83691f6df7` |

Es gibt 193.665 Zeilenumbrüche; die Nutzdaten sind identisch. Die Altdatei und der gespeicherte Kind-Hash wurden nicht umgeschrieben. Der erste strikte Root-Hashlauf meldete genau diese Abweichung. Die anschließende Gegenprobe bestätigt ausschließlich für diese bekannte Datei die exakte CRLF-Rekonstruktion einschließlich Länge. Andere Hashabweichungen werden nicht normalisiert oder übergangen.

`test-results/overlay-native-root-check.json` hält die Root-Prüfung und diese Ausnahme maschinenlesbar fest. Der native Bericht bleibt ein Teilbefund: keine neue erfolgreiche Decompilation der offenen Maus-/Statuspfade. Die eigenen Kairos-Prüfungen belegen deren Implementierung in Kairos und erweitern die SunnyCapturer-Evidenz nicht.
