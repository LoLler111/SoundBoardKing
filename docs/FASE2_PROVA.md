# Fase 2 — Prova del modulo microfono

Il modulo (APO `SbkMicFx.dll`) si prova solo su un PC con un **microfono fisico** e usandolo
**davanti al PC**, non in Desktop remoto: in remoto Windows vede solo "Audio remoto".

## Cosa è già verificato
- La DLL compila pulita (avvisi livello 4 trattati come errori).
- Prova end-to-end automatica (`dotnet test`, dopo `build-native.ps1 -Tests`): il codice reale
  dell'APO riceve il suono scritto dall'app e lo somma al buffer del microfono, con livello
  corretto, uguale su entrambi i canali e senza toccare il microfono quando l'app è chiusa.

## Cosa resta da verificare (questa prova)
- Che Windows carichi davvero l'APO sul microfono (registrazione, `DisableProtectedAudioDG`,
  catena EFX/SFX/MFX).
- Lo strumento di installazione: permessi delle chiavi di registro e ripristino completo.

## Preparazione
1. Copia `artifacts\publish\SoundBoardKing-win-x64.zip` sul PC ed estrailo in una cartella
   (non serve installare .NET).
2. Avvia `SoundBoardKing.exe` e importa la lista di SoundPad, se non l'hai già fatto.
3. In Discord disattiva la **soppressione del rumore (Krisp)** durante la prova: potrebbe
   filtrare i suoni come se fossero rumore di fondo.

## Prova
1. **Impostazioni → Microfono**: scegli il tuo microfono, lascia "Endpoint (EFX) — consigliato"
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
| Resta "non installato" | `%ProgramData%\SoundBoardKing\micsetup.log`: l'installazione è andata a buon fine? |
| Resta "non installato" ma l'installazione è OK | `%ProgramData%\SoundBoardKing\micfx.log`: se manca o non ha righe `LockForProcess`, Windows non ha caricato l'APO. Reinstalla scegliendo **SFX**, poi prova **MFX** |
| "attivo" ma il suono non si sente | Krisp o altra soppressione del rumore attiva; volume del microfono nell'app a zero |
| Il microfono non funziona più | Impostazioni → Microfono → **Rimuovi**. In emergenza, da un prompt amministratore: `SoundBoardKing.MicSetup.exe uninstall --all` |

Da riportare: quale posizione (EFX/SFX/MFX) ha funzionato, e i due file di log.

## Cosa modifica l'installazione (tutto viene ripristinato da "Rimuovi")
- Copia la DLL in `%ProgramFiles%\SoundBoardKing\MicFx` e la registra come oggetto COM/APO.
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio\DisableProtectedAudioDG = 1`:
  permette al motore audio di caricare APO non firmate da Microsoft (come fa Equalizer APO).
- Aggiunge l'APO alle proprietà effetti del microfono scelto e attiva "Miglioramenti audio".
  Se il driver aveva già un suo effetto in quella posizione, lo mantiene e li mette in lista
  insieme.
- Per scrivere quelle chiavi, che appartengono al servizio audio, ne prende temporaneamente la
  proprietà. I permessi originali sono salvati e rimessi alla rimozione.
- I valori originali sono salvati in `HKLM\SOFTWARE\SoundBoardKing\MicFx`.
