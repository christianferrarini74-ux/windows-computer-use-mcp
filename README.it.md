# Windows Computer Use MCP

> *English documentation (primary): [README.md](README.md)*

Server MCP locale per controllare il desktop Windows da un client compatibile. È scritto in C#/.NET 10, usa il trasporto MCP `stdio` e offre due modi complementari di interagire: input fisico con coordinate e automazione semantica UI Automation (UIA).

## Funzionalità

- Elenco e attivazione delle finestre Windows, con HWND, PID, processo, titolo, stato e rettangolo fisico.
- Screenshot DPI-aware di schermo virtuale, monitor, finestra o regione. Le immagini includono i metadati per convertire coordinate dell’immagine in coordinate fisiche.
- Mouse e tastiera tramite Win32 `SendInput`; il mouse supporta movimento, click, doppio click, click destro, trascinamento e rotellina.
- Albero UIA compatto, filtrabile per profondità e numero di nodi, con pattern e coordinate degli elementi, retry automatico sugli errori COM transitori (`0x80040201`) ed errore esplicito `STALE_ELEMENT` per `elementId` scaduti.
- Azioni UIA `focus`, `invoke`, `set_value`, `select`, `toggle`, `expand` e `collapse` (serializzate internamente per evitare race condition su WinUI).
- `launch_app` per avviare app desktop nella sessione utente interattiva; `wait` per attese temporizzate, finestre e controlli UIA; `act_and_observe` per un’azione seguita da una nuova osservazione; `run_sequence` per più passi in una chiamata.
- Audit JSONL, policy Allowlist/Denylist/Unrestricted, controlli UIPI e kill-switch globale **F10**.
- Banner “Computer Use — F10 to quit” e alone ciano persistente sul puntatore mentre il server è attivo.

## Strumenti MCP

| Strumento | Quando usarlo |
|---|---|
| `list_windows` | Individuare una finestra e ottenere il suo HWND. Filtri disponibili per processo e titolo; le finestre minimizzate sono escluse per default. |
| `screenshot` | Acquisire `screen`, `monitor`, `window` o `region`; supporta JPEG/PNG e restituisce trasformazione e origine delle coordinate. |
| `observe_window` | Ispezione iniziale consigliata: screenshot e albero UIA della finestra in una sola chiamata. Screenshot e UIA si possono disattivare o limitare. |
| `get_uia_tree` | Ispezione UIA dedicata: profondità, limite nodi, sottoalbero da `elementId`, elemento focalizzato, elementi fuori schermo e filtro interattivo. Restituisce `STALE_ELEMENT` se il `rootElementId` non è più valido. |
| `launch_app` | Avviare un eseguibile, documento o URI nella sessione utente interattiva (passando da `explorer.exe` se il server è elevato) e attendere opzionalmente la finestra. |
| `focus_window` | Portare in primo piano una finestra per HWND o PID; ripristina finestre minimizzate per default. |
| `mouse` | Azione fisica del mouse. Coordinate `image` (default, con trasformazione dell’ultima cattura o `captureId`) o `screen` fisiche. |
| `keyboard` | Inserire testo, premere un tasto o inviare una combinazione; il flag `sensitive` evita di registrare il testo in chiaro nell’audit. |
| `uia_action` | Eseguire un pattern supportato su `elementId` ottenuto dall’albero UIA. `set_value` usa il campo `value`. |
| `wait` | Attendere `delay`, una finestra per processo/titolo oppure un elemento UIA per HWND e AutomationId/nome. |
| `act_and_observe` | Eseguire un singolo passo e osservare subito il risultato; utile quando la prossima azione dipende da ciò che appare. |
| `run_sequence` | Eseguire in ordine passi già noti (`focus_window`, `mouse`, `keyboard`, `uia_action`, `wait`) e, se richiesto, acquisire uno screenshot finale. Policy e kill-switch vengono controllati a ogni passo. |

Per esplorare una nuova schermata, in genere: `list_windows` → `observe_window` → una o più azioni. Per più azioni già determinate, raggrupparle in `run_sequence` riduce i round-trip. Se serve valutare lo stato dopo ciascuna azione, usare `act_and_observe`.

## Requisiti e compilazione

- Windows 10/11 e .NET SDK 10.
- Il server interagisce con il desktop interattivo della sessione Windows in cui viene avviato.

```powershell
dotnet build .\src\WindowsComputerUse.McpServer\WindowsComputerUse.McpServer.csproj
dotnet test .\tests\WindowsComputerUse.Core.Tests\WindowsComputerUse.Core.Tests.csproj
```

Per generare una cartella distribuibile:

```powershell
dotnet publish .\src\WindowsComputerUse.McpServer\WindowsComputerUse.McpServer.csproj -c Release -o .\out\ReleaseInteractive
```

La cartella di output contiene l’eseguibile e le dipendenze. Registra nel client MCP il percorso completo a `WindowsComputerUse.McpServer.exe`.

## Configurazione

Il server usa la policy `Allowlist` vuota se non trova una configurazione. Cerca `computer-use.policy.json` prima nella directory corrente e poi accanto all’eseguibile. `WCU_POLICY_FILE` specifica un percorso esplicito. È possibile partire da [`computer-use.policy.example.json`](computer-use.policy.example.json), che autorizza Notepad e Paint.

Formato delle regole:

```json
{
  "mode": "Allowlist",
  "rules": [
    { "processName": "notepad.exe" },
    { "titlePattern": ".*Test Bench.*" }
  ]
}
```

Modalità: `Allowlist` permette solo i processi/titoli corrispondenti; `Denylist` blocca quelli corrispondenti; `Unrestricted` non filtra per processo/titolo. I nomi processo senza suffisso `.exe` vengono normalizzati. UIPI continua a bloccare l’interazione con finestre elevate se il server non è elevato.

Variabili d’ambiente:

| Variabile | Descrizione |
|---|---|
| `WCU_POLICY_MODE` | Sovrascrive la modalità: `Allowlist`, `Denylist` o `Unrestricted`. |
| `WCU_POLICY_FILE` | Percorso del JSON policy da caricare. |
| `WCU_ALLOW_PROCESSES` | Aggiunge processi separati da virgole alle regole caricate. |
| `WCU_AUDIT_DIR` | Cartella per i log `audit-YYYYMMDD.jsonl`; se omessa, i log vanno in `logs` accanto all’eseguibile. |

Se modalità, policy e variabili d’ambiente sono definite insieme, la modalità d’ambiente prevale e `WCU_ALLOW_PROCESSES` aggiunge regole al file. Evitare configurazioni duplicate in file e variabili quando si vuole una lista precisa.

### Esempio di registrazione MCP

Esempio TOML per un client che accetta `mcp_servers` in `config.toml` (adattare il percorso all’output effettivamente pubblicato):

```toml
[mcp_servers.windows-computer-use]
command = 'D:\\Coding\\C#\\Computer_Use\\out\\ReleaseInteractive\\WindowsComputerUse.McpServer.exe'
args = []

[mcp_servers.windows-computer-use.env]
WCU_POLICY_FILE = 'D:\\Coding\\C#\\Computer_Use\\computer-use.policy.json'
WCU_POLICY_MODE = 'Allowlist'
```

Dopo aver modificato la configurazione, riavviare o ricaricare il client MCP. La presenza del server si può verificare controllando che il client mostri i 12 strumenti sopra elencati; una chiamata a `list_windows` conferma la connessione operativa. Il client avvia il processo tramite `stdio`: non aprire una seconda istanza manuale dello stesso server per usarlo dal client.

## Kill-switch, banner e puntatore

Premere **F10** per fermare immediatamente Computer Use. Il kill-switch invoca l’arresto dell’host ed esce dal processo server; il client MCP mostrerà quindi la connessione interrotta e occorrerà riavviare/ricaricare il server dal client. F10 è globale mentre il server ha attivato il controllo. Il banner mostra la combinazione e il cerchio ciano segue e indica la posizione del puntatore anche quando è fermo. Il puntatore evidenziato è trasparente ai click.

## Test bench

La guida completa per avviare e usare l’app di prova, con scenari per tutti gli strumenti e risultati dell’ultima prova manuale, è in [`test/README.md`](test/README.md). Il banco è una finestra WPF usa-e-getta: non apre né modifica documenti dell’utente.

## Struttura del repository

- `src/WindowsComputerUse.Core`: acquisizione schermo, DPI, input Win32, UIA, policy, kill-switch e audit.
- `src/WindowsComputerUse.McpServer`: host MCP `stdio`, registrazione degli strumenti e manifest DPI PerMonitorV2.
- `tests/WindowsComputerUse.Core.Tests`: unit test del Core.
- `test`: applicazione manuale Windows Computer Use Test Bench e relativa guida.
- `computer-use.policy.example.json`: esempio di policy; il file locale `computer-use.policy.json` non va distribuito come configurazione predefinita.
