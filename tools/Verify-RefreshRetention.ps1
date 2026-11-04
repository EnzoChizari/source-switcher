#Requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$SourceName,
    [Parameter(Mandatory)][ValidateRange(8000,768000)][int]$ExpectedRateHz,
    [ValidateRange(1,20)][int]$Clicks = 3,
    [string]$OutputPath = '.\refresh-retention-result.json'
)

# Live regression test: requires an open popup and an already detected, active source.
# It clicks Refresh/retry only; it never starts/restarts a player or selects an app/rate.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$app = (Resolve-Path -LiteralPath $AppPath).Path
$appDirectory = Split-Path -Parent $app
foreach ($library in @('NAudio.Core.dll','NAudio.Wasapi.dll','WindowsSourceSwitcher.WindowsShared.dll')) {
    Add-Type -Path (Join-Path $appDirectory $library)
}
$process = Get-Process -Name WindowsSourceSwitcher.Tray -ErrorAction Stop |
    Where-Object { $_.Path -eq $app } | Select-Object -First 1
if (-not $process) { throw 'The specified tray app is not running.' }
$controller = [WindowsLosslessSwitcher.Services.CoreAudioEndpointController]::new()
$settingsFile = Join-Path $env:APPDATA 'DX5SourceSwitcher\settings.json'
$settings = Get-Content -Raw -LiteralPath $settingsFile | ConvertFrom-Json
if ($settings.SelectedSourceDisplayName -ne $SourceName) {
    throw 'The named source must already be the selected master before this test.'
}
$endpoint = $settings.TargetEndpointId
$expectedText = ($ExpectedRateHz / 1000.0).ToString('0.###',[Globalization.CultureInfo]::InvariantCulture) + ' kHz stream'
$ui = [Windows.Automation.AutomationElement]
$scope = [Windows.Automation.TreeScope]
$controlType = [Windows.Automation.ControlType]

function Get-Popup {
    $condition = [Windows.Automation.PropertyCondition]::new($ui::ProcessIdProperty,[int]$process.Id)
    foreach ($window in $ui::RootElement.FindAll($scope::Children,$condition)) {
        if ($window.Current.Name.StartsWith('DX5 Source Switcher')) { return $window }
    }
    throw 'Open the tray popup before running the live regression test.'
}

function Get-SourceRow {
    # WinForms can replace rows between UI Automation calls during a session census.
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            $condition = [Windows.Automation.AndCondition]::new(
                [Windows.Automation.PropertyCondition]::new($ui::ControlTypeProperty,$controlType::ListItem),
                [Windows.Automation.PropertyCondition]::new($ui::NameProperty,$SourceName))
            $row = (Get-Popup).FindFirst($scope::Descendants,$condition)
            if (-not $row) { throw 'Source row is unavailable.' }
            $textCondition = [Windows.Automation.PropertyCondition]::new($ui::ControlTypeProperty,$controlType::Text)
            $values = @($row.FindAll($scope::Children,$textCondition) | ForEach-Object { $_.Current.Name })
            if ($values.Count -lt 3) { throw 'Source row is being rebuilt.' }
            return [pscustomobject]@{ Name=$values[0]; State=$values[1]; Rate=$values[2] }
        } catch {
            if ($attempt -eq 19) { throw }
            Start-Sleep -Milliseconds 50
        }
    }
}

function Read-Formats {
    return [pscustomobject]@{
        Device=$controller.GetCurrentDeviceFormat($endpoint)
        Mix=$controller.GetCurrentMixFormat($endpoint)
    }
}

$beforeRow = Get-SourceRow
$beforeFormats = Read-Formats
if ($beforeRow.State -ne 'Active' -or $beforeRow.Rate -ne $expectedText) {
    throw 'The source must already be active with the expected automatic stream rate.'
}
if ($beforeFormats.Device.SampleRateHz -ne $ExpectedRateHz -or $beforeFormats.Mix.SampleRateHz -ne $ExpectedRateHz) {
    throw 'Device and shared output must already match the expected rate before this test.'
}
$checks = [Collections.Generic.List[object]]::new()
try {
    foreach ($click in 1..$Clicks) {
        $buttonCondition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new($ui::ControlTypeProperty,$controlType::Button),
            [Windows.Automation.PropertyCondition]::new($ui::NameProperty,'Refresh / retry'))
        $button = (Get-Popup).FindFirst($scope::Descendants,$buttonCondition)
        if (-not $button) { throw 'Refresh button is unavailable.' }
        $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 1000
        $row = Get-SourceRow
        $formats = Read-Formats
        $passed = $row.State -eq 'Active' -and $row.Rate -eq $expectedText -and
            $formats.Device.SampleRateHz -eq $ExpectedRateHz -and $formats.Mix.SampleRateHz -eq $ExpectedRateHz -and
            $formats.Device.BitDepth -eq $beforeFormats.Device.BitDepth -and $formats.Device.Channels -eq $beforeFormats.Device.Channels
        $checks.Add([pscustomobject]@{ Click=$click; Row=$row; Formats=$formats; Passed=$passed })
        if (-not $passed) { throw "Refresh lost or changed the playing source on click $click." }
    }
} finally {
    $output = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
    [pscustomobject]@{
        Mode='Refresh/retry retention on existing playback; no player/source/rate changes'
        CompletedUtc=[DateTimeOffset]::UtcNow.ToString('o')
        AppVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion
        Source=$SourceName; ExpectedRateHz=$ExpectedRateHz; Before=$beforeRow
        Checks=$checks.ToArray(); Passed=($checks.Count -eq $Clicks -and @($checks | Where-Object { -not $_.Passed }).Count -eq 0)
    } | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $output
}
Get-Content -Raw -LiteralPath $output
