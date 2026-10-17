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
- `publish.ps1` crea `artifacts\publish\SoundBoardKing-win-x64.zip`, un pacchetto che non richiede .NET installato.
- Prova del modulo microfono su un PC reale: [docs/FASE2_PROVA.md](docs/FASE2_PROVA.md).

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

I dati utente stanno in `%APPDATA%\SoundBoardKing` (`library.json`, `settings.json`, `error.log`).
