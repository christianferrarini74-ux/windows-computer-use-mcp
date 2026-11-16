# Windows Computer Use Test Bench

Il banco è una piccola applicazione WPF temporanea per provare a mano gli strumenti MCP su controlli noti e ripetibili. Mantiene i dati solo in memoria: non apre file dell’utente e **Reset all** riporta tutti i controlli allo stato iniziale.

## Avvio

Aprire PowerShell nella cartella radice del repository e avviare:

```powershell
dotnet run --project .\test\WindowsComputerUse.TestBench.csproj
```

La finestra si chiama **Windows Computer Use Test Bench**. Lasciarla visibile nella stessa sessione Windows del server. Il banco è un’app separata: il client MCP deve avere già caricato e avviato `WindowsComputerUse.McpServer`.

Per pubblicare un eseguibile autonomo del banco (facoltativo):

```powershell
dotnet publish .\test\WindowsComputerUse.TestBench.csproj -c Release -o .\test\publish
```

## Procedura consigliata

1. Avviare il banco e verificare che il server MCP risulti connesso nel client.
2. Chiamare `list_windows` con filtro processo `WindowsComputerUse.TestBench` oppure titolo `Windows Computer Use Test Bench`; annotare l’HWND restituito.
3. Chiamare `focus_window` con quell’HWND, poi `observe_window` con screenshot e UIA. L’osservazione è il punto di partenza per identificare controlli e coordinate.
4. Eseguire uno scenario qui sotto. Usare `act_and_observe` quando si vuole vedere subito l’esito dell’azione; per azioni indipendenti già note, raggrupparle in `run_sequence`.
5. Verificare sia il risultato visibile sia il testo nella barra di stato azzurra. Per controllare i valori semantici, osservare nuovamente l’albero UIA.
6. Premere **Reset all** tra scenari che dipendono dallo stato iniziale.

Le coordinate dello screenshot restituito da una cattura ridimensionata sono coordinate `image`; fornirne il `captureId` se si usa `mouse` e nel frattempo è stata acquisita un’altra immagine. In alternativa usare `coordinateSpace: "screen"` con coordinate fisiche. Non usare le coordinate UIA come coordinate dell’immagine: `boundingRect` è in coordinate fisiche dello schermo.

## Controlli nel banco

| Area | Controllo / AutomationId | Risultato atteso |
|---|---|---|
| Text and keyboard | Casella `inputTextBox`, pulsante `applyButton`, output `outputTextBox`, reset `resetButton` | Il testo appare nell’output e lo stato indica che è stato applicato. Ctrl+Invio applica il testo dalla finestra. |
| UI Automation patterns | Checkbox `testCheckBox` | Toggle aggiorna segno di spunta e stato. |
| UI Automation patterns | ComboBox `testComboBox`: Alpha, Bravo, Charlie | La selezione appare nel controllo e nello stato. |
| UI Automation patterns | Lista `testListBox`: Row one, Row two, Row three | Selezione di una riga e aggiornamento dello stato. |
| Expand, drag and scroll | Expander `testExpander`, contenuto `expandedContent` | Expand/collapse mostra o nasconde il testo e aggiorna lo stato. |
| Expand, drag and scroll | Slider `testSlider`, valori 0–100 a scatti di 10 | Trascinamento o `set_value` aggiorna valore e stato. |
| Dynamic UIA element and wait | Pulsante `createDynamicButton`; elemento creato `dynamicActionButton` | Dopo il click appare “Dynamic action ready”; invocarlo aggiorna lo stato. Reset lo rimuove. |
| Modal window and scroll target | Pulsante `openDialogButton`; finestra “Computer Use Test Dialog” | Si apre una finestra modale distinta; il pulsante “Close dialog” la chiude e aggiorna lo stato della finestra principale. Esc è un modo alternativo per chiudere il dialogo. |
| Modal window and scroll target | ScrollViewer `testScrollViewer`, pulsanti `scrollRow01`–`scrollRow24` | La rotellina scorre questa lista indipendentemente dal pannello sinistro; click su una riga aggiorna lo stato. |

## Scenari per strumento

Gli esempi descrivono i passaggi, non una sintassi RPC universale: i client mostrano i campi degli strumenti secondo il proprio formato. Usare l’HWND e gli `elementId` dell’osservazione corrente; i `elementId` possono cambiare a ogni nuova lettura dell’albero.

| Strumento | Prova manuale |
|---|---|
| `list_windows` | Filtrare per processo `WindowsComputerUse.TestBench` o titolo `Test Bench`; verificare titolo, HWND, PID e rettangolo. Ripetere con `includeMinimized` se si vuole includere una finestra minimizzata. |
| `focus_window` | Usare HWND o PID del banco. Verificare che torni in primo piano; facoltativamente minimizzarlo e richiamare con `restoreIfMinimized: true`. |
| `observe_window` | Fornire HWND. Verificare che la risposta contenga screenshot e albero UIA; riprovare disattivando uno dei due o riducendo `maxDepth`/`maxNodes`. |
| `screenshot` | Acquisire la finestra (`target: window`, HWND), l’intero schermo (`screen`), un monitor (`monitor`, indice da zero) e una regione (`region` con X/Y/larghezza/altezza fisici). Verificare `captureId`, origine, dimensioni e scale. JPEG è più rapido; PNG è lossless. |
| `get_uia_tree` | Usare HWND del banco. Trovare AutomationId come `inputTextBox`, `testCheckBox`, `testComboBox`, `testSlider`; provare `interactiveOnly`, `includeOffscreen`, profondità e limite nodi. Per un sottoalbero, usare un `rootElementId` ottenuto dalla lettura appena fatta. |
| `uia_action` | Ottenere un `elementId` fresco. Eseguire `set_value` su `inputTextBox`, `invoke` su `applyButton`, `toggle` sulla checkbox, `expand` sull’Expander e `select` su un elemento della ComboBox o della lista. `set_value` riceve il campo `value`. |
| `keyboard` | Focalizzare l’input, fare hotkey Ctrl+A, digitare un testo e poi Ctrl+Enter. Confrontare input, output e stato. Verificare anche `key_press` su un controllo adatto. |
| `mouse` | Click su Apply; trascinare lo slider; scorrere il pannello sinistro e poi la lista destra; fare click su una riga. Provare coordinate immagine con il `captureId` e coordinate schermo fisiche. |
| `wait` | Dopo aver creato il controllo, attendere `mode: uia_element`, HWND del banco e `automationId: dynamicActionButton`. Aprire il dialogo e attendere `mode: window`, titolo `Computer Use Test Dialog` o processo `WindowsComputerUse.TestBench`. Si può anche verificare `mode: delay`. |
| `act_and_observe` | Eseguire un’azione singola, per esempio toggle della checkbox o click Apply, e verificare che nella stessa risposta arrivi una nuova osservazione. Provare a disattivare screenshot/UIA quando non servono. |
| `run_sequence` | Eseguire una breve sequenza nota, per esempio focus input → Ctrl+A → inserimento testo → Ctrl+Enter; richiedere `captureFinalScreenshot` se serve. Controllare risultati e stato finale. Ogni passo può essere `focus_window`, `mouse`, `keyboard`, `uia_action` o `wait`. |

### Sequenza tastiera d’esempio

1. Dall’albero recente, usare `uia_action` `focus` sull’elemento `inputTextBox` (oppure click fisico sulla casella).
2. In `run_sequence`, eseguire in ordine: keyboard `hotkey` con `keys: ["Ctrl", "A"]`; keyboard `type_text` con il testo desiderato; keyboard `hotkey` con `keys: ["Ctrl", "Enter"]`.
3. Leggere l’output con `observe_window` o richiedere uno screenshot finale nella sequenza.

La forma delle chiavi è il nome chiave accettato dal server; se il client non offre `run_sequence`, ripetere le operazioni con `keyboard` singolo.

## Prove manuali eseguite

Il 16 novembre 2026, dopo il riavvio del server MCP, è stata svolta una prova interattiva completa sul banco. Esiti osservati:

- individuazione, focus e osservazione combinata screenshot/UIA del banco riusciti;
- testo inserito e applicato con tastiera e Ctrl+Invio; input e output coincidevano;
- checkbox attivata via UIA e stato aggiornato;
- ComboBox espansa e selezionata su Bravo; stato aggiornato;
- Expander aperto; contenuto espanso visibile;
- slider trascinato col mouse al valore 70 e valore confermato dalla UI;
- pannello sinistro e lista destra scrollati separatamente;
- dialogo modale trovato con `wait` come finestra separata e chiuso con il proprio pulsante;
- controllo dinamico creato, rilevato da `wait` per AutomationId in 23 ms e invocato;
- alone del puntatore visibile anche a mouse fermo mentre Computer Use era attivo.

Questa è una verifica manuale funzionale, non un benchmark statistico né una sostituzione degli unit test. Il kill-switch F10 non è stato riattivato durante quest’ultima sessione di prove; in una prova separata l’utente ha confermato che F10 ferma il server.

## Misurare le prestazioni

Per confronti ripetibili, tenere la finestra ferma e non usare mouse/tastiera durante la misura.

1. Annotare client, build/configurazione server, dimensioni finestra e data.
2. Fare una chiamata di riscaldamento per ogni scenario, poi almeno cinque ripetizioni.
3. Registrare latenza end-to-end del client, durata diagnostica server, esito, numero nodi UIA, dimensione immagine e configurazione `maxWidth`/formato.
4. Confrontare `observe_window` con cattura+albero separati; confrontare una singola azione con `act_and_observe`; confrontare azioni note separate con `run_sequence`.
5. Non confrontare risultati con screenshot di dimensioni/formati diversi o alberi UIA con limiti diversi. Riportare mediana e intervallo, non solo il valore migliore.

## Ripristino e chiusura

- **Reset all** ripristina testo, output, checkbox, ComboBox, lista, Expander, slider (50), scroll iniziale e rimuove il controllo dinamico.
- Il pulsante del dialogo lo chiude senza chiudere l’applicazione.
- Chiudere la finestra principale per terminare il banco.
- **F10** è il kill-switch globale del server MCP, non del banco: termina il processo server e interrompe la connessione MCP. Riavviare/ricaricare il server dal client per continuare.
