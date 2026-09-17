<#
.SYNOPSIS
    Builds, signs and describes FolderTreeMD's sparse package (PLAN.md M7, DECISIONS.md D25).

.DESCRIPTION
    Produces the identity package that gives the unpackaged FolderTreeMD.exe a package identity and
    registers its Explorer context-menu command. There is no Visual Studio packaging project: the package
    is a folder holding AppxManifest.xml plus the logo assets, packed with MakeAppx.exe (the tool the
    .wapproj would have called) and signed with SignTool.

    Steps, in order:
      1. dotnet publish the WPF app to a stable folder - that folder is the package's *external location*,
         because the manifest declares uap10:AllowExternalContent and the app's binaries stay outside.
      2. Copy the logo assets next to the published app as well, because the manifest's VisualElements
         paths resolve against the installation directory (the external location) at runtime.
      3. Check the invariants that nothing at runtime checks: the manifest's CLSID and -Embedded argument
         against the code, Identity/@Publisher against the signing certificate, and the executable named by
         the manifest against the external location.
      4. MakeAppx.exe pack with /nv, which skips validating the paths that point outside the package.
      5. SignTool sign with the certificate from the certificate store, then check that the package carries
         that signature (chain trust is a deployment concern - see the note in the signing step).

    Signing never takes a password: the script signs from the store (SignTool /sha1), because a private-key
    password on a command line is readable by other processes and routinely captured in build logs.

    The certificate's Subject must match Identity/@Publisher in AppxManifest.xml ("CN=FolderTreeMD Dev").
    See docs/msix-signing.md for creating it, for trusting it and for registering the package.

.PARAMETER Configuration
    Build configuration to publish. Default Debug.

.PARAMETER CertificateSubject
    Subject of the signing certificate, as it appears in the certificate store. Default 'CN=FolderTreeMD Dev'.

.PARAMETER CertificateThumbprint
    Thumbprint to sign with, when more than one certificate matches the subject. Optional.

.PARAMETER CertificateStoreLocation
    Store to look in: CurrentUser (default) or LocalMachine. The private key must be there.

.PARAMETER SkipSign
    Pack only. Useful for validating the manifest without a certificate.

.EXAMPLE
    ./build-package.ps1 -SkipSign
.EXAMPLE
    ./build-package.ps1 -CertificateThumbprint 15E006D2504D106CA3C4F15ED013B8A3C437CC03
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Debug',
    [string] $CertificateSubject = 'CN=FolderTreeMD Dev',
    [string] $CertificateThumbprint,
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string] $CertificateStoreLocation = 'CurrentUser',
    [switch] $SkipSign
)

# LEARN[33] (M7, DECISIONS.md D25): the package is built by MakeAppx from a hand-written manifest instead of
# a Windows Application Packaging Project.
# Why: a .wapproj is a thin MSBuild wrapper that calls MakeAppx, and the wrapper needs the
#   Microsoft.DesktopBridge.* targets that only Visual Studio's packaging component installs (verified
#   absent on this machine: a recursive search of both Visual Studio trees and the .NET SDK finds none).
#   A project file that cannot be built here could not be verified either, and this project does not ship
#   unverified artifacts.
# Alternatives considered: (a) install Visual Studio with the packaging workload — rejected for this
#   milestone: a multi-gigabyte machine change that the plan does not ask for, and the deliverable is the
#   package, not the project file; (b) commit a .wapproj for other machines while building with MakeAppx
#   here — rejected: an unverifiable second build path that would silently rot; (c) hand-edit an MSIX by
#   zipping files — rejected outright: AppxBlockMap.xml and [Content_Types].xml must be correct, and
#   MakeAppx is the supported tool that writes them (and validates the manifest schema while packing).
# Pros: the manifest is the single source of truth and is validated by the same tool that packs it; the
#   route works with the Windows SDK alone, which is already a build prerequisite.
# Cons: no Visual Studio project integration (no F5 deploy, no designer for the manifest), and the pack
#   step lives in this script rather than in the solution build.
# Revised in the M7 fix round: the invariants nothing at runtime checks are asserted here before packing or
#   signing (CLSID in both manifest places, the -Embedded argument, the publisher/subject pair, and the
#   executable's presence in the external location), signing is done from the certificate store so no
#   password reaches a command line, the SDK tools are chosen by parsed version, and the signature
#   verification step was replaced by a signature-identity check: measured, neither /pa nor /pa /r <cer> can
#   pass for a self-signed development certificate, and its exit code is checked where native tools remain.
# See also: LEARN[32]
$ErrorActionPreference = 'Stop'

$packageDirectory = $PSScriptRoot
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $packageDirectory)
# Outside the package folder on purpose: only the manifest and the assets may end up in the package, so the
# payload is staged in its own directory rather than packing this directory and excluding things from it.
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts\msix'
$payloadDirectory = Join-Path $artifactsDirectory 'payload'
$publishDirectory = Join-Path $artifactsDirectory 'app'
$packagePath = Join-Path $artifactsDirectory 'FolderTreeMD.Sparse.msix'
$manifestPath = Join-Path $packageDirectory 'AppxManifest.xml'
$explorerCommandPath = Join-Path $repositoryRoot 'src\FolderTreeMD\ExplorerCommand.cs'
$shellRegistrationPath = Join-Path $repositoryRoot 'src\FolderTreeMD.Core\ShellRegistration.cs'

# The SDK tools are chosen by the parsed version component of their path, not lexically: "10.0.9999.0" sorts
# after "10.0.22000.0" as a string but is the older SDK.
function Select-SdkTool {
    param([Parameter(Mandatory)][string] $Name)

    $candidates = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$Name" -ErrorAction SilentlyContinue
    if (-not $candidates) { return $null }

    return $candidates |
        ForEach-Object {
            [pscustomobject]@{
                Path = $_.FullName
                Version = [version]$_.Directory.Parent.Name
            }
        } |
        Sort-Object Version -Descending |
        Select-Object -First 1 -ExpandProperty Path
}

$makeAppx = Select-SdkTool -Name 'makeappx.exe'
$signTool = Select-SdkTool -Name 'signtool.exe'

if (-not $makeAppx) { throw 'MakeAppx.exe not found. Install the Windows 10/11 SDK (10.0.19041 or later).' }
if (-not $SkipSign -and -not $signTool) { throw 'SignTool.exe not found. Install the Windows SDK, or pass -SkipSign.' }

function Read-Attribute {
    param(
        [Parameter(Mandatory)][xml] $Document,
        [Parameter(Mandatory)][string] $ElementName,
        [Parameter(Mandatory)][string] $AttributeName
    )

    $node = $Document.SelectSingleNode("//*[local-name()='$ElementName']")
    if (-not $node) { throw "AppxManifest.xml has no <$ElementName> element." }
    return $node.GetAttribute($AttributeName)
}

Write-Host "==> Checking the manifest against the code and the certificate"
[xml] $manifest = Get-Content $manifestPath -Raw
$source = Get-Content $explorerCommandPath -Raw
$coreSource = Get-Content $shellRegistrationPath -Raw

$codeClsid = [regex]::Match($source, 'ClsidString\s*=\s*"([^"]+)"').Groups[1].Value
$codeEmbeddedArgument = [regex]::Match($source, 'EmbeddedArgument\s*=\s*"([^"]+)"').Groups[1].Value
$codePackagedIdentityName = [regex]::Match($coreSource, 'PackagedIdentityName\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $codeClsid) { throw "Could not read ClsidString from $explorerCommandPath." }
if (-not $codeEmbeddedArgument) { throw "Could not read EmbeddedArgument from $explorerCommandPath." }
if (-not $codePackagedIdentityName) { throw "Could not read PackagedIdentityName from $shellRegistrationPath." }

$manifestClassId = Read-Attribute -Document $manifest -ElementName 'Class' -AttributeName 'Id'
$manifestArguments = Read-Attribute -Document $manifest -ElementName 'ExeServer' -AttributeName 'Arguments'
$manifestExecutable = Read-Attribute -Document $manifest -ElementName 'ExeServer' -AttributeName 'Executable'
$manifestPublisher = Read-Attribute -Document $manifest -ElementName 'Identity' -AttributeName 'Publisher'
$manifestIdentityName = Read-Attribute -Document $manifest -ElementName 'Identity' -AttributeName 'Name'

# Every Verb, not just the first: since D27 the extension carries one desktop5:Verb per surface (Directory and
# Directory\Background), and a SelectSingleNode lookup would check only the first of them — leaving the
# background verb's CLSID free to drift unnoticed (OCR round-4 finding 1).
$manifestVerbs = @($manifest.SelectNodes("//*[local-name()='Verb']"))

if (-not [string]::Equals($manifestClassId, $codeClsid, [StringComparison]::OrdinalIgnoreCase)) {
    throw "AppxManifest.xml com:Class/@Id is '$manifestClassId' but ExplorerCommandInfo.ClsidString is '$codeClsid'."
}
if ($manifestVerbs.Count -eq 0) {
    throw 'AppxManifest.xml declares no desktop5:Verb, so no entry could appear in the shell.'
}

foreach ($verb in $manifestVerbs) {
    $verbClsid = $verb.GetAttribute('Clsid')
    $verbType = $verb.ParentNode.GetAttribute('Type')
    if (-not [string]::Equals($verbClsid, $codeClsid, [StringComparison]::OrdinalIgnoreCase)) {
        throw "AppxManifest.xml desktop5:Verb for ItemType '$verbType' has Clsid '$verbClsid' but ExplorerCommandInfo.ClsidString is '$codeClsid'."
    }
}
if (-not [string]::Equals($manifestArguments, $codeEmbeddedArgument, [StringComparison]::OrdinalIgnoreCase)) {
    throw "AppxManifest.xml com:ExeServer/@Arguments is '$manifestArguments' but ExplorerCommandInfo.EmbeddedArgument is '$codeEmbeddedArgument'."
}

# The third lockstep value (M8 fix round 3, item 4): the app skips its portable self-registration only for the
# package whose name it compares against, so Identity/@Name and ShellRegistration.PackagedIdentityName must be
# the same string. Renaming the package without this constant would silently stop the skip — a packaged install
# writing classic-menu keys, which is L31 again.
if (-not [string]::Equals($manifestIdentityName, $codePackagedIdentityName, [StringComparison]::Ordinal)) {
    throw "AppxManifest.xml Identity/@Name is '$manifestIdentityName' but ShellRegistration.PackagedIdentityName is '$codePackagedIdentityName'."
}
Write-Host "    CLSID $codeClsid, argument $manifestArguments and identity name $manifestIdentityName match the code (com:Class/@Id, all $($manifestVerbs.Count) desktop5:Verb elements and Identity/@Name)"

$certificate = $null
if (-not $SkipSign) {
    $storePath = "Cert:\$CertificateStoreLocation\My"

    # Thumbprints copied out of the certificate UI often carry invisible characters (U+200E and friends);
    # normalising to hex digits here keeps that from turning into a misleading "no certificate found".
    $wantedThumbprint = if ($CertificateThumbprint) { ($CertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant() } else { $null }

    $certificateCandidates = @(Get-ChildItem $storePath |
        Where-Object { $_.Subject -eq $CertificateSubject -and $_.HasPrivateKey } |
        Where-Object { -not $wantedThumbprint -or $_.Thumbprint -eq $wantedThumbprint })

    if ($certificateCandidates.Count -eq 0) {
        throw "No certificate with private key and subject '$CertificateSubject' in $storePath. See docs/msix-signing.md §3."
    }
    if ($certificateCandidates.Count -gt 1) {
        throw "More than one certificate matches '$CertificateSubject' in $storePath; pass -CertificateThumbprint."
    }

    $certificate = $certificateCandidates[0]
    if ($certificate.Subject -ne $manifestPublisher) {
        throw "The signing certificate's subject is '$($certificate.Subject)' but Identity/@Publisher is '$manifestPublisher'; they must be identical."
    }
    Write-Host "    certificate $($certificate.Thumbprint) matches Identity/@Publisher"
}

Write-Host "==> Publishing the app to $publishDirectory (the package's external location)"
if (Test-Path $publishDirectory) { Remove-Item $publishDirectory -Recurse -Force }
dotnet publish (Join-Path $repositoryRoot 'src\FolderTreeMD\FolderTreeMD.csproj') `
    -c $Configuration `
    -o $publishDirectory `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host '==> Copying the logo assets into the external location'
Copy-Item (Join-Path $packageDirectory 'Assets') -Destination $publishDirectory -Recurse -Force

# /nv switches off MakeAppx's own check that the manifest's files exist (they are external by design), so the
# one file the verb cannot work without is checked here instead.
$externalExecutable = Join-Path $publishDirectory $manifestExecutable
if (-not (Test-Path $externalExecutable)) {
    throw "The manifest names '$manifestExecutable', but the external location has no such file: $externalExecutable"
}
Write-Host "    $manifestExecutable is present in the external location"

Write-Host '==> Staging the package payload (manifest + assets only)'
if (Test-Path $payloadDirectory) { Remove-Item $payloadDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
Copy-Item $manifestPath -Destination $payloadDirectory -Force
Copy-Item (Join-Path $packageDirectory 'Assets') -Destination $payloadDirectory -Recurse -Force

Write-Host "==> Packing $packagePath"
& $makeAppx pack /o /nv /d $payloadDirectory /p $packagePath
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE" }

if ($SkipSign) {
    Write-Host '==> Packed without signing (-SkipSign); no signature was verified'
}
else {
    Write-Host "==> Signing with the store certificate $($certificate.Thumbprint)"
    # No password anywhere: SignTool takes the key from the store. /sm selects the machine store, matching
    # the Cert:\LocalMachine\My lookup above — without it SignTool would search the user's store and fail.
    $signArguments = @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint)
    if ($CertificateStoreLocation -eq 'LocalMachine') { $signArguments += '/sm' }
    $signArguments += $packagePath

    & $signTool @signArguments
    if ($LASTEXITCODE -ne 0) { throw "SignTool sign failed with exit code $LASTEXITCODE" }

    # `signtool verify /pa` cannot pass here, and neither can `/pa /r <cer>`: measured, both report
    # "terminated in a root certificate which is not trusted" because a self-signed development certificate
    # is not a trusted *root* — giving the .cer to /r offers a candidate root, it does not trust it, and a
    # failing verify would only be a false negative for a package whose signature is intact. What can be
    # checked locally is that the package carries *our* signature and that the signature is not broken.
    #
    # The trust-related statuses a self-signed chain produces (UnknownError, NotTrusted) are expected and
    # pass; everything that means "the signature itself is wrong or missing" fails, because after dropping
    # `verify` this is the only integrity check left.
    $rejectedStatuses = @('NotSigned', 'HashMismatch', 'NotSupportedFileFormat', 'Incompatible')
    $signature = Get-AuthenticodeSignature -FilePath $packagePath

    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "The package is not signed by $($certificate.Thumbprint) (Get-AuthenticodeSignature reports '$($signature.Status)')."
    }
    if ($rejectedStatuses -contains $signature.Status.ToString()) {
        throw "The package's signature is invalid: Get-AuthenticodeSignature reports '$($signature.Status)'."
    }

    Write-Host "    signed by $($signature.SignerCertificate.Thumbprint); status '$($signature.Status)' (a trust-store outcome, expected for a self-signed development certificate; HashMismatch/NotSigned would have failed)"
}

Write-Host ''
Write-Host "Package:  $packagePath"
Write-Host "External: $publishDirectory"
Write-Host ''
Write-Host 'Register it for the current user with:'
Write-Host "  Add-AppxPackage -Path `"$packagePath`" -ExternalLocation `"$publishDirectory`""
Write-Host 'Remove it again with:'
Write-Host '  Get-AppxPackage FolderTreeMD.Sparse | Remove-AppxPackage'
