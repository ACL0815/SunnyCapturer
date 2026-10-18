# Kairos Capture: OpenAI OAuth und OCR

Stand: 18.10.2026. Grundlage sind der lokale [Implementierungsvertrag](reverse/OPENAI-OAUTH-VERTRAG.md), die vollständig geprüften eigenen Service-Entwürfe und die unten verlinkten offiziellen OpenAI-Seiten. Es wurde kein SunnyCapturer-Quellcode übernommen. Echter Login, reale Inferenz, Refresh und Widerruf mit einem Nutzerkonto sind **nicht abgenommen**.

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

Der interne `OpenAiUserException` ist der zusätzliche UI-Vertrag für bekannte lokale Zustände. Sein Konstruktor akzeptiert ausschließlich `OpenAiUserError`, keine freie Meldung. Die UI darf dessen feste App-Meldungen sowie `OpenAiRequestException.Message` anzeigen. Unbekannte Library-, IO- und Protokollausnahmen erhalten weiterhin eine allgemeine Meldung. Öffentliche Account-, Model- und Service-Signaturen bleiben erhalten.

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

Offen bleiben reale Konto-/Workspace-Zulassung, der verfügbare bildfähige Katalog, Nutzerlogin, reale Tokenrotation/Widerruf, OCR-Qualität und Latenz. Die Oberfläche muss bei Kontoänderung ihre Modellliste neu laden und alte Deltas verwerfen. Die Prüfung, dass die Produkt-UI ausschließlich den endgültig geschwärzten Export sendet und Diagnose-/Demo-Modi keine Produktkonten lesen, gehört zur Shell-/Editor-Integration.
