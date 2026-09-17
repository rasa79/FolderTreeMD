# FolderTreeMD

> Right-click any folder in Windows 11 and get its full recursive content as a clean, editable Markdown nested list — on your clipboard or in a file.

FolderTreeMD is a small Windows 11 utility for anyone who needs to document a directory tree: developers writing docs, people archiving project structures, or anyone who has ever typed out a folder listing by hand. One click produces a nested bullet list with file sizes and attributes, ready to paste into a README, a wiki, or a note.

```text
- [MyProject]
    - [src]
        - [Core]
            - Engine.cs (12.4 KB) [A]
        - App.cs (3.1 KB) [A]
    - README.md (2.0 KB) [A]
```

## Features

* **Explorer context menu** — right-click a folder *or* the empty space inside it → `Copy folder content as markdown`. The listing lands on the clipboard, confirmed by a toast.
* **Classic WPF window** — pick a folder, tune the listing, copy or save.
* **Headless CLI** — `FolderTreeMD.exe --folder "<path>" [--to "<output.md>"]`, scriptable, with well-defined exit codes.
* **Real Markdown grammar** — `- ` bullets, two-space indents, folders in `[brackets]`, sizes as `(N B)` / `(N.N KB)`, attributes `[H R S A]`, folders first, ordinal case-insensitive ordering, UTF-8 without BOM.
* **Honest about failures** — a folder you cannot read appears in the listing marked as access-denied; its siblings still list. Nothing is silently swallowed.
* **Safe with links and loops** — symlinks are shown as `(link → target)` and junction cycles are detected, not followed forever.
* **Two distributions** — an MSIX sparse package (entry in the Windows 11 *top-level* menu) and a zero-install portable single exe (entry under *Show more options*). They coexist without interfering.

## Tech Stack

* **Language:** C# on .NET 8
* **UI:** WPF (`net8.0-windows10.0.17763.0`)
* **Shell integration:** `IExplorerCommand` COM server, registered through an MSIX sparse package (`windows.fileExplorerContextMenus`)
* **Packaging:** hand-written `AppxManifest.xml` + MakeAppx + SignTool (no `.wapproj`); single-file publish profile for the portable build
* **Tests:** xUnit (161 tests), `dotnet format` for lint
* **One external dependency:** `Microsoft.Toolkit.Uwp.Notifications` (toast notifications)

## Requirements

To **use** the portable build:

* Windows 11 — nothing else. No .NET runtime, no installer, no admin rights.

To **build** from source:

* Windows 11 + .NET 8 SDK
* For the MSIX package additionally: Windows SDK signing/packaging tools (`MakeAppx.exe`, `signtool.exe`) — see `docs/msix-signing.md`

## Getting Started

### Option A — the portable single file (recommended)

```powershell
git clone https://github.com/rasa79/FolderTreeMD.git
cd FolderTreeMD
dotnet publish src/FolderTreeMD/FolderTreeMD.csproj -p:PublishProfile=PortableSingleFile
```

Download: grab `FolderTreeMD.exe` from [Releases](https://github.com/rasa79/FolderTreeMD/releases) (~79 MB, self-contained, icon embedded). Copy it anywhere — another PC, a USB stick — and **run it once**. That first run opens the window and registers the right-click entry. Done.

> Why a publish profile? Plain `PublishSingleFile` leaves WPF's native libraries next to the exe — eight files instead of one. The profile adds the four switches that make it truly single-file (measured, not guessed).

### Option B — build and run from source

```powershell
dotnet build FolderTreeMD.sln
dotnet run --project src/FolderTreeMD
```

### Option C — the MSIX package (top-level Windows 11 menu)

```powershell
pwsh src/FolderTreeMD.Package/build-package.ps1
```

Builds, packs, signs and registers the sparse package. This needs a code-signing certificate trusted on the machine, and **a sign-out (or reboot) after the first registration** — restarting Explorer is not enough (the shell caches verbs per session; `KNOWN_LIMITATIONS.md` L16). Full walkthrough: `docs/msix-signing.md`.

## Usage

**Right-click (Explorer):** folder icon or empty space inside a folder → `Copy folder content as markdown` (top-level menu with the MSIX package; under *Show more options* with the portable exe) → the listing is on your clipboard.

**Command line:**

```powershell
# listing to the clipboard, with a toast confirmation
FolderTreeMD.exe --folder "C:\Users\me\Documents\MyProject"

# listing to a file instead
FolderTreeMD.exe --folder "C:\Users\me\Documents\MyProject" --to "listing.md"

# register / remove the right-click entry manually
FolderTreeMD.exe --install
FolderTreeMD.exe --uninstall
```

Exit codes: `0` success · `1` malformed invocation or a folder that does not exist · `2` the work itself failed (enumeration, write, clipboard, registry).

The portable exe is **self-repairing**: move it, run it once from its new home, and the menu entry is rewritten to point at the new path. The check runs on every start and writes nothing when the registry already matches.

## Architecture

One tested, headless core drives every entry point — the window, the CLI, and the Explorer command are three thin shells over the same engine:

```text
WPF window  ─┐
CLI runner  ─┼──►  FolderTreeMD.Core  (listing engine, pure CLI parser, registration plan)
COM server  ─┘         ▲
 (IExplorerCommand)    └── xUnit tests target only this project
```

* `src/FolderTreeMD.Core` — the recursive listing engine, the frozen Markdown grammar, the pure command-line parser, and the portable-registration plan as testable data. Targets plain `net8.0`, holds no Windows-only APIs.
* `src/FolderTreeMD` — the WPF window, the CLI runner, and the out-of-process `IExplorerCommand` COM server.
* `src/FolderTreeMD.Package` — the sparse-package manifest and the build/pack/sign/register script.
* `tests/FolderTreeMD.Core.Tests` — 161 tests against the Core project, including real-ACL access-denied fixtures.

## Design Decisions

### One core, three shells

Every behavior that can be wrong — the grammar, the parser, the registration rules — lives in `FolderTreeMD.Core` as pure, testable code. The WPF project is deliberately untestable-by-design and kept thin; that trade-off is what lets a 161-test suite cover everything that matters in under a second.

### Out-of-process COM for the modern menu (D26)

Microsoft's documentation shows `windows.fileExplorerContextMenus` extensions as in-proc native DLLs (`com:SurrogateServer`) — and managed in-proc DLLs are unsupported. FolderTreeMD registers an out-of-process `com:ExeServer` instead: the same exe, started with `-Embedded`. It works (measured on Windows 11 25H2), keeps the whole product in one C# codebase, and needed no C++ shim. The one catch: a *first-time* registration only appears after a new shell session.

### The portable exe never clobbers the package (D28)

Both distributions can be installed at once. The portable build self-registers its classic `HKCU` verbs on every start — **unless** the process belongs to this app's own MSIX package (`Package.Id.Name == "FolderTreeMD.Sparse"`), in which case it writes nothing. Keying on the package *name* rather than the family name keeps the rule stable even if the signing certificate is re-created (the family name carries the publisher hash).

### Iterative engine with explicit failure semantics

Enumeration is iterative (no recursion depth limit), follows an explicit options object with `IgnoreInaccessible = false` — because the framework default silently swallowed access-denied folders, which is exactly the failure a documentation tool must never hide.

## Testing

```powershell
dotnet test FolderTreeMD.sln
```

161 tests (one skipped by design) against the Core project: grammar conformance tables, the CLI parser's refusal matrix, the registration plan's idempotence and repair rules, and real-ACL fixtures that prove access-denied folders surface honestly instead of vanishing.

## Known Limitations

* **Windows 11 only**, File Explorer only — third-party file managers never show shell-extension verbs, on any platform, by design.
* The portable exe's entry lives under **Show more options** (the classic menu); the Windows 11 top-level menu requires MSIX package identity — a platform rule, not a choice.
* The entry covers **folders and folder background only** — right-clicking a *file* offers nothing.
* After a *first-time* MSIX registration, **sign out or reboot**; restarting Explorer is not enough.

The full, evidence-backed list — including why each one stands — is in `KNOWN_LIMITATIONS.md`.

## Roadmap

* [x] Core listing engine with frozen Markdown grammar
* [x] WPF window, headless CLI, exit-code contract
* [x] MSIX sparse package with the Windows 11 top-level menu entry
* [x] Portable single-file exe with self-registration and self-repair
* [ ] No committed plans beyond this — the tool does what it set out to do

## Contributing

Contributions are welcome.

1. Fork the repository.
2. Create a feature branch.
3. Make your changes.
4. Add or update tests (`dotnet test` and `dotnet format --verify-no-changes` must pass).
5. Create a pull request.

A note for the curious: this project was built with an agent-loop workflow — every milestone implemented by one model, independently reviewed by another, and approved by a human before the next began. The `DECISIONS.md` and `KNOWN_LIMITATIONS.md` files in this repo are real artifacts of that process, kept because they document the product's engineering, not the process itself.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file.

## Author

**YOUR-GITHUB-USERNAME**

* GitHub: https://github.com/rasa79
