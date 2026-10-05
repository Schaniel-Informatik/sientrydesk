<#
.SYNOPSIS
    Zeigt Einstellungen und Installationsstatus der SI-EntryDesk-Apps in Intune. Nur lesende Abfragen.

.DESCRIPTION
    Braucht das Modul Microsoft.Graph.Authentication 2.36.1 (neuere Versionen verlangen PowerShell mit .NET 10).
    Beim ersten Aufruf Anmeldung mit Gerätecode (2 Minuten Zeit), danach still aus dem Schlüsselbund.
    Verwendet nur GET und den Bericht retrieveDeviceAppInstallationStatusReport, ändert nichts in Intune.

.EXAMPLE
    pwsh -NoProfile -File tools/intune-check.ps1
#>
Import-Module Microsoft.Graph.Authentication -RequiredVersion 2.36.1
Connect-MgGraph -Scopes 'DeviceManagementApps.Read.All','DeviceManagementManagedDevices.Read.All' -UseDeviceCode -ContextScope CurrentUser -NoWelcome
$beta = 'https://graph.microsoft.com/beta'
function Get-Graph($uri) { Invoke-MgGraphRequest -Method GET -Uri $uri }
function Get-All($uri) { $r = Get-Graph $uri; $r.value; while ($r.'@odata.nextLink') { $r = Get-Graph $r.'@odata.nextLink'; $r.value } }
$apps = Get-All "$beta/deviceAppManagement/mobileApps?`$filter=isof('microsoft.graph.win32LobApp')" | Where-Object { $_.displayName -match 'Entry ?Desk' }
if (-not $apps) { 'Keine Apps mit EntryDesk im Namen.'; return }
foreach ($summary in $apps) {
    $a = Get-Graph "$beta/deviceAppManagement/mobileApps/$($summary.id)"
    ''
    "== $($a.displayName)  [$($a.id)]"
    "  App-Version:        $($a.displayVersion)"
    "  Paket:              $($a.fileName), $([math]::Round($a.size / 1MB, 1)) MB, Inhalt Version $($a.committedContentVersion), Status $($a.uploadState) / $($a.publishingState)"
    "  Installation:       $($a.installCommandLine)"
    "  Deinstallation:     $($a.uninstallCommandLine)"
    "  Ausführen als:      $($a.installExperience.runAsAccount), Neustart: $($a.installExperience.deviceRestartBehavior)"
    "  Architektur:        $($a.allowedArchitectures) $($a.applicableArchitectures), mind. Windows $($a.minimumSupportedWindowsRelease)"
    foreach ($rule in $a.rules) {
        switch -Wildcard ($rule.'@odata.type') {
            '*PowerShellScriptRule' {
                $text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($rule.scriptContent))
                $version = ([regex]::Match($text, "\`$version\s*=\s*\[version\]\s*'([^']+)'")).Groups[1].Value
                $hash = ([regex]::Match($text, "\`$configHash\s*=\s*'([^']+)'")).Groups[1].Value
                "  Erkennung:          Skript ($($rule.ruleType)), 32 Bit: $($rule.runAs32Bit), Signatur: $($rule.enforceSignatureCheck), Version $version, Konfig-Hash $($hash.Substring(0, [Math]::Min(12, $hash.Length)))…"
            }
            '*RegistryRule' {
                "  Erkennung:          Registrierung $($rule.keyPath) \ $($rule.valueName), $($rule.operationType) $($rule.operator) '$($rule.comparisonValue)', 32 Bit: $($rule.check32BitOn64System)"
            }
            default { "  Regel:              $($rule.'@odata.type') ($($rule.ruleType))" }
        }
    }
    foreach ($rel in (Get-All "$beta/deviceAppManagement/mobileApps/$($a.id)/relationships")) {
        if ($rel.targetType -eq 'child') { "  Abhängigkeit:       $($rel.targetDisplayName) ($($rel.dependencyType))" }
    }
    foreach ($as in (Get-All "$beta/deviceAppManagement/mobileApps/$($a.id)/assignments")) {
        $group = $as.target.groupId
        if ($group) { try { $group = (Get-Graph "https://graph.microsoft.com/v1.0/groups/$group`?`$select=displayName").displayName } catch { } }
        "  Zuweisung:          $($as.intent) -> $group ($($as.target.'@odata.type' -replace '#microsoft.graph.', ''))"
    }

    $tmp = New-TemporaryFile
    Invoke-MgGraphRequest -Method POST -Uri "$beta/deviceManagement/reports/retrieveDeviceAppInstallationStatusReport" `
        -Body (@{ filter = "(ApplicationId eq '$($a.id)')" } | ConvertTo-Json) -ContentType 'application/json' -OutputFilePath $tmp
    $report = Get-Content -Raw $tmp | ConvertFrom-Json
    Remove-Item $tmp
    $cols = @($report.Schema.Column)
    if (-not $report.Values) { '  Geräte:             noch keine Meldung' }
    foreach ($row in $report.Values) {
        $h = @{}; for ($i = 0; $i -lt $cols.Count; $i++) { $h[$cols[$i]] = $row[$i] }
        "  Gerät:              {0}: {1} {2} Fehler {3}, Version {4}, {5} UTC" -f $h.DeviceName, $h.AppInstallState_loc,
            $h.AppInstallStateDetails_loc, $(if ($h.HexErrorCode) { $h.HexErrorCode } else { '–' }), $h.AppVersion, $h.LastModifiedDateTime
    }
}
