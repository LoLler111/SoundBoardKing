# SoundBoardKing — Analisi

Soundboard per Windows ispirata a SoundPad, con telecomando Android.
Stack: **C# / .NET 8 + WPF + NAudio** (Windows), **C++** (modulo microfono APO), **Kotlin + Jetpack Compose** (Android).

---

## 1. Cosa fa SoundPad (rilevato dalla cartella `C:\Sviluppo\SoundPad_Licensed`)

| File | Ruolo |
|---|---|
| `Soundpad.exe` | App principale (UI, riproduzione, decodifica via Media Foundation, tasti rapidi, API remota) |
| `UniteFx.dll` / `UniteFx-ARM.dll` | **APO** (Audio Processing Object): implementa `IAudioProcessingObject`, `IAudioProcessingObjectRT`, `IAudioSystemEffects`. Caricata da `audiodg.exe` nella catena del microfono |
| `SoundpadService.exe` | Processo con privilegi elevati: registrazione dell'APO, hook tastiera (`SetWindowsHookExW`) per funzionare anche con giochi avviati come amministratore *(dedotto, non verificato)* |
| `TTS.dll` | Sintesi vocale |
| `languages\*.mo` | 36 traduzioni (gettext) |

### Come mette il suono nel microfono
Non usa cavi virtuali e non inietta codice in Discord o nei giochi. Registra la sua APO sul **dispositivo microfono**: il motore audio di Windows (`audiodg.exe`) la esegue su ogni blocco di campioni catturati, e l'APO ci somma l'audio che `Soundpad.exe` le passa tramite **memoria condivisa** (`UniteFxMappingObject1.8.0`, `UniteFxTransferMutex1.8.0`, `UniteFxCallback1.8.0`).
Effetto: l'utente seleziona sempre il **microfono reale** in Discord, OBS, giochi, e il suono c'è già dentro.

### Formato lista suoni `.spl` (XML)
```xml
<Soundlist>
  <Sound hash="..." url="Suoni\airhorn.mp3" artist="" title="airhorn" duration="0:03" addedOn="2021-04-24"/>
  ...
  <Categories>
    <Category type="1" icon="stock_icon_cat_list" hidden="true"/>   <!-- "Tutti i suoni" di sistema -->
    <Category name="Voice" icon="gen_V">
      <Category name="AldoGioGia" icon="gen_A"> <Sound id="1"/> ... </Category>
    </Category>
  </Categories>
  <Hotbar>
    <HotbarPage>
      <HotbarItem id="0" soundId="78"/>      <!-- id = pagina*1000 + slot -->
      <HotbarItem id="4019" soundId="675"/>
    </HotbarPage>
  </Hotbar>
</Soundlist>
```
- `Sound` di primo livello: la libreria. L'**id** di un suono è la sua **posizione (base 1)** nell'elenco.
- `Category` annidabili; contengono riferimenti `<Sound id="N"/>`.
- `url` relativo alla cartella del file `.spl` (in `Suoni\Suoni.spl` invece i percorsi sono assoluti, `F:\Altro\Soundpad\...`, non più validi).
- **Hotbar = i "preferiti"**: 5 pagine × 20 slot (id `0–19`, `1000–1019`, … `4000–4019`).
- Lista attuale: 985 suoni (~1,1 GB; mp3, m4a, mp4, aac, wav, opus, 3gp, mpeg), 24 categorie, 100 slot hotbar tutti occupati.

### API remota di SoundPad
Named pipe `\\.\pipe\sp_remote_control`, comandi testuali: `DoPlaySound(index[,speakers,mic])`, `DoPlaySoundFromCategory(cat,idx[,speakers,mic])`, `DoStopSound()`, `DoTogglePause()`, `DoPlayCurrentSoundAgain()`, `GetSoundlist()`, `GetCategories()`, `GetPlayStatus()`, `IsAlive()`.
Utile come riferimento: i flag `speakers`/`mic` permettono di suonare solo in cuffia, solo nel microfono o entrambi.

---

## 2. Architettura SoundBoardKing

```
┌──────────────────────── PC Windows ────────────────────────┐
│ SoundBoardKing.exe (WPF, .NET 8)                           │
│  ├─ Core: libreria, categorie, hotbar, import .spl, JSON   │
│  ├─ AudioEngine (NAudio)                                   │
│  │    ├─ decodifica → float 32                             │
│  │    ├─ uscita CASSE/CUFFIE (WASAPI)                      │
│  │    └─ uscita MICROFONO ──► IMicFeed ──┐                 │
│  ├─ Hotkeys globali                      │                 │
│  └─ RemoteServer (HTTP + WebSocket) ◄────┼──── Android     │
│                                          │ memoria condivisa│
│ audiodg.exe                              ▼                 │
│  └─ SbkMicFx.dll (APO C++) : mic reale + suono → app VoIP  │
└────────────────────────────────────────────────────────────┘
```

### Progetti della soluzione
| Progetto | Linguaggio | Contenuto |
|---|---|---|
| `SoundBoardKing.Core` | C# | Modello dati, import `.spl`, persistenza JSON, logica playlist/hotbar |
| `SoundBoardKing.Audio` | C# | Decodifica (NAudio + Media Foundation; Opus via Concentus), mixer, uscita casse, interfaccia `IMicFeed` |
| `SoundBoardKing.App` | C# WPF | UI, tasti rapidi, tray icon, impostazioni |
| `SoundBoardKing.Remote` | C# | Server Kestrel REST + WebSocket, mDNS, pairing QR *(Fase 3)* |
| `SbkMicFx` | C++ | APO microfono, x64 + ARM64 *(Fase 2)* |
| `SbkSetup` | C# (elevato) | Registra/rimuove l'APO sui microfoni, riavvia il servizio audio |
| `android/` | Kotlin | Telecomando *(Fase 4)* |

### Modulo microfono (APO) — progetto

> Stato: implementato (Fase 2). Rispetto al progetto iniziale: la memoria condivisa usa un ring
> **multi-lettore** (ogni istanza dell'APO segue il produttore con una propria posizione), quindi
> funziona anche con più app che registrano insieme. L'audio nel ring è mono, e l'APO lo
> ricampiona se il microfono gira a un'altra frequenza. Prova sul PC reale: [FASE2_PROVA.md](FASE2_PROVA.md).
- **Dove si registra**: sul dispositivo di cattura scelto, come effetto di flusso/modalità (chiavi `FxProperties` in `HKLM\...\MMDevices\Audio\Capture\{id}`). Serve amministratore → lo fa `SbkSetup`, una sola volta per microfono, poi riavvio di `Audiosrv`.
- **Firma**: `audiodg.exe` protetto carica solo APO firmati da Microsoft. Le APO di terze parti (es. Equalizer APO) impostano `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio\DisableProtectedAudioDG = 1`. Da verificare cosa fa SoundPad e se esistono alternative.
- **Requisito utente**: "Miglioramenti audio" attivi sul microfono; l'app lo controlla e lo segnala.
- **Scambio dati**: la DLL (in `audiodg.exe`, account LocalService) crea una sezione di memoria condivisa `Global\SoundBoardKing.MicFx.<deviceId>` con permessi di lettura/scrittura per l'utente. Dentro: intestazione (versione protocollo, formato del microfono: sample rate e canali, heartbeat) + **ring buffer lock-free** di float 32.
- **Regole d'oro nel thread realtime**: niente allocazioni, niente lock bloccanti, niente I/O. Se l'app non scrive (heartbeat scaduto) l'APO lascia passare il microfono intatto.
- **Tutta la logica sta nell'app C#**: ricampiona al formato del microfono, applica volume, scrive nel ring buffer. La DLL fa solo `mic[i] += buffer[i]` con clipping morbido. Meno codice nativo = meno crash dell'audio di sistema.
- `IMicFeed` astrae l'uscita: implementazione `ApoMicFeed` (principale) e, se servisse un ripiego, `VirtualCableMicFeed` (VB-CABLE).

### Modello dati (JSON, `%APPDATA%\SoundBoardKing\library.json`)
```json
{
  "sounds":     [{ "id": "guid", "title": "airhorn", "artist": "", "path": "D:\\Suoni\\airhorn.mp3",
                   "durationMs": 3000, "volume": 1.0, "hotkey": "Ctrl+Num1", "addedOn": "2021-04-24" }],
  "categories": [{ "id": "guid", "name": "Voice", "icon": "gen_V", "soundIds": ["guid", "..."],
                   "children": [{ "id": "guid", "name": "Teorema", "icon": "gen_T", "soundIds": [], "children": [] }] }],
  "hotbar":     { "slots": { "0": "guid", "1000": "guid" } }
}
```
Id stabili (GUID) al posto della posizione: riordinare la lista non rompe preferiti, categorie e telecomando.

---

## 3. Protocollo PC ↔ Android *(Fasi 3–4)*

> Stato: server implementato (Fase 3). Il protocollo definitivo è in [PROTOCOLLO_REMOTO.md](PROTOCOLLO_REMOTO.md).
> Differenze rispetto a questa bozza: la scoperta usa un broadcast UDP invece di mDNS, e
> l'abbinamento passa da un codice di 6 cifre (anche dentro il QR) che il telefono scambia con
> un token permanente.

- **Scoperta**: mDNS `_soundboardking._tcp` sulla rete locale; in alternativa QR code mostrato dal PC (`sbk://192.168.1.10:8765?token=...`).
- **Sicurezza**: token di abbinamento generato dal PC (QR o PIN a 6 cifre), richiesto su ogni richiesta; regola Windows Firewall solo per reti private.
- **REST**
  - `GET /api/state` → libreria completa: suoni (solo metadati), albero categorie, hotbar, stato riproduzione
  - `GET /api/sounds/{id}/icon` (opzionale)
- **WebSocket `/ws`**
  - comandi telefono → PC: `{"cmd":"play","soundId":"...","speakers":true,"mic":true}`, `stop`, `pause`, `volume`, `hotbarPage`
  - eventi PC → telefono: `playing`, `stopped`, `progress`, `libraryChanged` (il telefono ricarica `/api/state`)
- I file audio **non** vengono trasferiti: il telefono è solo un telecomando.

### App Android (Kotlin + Compose)
Schermate: Connessione (scoperta automatica / scansione QR) → **Preferiti** (griglia 4×5, scorrimento tra le 5 pagine) · Categorie (albero) · Cerca (tutti i suoni) · barra "in riproduzione" con Stop. Vibrazione al tocco, schermo sempre acceso, riconnessione automatica.

---

## 4. Fasi

| Fase | Contenuto | Fatto quando |
|---|---|---|
| **1** | App WPF: libreria, import `.spl` SoundPad, categorie, hotbar 5×20, riproduzione su casse/cuffie, stop/pausa/volume, tasti rapidi globali, salvataggio JSON | La lista attuale (985 suoni) si importa e si riproduce dalla hotbar e dai tasti rapidi |
| **2** | APO `SbkMicFx` + `SbkSetup` + `ApoMicFeed` | In Discord con il microfono reale selezionato si sente il suono; chiudendo l'app il microfono torna normale |
| **3** | Server remoto nell'app Windows (REST + WebSocket, mDNS, QR, firewall) | Un client di test elenca i suoni e ne avvia uno |
| **4** | App Android | Dal telefono si vedono preferiti, categorie, ricerca e si avviano/fermano i suoni |
| **5** | Extra: modifica/taglio suoni, registrazione, TTS, normalizzazione volume, pressione automatica del tasto push-to-talk durante la riproduzione, compatibilità con la pipe `sp_remote_control` (plugin Stream Deck esistenti), traduzioni | — |

---

## 5. Rischi

- **APO (Fase 2) è la parte più delicata**: codice nativo dentro il motore audio di Windows; un bug blocca l'audio di sistema. Gli aggiornamenti di Windows hanno rotto in passato le APO non installate tramite driver. Va testata su un PC con **microfono fisico** (la sessione attuale in Desktop remoto vede solo "Audio remoto").
- `DisableProtectedAudioDG` abbassa una protezione di sistema: va spiegato all'utente in fase di installazione.
- Anti-cheat: rischio basso, l'APO non tocca i processi dei giochi.
- Hotkey con giochi avviati come amministratore: serve un componente elevato (come `SoundpadService`) o avviare l'app elevata.

## 6. Note legali
"Prendere spunto" va bene: funzioni e flussi non sono protetti. Non vanno copiati codice decompilato, icone, testi, nome o grafica di SoundPad (Leppsoft). La libreria di suoni contiene brani protetti: uso personale, da non distribuire con l'app.

## 7. Ambiente di sviluppo
- .NET 8 SDK
- Visual Studio 2022 (Community) o Build Tools con carico "Sviluppo di applicazioni desktop con C++" + Windows 11 SDK (per l'APO)
- Android Studio (Fase 4)
- git (consigliato)
