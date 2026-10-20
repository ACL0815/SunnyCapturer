# Kairos Capture: OpenAI OAuth und OCR

Stand: 20.10.2026. Grundlage sind der lokale [Implementierungsvertrag](reverse/OPENAI-OAUTH-VERTRAG.md), die vollständig geprüften eigenen Service-Entwürfe und die unten verlinkten offiziellen OpenAI-Seiten. Es wurde kein SunnyCapturer-Quellcode übernommen. Eine echte OCR-Anfrage mit synthetischem Bild und vorhandener Nutzersitzung ist bestanden. Ein neuer echter Login, reale Tokenrotation und Widerruf wurden in dieser Prüfung nicht durchgeführt.

## Anmeldung und Identität

Kairos öffnet ausschließlich auf `SignInAsync` den Systembrowser. Der Loopback-Listener auf `127.0.0.1` läuft vorher. Pfad und Schema bleiben `/auth/callback` und HTTP; innerhalb eines Versuchs ist auch der Port unverändert. State, Nonce und PKCE-Verifier enthalten jeweils neue 256 Bit Zufall, die Challenge nutzt S256. Die stabile Host-ID liegt vor dem Browserstart im geschützten eigenen Store.

Eine neue Registrierung beginnt mit `dynamic_agent_client` und `agent_name_hint=Kairos Capture`. Codeaustausch und spätere Anmeldung verwenden die ausgestellte Client-ID. Ein nach einem fehlgeschlagenen Austausch ausstehender Client bleibt für einen erneuten Versuch erhalten. Rückkehr mit ausgewähltem Konto verwendet dessen eigene Client-ID und gespeicherte Hinweise. Ein fremdes Subjekt ersetzt das gewählte Konto nicht. Fehlercallbacks prüfen zuerst State; doppelte Parameter, veränderte Client-IDs und abweichende Callback-Adressen einschließlich Benutzerinfo werden verworfen. Es gibt kein Client-Secret. Das entspricht dem [OpenAI-Anmeldevertrag](https://developers.openai.com/siwc/token-sharing-open-source/sign-in).

`Microsoft.IdentityModel.JsonWebTokens` **8.23.0** ist die vom Projektowner bereitzustellende Abhängigkeit. `JsonWebTokenHandler` prüft Signatur, Issuer, Audience und Lebensdauer mit fünf Sekunden Zeittoleranz. Nur RS256 und ein passender RSA-Signaturschlüssel aus dem fest adressierten OpenAI-JWKS werden akzeptiert; unbekannte `kid` lösen ein erneutes Laden aus. JWT-Header und Claims mit doppelten Schlüsseln werden zurückgewiesen. Zusätzliche OIDC-Prüfungen binden Nonce, `azp` bei mehreren Audiences, `iat` und gegebenenfalls das bereits bekannte Subjekt. Erst danach werden Identitätsdaten übernommen. Bei Refresh darf Nonce fehlen; eine vorhandene muss weiterhin passen. Die offizielle [Signaturvalidierung](https://developers.openai.com/siwc/website) empfiehlt eine gepflegte JWT-Bibliothek und einen kleinen Zeitpuffer.

## Sitzungen und Ablage

Der Produktstore liegt ausschließlich unter `%LOCALAPPDATA%/KairosCapture/openai-session.bin`. Es werden keine Tokens anderer Programme, Browserprofile oder Umgebungsvariablen eingelesen. Jeder Datensatz ist durch Issuer, ausgestellte Client-ID und validiertes Subjekt getrennt; die E-Mail dient nur zur Anzeige.

Der gesamte Snapshot wird mit Windows DPAPI im CurrentUser-Kontext und fester App-Entropie verschlüsselt. Eine temporäre Datei enthält bereits ausschließlich Chiffretext. Nach Schreiben und synchronem Flush veröffentlicht `File.Replace` den Snapshot. Bei fehlendem Ziel nutzt der Store `File.Move`. Ein fehlgeschlagener Austausch kann den Zielnamen entfernen. Deshalb erhält der Store die Temp-Dateien, solange kein gültiger Hauptstand lesbar ist. Bei fehlendem Ziel liest er den neuesten eindeutig geordneten nicht leeren Temp-Stand. Ist dieser nicht lesbar, meldet er einen Fehler und erhält alle Dateien. Leser und Schreiber halten dabei dieselbe Sperre. Klartext-Bytepuffer werden gelöscht. Beschädigte oder nicht entschlüsselbare Daten bleiben unverändert. DPAPI ist eine Kairos-Entscheidung; der [OpenAI-Vertrag zur geschützten Ablage](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions) verlangt keine spezielle Windows-Technik.

Der `OpenAiService`-Konstruktor führt keine Speicherzugriffe aus. `IsLoaded` ist zunächst `false`; `Accounts` ist leer und `ActiveAccount` ist `null`. `EnsureLoadedAsync(ct)` lädt außerhalb des UI-Threads unter Service-Gate und Dateisperre. Bei `SessionBusy` wiederholt es den Sperrerwerb in einem gemeinsamen Fenster von 35 Sekunden. Abbruch und fehlgeschlagene Ladeversuche erlauben einen neuen Aufruf. Parallele erfolgreiche Aufrufe lesen den Anfangszustand einmal. Kontenabfrage, Kontowahl, Anmeldung, Abmeldung, Modelle und OCR laden bei Bedarf vor ihrem ersten zustandsabhängigen Zugriff.

Das Einstellungsfenster zeigt während eines laufenden Ladeversuchs „OpenAI-Konto wird geladen…“ und übernimmt nach Erfolg einmalig `Settings.AccountKey`. Kontenanzeige und Tray aktualisieren sich nach Erfolg, Fehler oder Abbruch. Nach Abbruch steht dort „nicht geladen · erneut versuchen“. Die `OperationCanceledException` wird an den Aufrufer weitergegeben. Nach einem Ladefehler bleibt der Service verfügbar. Verbinden, Modelle aktualisieren und Texterkennung versuchen das Laden beim nächsten Klick erneut. `OcrResultWindow.cs` lädt vor seiner Kontoprüfung; die OCR-Probe lädt vor der gespeicherten Kontowahl.

Fehlt die Hauptdatei und fehlen alle Temp-Dateien oder haben sie ausschließlich 0 Byte, liefert `Load()` einen frischen Zustand. Eine nicht leere unlesbare Temp kann eine vollständige Sitzung enthalten, die DPAPI vorübergehend nicht entschlüsseln kann. Ihr Fehler ist kein Beweis für Datenmüll. Ein solcher neuester Kandidat blockiert den Rückfall auf einen älteren Stand. Bei unbekannter Reihenfolge blockieren auch mehrere nicht leere Legacy-Temps, die Mischung aus Legacy- und geordneten Temps sowie gleiche höchste Ordnungsnummern. Ein erfolgreich veröffentlichter, lesbarer Hauptstand erlaubt weiterhin das Cleanup abgebrochener Schreibversuche.

Bei `CredentialsUnreadable` erscheint in den Einstellungen **Gespeicherte Anmeldung zurücksetzen**. Diese ausdrückliche Nutzeraktion umgeht `EnsureLoadedAsync`, hält aber dieselbe Service-Gate und Store-Sperre. Sie verschiebt Hauptdatei und sämtliche `openai-*.tmp` unverändert in eine Sicherung unter `%LOCALAPPDATA%\KairosCapture\session-backup-<UTC-Zeitstempel>-<GUID>`. Keine dieser Dateien wird beim Reset gelöscht. Der Erfolgsstatus nennt den vollständigen Sicherungspfad. Danach sind der Service frisch geladen und Verbinden sowie Anmelden wieder möglich. Kontowahl und Modellwahl werden zurückgesetzt.

Während der Verschiebungen heißt der Sicherungsordner `session-reset.pending`. Seine Existenz verhindert sowohl `Load()` als auch `SaveAsync()`. Nach einem Fehler, Abbruch oder Neustart setzt dieselbe Nutzeraktion die verbleibenden Verschiebungen fort. Erst das Umbenennen dieses Ordners in den fertigen Sicherungsnamen schließt den Reset ab. Damit kann ein teilweise verschobener Dateisatz nie als ältere oder leere Sitzung erscheinen. Dauerhaft gesperrte Dateien müssen vor einer erfolgreichen Wiederholung freigegeben werden; die Meldung nennt den unvollständigen Sicherungsordner. Vorhandene Sicherungen werden weder automatisch geladen noch beim späteren Cleanup entfernt.

Eine Instanz-Gate und eine exklusive Store-Dateisperre serialisieren Refresh auch zwischen Prozessen. Nach Sperrerwerb wird der aktuelle Store neu gelesen. Der Refresh sendet ausgestellte Client-ID, Refresh-Token und Resource, lässt `scope` weg und ersetzt Tokens, Scope und Ablauf gemeinsam. Ein fehlender oder unveränderter Ersatz-Refresh-Token wird zurückgewiesen. `earliest_refresh_at` wird beachtet. Nach begonnenem Refresh beendet ein Aufrufer-Abbruch die Speicherung einer empfangenen Rotation nicht; ein interner Timeout begrenzt diese Phase. Bei unklarer Netzunterbrechung wird eine Rotation nicht automatisch wiederholt. Eine unterbrochene Übertragung oder ein Prozessabsturz kann weiterhin eine erneute Anmeldung erfordern. Die [Tokenreferenz](https://developers.openai.com/siwc/token-sharing-open-source/token-reference) beschreibt den Austausch gegen einen Ersatz-Refresh-Token.

Logout stoppt laufende Requests und eine ausstehende Anmeldung. Der relevante aktive Account wird unter derselben Gate nach Abschluss oder Abbruch eines Logins ermittelt. Discovery muss den fest erwarteten Issuer liefern; der Widerrufsendpoint muss HTTPS auf `auth.openai.com` ohne Benutzerinfo, Fremdport oder Fragment sein. Widerruf erhält ausschließlich das eigene Refresh-Token und die zugehörige Client-ID. Für Netzwerk-/Serverfehler gibt es maximal einen weiteren Versuch. Lokale Tokens werden auch bei unbestätigtem Widerruf entfernt; `SignOutAsync` gibt dann eine sichtbare Warnung zurück. Host-ID und Registrierung bleiben erhalten.

## OCR und Fehlermeldungen

Vor Modellliste und OCR müssen `resource.invoke` und `chatgpt.tokens.use.direct` gewährt sein. Eine bestätigte Identität ohne Direct-Freigabe bleibt angemeldet, darf aber keine Inferenz auslösen. Es gibt keinen Wechsel zu einem API-Key oder anderer Abrechnung.

Der Modellkatalog verwendet `models[]`, filtert `visibility=list`, bewahrt die Serverreihenfolge und liefert `display_name` und `slug`. OCR sendet das vom Aufrufer übergebene PNG als `input_image`-Data-URL in einem `input`-Array an den öffentlichen Responses-Endpunkt. `store=false` und `stream=true` sind fest gesetzt. Tools und zusätzliche Generierungsparameter werden nicht mitgesendet. Siehe [Modelle und Inferenz](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), [zulässige Preview-Eingaben](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations) und [Bild-Data-URLs](https://developers.openai.com/api/docs/guides/images-vision).

Nur `response.completed` mit `response.status=completed` liefert ein erfolgreiches Ergebnis. EOF, `[DONE]` ohne Bestätigung, `failed`, `incomplete`, `cancelled`/`canceled`, Refusal, ungültiges JSON und widersprüchliche Eventtypen sind Fehler. Bereits gemeldete Deltas bleiben Teiltext; die Oberfläche muss einen Fehler-/Abbruchstatus darstellen. Der ursprüngliche Session-CancellationToken wird festgehalten, damit auch A → B → A einen alten Request als abgebrochen erkennt.

`OpenAiRequestException` bildet Auth-, Scope-, Konto-/Workspace-, Guthaben-, Raten-, App-Limit-, Routing- und temporäre Fehler getrennt ab. HTTP-Status, begrenzte sichere Codes/Parameter, Request-ID und Antwortform bleiben erhalten; rohe Providertexte oder Tokenwerte werden nicht angezeigt. Eine `detail`-Antwort ist kein stabiler Fehlercode. Grundlage: [Fehler und Wiederherstellung](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery).

Der interne `OpenAiUserException` ist der zusätzliche UI-Vertrag für bekannte lokale Zustände. Sein Konstruktor akzeptiert ausschließlich `OpenAiUserError`, keine freie Meldung. Die UI darf dessen feste App-Meldungen sowie `OpenAiRequestException.Message` anzeigen. Für OCR-Protokollfehler enthält `OpenAiOcrProtocolException` ausschließlich einen festen `OpenAiOcrReason` und dessen deutsche App-Meldung. Unbekannte Library-Ausnahmen erhalten weiterhin eine allgemeine Meldung. Die Kontowahl erfolgt asynchron mit `SelectAccountAsync(key, ct)`. `GetAccountsAsync(ct)` ergänzt die synchrone Anzeige um eine Kontenabfrage mit vorherigem Laden.

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

## Sporadische Fehler beim Sitzungsaustausch (20.10.2026)

Der unveränderte Stand scheiterte in 3 von 20 direkten Aufrufen von `OpenAiSelfChecks.Run()`, also in 15 Prozent der Läufe. Betroffen waren Anmeldung, Abmeldung und Refresh. Das lokale Protokoll liegt unter `test-results/storage-flake/baseline-openai.json`. Der vorgeschaltete unveränderte Release-Build bestand mit 0 Warnungen und neun bestandenen Offline-Suiten.

Die Ursachenkette wurde mit getrennten Gegenproben eingegrenzt:

1. Die Meldung `Persist/Storage/IOException` entsteht beim atomaren `File.Replace`, nicht beim Erwerb von `openai-session.lock`. Die lokale FirstChance-Instrumentierung erfasst `0x80070497`, Win32 1175. Die vollständigen Aufrufketten für Anmeldung und Abmeldung stehen in `test-results/storage-flake/trace-targeted-fast.jsonl` und `targeted-fast.log`.
2. Derselbe Fehler tritt ohne `OpenAiService`, HTTP-Fakes, Callback-Produzenten oder Abbruch-Tasks auf. Ein Runner hält eine einzige Store-Sperre und erwartet jeden `SaveAsync`-Aufruf vor dem nächsten. Von 2.000 Speichervorgängen scheiterten sechs mit 1175. Siehe `storage-only.log` und `trace-storage-only.jsonl`. Damit sind geteilte Fixture-Pfade und verwaiste Service-Tasks für diese Reproduktion ausgeschlossen.
3. Ein kontrolliert geöffneter Lesehandle auf `openai-session.bin` ohne `FileShare.Delete` verhindert den Austausch mit `0x80070020`, Win32 32. Ein zusätzlicher isolierter Leser-Wettlauf erzeugte 334 solcher Fehler bei 1.000 Austauschversuchen. Die Ergebnisse stehen in `held.log`, `trace-held.jsonl`, `handle-matrix.json` und `race-counterprobe.log`. Ein gemappter Leser mit erlaubtem Löschen verhinderte den Austausch nicht. Diese Gegenprobe belegt keinen bestimmten Fremdprozess für die natürlichen 1175-Ausfälle.
4. Die beeinflussbare Wurzel ist der einmalige Austauschversuch in `SaveAsync`. `LockAsync` koordiniert ausschließlich Teilnehmer, die dieselbe Sperrdatei verwenden. Ein Fehler beim Austausch wurde sofort weitergegeben und der vorbereitete verschlüsselte Zwischenstand in `finally` entfernt. Der Schutz gegen konkurrierende App-Instanzen ersetzt daher keine Behandlung eines vorübergehend blockierten Austauschs.

Der Prozess hinter den natürlichen 1175-Ausfällen wurde nicht bestimmt. Diese Win32-Ausnahme enthält keinen Dateipfad. Die Restart-Manager-Abfragen mit bekanntem Pfad lieferten keinen Halter. Defender und Indexer bleiben unbestätigte Kandidaten. Eine weitere isolierte Serie und eine Gegenprobe mit unverändertem Ersatzpfad blieben jeweils bei 2.000 Vorgängen fehlerfrei. Diese Ergebnisse zeigen die zeitliche Streuung des Fehlers, nicht dessen Abwesenheit.

Im damaligen Fix `4390648` wiederholt `SaveAsync` den Austausch ausschließlich bei den belegten Win32-Codes 32 und 1175. Es verwendet dieselbe verschlüsselte temporäre Datei und wartet zwischen Versuchen abbrechbar 100 Millisekunden. Das Zeitfenster von fünf Sekunden entspricht der vorhandenen Store-Sperre. Nach Ablauf bleibt die ursprüngliche `IOException` ein Fehler. Andere I/O-Fehler werden sofort weitergegeben. Die HTTP-Anfrage und insbesondere eine bereits verbrauchte Tokenrotation werden dadurch nicht wiederholt.

Für 1175 garantiert der [Windows-Vertrag von ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew), dass ersetzte Datei und Ersatzdatei ihre ursprünglichen Namen behalten. Dadurch kann derselbe vorbereitete Austausch erneut versucht werden. Die Codes 1176 und 1177 wurden in `4390648` noch nicht behandelt. Die folgende Diagnose korrigiert dessen Verlustpfade und die beiden Nachfolgeentwürfe auf `integrate/r9`.

Der neue Offline-Check `Atomic snapshot replacement contention` erzwingt einmalige Fehler 32 und 1175 an einer schmalen austauschbaren `File.Replace`-Operation. Anschließend prüft er den gespeicherten Inhalt. Ein weiterer Fall hält einen echten Lesehandle bis zum nachgewiesenen ersten Konflikt und erwartet danach denselben Speichervorgang bis zum Ende. Dauerhafte Fehler, Aufrufer-Abbruch und ein anderer I/O-Code prüfen Fehlerweitergabe, Zeitgrenze, unveränderten alten Snapshot und entfernte temporäre Dateien. Die Standardimplementierung der austauschbaren Operation bleibt `File.Replace`.

Der Regressionstest wurde vor dem Retry-Fix in Commit `8f07e1e` gespeichert. Der Release-Build hatte 0 Warnungen. Der neue Check lieferte erwartungsgemäß Exit 1 mit `Atomic snapshot replacement contention: step=Unknown class=Unknown types=System.IO.IOException`. Die Berichte stehen in `regression-red.log` und `regression-red.json`. Mit Fix besteht derselbe Check einschließlich aller neun Suiten; siehe `regression-green.log` und `regression-green.json`. Alle genannten lokalen Artefakte liegen im ignorierten Ordner `test-results/storage-flake/`.

Die abschließende isolierte Schreibprobe führte mit Fix 2.000 `SaveAsync`-Aufrufe ohne Fehler aus und wartete jeweils auf deren Abschluss. Sie erzwang zusätzlich genau einen echten Leser-Konflikt. `replace-probe.log` protokolliert Operation, HResult, beide Dateinamen relativ zum Fixture und den kontrollierten Halter. Der Halter wurde nach dem ersten nativen Fehler freigegeben; der bereits gestartete Speichervorgang setzte danach erfolgreich fort.

Der Produktfix besteht 30 vollständige Self-Test-Läufe in Folge, jeweils mit Exit 0 und 9/9 Suiten. Die gemessene Fehlerquote beträgt damit 0/30 nach zuvor 3/20. `after.json` enthält alle Läufe; die einzelnen Berichte stehen unter `after/01/` bis `after/30/`. Diese Serie verwendet noch die strengere Assertion auf genau zwei Austauschversuche. Die endgültige Testfassung erlaubt weitere native Konflikte und prüft mindestens einen Wiederholungsversuch sowie den gespeicherten Inhalt. Auch diese Fassung besteht 30 direkte OpenAI-Läufe in Folge, protokolliert in `final-openai.json`. Der Produktcode ist in beiden Serien funktional gleich.

Der abschließende vollständige Aufruf von `pwsh -File scripts/build.ps1` besteht mit Exit 0, 0 Warnungen und 9/9 Suiten, einschließlich WPF-Vorschauen und lokalem Publish. `final-build.log`, `final-build-result.json` und `final-build-self-test.json` enthalten den Nachweis. Die echte Nutzersitzung wurde nicht berührt, es gab keine echten OpenAI-Anfragen und kein Prozess wurde beendet.

## Datenerhalt bei unterbrochener Veröffentlichung (20.10.2026)

Die gemeinsame Prämisse von `8acf29c` und `d031752` lautete, dass `SaveAsync` jeden Zwischenstand vor seiner Rückkehr reparieren kann. Sie ist falsch. Nach einem destruktiven Austauschfehler kann der Hauptname fehlen und ein fremder Handle jeden anschließenden Rename verhindern. Abbruch und Frist erzwingen dann eine Rückkehr mit erhaltenen Temp-Daten. Ein Leser, der nur `openai-session.bin` kennt, meldet trotzdem einen leeren Store. Ein nachfolgendes pauschales Orphan-Cleanup vernichtet anschließend die erhaltene Kopie. Zusätzliche Retry-Codes beseitigen diese Abhängigkeit nicht.

### Vertrag und Gegenproben

Die folgenden Microsoft-Quellen wurden am 20.10.2026 gelesen.

- [ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew), Dokumentstand 01.06.2023. Ohne Backup entfernt 1176 die alte Datei. Bei 1177 bleibt sie unter anderem Namen erhalten. Beide Fehler erhalten die Ersatzdatei. Bei 1175 und sonstigen Fehlern bleiben beide ursprünglichen Namen bestehen. Die Funktion enthält mehrere Dateisystemschritte.
- [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw), Dokumentstand 01.06.2023. Ein Zielverzeichnis verhindert die Veröffentlichung. Quelle und Ziel des Kairos-Protokolls liegen im selben Verzeichnis und auf demselben Volume. Es gibt keinen Kopieren-und-Löschen-Wechsel zwischen Volumes.
- [CreateFileW, dwShareMode](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew), Dokumentstand 09.02.2023. Fehlendes `FILE_SHARE_DELETE` verhindert Lösch- und Rename-Zugriffe. Ein Handle kann dennoch Lesen erlauben. Ohne Lesefreigabe ist auch das Lesen bis zur Freigabe blockiert.
- [.NET-10-Implementierung von File.Replace und File.Move](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Windows.cs). `ReplaceFile` ruft den Windows-Austausch mit dem angegebenen Backup auf. `MoveFile` reicht den Windows-Fehler weiter.

Die isolierte historische Probe kompiliert jeweils die unveränderte `OpenAiStorage.cs` aus `dev`, `8acf29c` und `d031752`. Sie verwendet DPAPI mit synthetischen Zuständen in eigenen Temp-Verzeichnissen. 1176 wird durch Entfernen des eigenen alten Zielnamens nachgebildet. 1177 benennt diesen Teststand um. Der nachfolgende Move und seine Handle-Konflikte laufen nativ unter Windows. Der Halter der ursprünglich sporadischen 1175 bleibt unbestimmt. Die Probe belegt keine Beteiligung von Defender oder Indexer.

Auszüge aus den unveränderten Versionen:

```text
dev code=1176 scenario=failure failure=IOException win32=1176 snapshotExists=False tmpCount=0 load=EMPTY
8acf29c code=1176 scenario=cancel failure=TaskCanceledException win32=5435 snapshotExists=False tmpCount=0 load=EMPTY
d031752 code=1176 scenario=blocked-temp failure=IOException win32=32 snapshotExists=False tmpCount=1 load=EMPTY
```

Bei `d031752` löscht der nächste Save die gültige erhaltene Datei noch vor dem neuen Schreibversuch. Eine weiterhin gemeldete Temp-Anzahl von eins beweist keinen Erhalt. Nach einem bereits abgebrochenen neuen Schreibversuch kann an ihrer Stelle eine leere neue Datei stehen. Der neue Regressionstest prüft deshalb Pfad, verschlüsselte Bytes und geladenen Inhalt vor und nach dem Folgeaufruf.

Der zusätzliche Invariantentest auf `dev` liefert vor dem Fix Exit 1 mit `Recovery invariant: cancellation after 1176 lost the only session snapshot.` Der reguläre Build ohne diesen Test war zuvor mit 0 Warnungen und 9/9 Suiten grün.

Die ergänzte Gegenprobe prüft den Folge-Abbruch auch anhand der verschlüsselten Bytes. `d031752-preservation.txt` meldet `preserved=False tmpLengths=0 load=EMPTY`. Der erzwungen neu kompilierte Fix meldet bei identischem Ablauf `preserved=True tmpLengths=278 load=new`. `fixed-probe.txt` enthält beide Codes 1176 und 1177 sowie Abbruch, Erfolg und blockierten Move. Eine erste Hilfsprobe verwendete wegen des beim Kopieren erhaltenen Quellzeitstempels noch das historische Binary. Diese Ausgabe liegt getrennt in `fixed-probe-stale-artifact.txt` und ist kein Fix-Nachweis. Der gültige Vergleich erzwingt `--no-incremental` und verwendet dieselben Quellbytes wie der Worktree.

### Zustände und Leseergebnis

`S` bezeichnet den kanonischen Hauptstand, `T` den neuen Temp-Stand und `P` einen gültigen Vorgänger im Temp-Verzeichnis. `neu` und `alt` meinen vollständige entschlüsselbare Snapshots. Der Einstieg ohne bisherige Speicherung darf einen neuen leeren Store erzeugen. Die Tabelle beschreibt Prozessende an den Protokollgrenzen einschließlich der von Windows dokumentierten Rename-Zwischenstände. Hardwareverlust und dauerhaft verweigerter Zugriff sind keine erfolgreich lesbaren Dateisystemzustände.

| Zustand oder Schnitt | Dateien | Load auf dev | Load auf integrate/r9 mit d031752 | Load mit Fix unter Sperre |
| --- | --- | --- | --- | --- |
| Vor Erstellung, Teilwrite, Schreib- oder Flushfehler, Abbruch | S alt, T fehlt oder unvollständig | alt | alt | alt |
| Vollständig geschrieben, vor Austausch | S alt, T neu | alt | alt | alt |
| Replace 32, 1175 oder 5, Abbruch oder Frist | S alt, T neu oder entfernt | alt, Cleanup kann Fehler überdecken | alt | alt, ursprünglicher Fehler bleibt |
| Prozessende nach 1176 | S fehlt, T neu | leer | leer | neu |
| Prozessende nach 1177 | S fehlt, T neu, alt unter anderem Namen | leer | leer | neu |
| Abbruch nach 1176 oder 1177 | S fehlt | leer, T wird gelöscht | neu, falls sofortiger Move gelingt | neu aus erhaltenem T, auch wenn Save abbricht |
| Move nach 1176 oder 1177 durch Handle blockiert | S fehlt, T neu | leer | leer | neu aus T, während Rename weiter scheitern darf |
| Folge-Save scheitert oder ist bereits abgebrochen | S fehlt, P gültig | leer | leer, gültiges P wird vorab gelöscht | P oder vollständig geschriebenes neueres T |
| Neuer Teilwrite nach früherem Fehler | S fehlt, P gültig, T nicht leer und unlesbar | leer | leer | ausdrücklicher Fehler mit Reset-Ausweg; kein Rückfall auf P |
| Veröffentlichung erfolgreich, vor oder nach Cleanup | S neu, P möglicherweise vorhanden | neu | neu | neu |
| Älteres T besitzt einen größeren Zeitstempel als S | S neu, T alt | neu | neu | neu, Hauptstand hat Vorrang |
| Kanonischer Stand beschädigt oder nicht lesbar | S vorhanden, P gültig | ausdrücklicher Fehler | ausdrücklicher Fehler | ausdrücklicher Fehler, P bleibt erhalten |
| Verzeichnis am kanonischen Pfad | Ziel ist ein Verzeichnis, P gültig | leer durch File.Exists | leer durch File.Exists | ausdrücklicher Fehler, P bleibt erhalten |
| Neuester Kandidat ohne Lesefreigabe | S fehlt, T gesperrt, P älter | leer | leer | ausdrücklicher I/O-Fehler statt Rückfall auf P |
| Nur unlesbare nicht leere Kandidaten | S fehlt, T unlesbar | leer | leer | ausdrücklicher Fehler mit Reset-Ausweg; Dateien bleiben erhalten |
| Ausschließlich leere Temps | S fehlt, T hat 0 Byte | leer | leer | frischer Zustand, Temps bleiben bis zum erfolgreichen Save |
| Mehrere gültige Legacy-Dateien ohne Ordnungsnummer | S fehlt, mehrere GUID-Temps | leer | leer | ausdrücklicher Fehler, keine erfundene Reihenfolge |
| Erstinstallation vor erster vollständiger Speicherung | weder S noch T | neuer leerer Store | neuer leerer Store | neuer leerer Store |

Bei `8acf29c` löscht `finally` das einzige T auch dann, wenn der Abbruch oder die Frist nach 1176 beziehungsweise 1177 eintritt. Das unterscheidet diesen Stand vom anschließend ebenfalls unzureichenden sofortigen Move in `d031752`.

### Gesperrte Leser und erhaltene Temp-Stände

`Load()` bleibt lesend. Es benennt keine Datei um und kopiert nicht auf den kanonischen Pfad. Ein begonnener Reset wird vor jedem Leseversuch erkannt. Sonst gewinnt der gültige Hauptstand. Nur tatsächliches Fehlen des Hauptpfads eröffnet die Suche nach Temp-Snapshots. Dieselbe DPAPI- und Strukturprüfung gilt für Haupt- und Temp-Dateien. Ausschließlich leere Temps werden übersprungen. Ein nicht lesbarer neuester nicht leerer Kandidat beendet das Laden mit Fehler. Ein I/O- oder Zugriffsfehler wird weitergegeben. Weder dieser noch ein DPAPI- oder Strukturfehler beweist, dass ein älterer Stand gewählt werden darf.

Alle produktiven Leser halten `LockAsync` über den vollständigen Lesevorgang. Die sechs schreibenden Service-Pfade hielten diese Sperre bereits von `LoadState` bis zum Speichern. Das initiale Lesen erfolgt inzwischen in `EnsureLoadedAsync`, ebenfalls unter dieser Sperre. Es gibt keinen verschachtelten Sperrerwerb. Ein einzelner Sperrversuch meldet nach fünf Sekunden `SessionBusy`; das initiale Laden wiederholt ihn asynchron bis zur oben beschriebenen Gesamtfrist.

Ein Leser ohne Sperre könnte zuerst einen fehlenden Hauptnamen sehen und danach nur einen alten Temp-Stand finden. Dazwischen kann ein neuer Stand veröffentlicht und bei einem weiteren Save wieder umbenannt worden sein. Auch ein abschließender Recheck des Hauptnamens würde diese beiden Veröffentlichungen nicht erkennen. Die Gegenprüfung bevorzugt deshalb die bestehende Sperre gegenüber einem zusätzlichen Protokoll für Leser ohne Sperre.

Neue Temp-Namen enthalten `openai-{order:D19}-{guid}.tmp`. Unter der Schreibsperre ist `order` mindestens die aktuelle UTC-Tickzahl und größer als jede noch vorhandene Temp-Ordnung. Legacy-Zeitstempel werden nur als Untergrenze für die Vergabe berücksichtigt. Für gleichzeitig erhaltene neue Kandidaten bleibt die Reihenfolge auch bei gleichen Dateizeitstempeln oder rückwärts laufender Uhr eindeutig. Eine über alle bereits entfernten Dateien global steigende Nummer ist dafür nicht erforderlich.

Eine einzelne gültige GUID-Temp-Datei aus älteren Versionen bleibt wiederherstellbar. Mehrere nicht leere Legacy-Dateien haben keine beweisbare Reihenfolge. Das gilt auch für die Mischung mit geordneten Dateien. Unterschiedliche `LastWriteTimeUtc` lösen dieses Problem nach einer Uhrkorrektur nicht. Der Store erhält die Dateien und meldet `CredentialsUnreadable` mit dem Reset-Ausweg. Die neue Schreibordnung gilt für kooperierende Schreiber mit diesem Protokoll.

`SaveAsync` wiederholt 32, 1175, 1176 und 1177 einschließlich `File.Move` in demselben abbrechbaren Fünf-Sekunden-Fenster. Zwischen Versuchen liegen 100 Millisekunden. Der Datenerhalt hängt davon nicht ab. Ein abgebrochener oder endgültig fehlgeschlagener Move lässt T als lesbaren Stand bestehen.

Cleanup läuft unter derselben Schreibsperre ausschließlich nach vollständigem direkten Lesen und Validieren von S. Ein erfolgreiches Fallback-`Load()` genügt ausdrücklich nicht als Löschfreigabe. Bei fehlendem, beschädigtem, gesperrtem oder als Verzeichnis vorhandenem S bleiben sämtliche Kandidaten erhalten. Ein Cleanup-Fehler beendet die Bereinigung ohne die ursprüngliche Save-Ausnahme zu ersetzen. Ein späterer Save mit gültigem S kann die übrig gebliebenen Temp-Dateien entfernen.

Die bei 1177 umbenannte alte Datei wird für dieses Protokoll nicht benötigt, weil das neuere gültige T erhalten bleibt. Windows nennt ohne expliziten Backup-Pfad keinen vertraglich bekannten Ersatznamen für die Altdatei. Deshalb gibt es keinen ungezielten Such- und Löschlauf. Bereits vom Altcode verlorene T-Daten lassen sich damit nicht nachträglich rekonstruieren. Ist nur eine unbekannt benannte Windows-Altdatei übrig, braucht deren Wiederherstellung einen bekannten Pfad. Das ist kein vom korrigierten Protokoll erzeugbarer Endzustand.

Der Erhaltungsnachweis beruht auf dem gesamten Lade- und Schreibprotokoll. Bis zum vollständig geschriebenen und synchron geflushten T verändert der Schreiber keinen Vorgänger. Während der Veröffentlichung existiert der vollständige Inhalt unter T oder S, einschließlich der dokumentierten destruktiven Replace-Fehler. Cleanup löscht Temp-Daten erst, nachdem S unter durchgehend gehaltener Sperre vollständig lesbar ist. Der gesperrte Leser liest den Hauptstand oder den neuesten eindeutig geordneten nicht leeren Kandidaten. Kann er diesen nicht lesen oder nicht eindeutig bestimmen, liefert er einen Fehler statt eines älteren oder frischen Zustands. Ein Prozessende vor oder nach diesen Schritten beseitigt keine weitere Kopie. Ein ausdrücklicher Reset erhält die Dateien außerhalb der aktiven Kandidatenmenge und sperrt das Lesen sowie Schreiben, bis alle Verschiebungen abgeschlossen sind.

### Verifikationsumfang

`Session snapshot recovery invariant` prüft ausdrücklich Sols Abbruch nach 1176 sowie Opus A1 mit nativ blockierter Temp-Datei beim Move nach 1176. Die Tests prüfen auch 1177, dauerhafte Blockade, Freigabe nach einem Konflikt, Folge-Abbruch und Folge-Save. `Snapshot filesystem failures and crash cuts` injiziert die fünf Fehlercodes und Abbruch an jeder beobachtbaren Schreibgrenze, jeweils mit kanonischem Vorgänger und mit ausschließlich wiederherstellbarem Vorgänger. Replace und Move erhalten zusätzlich einmalige Fehler, Abbruch und Frist. Prozessende wird durch die entsprechenden verbliebenen Dateizustände mit einer neuen Storage-Instanz nachgebildet. Es wird kein Prozess beendet.

`Snapshot recovery ordering and cleanup` prüft Hauptdatei-Vorrang, vertauschte Zeitstempel, monotone Vergabe, Fehler bei unlesbaren neueren Kandidaten, einen lesegesperrten neuesten Kandidaten, Legacy-Mehrdeutigkeit, ein Zielverzeichnis und gesperrtes Cleanup. `Constructor never waits for snapshot recovery on STA` hält die Schreibsperre länger als fünf Sekunden. Der Konstruktor muss auf einem STA-Thread mit Dispatcher-Kontext innerhalb von 100 ms zurückkehren; das anschließend gestartete Laden wartet asynchron und liest erst nach Freigabe. Weitere Checks prüfen paralleles Laden, Abbruch, Frist, erneute Aufrufe, alle sechs Operationen und ausschließlich leere erste Speicherungen. Die Shell-Suite prüft Startfehler, den Reset sowie anschließende Klicks auf Modelle, OCR und Verbinden mit synthetischen Konten und Fake-HTTP. Der vorhandene Contention-Test prüft weiterhin das echte Fünf-Sekunden-Fenster mit einer Abbruchobergrenze als Testabsicherung.

Die lokalen Belege dieses Durchlaufs liegen in `%TEMP%/Kairos-oauth-diagnose-b334746080c5450b856bc9b27fa25618`. `history-*.txt` und `run-history.ps1` enthalten die historischen Proben. `baseline-invariant.json` enthält den roten Test auf dev. `fix-verified-build.log` enthält den grünen vollständigen Build. `independent-review.md` dokumentiert die einzige unabhängige Gegenprüfung durch `codex/gpt-6.1-sol`, `reasoningEffort=xhigh`, `serviceTier=default`. Ihr ungesperrter Erstentwurf wurde verworfen. Gesperrte Leser, kanonische Cleanup-Prüfung und Legacy-Mehrdeutigkeit sind im endgültigen Entwurf berücksichtigt. Eine zusätzliche unabhängige Prüfung des fertigen Diffs wurde nicht durchgeführt.

Die folgenden 18 Mutationen dokumentieren den historischen Stand `e25fce9`. Sie kompilierten und lieferten im betroffenen Storage-Check Exit 1. Nach jeder Wiederherstellung lieferte derselbe Check Exit 0. `run-mutations.ps1`, `mutations.json`, `M*-test.log` und `M*-restored-test.log` enthalten Änderungen und Ausgaben. Diese gezielten Checks laufen durch Reflection gegen die reale gebaute App-Assembly. M08 schrieb damals das Überspringen unlesbarer Kandidaten fest und ist durch die unten dokumentierte W2-Gegenprobe widerlegt. M09 und M18 beschreiben ebenfalls den damaligen Stand vor der asynchronen Initialisierung. Die absichtlich veränderten Gegenproben sind keine erneuten Verlustfälle des unveränderten Fixes.

| Mutation | Entfernte oder verfälschte Schutzentscheidung | Mutation | Wiederhergestellt |
| --- | --- | --- | --- |
| M01 | Temp-Recovery | Exit 1, Verlust nach 1176 | Exit 0 |
| M02 | Erhalten der einzigen Kopie | Exit 1, Verlust nach 1176 | Exit 0 |
| M03 | Vollständige kanonische Prüfung vor Cleanup | Exit 1, gültige Kandidaten gelöscht | Exit 0 |
| M04 | Vorrang des Hauptstands | Exit 1, älterer oder nicht veröffentlichter Stand | Exit 0 |
| M05 | Untergrenze durch vorhandene Ordnungsnummern | Exit 1, neuester Stand übergangen | Exit 0 |
| M06 | Absteigende Auswahl | Exit 1, älterer Stand | Exit 0 |
| M07 | Ordnungsnummer statt Dateizeit | Exit 1, älterer Stand | Exit 0 |
| M08 | Überspringen eines nachweislich ungültigen Kandidaten | Exit 1, gültiger Vorgänger nicht gelesen | Exit 0 |
| M09 | Fehler statt leer bei ausschließlich ungültigen Dateien | Exit 1, ungültiger Store akzeptiert | Exit 0 |
| M10 | Weitergabe eines Lesefehlers beim neuesten Kandidaten | Exit 1, Rückfall auf älteren Stand | Exit 0 |
| M11 | Ablehnen mehrdeutiger Legacy-Dateien | Exit 1, unbekannte Reihenfolge akzeptiert | Exit 0 |
| M12 | Prüfung eines bereits abgebrochenen Aufrufs | Exit 1, neue Datei trotz vorherigem Abbruch | Exit 0 |
| M13 | Abbruch vor Veröffentlichung | Exit 1, Abbruch vor Veröffentlichung ignoriert | Exit 0 |
| M14 | Retry nach 1176 und 1177 | Exit 1, transienter Austausch nicht fortgesetzt | Exit 0 |
| M15 | Move innerhalb desselben Retry-Blocks | Exit 1, blockierter Move nicht fortgesetzt | Exit 0 |
| M16 | Fristgrenze | Exit 1, Versuch nach abgelaufener Frist | Exit 0 |
| M17 | Ursprüngliche Ausnahme trotz Cleanup-Fehler | Exit 1, Fehlerkategorie überdeckt | Exit 0 |
| M18 | Sperre im Konstruktor | Exit 1, ungesperrter Leser | Exit 0 |

Der abschließende Aufruf `pwsh -File scripts/build.ps1 -BuildOnly` liefert Exit 0, 0 Warnungen und 9/9 bestandene Suiten einschließlich der Vorschauen. `final-build.log` und `final-build-self-test.json` enthalten den Nachweis. Anschließend bestehen 20 vollständige `--self-test`-Läufe in Folge mit jeweils Exit 0 und 9/9, insgesamt 180 bestandene Suiten. `run-repeats.ps1`, `repeats.json` und `repeat/01/self-test.json` bis `repeat/20/self-test.json` enthalten Ablauf und Einzelberichte. Die Berichtzeitpunkte liegen am 20.10.2026 zwischen 13:55:31 und 14:04:58 Uhr in Europe/Berlin.

Jeder dieser 20 Läufe prüft denselben DLL-Hash `1BCAF5AEEA887467291E738A1E460892B2921B15A37392538B9D237316F3949A`. Es gab keinen erneuten Datenverlust des unveränderten Fixes. Die echte Sitzung und das echte Log wurden ausschließlich gehasht. Ihre SHA-256-Werte sind vor und nach der Verifikation identisch. Sitzung `7C5E17EFC6B8E2C326A1BC8EC35E74082EAA5B22FBFFB2CF71DBEC7005499BC4`. Log `BDE9BA8D6D43BE3A2122F528EF15B6D6CCA92A413F7C2295386F99CFD7729764`. Es gab keine echten OpenAI-Anfragen, keinen Publish und keinen beendeten Prozess. Eigene Läufe setzten `KAIROS_DIAGNOSTIC_LOG_DIRECTORY` auf den privaten Temp-Ordner. Das Build-Skript verwendete seinen vorgesehenen Diagnosepfad unter `test-results`.

## Diagnose-Stufe 3: Lesbarkeit ist kein Altersnachweis (20.10.2026)

### Belegte Ursachenkette

Die Prüfung beginnt bei `8961a67` auf `integrate/r10`. `e25fce9` erhält Temp-Snapshots nach destruktiven Veröffentlichungsfehlern. Sein Leser überspringt jedoch DPAPI- und Strukturfehler. `b4604d0` erlaubt darüber hinaus einen frischen Zustand ohne lesbaren Kandidaten. `8961a67` korrigiert diesen zweiten Fall für nicht leere Dateien. Das Überspringen eines neueren unlesbaren Kandidaten bei vorhandenem lesbarem Vorgänger bleibt bestehen.

Die gemeinsame falsche Prämisse lautet: „Wenn die Entschlüsselung eines Kandidaten fehlschlägt, darf dieser Kandidat bei der Auswahl verworfen werden.“ Der Leser kann eine dauerhaft beschädigte Datei und eine vollständige Datei mit vorübergehend gescheiterter Entschlüsselung anhand dieses Ergebnisses nicht unterscheiden. Die neue Gegenprobe erhält die verschlüsselten Bytes unverändert und injiziert genau einen `CryptographicException`-Fehler vor dem echten DPAPI-Aufruf. Der ältere Snapshot enthält ein Konto, der neuere zwei. Auf `8961a67` wird das Laden trotz des Fehlers erfolgreich und veröffentlicht den älteren Kontensatz im Service. Ein folgender Service-Save kann deshalb einen veralteten Snapshot veröffentlichen; das bestehende Cleanup darf danach die neuere Temp löschen. Die erste falsche Entscheidung liegt im erfolgreichen Rückfall beim Lesen.

Die zweite Ursache betrifft die Erreichbarkeit einer Reparatur. Alle bisherigen Kontoaktionen einschließlich Verbinden führen zuerst `EnsureLoadedAsync` aus. Eine dauerhaft unlesbare Hauptdatei oder einzige nicht leere Temp verhindert damit auch eine neue Anmeldung. Die Meldung verweist auf Aktionen, die denselben Fehler erneut auslösen. Der Offline-Test verwendet eine einzelne 1-Byte-Temp und prüft die tatsächliche WPF-Aktion.

Der erste vollständige Repro-Lauf mit unveränderter Auswahlentscheidung meldet am 20.10.2026 um 16:11 Uhr in Europe/Berlin zwei fehlgeschlagene Suiten. Die übrigen sieben Suiten bestehen. Originalausgaben aus `repro-self-test.json`:

```text
Unreadable newest snapshot never rolls back: W2: a transient DPAPI failure on the newest snapshot loaded an older account set.
W1: unreadable credentials provide no usable reset action and backup location.
```

### Zustandsmodell und Entscheidung

Der Hauptname bezeichnet den veröffentlichten Stand. Eine Temp neben einem lesbaren Hauptstand ist ein noch nicht veröffentlichter oder abgebrochener Schreibversuch. Deshalb bleibt der bestehende Vorrang des Hauptstands erhalten. Bei fehlendem Hauptnamen bilden die nicht leeren Temps die Recovery-Kandidaten. Jeder Kandidat hat eine bekannte Ordnungsnummer oder eine unbekannte Legacy-Ordnung. Seine momentane Lesbarkeit ändert diese Ordnung nicht. Ein begonnener ausdrücklicher Reset ist ein eigener Zustand vor dieser Auswahl.

| Zustand unter der Store-Sperre | Vorher auf `8961a67` | Nachher |
| --- | --- | --- |
| Hauptdatei lesbar, Temps beliebig | Hauptstand | Hauptstand |
| Hauptdatei vorhanden, DPAPI oder Struktur nicht lesbar | Fehler ohne Reparaturaktion | `CredentialsUnreadable`, Retry oder ausdrücklicher Reset |
| Hauptdatei oder neueste Temp durch Handle gesperrt | I/O-Fehler | I/O-Fehler; kein Rückfall, nach Freigabe erneut laden |
| Hauptdatei fehlt, keine Temps oder alle Temps 0 Byte | Frischer Zustand | Frischer Zustand |
| Hauptdatei fehlt, einziger nicht leerer Kandidat lesbar | Kandidat | Kandidat |
| Einziger nicht leerer Kandidat vorübergehend unlesbar | Fehler | Fehler, Bytes bleiben; Retry lädt nach Wegfall der Störung |
| Einziger nicht leerer Kandidat dauerhaft unlesbar | Fehler ohne Ausweg | Fehler mit Reset-Aktion und Sicherungsort |
| Mehrere geordnete Kandidaten, neuester lesbar | Neuester lesbarer Stand | Neuester Stand; ältere unlesbare Kandidaten spielen keine Rolle |
| Neuerer nicht leerer Kandidat unlesbar, älterer lesbar | Älterer Stand, danach möglicher Verlust des neueren | Fehler; kein Service-Zustand veröffentlicht, neuere Datei bleibt |
| Neuere Temp 0 Byte, älterer Kandidat lesbar | Älterer Stand | Älterer Stand; 0 Byte enthält keinen Snapshot |
| Zwei nicht leere Kandidaten mit gleicher höchster Ordnung | Fehler nur bei zwei lesbaren Kandidaten | Fehler unabhängig von der momentanen Lesbarkeit |
| Eine nicht leere Legacy-Temp | Lesen oder Fehler | Lesen oder Fehler mit Reset-Ausweg |
| Mehrere nicht leere Legacy-Temps | Fehler bei mehreren lesbaren; sonst möglicher Rückfall | Fehler wegen unbekannter Reihenfolge |
| Legacy- und geordnete nicht leere Temps gemischt | Geordneter lesbarer Kandidat verdrängt Legacy | Fehler; keine belegte gemeinsame Reihenfolge |
| Reset begonnen, nur einige Dateien verschoben | Kein solcher Zustand modelliert | `Load` und `Save` blockieren; Reset fortsetzen |
| Reset vollständig, Sicherungsordner fertig benannt | Keine Aktion vorhanden | Frischer Service; Sicherung bleibt bei späteren Saves erhalten |
| Ladeversuch abgebrochen | Dauerhaft „wird geladen…“ möglich | „nicht geladen · erneut versuchen“, OCE wird weitergegeben |

Der Lead-Entwurf ist damit für die Auswahl belegt. Er wird bei unbekannter Reihenfolge konservativ erweitert. Der Reset benötigt zusätzlich `session-reset.pending`, weil mehrere einzelne Renames keine gemeinsame atomare Operation bilden. Ein bloßes Verschieben ohne diesen Marker könnte nach einer Unterbrechung einen alten Rest wieder als aktuelle Sitzung laden. Eine vollständige Umstellung auf generationsbasierte Speicherverzeichnisse würde dagegen auch das bestehende Veröffentlichungsprotokoll ändern. Der Marker begrenzt die Änderung auf den ausdrücklich gewünschten Reset.

Der alte Check an `OpenAiSelfChecks.cs:492-493` prüfte, dass eine neuere unlesbare Temp übersprungen wird. Er wurde durch Fehlererwartungen für nicht leere DPAPI- und Strukturfehler ersetzt. Ein leerer neuerer Kandidat bleibt ausdrücklich überspringbar. Der Testname für den ersten Speicherversuch lautet jetzt `Only empty first-save temps permit a fresh session`.

### Tests und Mutationen

`TransientRecoveryCheck` prüft W2 mit zwei synthetischen Konten und genau einem DPAPI-Fehler. Nach dem Fehler bleiben beide Dateien erhalten, die neueren verschlüsselten Bytes sind unverändert und `IsLoaded` bleibt `false`. Der nächste Ladeversuch liefert beide Konten. Weitere Fälle prüfen leere neuere Temps, unlesbare ältere Temps, gleiche Ordnungen sowie Legacy-Mischungen.

`StorageResetChecks` unterbricht den Reset jeweils vor und nach jeder der drei Verschiebungen. Eine neue Storage-Instanz darf anschließend weder laden noch speichern. Für jede Quelldatei existiert genau eine bytegleiche Kopie im aktiven Verzeichnis oder in `session-reset.pending`. Nach Wiederholung liegen alle Dateien in der angekündigten Sicherung. Ein frischer Save verändert die Sicherungsbytes nicht. `ServiceResetChecks` prüft sowohl die beschädigte Hauptdatei als auch die einzige beschädigte Temp, gehaltene Store-Sperre, Abbruch und das anschließende Erreichen des synthetischen Browsers. `LoadedSessionResetCheck` prüft die Invalidierung geladener Konten und einer laufenden Fake-HTTP-Anfrage auch bei einem fehlgeschlagenen Reset.

Die Shell-Suite betätigt den echten WPF-Reset-Button und danach Verbinden. Sie prüft die Meldung, den Sicherungspfad, unveränderte Bytes und den frischen Service. Der Abbruchtest prüft jetzt ausdrücklich, dass eine OCE beim Aufrufer ankommt, sowie den Status in Einstellungen und Tray. Ein leerer `catch (OperationCanceledException)` kann den Check nicht mehr grün machen.

Alle folgenden 20 isolierten Mutationen kompilierten. Der jeweilige Check wurde rot mit Exit 1. Nach Wiederherstellung und erneutem Build wurde derselbe Check grün mit Exit 0.

| Mutation | Entfernte oder verfälschte Entscheidung | Rot / wiederhergestellt |
| --- | --- | --- |
| M01 | Rückfall auf älteren Kandidaten nach Entschlüsselungsfehler erlaubt | 1 / 0 |
| M02 | Leere Temps nicht aus der Kandidatenmenge ausgeschlossen | 1 / 0 |
| M03 | Unbekannte Legacy-Reihenfolge akzeptiert | 1 / 0 |
| M04 | Gleiche höchste Ordnungen akzeptiert | 1 / 0 |
| M05 | `Load` ignoriert begonnenen Reset | 1 / 0 |
| M06 | UI-Ladegrenze verschluckt OCE | 1 / 0 |
| M07 | `SaveAsync` ignoriert begonnenen Reset | 1 / 0 |
| M08 | Reset lässt Hauptdatei aus | 1 / 0 |
| M09 | Reset lässt Temps aus | 1 / 0 |
| M10 | Reset kopiert statt zu verschieben | 1 / 0 |
| M11 | Reset schließt Sicherungsverzeichnis nicht ab | 1 / 0 |
| M12 | Reset umgeht Store-Sperre | 1 / 0 |
| M13 | Reset erhält den alten Zustand im Service-Cache | 1 / 0 |
| M14 | Reset lässt die alte Kontoanfrage laufen | 1 / 0 |
| M15 | Reset-Aktion läuft wieder durch `EnsureLoadedAsync` | 1 / 0 |
| M16 | Reset-Button ohne Click-Handler | 1 / 0 |
| M17 | Fehlermeldung nennt die Reset-Aktion nicht | 1 / 0 |
| M18 | Tray behauptet nach Abbruch weiterhin „wird geladen“ | 1 / 0 |
| M19 | Abgeschlossene UI-Ladeversuche bleiben als laufend gezählt | 1 / 0 |
| M20 | Reset ignoriert ein bereits abgebrochenes Token | 1 / 0 |

Die Belege liegen in `%TEMP%\Kairos-oauth-astra-ec90e9d2e97645a3a41ae22ee80aff2f`. `repro-self-test.json` enthält die roten W1- und W2-Proben. `fix-build.log` und `fix-self-test.json` enthalten den ersten grünen vollständigen Lauf. `targeted-final-green.log` enthält die gezielten Prüfungen. `run-mutations.ps1`, `mutations.json` und `M*-test.log` dokumentieren jede Mutation und ihre Gegenprobe. Der korrigierte Hilfsrunner verwendet eine neutrale WPF-Application ohne Produktstart. Der erste Hilfsrunner mit versehentlichem `App.OnStartup` ist kein UI-Nachweis; seine Ausgabe bleibt getrennt in `targeted-green.log`. Die reguläre App lief für die Verifikation ausschließlich im Diagnosemodus.

Die Diagnose und Umsetzung erfolgten in diesem Worker ohne Delegation. Das Prinzip „Attack the Premise“ führte zum Test eines einmaligen Entschlüsselungsfehlers bei unveränderten Bytes. „Model the Domain“ führte zur Trennung zwischen Reihenfolge, Lesbarkeit und begonnenem Reset. Eine unabhängige Review-Runde wurde gemäß Auftrag nicht ausgeführt. Die WPF-Fehleransicht wurde zusätzlich außerhalb des Bildschirms gerendert und visuell geprüft; `reset-settings.png` enthält die sichtbare Aktion und den Sicherungshinweis.

Aus `targeted-final-green.log` nach dem Fix:

```text
PASS TransientRecoveryCheck
PASS StorageResetChecks
PASS ServiceResetChecks
PASS LoadedSessionResetCheck
PASS CheckOpenAiReset
PASS CheckOpenAiStartupRetry
```

### Abschluss dieses Durchlaufs

15 vollständige Aufrufe von `pwsh -File scripts/build.ps1 -BuildOnly` bestehen hintereinander mit Exit 0, 0 Warnungen und jeweils 9/9 Suiten. Jeder Aufruf führt auch die WPF-Vorschauen aus. Insgesamt sind 135 Suiten bestanden. Die Serie läuft am 20.10.2026 von 16:25:42 bis 16:35:57 Uhr in Europe/Berlin. Alle Läufe verwenden denselben DLL-SHA-256 `13A8EF8DCA8E7EA0C5C7B5207C5C52FA4813DB5A9898DBD6BB941DF5924CAE33`. `run-repeats.ps1`, `repeats.json` und `repeat/01` bis `repeat/15` enthalten den Ablauf, sämtliche Build-Ausgaben und die einzelnen `self-test.json`-Berichte. `final-source-hashes.json` bindet die geprüften Quellbytes an den finalen Stand; `verification-summary.json` fasst die Belege zusammen. Kein Invariantentest des wiederhergestellten Fixes wurde rot. Absichtlich entfernte Schutzmaßnahmen in den Mutationen zählen nicht als erneuter Verlustfall des Fixes.

Das echte Anwendungslog hat vorher und nachher denselben SHA-256 `BDE9BA8D6D43BE3A2122F528EF15B6D6CCA92A413F7C2295386F99CFD7729764` und bleibt 228939 Byte groß. Die echte Sitzungsdatei wurde in diesem Durchlauf nicht geöffnet. Eigene Aufrufe setzen `KAIROS_DIAGNOSTIC_LOG_DIRECTORY` auf den privaten Temp-Ordner. Weil `build.ps1` diese Variable selbst setzt, zeigt dessen Diagnoseverzeichnis in diesem Worktree per eigener Junction ebenfalls auf den privaten Temp-Ordner. Es gab keine echten OpenAI-Anfragen, keinen Publish, Push oder PR und keine beendeten Nutzerprozesse. Alle Testdaten liegen in eigenen temporären Fixtures. Capture und Editor wurden nicht geändert.
