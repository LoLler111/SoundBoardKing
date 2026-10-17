# Fase 4 — Prova dell'app Android

## Cosa è già verificato
- Il codice di rete dell'app, eseguito sul PC (`RealPcTest`), ha usato la vera SoundBoardKing:
  - ha trovato il PC con il broadcast UDP;
  - si è abbinato e ha scaricato lo stato (985 suoni, 5 categorie, 100 preferiti);
  - ha mandato i comandi via WebSocket e ricevuto le risposte.
- Client REST/WebSocket, link di abbinamento ed eventi sono coperti da test unitari.
- Le schermate sono disegnate con Robolectric/Roborazzi: si vedono in `android/app/src/test/snapshots/`.

## Cosa resta da verificare (questa prova)
L'app su un telefono vero: fotocamera/QR, tocco, vibrazione, schermo sempre acceso, Wi-Fi reale.

## Installazione
1. Copia `artifacts\publish\SoundBoardKing-android.apk` sul telefono (cavo, Drive, Telegram…).
2. Aprilo: Android chiede di consentire l'installazione da quella app ("origini sconosciute").
   L'APK è firmato con una chiave di sviluppo, quindi Play Protect potrebbe avvisare:
   scegli "Installa comunque".
3. Serve Android 8.0 o successivo.

## Prova
1. Sul PC apri SoundBoardKing e premi il pulsante **Telefono** (icona del cellulare in alto).
   Se è la prima volta, Windows chiede il permesso del firewall: **Consenti**.
2. Sul telefono, connesso alla **stessa rete Wi-Fi** del PC, fai una di queste cose:
   - inquadra il QR con la **fotocamera** del telefono: si apre direttamente SoundBoardKing;
   - oppure apri l'app → **Scansiona il QR**;
   - oppure tocca il PC nella lista "PC trovati sulla rete" e inserisci il codice di 6 cifre.
3. Sul PC compare "Abbinato: <nome del telefono>".
4. Prova:
   - **Preferiti**: 5 pagine (scorri o tocca 1–5); tocca un pulsante e il suono parte sul PC;
   - **Categorie**: entra e esci dalle sottocategorie, anche con il tasto Indietro;
   - **Cerca**: filtra per nome;
   - in basso pausa, stop e l'interruttore del microfono.
5. Cambia qualcosa sul PC (rinomina un suono, aggiungilo ai preferiti): il telefono si aggiorna da solo.
6. Chiudi e riapri l'app: si riconnette senza riabbinare.

## Se non funziona
| Sintomo | Cosa controllare |
|---|---|
| "Nessun PC trovato" | Stessa rete Wi-Fi? Alcune reti ospiti o aziendali isolano i dispositivi; prova "Inserisci indirizzo e codice a mano" con l'indirizzo mostrato nella finestra Telefono |
| "PC non raggiungibile" | Firewall di Windows: SoundBoardKing deve essere consentito sulle reti private (Sicurezza di Windows → Firewall → Consenti app) |
| "Codice non valido o scaduto" | Il codice vale una sola volta e per 10 minuti: chiudi e riapri la finestra Telefono |
| Il suono non parte | Il messaggio in basso nel telefono dice il motivo (file mancante, nessun dispositivo audio…) |

## Ricompilare
```bash
powershell -File publish.ps1
```
Serve Android Studio installato (usa il suo Java). Per rigenerare le immagini delle schermate:
```bash
android\gradlew.bat -p android recordRoborazziDebug
```
