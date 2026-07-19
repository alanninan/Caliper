param(
    [Parameter(Mandatory)]
    [int]$AppPid,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "ui-test-results")
)

$ErrorActionPreference = "Continue"
$pass = 0
$fail = 0
$results = @()
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++
            $script:results += @{ name = $Name; status = "PASS" }
        } else {
            throw "$output"
        }
    } catch {
        $script:fail++
        $script:results += @{ name = $Name; status = "FAIL"; detail = "$_" }
    }
}

Test-UI "Chat workspace essentials" {
    winapp ui wait-for "ModelQuickSwitcher" -a $AppPid -t 5000
    winapp ui wait-for "PermissionModePicker" -a $AppPid -t 2000
    winapp ui wait-for "ToggleSessionsPaneButton" -a $AppPid -t 2000
    winapp ui wait-for "InspectorToggleButton" -a $AppPid -t 2000
    winapp ui wait-for "PromptTextBox" -a $AppPid -t 2000
}

Test-UI "Settings grouped sections are reachable" {
    winapp ui invoke "Settings" -a $AppPid
    foreach ($id in @(
        "GeneralSettingsNavItem", "ModelsProvidersSettingsNavItem", "AgentBehaviorSettingsNavItem",
        "ContextMemorySettingsNavItem", "ToolsSettingsNavItem", "SubagentsSettingsNavItem",
        "PermissionsSettingsNavItem", "ExecutionSettingsNavItem", "McpSettingsNavItem",
        "SearchSettingsNavItem", "AdvancedSettingsNavItem")) {
        winapp ui wait-for $id -a $AppPid -t 3000
    }
}

Test-UI "Scheduled run history is reachable" {
    winapp ui invoke "SchedulesNavigationItem" -a $AppPid
    winapp ui invoke "ScheduleHistoryModeItem" -a $AppPid
    winapp ui wait-for "RefreshScheduleHistoryButton" -a $AppPid -t 3000
}

Test-UI "Interactive controls have AutomationIds and names" {
    $elements = (winapp ui inspect -a $AppPid --interactive --json 2>$null | ConvertFrom-Json).elements
    $missing = @($elements | Where-Object {
        $_.type -match "Button|TextBox|ComboBox|CheckBox|ToggleSwitch|RadioButton" -and
        $_.name -notmatch "Minimize|Maximize|Close|System" -and
        -not $_.automationId
    })
    if ($missing.Count -gt 0) {
        throw (($missing | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ", ")
    }
}

winapp ui screenshot -a $AppPid -o (Join-Path $OutputDirectory "schedule-history.png") 2>$null
$results | ConvertTo-Json | Out-File (Join-Path $OutputDirectory "results.json")
Write-Host "Passed: $pass | Failed: $fail"
if ($fail -gt 0) { exit 1 }
