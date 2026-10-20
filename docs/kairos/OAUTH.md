# Kairos Capture: OpenAI OAuth und OCR

Stand: 19.10.2026. Grundlage sind der lokale [Implementierungsvertrag](reverse/OPENAI-OAUTH-VERTRAG.md), die vollständig geprüften eigenen Service-Entwürfe und die unten verlinkten offiziellen OpenAI-Seiten. Es wurde kein SunnyCapturer-Quellcode übernommen. Eine echte OCR-Anfrage mit synthetischem Bild und vorhandener Nutzersitzung ist bestanden. Ein neuer echter Login, reale Tokenrotation und Widerruf wurden in dieser Prüfung nicht durchgeführt.

## Anmeldung und Identität

Kairos öffnet ausschließlich auf `SignInAsync` den Systembrowser. Der Loopback-Listener auf `127.0.0.1` läuft vorher. Pfad und Schema bleiben `/auth/callback` und HTTP; innerhalb eines Versuchs ist auch der Port unverändert. State, Nonce und PKCE-Verifier enthalten jeweils neue 256 Bit Zufall, die Challenge nutzt S256. Die stabile Host-ID liegt vor dem Browserstart im geschützten eigenen Store.

Eine neue Registrierung beginnt mit `dynamic_agent_client` und `agent_name_hint=Kairos Capture`. Codeaustausch und spätere Anmeldung verwenden die ausgestellte Client-ID. Ein nach einem fehlgeschlagenen Austausch ausstehender Client bleibt für einen erneuten Versuch erhalten. Rückkehr mit ausgewähltem Konto verwendet dessen eigene Client-ID und gespeicherte Hinweise. Ein fremdes Subjekt ersetzt das gewählte Konto nicht. Fehlercallbacks prüfen zuerst State; doppelte Parameter, veränderte Client-IDs und abweichende Callback-Adressen einschließlich Benutzerinfo werden verworfen. Es gibt kein Client-Secret. Das entspricht dem [OpenAI-Anmeldevertrag](https://developers.openai.com/siwc/token-sharing-open-source/sign-in).

`Microsoft.IdentityModel.JsonWebTokens` **8.23.0** ist die vom Projektowner bereitzustellende Abhängigkeit. `JsonWebTokenHandler` prüft Signatur, Issuer, Audience und Lebensdauer mit fünf Sekunden Zeittoleranz. Nur RS256 und ein passender RSA-Signaturschlüssel aus dem fest adressierten OpenAI-JWKS werden akzeptiert; unbekannte `kid` lösen ein erneutes Laden aus. JWT-Header und Claims mit doppelten Schlüsseln werden zurückgewiesen. Zusätzliche OIDC-Prüfungen binden Nonce, `azp` bei mehreren Audiences, `iat` und gegebenenfalls das bereits bekannte Subjekt. Erst danach werden Identitätsdaten übernommen. Bei Refresh darf Nonce fehlen; eine vorhandene muss weiterhin passen. Die offizielle [Signaturvalidierung](https://developers.openai.com/siwc/website) empfiehlt eine gepflegte JWT-Bibliothek und einen kleinen Zeitpuffer.

## Sitzungen und Ablage

Der Produktstore liegt ausschließlich unter `%LOCALAPPDATA%/KairosCapture/openai-session.bin`. Es werden keine Tokens anderer Programme, Browserprofile oder Umgebungsvariablen eingelesen. Jeder Datensatz ist durch Issuer, ausgestellte Client-ID und validiertes Subjekt getrennt; die E-Mail dient nur zur Anzeige.

Der gesamte Snapshot wird mit Windows DPAPI im CurrentUser-Kontext und fester App-Entropie verschlüsselt. Eine temporäre Datei enthält bereits ausschließlich Chiffretext. Nach Schreiben und Flush ersetzt `File.Replace` den alten Snapshot atomar; die Erstablage nutzt `File.Move`. Klartext-Bytepuffer werden gelöscht. Beschädigte oder nicht entschlüsselbare Daten bleiben unverändert. DPAPI ist eine Kairos-Entscheidung; der [OpenAI-Vertrag zur geschützten Ablage](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions) verlangt keine spezielle Windows-Technik.

Eine Instanz-Gate und eine exklusive Store-Dateisperre serialisieren Refresh auch zwischen Prozessen. Nach Sperrerwerb wird der aktuelle Store neu gelesen. Der Refresh sendet ausgestellte Client-ID, Refresh-Token und Resource, lässt `scope` weg und ersetzt Tokens, Scope und Ablauf gemeinsam. Ein fehlender oder unveränderter Ersatz-Refresh-Token wird zurückgewiesen. `earliest_refresh_at` wird beachtet. Nach begonnenem Refresh beendet ein Aufrufer-Abbruch die Speicherung einer empfangenen Rotation nicht; ein interner Timeout begrenzt diese Phase. Bei unklarer Netzunterbrechung wird eine Rotation nicht automatisch wiederholt. Eine unterbrochene Übertragung oder ein Prozessabsturz kann weiterhin eine erneute Anmeldung erfordern. Die [Tokenreferenz](https://developers.openai.com/siwc/token-sharing-open-source/token-reference) beschreibt den Austausch gegen einen Ersatz-Refresh-Token.

Logout stoppt laufende Requests und eine ausstehende Anmeldung. Der relevante aktive Account wird unter derselben Gate nach Abschluss oder Abbruch eines Logins ermittelt. Discovery muss den fest erwarteten Issuer liefern; der Widerrufsendpoint muss HTTPS auf `auth.openai.com` ohne Benutzerinfo, Fremdport oder Fragment sein. Widerruf erhält ausschließlich das eigene Refresh-Token und die zugehörige Client-ID. Für Netzwerk-/Serverfehler gibt es maximal einen weiteren Versuch. Lokale Tokens werden auch bei unbestätigtem Widerruf entfernt; `SignOutAsync` gibt dann eine sichtbare Warnung zurück. Host-ID und Registrierung bleiben erhalten.

## OCR und Fehlermeldungen

Vor Modellliste und OCR müssen `resource.invoke` und `chatgpt.tokens.use.direct` gewährt sein. Eine bestätigte Identität ohne Direct-Freigabe bleibt angemeldet, darf aber keine Inferenz auslösen. Es gibt keinen Wechsel zu einem API-Key oder anderer Abrechnung.

Der Modellkatalog verwendet `models[]`, filtert `visibility=list`, bewahrt die Serverreihenfolge und liefert `display_name` und `slug`. OCR sendet das vom Aufrufer übergebene PNG als `input_image`-Data-URL in einem `input`-Array an den öffentlichen Responses-Endpunkt. `store=false` und `stream=true` sind fest gesetzt. Tools und zusätzliche Generierungsparameter werden nicht mitgesendet. Siehe [Modelle und Inferenz](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), [zulässige Preview-Eingaben](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations) und [Bild-Data-URLs](https://developers.openai.com/api/docs/guides/images-vision).

Nur `response.completed` mit `response.status=completed` liefert ein erfolgreiches Ergebnis. EOF, `[DONE]` ohne Bestätigung, `failed`, `incomplete`, `cancelled`/`canceled`, Refusal, ungültiges JSON und widersprüchliche Eventtypen sind Fehler. Bereits gemeldete Deltas bleiben Teiltext; die Oberfläche muss einen Fehler-/Abbruchstatus darstellen. Der ursprüngliche Session-CancellationToken wird festgehalten, damit auch A → B → A einen alten Request als abgebrochen erkennt.

`OpenAiRequestException` bildet Auth-, Scope-, Konto-/Workspace-, Guthaben-, Raten-, App-Limit-, Routing- und temporäre Fehler getrennt ab. HTTP-Status, begrenzte sichere Codes/Parameter, Request-ID und Antwortform bleiben erhalten; rohe Providertexte oder Tokenwerte werden nicht angezeigt. Eine `detail`-Antwort ist kein stabiler Fehlercode. Grundlage: [Fehler und Wiederherstellung](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery).

Der interne `OpenAiUserException` ist der zusätzliche UI-Vertrag für bekannte lokale Zustände. Sein Konstruktor akzeptiert ausschließlich `OpenAiUserError`, keine freie Meldung. Die UI darf dessen feste App-Meldungen sowie `OpenAiRequestException.Message` anzeigen. Für OCR-Protokollfehler enthält `OpenAiOcrProtocolException` ausschließlich einen festen `OpenAiOcrReason` und dessen deutsche App-Meldung. Unbekannte Library-Ausnahmen erhalten weiterhin eine allgemeine Meldung. Öffentliche Account-, Model- und Service-Signaturen bleiben erhalten.

## Offline-Prüfung und Reproduktion

`public static OpenAiSelfChecks.Run(): IReadOnlyList<string>` läuft synchron. Die Liste enthält ausschließlich Fehler; leer bedeutet bestanden. Jeder Lauf erzeugt eigene temporäre Stores und RSA-Schlüssel. Alle OpenAI-HTTP-Aufrufe werden von einem Fake-Handler beantwortet. Synthetische Login-Flows ersetzen den Browser durch einen lokalen Callback-Produzenten; es wird keine Authentifizierungsoberfläche geöffnet oder bedient. Aufgeräumt wird ausschließlich der kontrolliert aufgelöste eigene Testpfad.

Geprüfte Fälle:

- PKCE-Known-Vector und unabhängige Zufallswerte; gültige/ungültige Callback-Adressen, State, Client-ID, doppelte Parameter und OAuth-Ablehnung.
- RS256-Positivfall sowie fremde Signatur, falscher Issuer/Audience/Nonce/`azp`/Account, fehlende Claims, Ablauf, zukünftige Zeiten, unbekannter Schlüssel, ungeeignete Algorithmen und doppelte Headerfelder.
- DPAPI-Roundtrip, Manipulationsabwehr, erhaltener alter Snapshot nach abgebrochener Speicherung und entfernte temporäre Dateien.
- Modellfilter/Reihenfolge, Bearer-Zuordnung, Bild-Data-URL und exakte minimale Responses-Felder; abgeschlossene, leere, mehrzeilige, fehlerhafte und abgebrochene SSE-Streams.
- Zwei Service-Instanzen mit gemeinsamem Store erneuern nur einmal; Rotation überlebt Aufrufer-Abbruch. Terminale Refresh-Fehler entfernen Tokens, temporäre Fehler erhalten sie; fehlende/unveränderte Rotation und ungültige Scope-Daten erreichen keine Inferenz.
- Discovery-basierter Widerruf, Abwehr eines fremden Endpoints, Warnung bei unbestätigtem Widerruf und lokale Tokenlöschung.
- Synthetische neue/zurückkehrende Anmeldung, falsches Subjekt, falscher State, Ablehnung und Identity-only inklusive des sicheren UI-Fehlertyps; Logout während Login; A → B → A während einer laufenden Modellabfrage.

Der isolierte Service-Harness wurde am 18.10.2026 mit dem privaten SDK `C:/Users/Axel_desktop/.t3/projects/clockify/.dotnet/dotnet.exe`, SDK 10.0.401, .NET-Referenzen 10.0.12 und IdentityModel 8.23.0 kompiliert. Compilerflags enthielten `/nullable:enable /warnaserror+`. Ergebnis nach Korrektur: **Exit 0, `Failures: 0`**. Kein Projektfile wurde für diesen Harness geändert. Generierte Runner und Pakete liegen ausschließlich im ignorierten `test-results/openai-services/`, außerhalb von `src`.

Die Gegenproben reproduzierten im Altstand die Annahme von Callback-Benutzerinfo, falschen SSE-Erfolg nach `response.cancelled`/`response.canceled`, verlorene Rotation bei Aufrufer-Abbruch und die zu allgemeine Guthabenmeldung. Der spätere deterministische A/B/A-Test hielt den HTTP-Abbruch bis nach beiden Kontowechseln an und erhielt zunächst fälschlich einen Timeout. Nach den jeweiligen Korrekturen bestanden die Gegenproben. Es gab kein erneutes Auftreten derselben Sollverletzung nach ihrer Korrektur.

Nach Integration in die Shell führt `KairosCapture --self-test` denselben `Run()`-Vertrag aus. Der hier beschriebene isolierte Compilerlauf ist keine Abnahme des gesamten WPF-Projekts.

Offen bleiben andere Konto- und Workspace-Konstellationen, reale Tokenrotation und Widerruf sowie OCR-Qualität und Latenz für echte Aufnahmen. Die Oberfläche muss bei Kontoänderung ihre Modellliste neu laden und alte Deltas verwerfen. Die Prüfung, dass die Produkt-UI ausschließlich den endgültig geschwärzten Export sendet, gehört zur Shell-/Editor-Integration. Offline-Prüfungen und Vorschauen lesen keine Produktkonten. Nur der ausdrücklich aufgerufene Schalter `--openai-ocr-probe` verwendet die gespeicherte Nutzersitzung.

## Diagnose und Übersetzung (19.10.2026)

Die Callback-Seite bestätigt die Annahme des Rückrufs, noch keinen abgeschlossenen Login. Beim gemeldeten Stack `SendAsync ← SendJsonAsync ← SignInAsync` ohne `GetKeysAsync` scheitert der Token-POST vor JWKS, Identitätsprüfung und neuer Kontospeicherung. Der Dateizeitstempel allein beweist keinen Pending-Eintrag: Auch vor dem Browserstart wird der Store geschrieben. Ein neues Konto speichert seinen Pending-Client zwingend vor dem POST. Die konkrete Transportursache der beiden gemeldeten Fehler ist nicht mehr rekonstruierbar, weil der bisherige Wrapper die Originalausnahme verwarf.

Die Originalausnahme bleibt jetzt erhalten. Diagnose und UI unterscheiden Callback, Token, JWKS, Validierung, Speicherung und Modelle. Das Log enthält nur feste Schritte/Enum-Werte, Exception-Typketten, `HttpRequestError`, `SocketErrorCode`, HTTP-Status und Fehlercodes aus einer festen Liste; keine Messages, Request-IDs, E-Mails, URLs oder Anmeldewerte. Ein Autorisierungscode wird höchstens einmal wiederholt, ausschließlich bei DNS-Fehler oder Socket-Nachweis eines gescheiterten Verbindungsaufbaus. Antworten einschließlich HTTP 400/500, TLS-Fehler, Reset und Timeout lösen keinen solchen Retry aus. Refresh-Rotation wird weiterhin nicht automatisch wiederholt.

`KairosCapture --openai-probe [Ausgabedatei]` prüft ohne Produktstore und Browser Discovery, JWKS und einen Fake-Code-POST. Standardausgabe: `test-results/openai-probe.txt`, zusätzlich stdout. Die Probe verwendet dieselbe HttpClient-Fabrik (`SocketsHttpHandler`, `AllowAutoRedirect=false`, unendlicher Client-Timeout), dieselbe Formularfabrik und denselben `ResponseHeadersRead`-/JSON-Pfad wie die Anmeldung. Die Request-Werte sind absichtlich erfunden. HTTP 400 mit `invalid_client` oder `invalid_grant` ist der erwartete Nachweis einer empfangenen Tokenanfrage. Am 19.10.2026: Discovery **200**, JWKS **200**, Token **400 / invalid_grant**, Exit **0**. Der ursprüngliche Transportfehler war damit nicht reproduzierbar. Alle untersuchten Explorer- und Kairos-Prozesse enthielten keine `*_PROXY`-Variablen; Windows-Proxy war deaktiviert. Es wurde keine Netzwerkeinstellung geändert und kein echter Login durchgeführt.

Nach bestätigter Identität übernehmen die Einstellungen das aktive Konto sofort, auch bei fehlender Direct-Planfreigabe. Bei nutzbarer Freigabe wird der Katalog geladen: Ein gültiges Modell bleibt erhalten, sonst wird das erste gelistete Modell in Serverreihenfolge gewählt und gespeichert. Ein leerer Katalog bleibt als Fehler sichtbar. Tray und Kontostatus werden über die vorhandenen Änderungsereignisse sofort aktualisiert.

Der OCR-Dialog beginnt bei jedem Öffnen mit „Nicht übersetzen“. Die feste Liste erlaubt zusätzlich Deutsch, Englisch, Thai, Französisch, Spanisch, Italienisch, Japanisch und Chinesisch (vereinfacht). `ExtractTextAsync` akzeptiert dafür einen optionalen letzten Sprachparameter; andere Werte werden vor HTTP abgewiesen. Übersetzung und Transkription verwenden dasselbe PNG und behalten die Anweisung bei, Bildinhalt als nicht vertrauenswürdig zu behandeln. Ein Wechsel bricht den alten Request ab und verwirft dessen Deltas, Ergebnis und Abschlussstatus. Fertige Ergebnisse werden nur im Fenster pro Sprache zwischengespeichert; AutoCopy gilt auch für Übersetzungen. `--render-ocr-result <PNG>` ist ein Alias für die OCR-Vorschau.

## Reale OCR-Probe und fehlender Content-Type (19.10.2026)

`KairosCapture --openai-ocr-probe <Ausgabedatei>` rendert ein weißes PNG mit dem schwarzen Text `Kairos Test 123`. Der Schalter lädt `Settings.AccountKey` und `Settings.Model`, wählt das gespeicherte Konto und sendet genau eine OCR-Anfrage über denselben Service und Parser wie das Ergebnisfenster. Er öffnet keinen Browser und speichert keine Einstellungen. Die übliche Sitzungserneuerung durch den Service bleibt möglich.

Die Ausgabe enthält HTTP-Status, Content-Type-MediaType und Charset, die geordnete Folge von SSE-`event` und JSON-`type`, Feldnamen des ersten und letzten Ereignisses sowie sichere Fehlercodes und Fehlerparameter. Feldwerte, Header außer Content-Type, Kontodaten und rohe Antworttexte werden nicht ausgegeben. Nur wenn das bestätigte Ergebnis exakt dem synthetischen Text entspricht, erscheint `result=Kairos Test 123`. Andere Ergebnisse werden als `<unexpected-text>` ausgegeben. Die Antwort wird nur während der Probe mit der bestehenden Grenze von 2 MiB im Speicher gehalten. Größen- und Zeitgrenzen des Produktparsers bleiben bestehen.

Die Offline-Absicherung unterscheidet das OCR-Zeitlimit von zwei Minuten (`step=Ocr`), den Rotations-Timeout von 30 Sekunden (`step=Refresh`) und das Warten auf eine laufende Anmeldung (`step=Session`). JSON-Anfragen haben zusätzlich ein eigenes Zeitlimit von 30 Sekunden. Ein eigenes Zeitlimit wird als `TimeoutException` mit `outcome=Failed class=Timeout` und dem betroffenen Schritt gemeldet. Das gilt auch für das Lesen des JSON-Bodys, den Browserrückruf und den Fernwiderruf.

Abbrüche durch Aufrufer, Lifetime oder die zugehörige Session bleiben `OperationCanceledException` mit `outcome=Cancelled`; der Task endet als `Canceled`. Das gilt auch beim Warten auf den Browserrückruf. Während einer begonnenen Refresh-Rotation wartet ein Aufrufer- oder Session-Abbruch weiterhin auf deren begrenzten Abschluss, damit empfangene Ersatz-Tokens gespeichert werden. Läuft die Rotation anschließend in ein Zeitlimit oder scheitert sie anderweitig, hat der bereits angeforderte Abbruch Vorrang beim Task-Ergebnis und beim terminalen Log. Die Lifetime kann die Rotation selbst abbrechen. Eine fremde `OperationCanceledException` ohne abgebrochenes Token wird als Fehler gemeldet.

`SignInAsync`, `GetModelsAsync`, `ExtractTextAsync`, `SignOutAsync` und die gesamte `ProbeAsync` schreiben jeweils genau eine terminale Zeile mit `Succeeded`, `Failed` oder `Cancelled`. Die endgültige Zuordnung erfolgt an ihrer äußeren Grenze. HTTP- und JSON-Hilfsfunktionen schreiben ausschließlich `Started` und `Response`; ein wiederholter Token-Verbindungsaufbau schreibt vorher `Token/Retrying` mit der Fehlerklasse des ersten Versuchs und danach erneut diese Fortschrittszeilen. Die Probe wertet die erwartete Ablehnung des erfundenen Codes als Erfolg und schreibt ihren gemeinsamen Abschluss erst nach Discovery, JWKS und Token-Prüfung. Bei unbestätigtem Fernwiderruf bleibt die lokale Abmeldung wirksam und liefert weiterhin ihre Warnung; das terminale Log meldet den fehlgeschlagenen Fernwiderruf. Bricht der Aufrufer während des Fernwiderrufs ab, ist die lokale Abmeldung trotzdem abgeschlossen: `SignOutAsync` endet erfolgreich mit der Warnung; das terminale Log meldet den Schritt, in dem abgebrochen wurde (`Discovery/Cancelled` oder `Revoke/Cancelled`). Ein bereits gescheiterter Widerruf bleibt `Failed`, auch wenn danach abgebrochen wird. Ein Abbruch nach bestätigtem Widerruf ändert nichts mehr. Nur das Beenden der App bricht die Abmeldung als Ganzes ab.

IO-Fehler nach Empfang der OCR-Antwort heißen `ConnectionInterrupted`. Nur Laden, Sperren und Speichern des lokalen Sitzungsspeichers markieren Fehler als `Persist`; lokale IO-Fehler erhalten `class=Storage`. Die UI nennt sowohl Lesen als auch Speichern. Fehlende Konten und abgelaufene Sitzungen bleiben `Session`, ungültige Refresh-Parameter bleiben `Refresh`. Die Meldungen nennen jeweils einen passenden nächsten Schritt.

Der Parser akzeptiert ausschließlich UTF-8 mit optionaler UTF-8-BOM. Eine Antwort ohne SSE-Datenfelder meldet `NoEvents`. Sobald das erste `data:`-Feld gelesen wurde, meldet EOF ohne bestätigten Abschluss `UnexpectedEnd`, auch mitten im ersten Datenblock. Die Probe liest den Netzwerkstream einmal und verwendet die erfassten Bytes auch für die HTTP-Fehlerauswertung. Der Test prüft Request-Anzahl, Ergebnis und einen zweiten Leseversuch nach EOF, ohne eine feste Anzahl gepufferter Leseaufrufe vorauszusetzen. Ungültige Metadaten werden geschwärzt; dynamische Feldnamen mit ID-Präfix oder langen Hex- beziehungsweise Base62-Segmenten erscheinen als `<id>`. `RunCheck` zeigt nur Meldungen des eigenen Typs `SelfCheckFailure`, alle fremden Exceptions bleiben geschwärzt. Fake-HTTP, Streams und ein privater temporärer Store prüfen diese Verträge ohne echte OpenAI-Anfragen. Die Gegenproben und ihre Ausgaben der ersten Korrekturrunde liegen lokal unter `test-results/mutations/`.

Die Regressionstests der zweiten Korrekturrunde vom 20.10.2026 prüfen vollständige Logfolgen für Anmeldung, Modelle, OCR, Abmeldung und Probe. OCR-Stream und Refresh-Fakes warten mit `Task.Delay(Timeout.InfiniteTimeSpan, token)` auf das tatsächlich übergebene Token; der Login-Test wartet am lokalen Callback-Listener. Die Abbruchtests prüfen den Task-Zustand und eine Obergrenze von einer Sekunde nach dem Abbruchsignal. Ein zusätzlicher Test fordert während der geschützten Rotation einen Aufrufer-Abbruch an und lässt anschließend das interne Refresh-Zeitlimit auslösen. Der lokale Runner `test-results/r9/run-mutation.ps1` stellt nach jeder isolierten Mutation den unveränderten Fix wieder her und führt die neun Offline-Suiten erneut aus. Berichte und Build-Ausgaben liegen unter `test-results/r9/`.

Alle acht unterschiedlichen Mutationen lieferten Exit 1 mit dem jeweils erwarteten OpenAI-Testfehler. Nach jeder Wiederherstellung lieferten dieselben neun Offline-Suiten Exit 0. `test-results/r9/mutations.json` enthält die Fehlertexte und Ergebnisse. Die Gegenproben schalten folgende Fehler gezielt ein:

- A stellt den alten pauschalen Persist-Pfad in `AccessTokenAsync` wieder her.
- B ergänzt terminale Logs in `SendJsonAsync`, einmal mit der alten Abbruchunterscheidung und einmal mit pauschalem `Failed`.
- C stellt das fehlerhafte `Failed`-Log beim Login-Abbruch wieder her und entfernt getrennt den Abbruchvorrang nach einem Refresh-Timeout.
- D ersetzt die Aufrufer-Tokens beider `Operation`-Aufrufe durch `CancellationToken.None`, entfernt getrennt die Aufrufer-Verknüpfung der Anmeldung und die Lifetime-Verknüpfung der Rotation.

Der Gate-Test beendet zur Bereinigung zusätzlich die Service-Lifetime. So bleibt die Gegenprobe auch bei unterbrochener Aufrufer-Weitergabe kurz. Der erste Lauf dieser Callback-Mutation lief noch bis zum internen Anmelde-Zeitlimit aus; kein Prozess wurde beendet. Produktfehler-Fixtures und die absichtlich fremde `InvalidOperationException` im Schwärzungstest bleiben erhalten. Die übrigen direkten Assertion-Exceptions verwenden `SelfCheckFailure`.

Bei einer Firewall mit standardmäßig blockierten ausgehenden Verbindungen nutzt die Probe den bereits freigegebenen .NET-Host. Aus dem eigenen Worktree ausführen.

```powershell
$d = (Get-Content 'C:/Users/Axel_desktop/.t3/projects/kapture/.tools/dotnet-sdk-path.txt').Trim()
$env:DOTNET_ROOT = Split-Path $d
& $d 'src/KairosCapture/bin/Release/net10.0-windows/win-x64/KairosCapture.dll' --openai-ocr-probe 'test-results/ocr-probe.txt'
```

Zwei echte OCR-Anfragen wurden ausgeführt. Beide nutzten das gespeicherte Modell `gpt-6.1-sol`. Vor der Korrektur brach die erste Anfrage direkt nach HTTP 200 ab. Die vollständige Ausgabe liegt lokal in `test-results/ocr-probe-before.txt`.

```text
http.status=200
contentType.mediaType=<missing>
contentType.charset=<missing>
sse[1].event=<missing> json.type=<invalid-json>
first.fields=<none>
last.fields=<none>
check=ContentType
step=Unknown class=Protocol types=KairosCapture.Services.OpenAiOcrProtocolException httpRequestError=None socketError=None status= code=None
```

Die erste Version der Diagnose versuchte bei fehlendem Content-Type, den gesamten Body als JSON zu lesen. Deshalb ist `<invalid-json>` hier kein Beleg für fehlerhafte SSE-Ereignisse. Diese Erfassung wurde vor der zweiten Anfrage korrigiert. Es wurde kein roher Body gespeichert.

Die belegte Ursachenkette lautet:

1. `ExtractTextAsync` erhält HTTP 200. Die Probe misst MediaType und Charset als fehlend.
2. Die Bedingung `ContentType?.MediaType != "text/event-stream"` ist auch für `null` wahr. Der Service verwirft die Antwort, bevor `ReadSseAsync` ein Ereignis lesen kann.
3. Diese Ausnahme entsteht hinter `SendAsync`. Der bisherige OCR-Pfad markiert sie nicht mit einem Schritt. Daher erscheinen `step=Unknown`, `InvalidOperationException` und die allgemeine UI-Meldung.
4. Die beeinflussbare Wurzel ist die Annahme, eine gültige SIWC-Stream-Antwort enthalte zwingend den Content-Type-Header. Die zweite Anfrage widerlegt diese Annahme mit einem gültigen SSE-Body und bestätigtem synthetischem Text bei erneut fehlendem Header. Warum der Header auf der Gegenseite fehlt, ist nicht bestimmt.

Der Service lässt einen fehlenden MediaType jetzt durch den strengen SSE-Parser prüfen. Ein ausdrücklich anderer MediaType bleibt `reason=ContentType`. Die Prüfung erfolgt ohne Beachtung der Großschreibung. Gültiges JSON, vorhandener JSON-`type`, übereinstimmende Ereignisnamen, Grenzen für Ereignisse und Text sowie `response.completed` mit Status `completed` bleiben erforderlich. EOF, `[DONE]`, Refusal und Fehlerzustände bleiben Fehler. Zusätzliche typisierte Ereignisse wie das tatsächlich gelieferte `response.metadata` wurden bereits ignoriert und werden weiterhin ignoriert.

Die zweite Anfrage liefert Exit 0. Die vollständige Metadatenausgabe mit den Feldnamen liegt lokal in `test-results/ocr-probe-after.txt`. Dieser Auszug enthält die gesamte Ereignisfolge.

```text
http.status=200
contentType.mediaType=<missing>
contentType.charset=<missing>
sse[1].event=response.created json.type=response.created
sse[2].event=response.in_progress json.type=response.in_progress
sse[3].event=response.output_item.added json.type=response.output_item.added
sse[4].event=response.content_part.added json.type=response.content_part.added
sse[5].event=response.output_text.delta json.type=response.output_text.delta
sse[6].event=response.output_text.delta json.type=response.output_text.delta
sse[7].event=response.output_text.delta json.type=response.output_text.delta
sse[8].event=response.output_text.delta json.type=response.output_text.delta
sse[9].event=response.output_text.delta json.type=response.output_text.delta
sse[10].event=response.metadata json.type=response.metadata
sse[11].event=response.output_text.done json.type=response.output_text.done
sse[12].event=response.content_part.done json.type=response.content_part.done
sse[13].event=response.output_item.done json.type=response.output_item.done
sse[14].event=response.completed json.type=response.completed
result=Kairos Test 123
check=Passed
```

Die erfolgreiche Bildanfrage mit demselben Modell belegt, dass für diesen Fehler keine Änderung der Modellauswahl nötig ist. Fehlender `type`, widersprüchliche Namen und unbekannte Ereignisse verursachten den beobachteten Abbruch ebenfalls nicht. Die neue Offline-Fixture bildet die 14 Ereignisse mit fünf synthetischen Textdeltas und fehlendem Content-Type nach. Weitere Fälle prüfen explizit falschen MediaType, JSON statt SSE, fehlenden Typ, widersprüchliche Namen und unbestätigten Abschluss. Fehler werden als `step=Ocr`, HTTP-Status und festem `reason` protokolliert. Das Ergebnisfenster zeigt die zugehörige feste deutsche Meldung über den vorhandenen `UserMessage`-Pfad.

Der abschließende Lauf von `pwsh -File scripts/build.ps1` besteht einschließlich Release-Build, neun Offline-Suiten, WPF-Vorschauen und Publish. Ergebnis ist Exit 0 mit 0 Warnungen, 0 Fehlern und 9/9 bestandenen Suiten. Der Bericht liegt lokal in `test-results/self-test.json`. Die laufende Nutzerinstanz mit PID 47440 wurde nicht beendet.

Offizielle Quellen, abgerufen am 19.10.2026:

- [Models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference) beschreibt den öffentlichen Responses-Endpunkt, `store=false`, `stream=true` und den erforderlichen Abschluss durch `response.completed`. Die Seite belegt keine Garantie für einen vorhandenen Content-Type-Header.
- [Streaming API responses](https://developers.openai.com/api/docs/guides/streaming-responses) beschreibt HTTP-SSE und semantische Ereignisse mit `type`. Anwendungen können die für sie relevanten Ereignisse verarbeiten.
