# SoundBoardKing

Soundboard per Windows ispirata a SoundPad, con telecomando Android (in arrivo). Analisi e fasi in [docs/ANALISI.md](docs/ANALISI.md).

## Requisiti
- .NET 10 SDK

## Comandi
```bash
dotnet build SoundBoardKing.slnx
dotnet test SoundBoardKing.slnx
dotnet run --project src/SoundBoardKing.App
```

## Struttura
| Cartella | Contenuto |
|---|---|
| `src/SoundBoardKing.Core` | Modello dati, import `.spl` di SoundPad, salvataggio JSON |
| `src/SoundBoardKing.Audio` | Motore audio (NAudio): decodifica, mixer, uscita cuffie; `IMicFeed` per il microfono (Fase 2) |
| `src/SoundBoardKing.App` | App WPF: libreria, categorie, preferiti 5×20, tasti rapidi globali |
| `tests/SoundBoardKing.Core.Tests` | Test xUnit del Core |

I dati utente stanno in `%APPDATA%\SoundBoardKing` (`library.json`, `settings.json`, `error.log`).
