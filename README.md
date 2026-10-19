# SoundBoardKing

Soundboard per Windows ispirata a SoundPad, con telecomando Android (in arrivo). Analisi e fasi in [docs/ANALISI.md](docs/ANALISI.md).

## Requisiti
- .NET 10 SDK
- Per il modulo microfono: Visual Studio Build Tools 2026 con il carico "Sviluppo di applicazioni desktop con C++"

## Comandi
```bash
powershell -File build-native.ps1 -Tests
dotnet build SoundBoardKing.slnx
dotnet test SoundBoardKing.slnx
dotnet run --project src/SoundBoardKing.App
powershell -File publish.ps1
```
- `build-native.ps1` compila l'APO (`artifacts\native`) e, con `-Tests`, il banco di prova usato da `dotnet test`. Va lanciato prima di `dotnet build`, così la DLL finisce accanto all'app.
- `publish.ps1` crea in `artifacts\releases` tutto quello che va in una release (vedi [Rilasci](#rilasci)); serve `vpk` (`dotnet tool install -g vpk`). I pacchetti non richiedono .NET installato.
- Prova del modulo microfono su un PC reale: [docs/FASE2_PROVA.md](docs/FASE2_PROVA.md).
- App Android (`android/`, Kotlin + Compose): serve Android Studio, di cui si usa il Java incluso. `publish.ps1` crea anche `artifacts\publish\SoundBoardKing-android.apk`. Prova sul telefono: [docs/FASE4_PROVA.md](docs/FASE4_PROVA.md).
  ```bash
  android\gradlew.bat -p android testDebugUnitTest assembleRelease
  ```

## Rilasci
Le due app si aggiornano dalle [release di GitHub](https://github.com/LoLler111/SoundBoardKing/releases) del repository pubblico, quindi senza token.
Hanno un solo numero di versione, quello in [`version.txt`](version.txt): l'app Windows lo mostra in *Impostazioni → Versione*, quella Android in *⋮ → Versione … · aggiornamenti* (e in fondo alla schermata di abbinamento).

**Pubblicare una versione**
1. Aggiorna `version.txt` (es. `0.3.0`) e fai il commit.
2. Crea e invia il tag con lo stesso numero:
   ```bash
   git tag v0.3.0
   ```
   ```bash
   git push origin main v0.3.0
   ```
3. Il workflow [Release](.github/workflows/release.yml) gira su un runner Windows: compila modulo microfono, app e APK, esegue i test e pubblica la release. Se il tag non corrisponde a `version.txt` si ferma.

Il workflow (o `publish.ps1` in locale) mette nella release:

| File | A cosa serve |
|---|---|
| `SoundBoardKing-win-Setup.exe` | Installazione in `%LOCALAPPDATA%\SoundBoardKing`, con collegamenti; poi si aggiorna da sola |
| `SoundBoardKing-win-Portable.zip` | La stessa app da scompattare dove vuoi (anche su chiavetta); si aggiorna anch'essa |
| `SoundBoardKing-<versione>-full.nupkg`, `-delta.nupkg`, `releases.win.json` | Letti dall'aggiornamento di Windows ([Velopack](https://velopack.io)) |
| `SoundBoardKing-android.apk` | Telecomando Android, firmato sempre con la stessa chiave |

Per pubblicare a mano senza Actions: `powershell -File publish.ps1 -DownloadPrevious`, poi carica il contenuto di `artifacts\releases` con `vpk upload github` (o `gh release create`).

**Come si aggiornano**
- *Windows*: all'avvio l'app controlla le release, scarica in silenzio la nuova versione e mostra *Aggiorna alla x.y.z* nella barra in basso. Si installa quando chiudi l'app, oppure subito con quel pulsante (o con *Impostazioni → Riavvia e aggiorna*). Funziona per le copie installate con `Setup.exe` o scompattate dallo zip Portable, non per una cartella compilata a mano.
  - **Modulo microfono**: `SbkMicFx.dll` è caricata da `audiodg.exe` dalla copia in `C:\Program Files\SoundBoardKing\MicFx`, quindi l'aggiornamento dell'app non la tocca. Se la nuova versione ne porta una diversa, all'avvio l'app chiede di reinstallarla: `SoundBoardKing.MicSetup refresh` (amministratore) la copia e riavvia per un attimo l'audio di Windows. Se rispondi No, il microfono continua con quella vecchia e la domanda torna al prossimo avvio. La DLL è compilata in modo riproducibile (`/Brepro`), quindi la domanda arriva solo quando il modulo cambia davvero.
  - Disinstallando l'app da *Impostazioni di Windows → App*, il modulo viene tolto da tutti i microfoni (con richiesta di amministratore).
  - Il profilo di un'app installata con `Setup.exe` non può stare accanto all'eseguibile, perché quella cartella viene sostituita a ogni aggiornamento: resta in `%APPDATA%\SoundBoardKing` o dove lo sposti. Nello zip Portable "accanto all'eseguibile" vuol dire accanto al `SoundBoardKing.exe` che apri, che resta al suo posto.
  - Prova senza pubblicare: imposta `SOUNDBOARDKING_UPDATE_SOURCE` con la cartella `artifacts\releases` di una versione più nuova e avvia una copia installata.
- *Android*: alla prima apertura l'app legge l'ultima release; se è più nuova propone *Aggiorna*, scarica l'APK e apre l'installazione di Android. La prima volta Android chiede di consentire a SoundBoardKing di installare app. Prova senza pubblicare: compila con `-PupdateUrl=http://…/latest.json`, cioè un file nel formato dell'API GitHub `releases/latest`.

**Chiave di firma Android**
Un APK si installa sopra quello vecchio solo se è firmato con la stessa chiave, quindi ogni release usa la stessa: perderla vuol dire disinstallare l'app dai telefoni. La chiave non sta nel repository.
- In locale: `%USERPROFILE%\.soundboardking\release.jks`, con le password in `signing.properties` nella stessa cartella. Tienine una copia al sicuro. Senza questi file l'APK viene firmato con la chiave di debug e non può aggiornare quello pubblicato.
- Su GitHub (*Settings → Secrets and variables → Actions*): `ANDROID_KEYSTORE_BASE64` (il `.jks` in base64), `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` (`soundboardking`), `ANDROID_KEY_PASSWORD`. Il file base64 si ottiene con:
  ```bash
  powershell -Command "[Convert]::ToBase64String([IO.File]::ReadAllBytes(\"$env:USERPROFILE\.soundboardking\release.jks\")) | Set-Clipboard"
  ```
- Gli APK installati prima della versione 0.2.0 erano firmati con la chiave di debug: vanno disinstallati una volta (e il telefono riabbinato), poi gli aggiornamenti arrivano da soli.

Le app non sono firmate con un certificato di codice Windows, quindi al primo avvio di `Setup.exe` SmartScreen può mostrare "Windows ha protetto il PC" (*Ulteriori informazioni → Esegui comunque*).

## Struttura
| Cartella | Contenuto |
|---|---|
| `src/SoundBoardKing.Core` | Modello dati, import `.spl` di SoundPad, salvataggio JSON |
| `src/SoundBoardKing.Audio` | Motore audio (NAudio): decodifica, mixer, uscita cuffie; `ApoMicFeed` scrive nel microfono tramite l'APO |
| `src/SoundBoardKing.App` | App WPF: libreria, categorie, preferiti 5×20, tasti rapidi globali, gestione modulo microfono |
| `src/SoundBoardKing.Remote` | Server per il telefono dentro l'app: REST + WebSocket sulla porta 8765, scoperta UDP, abbinamento con QR/codice ([protocollo](docs/PROTOCOLLO_REMOTO.md)) |
| `src/SoundBoardKing.MicSetup` | Installatore del modulo microfono (amministratore): registra l'APO e la collega al microfono, con ripristino completo |
| `native/SbkMicFx` | APO in C++ caricata dal motore audio di Windows: somma i suoni al segnale del microfono |
| `native/SbkMicFx.Tests` | Banco di prova che esegue l'APO come farebbe Windows |
| `tests/SoundBoardKing.Core.Tests` | Test xUnit del Core |
| `tests/SoundBoardKing.Audio.Tests` | Prova end-to-end app ↔ APO tramite memoria condivisa |
| `tests/SoundBoardKing.Remote.Tests` | Server remoto: abbinamento, autorizzazioni, REST, WebSocket, scoperta |
| `android/` | Telecomando Android: abbinamento con QR/codice, preferiti, categorie, ricerca, riproduzione e microfono |

Libreria, categorie, preferiti e impostazioni (tranne i dispositivi audio) stanno in un unico
file di profilo, `SoundBoardKing.sbk`, riscritto a ogni modifica. Si trova in `%APPDATA%\SoundBoardKing`
finché da *Impostazioni → Profilo* non lo sposti altrove. Accanto all'eseguibile, o in una cartella a
fianco, viene ritrovato anche spostando tutto insieme su un altro PC:

```
Kit\App\SoundBoardKing.exe
Kit\App\SoundBoardKing.sbklink    ← contiene ..\Config\SoundBoardKing.sbk
Kit\Config\SoundBoardKing.sbk     ← i suoni sono scritti come ..\Suoni\…
Kit\Suoni\…
```

I suoni nella cartella del profilo o in una cartella a fianco sono salvati con percorsi relativi;
quelli altrove (un altro disco, cartelle lontane) restano assoluti. Restano sul PC, in `%APPDATA%\SoundBoardKing`, solo `local.json` (uscita audio,
posizione del profilo) ed `error.log`. Il primo avvio converte da solo i vecchi `library.json` e `settings.json`.
