# OpenAI OAuth für eine spätere Kairos-OCR

Prüfdatum: 18.10.2026. Dokumentationsrecherche durch GPT-6.1-Sol (low, priority), zentrale Aussagen durch Root an den Originalseiten gegengeprüft. **Dies ist ein Implementierungsvertrag, kein implementierter oder mit einem Konto getesteter Login.** Die Untersuchung der SunnyCapturer-Binärdatei ist davon unabhängig.

## Registrierung und Login

Für Open-Source- und lokale Apps beschreibt OpenAI eine dynamische Registrierung. Konten und Workspaces können dennoch unterschiedliche Freigaben haben; Registrierung allein garantiert keine Inferenz. Kommerzielle oder remote gehostete Anwendungen müssen den jeweils dafür beschriebenen Zugangsweg prüfen.

Quelle: [Übersicht](https://developers.openai.com/siwc/token-sharing-open-source).

Die Desktop-App benötigt eine eigene, stabile `ext_agent_host_id`. Die erste Browseranmeldung verwendet `client_id=dynamic_agent_client` und den tatsächlichen App-Namen als `agent_name_hint`. Der Callback liefert die ausgestellte Client-ID; nur diese gehört in den Codeaustausch und die gespeicherte Kontozuordnung.

Vor dem Browserstart den Loopback-Listener starten. Pro Versuch neue `state`, `nonce` und PKCE-Werte mit S256 erzeugen. Autorisierung: `https://auth.openai.com/api/accounts/authorize`; Tokenaustausch: `https://auth.openai.com/api/accounts/oauth/token`. Der Redirect verwendet `127.0.0.1`, beispielsweise `/auth/callback`; bei späteren Anmeldungen darf nur der Port variieren. Innerhalb eines Versuchs muss die URI exakt gleich bleiben.

Scopes: `openid profile email offline_access resource.invoke chatgpt.tokens.use.direct`; Resource: `https://api.openai.com/v1`. Callback und OAuth-Fehler an `state` binden. ID-Token über veröffentlichte Schlüssel prüfen, einschließlich Signatur, Issuer, ausgestellter Client-ID als Audience, Ablauf und Nonce. Kontowechsel erst nach validierter Identität übernehmen. Der gewährte Direct-Scope ist Voraussetzung für OCR über den ChatGPT-Plan. Es gibt kein Client-Secret.

Quelle: [Registrierung und Anmeldung](https://developers.openai.com/siwc/token-sharing-open-source/sign-in).

## Sitzung

Geschützte lokale Kontodatensätze atomar speichern. Für Windows ist DPAPI mit Nutzerbindung eine Kairos-Designentscheidung; OpenAI schreibt nicht speziell DPAPI vor. Keine fremden App-Sitzungen importieren.

Refreshes einer Sitzung serialisieren. Das rotierte Refresh-Token zusammen mit Access-Token, Scopes und Ablauf ersetzen. Beim Refresh die gespeicherte ausgestellte Client-ID und die Resource verwenden; `scope` weglassen. Logout stoppt laufende Requests, widerruft das Refresh-Token am durch Discovery ermittelten Widerrufsendpoint und entfernt die lokalen Token. Host-ID und Registrierung können bestehen bleiben. Ein fehlgeschlagener Widerruf darf nicht als erfolgreicher Server-Logout gemeldet werden.

Quellen: [Accounts und Sessions](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions), [Tokenreferenz](https://developers.openai.com/siwc/token-sharing-open-source/token-reference).

## Modelle und Bild-OCR

Mit dem neuen OAuth-Access-Token den kontospezifischen Katalog über `GET /v1/models` laden. Der dokumentierte Vertrag verwendet `models[]`, `visibility`, `display_name` und `slug`. Angezeigte Einträge filtern, Serverreihenfolge erhalten; nach Kontowechsel neu laden. Keine fest codierte Modell-ID als Zugangsbeweis verwenden.

Inferenz erfolgt am öffentlichen `POST https://api.openai.com/v1/responses` mit Bearer-Token, `store:false` und `stream:true`. Erfolg erst bei `response.completed`; fehlgeschlagene, unvollständige und abgebrochene Streams unterscheiden.

Quelle: [Modelle und Inferenz](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference).

Bilder sind bei passenden Modellen unterstützt. Die Files-Upload-API ist in diesem OAuth-Weg ausgeschlossen; eine PNG-Data-URL in `input_image` vermeidet diesen Uploadweg. Kontext als `input`-Array senden. Für reine OCR keine Tools und keine nicht unterstützten Felder wie `max_output_tokens` oder `temperature` mitsenden. Die App sollte erst auf ausdrücklichen OCR-Aufruf das bereits exportierte, endgültig geschwärzte Bild senden. Dies ist eine Kairos-Produktentscheidung.

Quellen: [Preview-Grenzen](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations), [Bildeingaben](https://developers.openai.com/api/docs/guides/images-vision).

## Offene Abnahme

Noch nicht geprüft: Konto-/Workspace-Zulassung, real verfügbarer bildfähiger Modellkatalog, echter Login, Refresh, Widerruf, OCR-Qualität und Latenz. Diese Punkte benötigen die spätere Anwendung und eine eigene Anmeldung. Ein fehlender Direct-Scope, eine Kontosperre oder ein Nutzungslimit sind externe Zustände und dürfen keine unbegrenzte Anmeldeschleife auslösen. Kein stiller Wechsel auf kostenpflichtige API-Key-Abrechnung.

Quelle: [Fehler und Wiederherstellung](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery).
