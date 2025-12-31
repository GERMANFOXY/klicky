# Klicky

Ein schlanker Auto-Clicker als WPF-Desktop-App mit violettem UI. Standard-Hotkey ist **F6** (global), sodass Start/Stop auch außerhalb des Fensters funktioniert.

## Features
- Start/Stop per Button oder globalem Hotkey (F6)
- Konfigurierbare Klickrate (CPS)
- Links- oder Rechtsklick
- Optional feste Zielposition oder aktueller Mauszeiger
- Moderner violetter Look

## Nutzung
1. App starten (`dotnet run` aus dem Projektordner oder die generierte EXE im `bin`-Ordner ausführen).
2. CPS einstellen (1–50) und Button auswählen.
3. Haken setzen, ob immer der aktuelle Cursor geklickt werden soll.
4. Mit "Starten" oder F6 den Auto-Clicker toggeln.
5. "Stoppen" oder erneut F6 zum Beenden.

## Build
- Voraussetzungen: .NET SDK 10.0 (oder aktueller) installiert.
- Debug-Build: `dotnet build`
- Starten aus dem Terminal: `dotnet run`
- Release-Build: `dotnet publish -c Release -r win-x64 --self-contained false`

## Hinweise
- Falls der Hotkey F6 schon von einem anderen Tool belegt ist, kann die Registrierung scheitern; dann erscheint ein Hinweis im Statusfeld.
- Auto-Clicker auf eigene Verantwortung nutzen.
