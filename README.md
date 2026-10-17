# SyncToyNext

**SyncToyNext** ist ein freier Nachbau von **Microsoft SyncToy 2.1** für Windows 10 und 11.

SyncToy war ein kleines, beliebtes Werkzeug, mit dem man zwei Ordner abgleichen konnte, zum Beispiel
den Dokumente-Ordner mit einer externen Festplatte oder einem USB-Stick. Microsoft pflegt es seit
vielen Jahren nicht mehr, und es lässt sich nicht mehr offiziell herunterladen. SyncToyNext bildet
Bedienung und Verhalten von SyncToy 2.1 möglichst genau nach, basiert aber auf aktuellem .NET 8 und
ist quelloffen (MIT-Lizenz).

> SyncToyNext ist ein unabhängiges Projekt und steht in keiner Verbindung zu Microsoft.
> „SyncToy“ ist eine Marke von Microsoft.

---

## Funktionen

- **Ordnerpaare:** beliebig viele benannte Paare aus einem linken und einem rechten Ordner,
  einzeln oder gemeinsam über **„Alle ausführen“** startbar.
- **Drei Aktionen wie im Original:**

  | Aktion | Neue/geänderte Dateien | Umbenennungen | Löschungen | Änderungen rechts |
  |---|---|---|---|---|
  | **Synchronize** | in beide Richtungen | in beide Richtungen | in beide Richtungen | werden übernommen |
  | **Echo** | links → rechts | links → rechts | links → rechts | werden ignoriert |
  | **Contribute** | links → rechts | links → rechts | keine | werden ignoriert |

- **Vorschau:** zeigt jede geplante Aktion mit Begründung, bevor etwas verändert wird. Einzelne
  Aktionen lassen sich abwählen. Abgewählte Aktionen werden beim nächsten Lauf erneut angeboten.
- **Umbenennungen und Verschiebungen** werden erkannt und auf der anderen Seite nachvollzogen,
  statt die Datei neu zu kopieren und das Original zu löschen.
- **Papierkorb:** überschriebene und gelöschte Dateien landen standardmäßig im Windows-Papierkorb.
- **Filter:** einzuschließende und auszuschließende Dateien (z. B. `*.docx; *.xlsx` oder `*.tmp; ~$*`),
  ausgeschlossene Unterordner sowie das Ausschließen schreibgeschützter, versteckter oder Systemdateien.
- **Dateiinhalte prüfen:** auf Wunsch Byte-für-Byte-Vergleich statt nur Größe und Änderungsdatum.
- **Kommandozeile** `SyncToyNextCmd.exe` für die Windows-Aufgabenplanung, wie `SyncToyCmd.exe`.
- **Protokoll** aller Läufe unter `%LOCALAPPDATA%\SyncToyNext\SyncToyNextLog.txt`.

### Zusätzliche Sicherheit gegenüber dem Original

- **Leeres Laufwerk:** Ist eine Seite plötzlich komplett leer, obwohl sie beim letzten Lauf Dateien
  enthielt (typisch: falscher USB-Stick angesteckt), fragt SyncToyNext nach, statt auf der anderen
  Seite alles zu löschen. Die Kommandozeile überspringt das Paar in diesem Fall, außer mit `--force`.
- **Änderungen nach der Vorschau:** Hat sich eine Datei zwischen Vorschau und Ausführung verändert,
  wird die betreffende Aktion übersprungen, statt die neue Version zu überschreiben.
- **Kein halbes Kopieren:** Dateien werden zuerst unter einem temporären Namen geschrieben und erst
  danach umbenannt. Ein Abbruch hinterlässt deshalb nie eine halb kopierte Datei unter dem echten Namen.
- **Fehler werden wiederholt:** Fehlgeschlagene oder abgebrochene Aktionen bietet SyncToyNext beim
  nächsten Lauf erneut an.

---

## Herunterladen und starten

Fertige Programme erzeugt der GitHub-Workflow bei jedem Push automatisch:

1. Im Repository auf **Actions** gehen, den neuesten erfolgreichen Lauf von **„Build“** öffnen
   und unten unter **Artifacts** herunterladen. Bei Versionen mit Tag (z. B. `v1.0.0`) liegen die
   ZIP-Dateien zusätzlich unter **Releases**.
2. Es gibt zwei Varianten:
   - **`SyncToyNext-win-x64`:** läuft ohne Installation. Enthält .NET bereits, ist daher größer (ca. 70 MB).
   - **`SyncToyNext-win-x64-framework`:** sehr klein, benötigt aber die
     [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
3. ZIP entpacken, beide `.exe`-Dateien im selben Ordner lassen und `SyncToyNext.exe` starten.

Windows SmartScreen kann beim ersten Start warnen, weil die Programme nicht signiert sind.
Über „Weitere Informationen“ → „Trotzdem ausführen“ lässt es sich starten.

---

## Bedienung

1. **Neues Ordnerpaar …** anklicken, Name, linken und rechten Ordner sowie die Aktion wählen.
   Unter **Optionen** lassen sich Filter und weitere Einstellungen festlegen.
2. **Vorschau** zeigt, was passieren würde. Mit **Ausführen** wird es umgesetzt.
3. Über **Bearbeiten …** lassen sich Paare später ändern. Zeigt ein Paar danach auf andere Ordner,
   beginnt es mit einem neuen Stand, wie bei einem neuen Paar.

### Wichtig beim ersten Lauf

Beim ersten Lauf eines Paars kennt SyncToyNext noch keine Vorgeschichte:

- **Synchronize:** Fehlende Dateien werden auf die jeweils andere Seite kopiert. Gibt es eine Datei
  auf beiden Seiten in unterschiedlichen Versionen, gewinnt die **neuere**.
- **Echo/Contribute:** Bei unterschiedlichen Versionen gewinnt die **linke** Seite.

Gelöscht wird beim ersten Lauf nie etwas. Am besten vorher die **Vorschau** ansehen.

---

## Kommandozeile und Zeitsteuerung

Wie bei SyncToy gibt es keinen eingebauten Zeitplaner. Für regelmäßige Läufe startet die
Windows-Aufgabenplanung das Kommandozeilenprogramm. Unter **Extras → Zeitgesteuert ausführen**
zeigt die Oberfläche die passenden Befehle zum Kopieren an.

```text
SyncToyNextCmd -R                 Alle Ordnerpaare ausführen, die für "Alle ausführen" aktiv sind
SyncToyNextCmd -R "Name"          Ein bestimmtes Ordnerpaar ausführen (auch: -R"Name")
SyncToyNextCmd -P ["Name"]        Nur Vorschau anzeigen, nichts ändern
SyncToyNextCmd -L                 Alle Ordnerpaare auflisten
SyncToyNextCmd -?                 Hilfe

--force                           Auch ausführen, wenn eine Seite plötzlich leer ist
--config "Datei"                  Andere Datei mit Ordnerpaaren verwenden
--no-log                          Kein Protokoll schreiben
```

Rückgabewert: `0` = erfolgreich, `1` = Fehler bei mindestens einem Paar, `2` = falscher Aufruf.

---

## So funktioniert der Abgleich

Damit SyncToyNext eine **gelöschte** Datei von einer **neuen** unterscheiden kann, merkt es sich
nach jedem Lauf den Stand beider Ordner. Dafür legt es, wie SyncToy mit seinen `SyncToy_*.dat`,
in jedem der beiden Ordner eine versteckte Datei `SyncToyNext_<Kennung>.dat` an. Diese Dateien
bitte nicht löschen. Ohne sie verhält sich das nächste Ausführen wie ein erster Lauf.

- Eine Datei gilt auf einer Seite als **geändert**, wenn sie neu ist oder sich Größe bzw.
  Änderungszeit gegenüber dem gemerkten Stand geändert haben.
- Beim Vergleich zwischen den beiden Seiten gilt eine Toleranz von 2 Sekunden, weil FAT- und
  exFAT-Datenträger (viele USB-Sticks) Zeiten nur auf 2 Sekunden genau speichern.
- Eine **Umbenennung** wird erkannt, wenn genau eine Datei verschwunden und genau eine neue mit
  identischer Größe und Änderungszeit aufgetaucht ist. Leere Dateien werden dabei nicht berücksichtigt.
- Wurde eine Datei **auf beiden Seiten** geändert, gewinnt bei Synchronize die neuere Version. Die
  ältere landet im Papierkorb. Haben beide Versionen dieselbe Änderungszeit, wird die Datei
  übersprungen und als Konflikt gemeldet.
- Wurde eine Datei auf einer Seite gelöscht und auf der anderen geändert, gewinnt die Änderung.
- Ordner werden nur gelöscht, wenn sie danach leer sind.

Die Ordnerpaare stehen in `%LOCALAPPDATA%\SyncToyNext\FolderPairs.json`.

---

## Bekannte Einschränkungen

- **Papierkorb:** Auf Laufwerken ohne Papierkorb (Netzwerkfreigaben, viele USB-Sticks) löscht
  Windows endgültig. Das war bei SyncToy genauso.
- **Groß-/Kleinschreibung:** Eine reine Änderung der Schreibweise (z. B. `bericht.txt` → `Bericht.txt`)
  wird nicht auf die andere Seite übertragen.
- **Sommer-/Winterzeit:** Auf FAT-Datenträgern können sich Zeitstempel bei der Zeitumstellung um eine
  Stunde verschieben. Dann werden Dateien unnötig erneut kopiert. Auch das trat bei SyncToy auf.
- **Kein Import:** Ordnerpaare aus dem originalen SyncToy werden nicht übernommen.

---

## Selbst bauen

Voraussetzung ist das [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build SyncToyNext.sln
dotnet test SyncToyNext.sln
dotnet run --project src/SyncToyNext.App
```

Fertige Einzeldateien wie im Workflow:

```powershell
dotnet publish src/SyncToyNext.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
dotnet publish src/SyncToyNext.Cmd -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
```

### Projektaufbau

| Projekt | Inhalt |
|---|---|
| `src/SyncToyNext.Core` | Sync-Engine ohne Oberfläche: Einlesen, Filter, Statusdateien, Planung, Ausführung, Papierkorb, Protokoll |
| `src/SyncToyNext.App` | WPF-Oberfläche (`SyncToyNext.exe`) |
| `src/SyncToyNext.Cmd` | Kommandozeile (`SyncToyNextCmd.exe`) |
| `tests/SyncToyNext.Core.Tests` | Automatische Tests der Engine mit echten Ordnern |

---

## Lizenz

[MIT](LICENSE)
