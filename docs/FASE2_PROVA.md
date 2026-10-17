# Fase 2 — Prova del modulo microfono

## Cosa è già verificato
- La DLL compila pulita (avvisi livello 4 trattati come errori).
- Prova end-to-end automatica (`dotnet test`, dopo `build-native.ps1 -Tests`): il codice reale
  dell'APO, creato come lo crea Windows (aggregazione COM), riceve il suono scritto dall'app e
  lo somma al buffer del microfono, con livello corretto, uguale su entrambi i canali e senza
  toccare il microfono quando l'app è chiusa.
- **Prova reale nel motore audio di Windows** (17/10/2026, VM Hyper-V con il microfono virtuale
  VB-CABLE "CABLE Output"):
  - l'installatore registra l'APO e, con la posizione **Automatica**, la inserisce in **LFX**;
  - `audiodg.exe` la carica e un programma che registra dal microfono riceve il tono inviato
    dall'app (RMS 0,354, componente a 440 Hz 0,500, silenzio prima e dopo);
  - "Rimuovi" rimette registro e permessi esattamente come prima.
- Cosa si è imparato:
  - su questo driver Windows ignora SFX/MFX/EFX e usa solo LFX/GFX, perché è un driver "classico";
  - il motore audio crea le APO con l'aggregazione COM, che il modulo ora supporta (prima la rifiutava e il microfono non si apriva).

## Cosa resta da verificare
Un microfono fisico con driver moderno (Realtek, USB con effetti di Windows): lì la posizione
Automatica sceglie **SFX**, che non è ancora stata provata su hardware vero.

## Preparazione
1. Copia `artifacts\publish\SoundBoardKing-win-x64.zip` sul PC ed estrailo in una cartella
   (non serve installare .NET).
2. Avvia `SoundBoardKing.exe` e importa la lista di SoundPad, se non l'hai già fatto.
3. In Discord disattiva la **soppressione del rumore (Krisp)** durante la prova: potrebbe
   filtrare i suoni come se fossero rumore di fondo.

## Prova
1. **Impostazioni → Microfono**: scegli il tuo microfono, lascia **"Automatica — consigliata"**
   e premi **Installa**. Conferma la richiesta di amministratore. L'audio di Windows si
   interrompe per un paio di secondi: è il servizio audio che riparte.
2. Apri un'app che usa il microfono. Va bene **Registratore di suoni** di Windows, oppure
   Discord → Impostazioni → Voce e video → "Verifichiamo".
3. In basso nell'app il pulsante deve diventare **"Microfono: attivo"**.
   Con "in attesa" il modulo è caricato, ma nessuna app sta usando il microfono.
4. Fai partire un suono: deve sentirsi nella registrazione o nel test di Discord, insieme
   alla tua voce.
5. Prova il cursore del volume accanto al pulsante del microfono e il pulsante stesso, che
   accende e spegne l'invio.

## Se non funziona
| Sintomo | Cosa controllare |
|---|---|
| Resta "non installato" | `%ProgramData%\SoundBoardKing\micsetup.log`: l'installazione è andata a buon fine? Che posizione ha scelto? |
| Resta "non installato" ma l'installazione è OK | `%ProgramData%\SoundBoardKing\micfx.log`: se manca o non ha righe `LockForProcess`, Windows non ha caricato l'APO in quella posizione. Reinstalla provando nell'ordine **LFX**, **SFX**, **GFX**, **MFX**, **EFX** |
| "attivo" ma il suono non si sente | Krisp o altra soppressione del rumore attiva; volume del microfono nell'app a zero |
| Il microfono non si apre più | Impostazioni → Microfono → **Rimuovi**. In emergenza, da un prompt amministratore: `SoundBoardKing.MicSetup.exe uninstall --all` |

Da riportare: quale posizione ha funzionato e i due file di log.

## Provare senza un microfono vero
Si può usare **VB-CABLE** (vb-audio.com) come microfono virtuale: installa il modulo su
"CABLE Output" e registra da quello. In una VM Hyper-V con sessione avanzata, o in Desktop
remoto con l'audio reindirizzato, i dispositivi locali della macchina sono nascosti ai programmi
della sessione: per registrare serve un programma che giri fuori sessione, ad esempio
un'attività pianificata come SYSTEM.

## Cosa modifica l'installazione (tutto viene ripristinato da "Rimuovi")
- Copia la DLL in `%ProgramFiles%\SoundBoardKing\MicFx` e la registra come oggetto COM/APO.
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio\DisableProtectedAudioDG = 1`:
  permette al motore audio di caricare APO non firmate da Microsoft (come fa Equalizer APO).
- Aggiunge l'APO alle proprietà effetti del microfono scelto e attiva "Miglioramenti audio".
  - Nelle posizioni moderne (SFX/MFX/EFX), se il driver ha già un suo effetto lo mantiene e li mette in lista insieme.
  - In quelle classiche (LFX/GFX) lo sostituisce fino alla rimozione.
- Per scrivere quelle chiavi, che appartengono al servizio audio, ne prende temporaneamente la
  proprietà. I permessi originali sono salvati e rimessi alla rimozione.
- I valori originali sono salvati in `HKLM\SOFTWARE\SoundBoardKing\MicFx`.
