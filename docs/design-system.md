# Design system

One source of colour, one library of controls, one list of pages. A page that follows these rules needs no visual rework later.

## The chain

`Components/Theme/DesignSystem.cs` (the only place hex colours live) → `WaymarkTheme.cs` (MudBlazor palette) → `--mud-palette-*` → `wwwroot/css/theme.css` (`--wm-*` tokens). Dark is the default; light follows the toggle. The terminal keeps its own always-dark palette in `terminal.css`.

## CSS layers (`wwwroot/css/`)

| File | Holds |
|---|---|
| `theme.css` | tokens only |
| `base.css` | reset, type, focus ring |
| `mud-overrides.css` | the only file that may select `.mud-*` |
| `mud-widths.css` | width scale for wrapped inputs |
| `shell.css` | app bar, menu, page frame |
| `components.css` | Ui library and pattern styles |
| `terminal.css` | console and live-log palette |
| `pages.css` | small page-level layout helpers |

## Rules

- Controls are pills (radius 999); cards 14, panels 16.
- One primary button per view. Danger is outlined red, never filled.
- Status is a dot plus a word, never colour alone. Tags are grey unless they state something.
- Auto on a code-running tool is amber (`auto run`).
- Pages never set `style=`, colours or raw Mud controls. Add a class to `pages.css` or extend a Ui component.

## Components

- `Components/Ui`: `WmButton`, `WmIconButton`, `WmLinkButton`, `WmStatus`, `WmTag`, `WmSegmented`, `WmTextField`, `WmSelect`, `WmTabs`, `WmToggle`, `WmSwitch`. They wrap MudBlazor so a page cannot pick a variant or colour.
- `Components/Patterns`: `PageFrame` (title, back link, actions, `Embedded`), `SectionHeader`, `TrustChain` (Claude → Tunnel → guard → your code).
- `Components/Layout/NavCatalog.cs` is the single page list. The menu and the Ctrl K palette both read it. `Hidden` pages (Connectors, Vault) stay routable but out of the menu.

## Adding a page

1. `@page` route, then `<PageFrame Title="…">` around the content.
2. Build it only from Ui and Pattern components.
3. Register it in `NavCatalog.cs`.
4. Run `powershell -ExecutionPolicy Bypass -File scripts/ui-check.ps1` (works in Windows PowerShell and `pwsh`); it must print `ui-check: clean.`

## The check

`scripts/ui-check.ps1` fails on inline styles, raw Mud controls and buttons outside `Components/Ui`, the retired `wm-chip` class, hex colours outside `DesignSystem.cs`, and `wm-*` classes with no CSS definition. Allow-lists sit at the top of the script, each with a reason.
