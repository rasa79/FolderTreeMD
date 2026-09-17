# MSIX sparse package — building, signing, trusting and installing

> For `PLAN.md` M7 and `DECISIONS.md` D25. The package gives the unpackaged `FolderTreeMD.exe` a package
> identity and registers its Explorer context-menu command (`UI_SPEC.md` §8). There is no Visual Studio
> packaging project: `src/FolderTreeMD.Package/AppxManifest.xml` is the package, `build-package.ps1` packs
> and signs it with the Windows SDK's `MakeAppx.exe` and `SignTool.exe`.
>
> Everything below was run on Windows 11 25H2 (10.0.26200) with Windows SDK 10.0.19041; the commands are
> the ones the milestone report quotes.

## 1. What the package contains, and what it does not

| In the package | Outside the package (`-ExternalLocation`) |
| --- | --- |
| `AppxManifest.xml`, `Assets\Square44x44Logo.png`, `Assets\Square150x150Logo.png`, `Assets\StoreLogo.png`, and the `AppxBlockMap.xml` / `[Content_Types].xml` MakeAppx generates | the published app: `FolderTreeMD.exe`, its `.dll`s, the .NET runtime configuration and a copy of `Assets\` |

`uap10:AllowExternalContent` is what lets the app's binaries stay outside; the manifest's `Executable` and
the `com:ExeServer` both resolve against the external location given at registration time. That is why
`MakeAppx` needs `/nv` — without it, packing fails because the manifest names a file the package does not
contain:

```
MakeAppx : error: Package creation failed.
MakeAppx : error: The file "FolderTreeMD.exe" referenced in the package manifest cannot be found.
```

## 2. Build the package

```powershell
cd src\FolderTreeMD.Package
# Pack only (no certificate needed); useful to validate a manifest edit:
./build-package.ps1 -SkipSign
# Pack and sign, taking the key from the certificate store (no password on any command line):
./build-package.ps1 -CertificateThumbprint <thumbprint>
```

The script publishes the app to `artifacts\msix\app` (the external location), stages the payload in
`artifacts\msix\payload` (manifest + assets only, so nothing else can leak into the package), packs
`artifacts\msix\FolderTreeMD.Sparse.msix` and signs it. `artifacts/` is gitignored: it holds build output,
the development `.pfx` and the `.cer`.

> The certificate password is never stored in this repository and the script takes it as a parameter.
> A `.pfx` is a private key — treat it as a credential.

## 3. Create the development certificate (once per machine)

`Identity/@Publisher` in the manifest must equal the certificate's **Subject**, so the two are changed
together. For local development, `CN=FolderTreeMD Dev`:

```powershell
$password = '<choose one>'                       # never commit this
$secure = ConvertTo-SecureString -String $password -AsPlainText -Force
$cert = New-SelfSignedCertificate `
  -Type Custom `
  -Subject 'CN=FolderTreeMD Dev' `
  -KeyUsage DigitalSignature `
  -FriendlyName 'FolderTreeMD Dev (sparse package signing)' `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
Export-PfxCertificate -Cert $cert -FilePath artifacts\msix\FolderTreeMD.Dev.pfx -Password $secure
Export-Certificate  -Cert $cert -FilePath artifacts\msix\FolderTreeMD.Dev.cer
```

`2.5.29.37` is the code-signing EKU; `2.5.29.19` (basic constraints) is deliberately empty, which is what
a self-signed development certificate wants.

## 4. Trust the certificate (once per machine)

`Add-AppxPackage` refuses an untrusted signature with `0x800B0109`, so the public `.cer` has to be trusted
before the package is registered:

```powershell
# Per-user (no elevation). Works for a per-user Add-AppxPackage.
Import-Certificate -FilePath artifacts\msix\FolderTreeMD.Dev.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople

# Machine-wide (elevated prompt). Needed when the package is registered for all users, or from a process
# that the deployment service validates machine-wide.
Import-Certificate -FilePath artifacts\msix\FolderTreeMD.Dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

**Measured in this session (disclosed because it cost a round):** with the certificate trusted only in
`Cert:\CurrentUser\TrustedPeople` and a per-user `Add-AppxPackage`, registration still failed with
`0x800B0109` — *"The root certificate of the signature in the app package or bundle must be trusted."* The
deployment service validates against machine stores, so the machine-wide import (or Developer Mode, or a
certificate from a real CA) is what makes a self-signed package installable here. The session that built
the package ran at **Medium** integrity, so it could not perform the machine-wide import itself; see
`KNOWN_LIMITATIONS.md` L12.

Independently of the deployment check, `signtool verify /pa` fails for a self-signed package
("terminated in a root certificate which is not trusted"). **Handing the exported `.cer` to `/r` does not
change that** — measured in the M7 fix round: `signtool verify /pa /r <cer> <msix>` reports the same error,
because `/r` offers a *candidate* root for chain building but does not make it trusted. `verify /pa`
therefore passes only once the certificate is in a trusted **root** store (`Cert:\LocalMachine\Root` or
`Cert:\CurrentUser\Root`), which is a stronger and security-relevant change than trusting it in
`TrustedPeople`. For a self-signed development certificate, read a `verify /pa` failure as *"this root is
not trusted here"*, **not** as *"the package is unsigned"*: `build-package.ps1` checks what can be checked
locally — that the package carries the expected certificate's signature, via
`Get-AuthenticodeSignature` — and leaves the trust decision to `Add-AppxPackage`.

## 5. Register and remove the package

```powershell
# Register for the current user, pointing at the published app folder:
Add-AppxPackage -Path artifacts\msix\FolderTreeMD.Sparse.msix -ExternalLocation (Resolve-Path artifacts\msix\app)

# Verify:
Get-AppxPackage FolderTreeMD.Sparse | Format-List Name, PackageFullName, PackageFamilyName, InstallLocation, EffectiveExternalLocation

# Remove:
Get-AppxPackage FolderTreeMD.Sparse | Remove-AppxPackage
```

`-ExternalLocation` must be the folder that actually holds `FolderTreeMD.exe`; if it is wrong the package
registers but the COM server cannot start, and the context-menu entry does nothing.

**Removing the package deletes its data.** `Remove-AppxPackage` takes the package's virtualized store with
it — `%LOCALAPPDATA%\Packages\FolderTreeMD.Sparse_tzy3t6t3yzs9p\`, including `LocalCache\Local\FolderTreeMD\embedded.log`,
the `-Embedded` server's diagnostic (see §6). Copy anything you want to keep out of that tree **before**
removing the package; after it, the files are gone and the next registration starts with an empty store.

Machine-wide (elevated) registration and removal, if ever needed:

```powershell
Add-AppxPackage -Stage artifacts\msix\FolderTreeMD.Sparse.msix -ExternalLocation (Resolve-Path artifacts\msix\app)
Add-AppxProvisionedPackage -Online -PackagePath artifacts\msix\FolderTreeMD.Sparse.msix
```

## 6. After installing

- **Sign out and back in (or reboot) — restarting Explorer is not enough for a first-time registration.**
  Measured on Windows 11 25H2: with a fresh registration, `Add-AppxPackage -ForceUpdateFromAnyVersion` plus
  two Explorer restarts (fresh process ids) left the entry missing and the instrumented server log with no
  shell activation at all; the entry appeared at the next right-click after a **sign-out**, with the shell's
  activations in the log. The same family of behaviour shows in the packaged-COM class resolution: in a
  session where the package had been unregistered and re-registered, `CoCreateInstance` on the registered
  CLSID returned `REGDB_E_CLASSNOTREG` — as it did for Microsoft's own `Microsoft.DesktopAppInstaller` and
  for PowerToys' `PowerRenameContextMenu`, from both an unpackaged client and a packaged context
  (`KNOWN_LIMITATIONS.md` L16, `STATUS.md` §2.5). A reboot is the stronger reset if a sign-out does not do it.
  Note what that failure does *not* mean: it is a limitation of **unpackaged** clients as probes, not of the
  registration. Measured in the same round, the shell activated a package that had just been re-registered
  under an identity the session already knew, with no sign-out at all — so sign out when the entry is
  *missing*, rather than before every test.
- Right-click a **folder**, or the **empty space inside a folder view** — the entry
  `Copy folder content as markdown` appears in the Windows 11 top-level menu (not under "Show more options").
  Clicking it copies that folder's listing to the clipboard and shows the §7 toast. On the background there
  is no selection, so the command asks the shell's site for the folder being viewed
  (`KNOWN_LIMITATIONS.md` L17).
- The `-Embedded` server appends a diagnostic to
  `%LOCALAPPDATA%\Packages\FolderTreeMD.Sparse_tzy3t6t3yzs9p\LocalCache\Local\FolderTreeMD\embedded.log`
  (`%LOCALAPPDATA%\FolderTreeMD\embedded.log` as the app asks for it — package identity redirects the write).
  It records the process start and its parent, the COM registration results, every `IExplorerCommand` call and
  every step of the background surface's site route; that file is what says whether the shell reached the
  class at all.
- If the entry is missing, check the deployment log:
  `Get-AppPackageLog -ActivityID <id from the Add-AppxPackage error>` or Event Viewer →
  *Applications and Services Logs → Microsoft → Windows → AppxDeployment-Server*.

## 7. Why this is not a Windows Application Packaging Project

A `.wapproj` needs `Microsoft.DesktopBridge.*` MSBuild targets that ship with Visual Studio's "Windows
application packaging project" component. The build machine for this milestone has **Visual Studio 2019
Build Tools only** — no DesktopBridge targets anywhere in the VS or .NET SDK trees — so the project type
could not be built, and an unbuildable project file would be an unverifiable artifact. `MakeAppx` produces
the same package, and `DECISIONS.md` D25 records the deviation, the evidence and the alternatives.

## 8. Common failures

| Symptom | Cause | Fix |
| --- | --- | --- |
| `0x800B0109` — root certificate not trusted | the signing certificate is not trusted where the deployment service looks | import the `.cer` into `Cert:\LocalMachine\TrustedPeople` (elevated), or use Developer Mode / a trusted CA certificate |
| `0x80073CF9` — version already registered | the same `Identity/@Version` is installed | `Get-AppxPackage FolderTreeMD.Sparse \| Remove-AppxPackage`, then register again |
| `0x80073CF6` — manifest invalid | manifest XML or a required attribute is wrong | pack with `build-package.ps1 -SkipSign`; MakeAppx reports the schema error |
| Registration succeeds, no menu entry | the shell session has not reloaded the registration, or the verb's CLSID differs from `com:Class/@Id` | **sign out and back in (or reboot)** — an Explorer restart is not enough for a first-time registration; then check the manifest and `ExplorerCommandInfo.ClsidString` agree |
| Entry appears, click does nothing | `-ExternalLocation` does not point at the folder holding `FolderTreeMD.exe`, or the COM server failed to register (it exits with code 2) | re-register with the correct `-ExternalLocation`; run `FolderTreeMD.exe -Embedded` by hand and check it stays alive |
