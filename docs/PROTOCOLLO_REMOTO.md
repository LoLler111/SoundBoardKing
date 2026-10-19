# Protocollo PC ↔ telefono (v1)

Il server gira dentro `SoundBoardKing.exe` e ascolta sulla rete locale:

| Cosa | Dove |
|---|---|
| HTTP + WebSocket | TCP **8765** |
| Scoperta | UDP **8765** |

Il telefono è solo un telecomando: i file audio restano sul PC.

## Scoperta
Il telefono invia in broadcast UDP (`255.255.255.255:8765`) il testo `SBK_DISCOVER_V1`.
Ogni PC con SoundBoardKing risponde, direttamente al mittente:
```json
{ "service": "SoundBoardKing", "protocol": 1, "name": "PC-SALOTTO", "port": 8765, "serverId": "…" }
```

## Abbinamento
1. Sul PC: pulsante **Telefono** → mostra un QR e un codice di 6 cifre, validi finché la finestra è aperta (massimo 10 minuti).
2. Il QR contiene `sbk://pair?h=<ip>[,<ip>…]&p=<porta>&c=<codice>&n=<nome PC>`.
3. Il telefono chiama `POST /api/pair` con `{ "code": "123456", "deviceName": "Pixel 8" }` e riceve `{ "token": "…", "deviceId": "…" }`.
   Dopo 5 codici sbagliati il codice viene invalidato e va riaperta la finestra.
4. Il token va salvato sul telefono e mandato a ogni richiesta (`Authorization: Bearer <token>`, oppure `?token=` per il WebSocket).
   Sul PC viene conservato solo il suo hash SHA-256. Un dispositivo si revoca dalla finestra Telefono.

## REST
| Metodo | Percorso | Auth | Risposta |
|---|---|---|---|
| GET | `/api/info` | no | `{ name, protocol, serverId }` |
| POST | `/api/pair` | codice | `{ token, deviceId }` |
| GET | `/api/state` | sì | stato completo, vedi sotto |
| POST | `/api/play/{soundId}?target=…` | sì | 204; 404 se il suono non esiste; 409 `{ error }` se non è partito (file mancante, nessun dispositivo audio, microfono non disponibile); 400 se `target` non è valido |
| POST | `/api/stop` | sì | 204 |
| POST | `/api/stop/{playbackId}` | sì | 204; 404 se quel suono è già finito |
| POST | `/api/pause` | sì | 204 (mette in pausa o riprende tutti i suoni) |
| POST | `/api/pause/{playbackId}` | sì | 204 (mette in pausa o riprende solo quel suono); 404 se è già finito |
| POST | `/api/effect/{nome}` | sì | 204 (cambia l'effetto sulla voce dei suoni); 400 se il nome non esiste |
| POST | `/api/effect-amount/{0-100}` | sì | 204 (intensità dell'effetto, vale subito anche per i suoni in corso); 400 fuori da 0-100 |

Errori: 401 se il token manca o non è valido.

### `GET /api/state`
```json
{
  "serverId": "…", "name": "PC-SALOTTO", "protocol": 1, "libraryVersion": 7,
  "sounds": [ { "id": "guid", "title": "airhorn", "artist": "", "durationMs": 3000, "missing": false, "hotkey": "Ctrl+1" } ],
  "categories": [ { "id": "guid", "name": "Voice", "soundIds": ["guid"], "children": [ … ] } ],
  "hotbar": { "pages": 5, "slotsPerPage": 100, "slots": [ { "page": 0, "slot": 0, "soundId": "guid" } ] },
  "playback": { "state": "playing", "soundId": "guid", "positionMs": 1200, "durationMs": 3000,
                "playing": [ { "id": "guid", "soundId": "guid", "positionMs": 1200, "durationMs": 3000, "paused": false } ] },
  "mic": { "state": "active", "enabled": true },
  "effect": "none", "effectAmount": 70, "playbackSpeed": 1.0
}
```
- `playback.state`: `stopped` | `playing` | `paused`.
- `playback.playing`: tutti i suoni in riproduzione insieme, dal più vecchio; `id` identifica quella riproduzione
  (serve a `stopOne` e `togglePauseOne`); `paused` dice se quel suono è fermo in pausa. `soundId` e i tempi fuori dalla lista descrivono l'ultimo suono partito, per i telefoni
  con una versione precedente.
  `state` è `paused` solo quando tutti i suoni sono in pausa.
- `mic.state`: `notInstalled` | `idle` | `active` | `incompatible` | `accessDenied`.
- `effect`: effetto sulla voce dei suoni che partono da ora: `none` | `chipmunk` | `deep` | `robot` | `radio` | `echo`.
  I PC senza effetti non lo mandano: va inteso come `none`.
- `effectAmount`: intensità dell'effetto da 0 a 100 (70 se manca); cambia al volo anche i suoni già partiti.
- `playbackSpeed`: velocità di tutti i suoni, come un disco (più veloce = più acuto), da 0.5 a 2; cambia al volo anche quelli in corso. Non viene salvata: a ogni avvio torna 1.

## WebSocket `/ws?token=…`
Messaggi JSON con un campo `type`.

Dal telefono al PC:
```json
{ "type": "play", "soundId": "guid" }
{ "type": "play", "soundId": "guid", "target": "speakers" }
{ "type": "stop" }
{ "type": "stopOne", "playbackId": "guid" }
{ "type": "togglePause" }
{ "type": "togglePauseOne", "playbackId": "guid" }
{ "type": "setEffect", "effect": "robot" }
{ "type": "setEffect", "amount": 40 }
{ "type": "setEffect", "amount": 40, "speed": 1.5 }
{ "type": "setMic", "enabled": false }
```

`target` (facoltativo) sceglie dove va il suono: assente = come al solito (cuffie, più microfono se acceso),
`speakers` = anteprima solo in cuffia, `mic` = solo nel microfono anche se è spento.

Dal PC al telefono:
```json
{ "type": "hello", "libraryVersion": 7 }
{ "type": "playback", "state": "playing", "soundId": "guid", "positionMs": 0, "durationMs": 3000, "playing": [ … ] }
{ "type": "libraryChanged", "libraryVersion": 8 }
{ "type": "mic", "state": "idle", "enabled": true }
{ "type": "effect", "effect": "robot", "amount": 40, "speed": 1.0 }
{ "type": "error", "message": "…" }
```
Se un `play` non va a buon fine (suono inesistente, file mancante, nessun dispositivo audio),
arriva un `error` con il motivo, in italiano e pronto da mostrare all'utente. La connessione resta aperta.

Quando arriva `libraryChanged`, il telefono ricarica `/api/state`. La posizione di riproduzione
non viene inviata di continuo: il telefono la calcola da `positionMs` e dall'ora di ricezione.

## Limiti noti
- Il traffico è HTTP in chiaro sulla rete locale: va bene in casa, non su reti pubbliche.
- Al primo avvio Windows chiede di consentire l'app nel firewall: scegli **reti private**.
