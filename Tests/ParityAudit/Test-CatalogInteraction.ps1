# Structural integration checks only: no UI click, process launch or OS mutation.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$main = Get-Content -LiteralPath (Join-Path $root 'MainWindow.xaml.cs') -Raw
$progress = Get-Content -LiteralPath (Join-Path $root 'CatalogProgressWindow.cs') -Raw
[xml]$app = Get-Content -LiteralPath (Join-Path $root 'App.xaml') -Raw
[xml]$window = Get-Content -LiteralPath (Join-Path $root 'MainWindow.xaml') -Raw
$count = 0
function Assert([bool]$value, [string]$name) {
    if (-not $value) { throw "FAILED: $name" }
    $script:count++
}
# All 20 existing routes remain, now grouped into the five requested pages.
$routes = [ordered]@{
    FullRepair = 'Full Repair'; QuickRepair = 'Quick Repair'
    WindowsUpdateFix = 'Windows Update Fix'; MicrosoftStoreFix = 'Microsoft Store Fix'
    ExplorerFix = 'Explorer Fix'; DiskInfo = 'Disk Info'; SystemReport = 'System Report'
    WindowsActivation = 'Windows Activation Status'; OfficeActivation = 'Office Activation Status'
    DisableDefender = 'Disable Defender'; RestoreDefender = 'Restore Defender'
    BitLockerManager = 'BitLocker Manager'; SmartAppControl = 'Smart App Control'
    EssentialTweaks = 'Essential Windows Tweaks'; GamingTweaks = 'Gaming Tweaks'
    RuntimeCompatibility = 'Games Runtime & Compatibility Check'; GpuDriverManager = 'GPU Driver Manager'
    Debloat = 'Advanced Windows Tweaks & De-Bloat'; MsiModeUtility = 'MSI Mode Utility'
    LegacyWindowsPanels = 'Legacy Windows Panels'
}
$search = Get-Content -LiteralPath (Join-Path $root 'MainWindow.Search.cs') -Raw
$charts = $window.SelectSingleNode('//*[local-name()="Grid" and @*[local-name()="Name"]="LiveChartsGrid"]')
Assert ($charts.ParentNode.GetAttribute('Name', 'http://schemas.microsoft.com/winfx/2006/xaml') -eq 'HomePage') 'live charts are directly visible on Home'
foreach ($catalog in 'EssentialTweaks','GamingTweaks','Debloat') {
    Assert ($search.Contains("Open${catalog}Async);")) "Home search routes to $catalog"
}
Assert ($main.Contains('IsVisibleOption(definition) && selectors[definition.Id].IsChecked == true')) 'hidden search results cannot enter bulk review'
Assert ($main.Contains('showActions && MatchesSearch(search.Text, action.Name')) 'text search includes action-only tools'
Assert ($search.Contains('if (hit is null) return;')) 'empty search does not navigate'

$buttons = @($window.SelectNodes('//*[local-name()="Button"]'))
$pageForRoute = @{
    FullRepair='RepairPage'; QuickRepair='RepairPage'; WindowsUpdateFix='RepairPage'; MicrosoftStoreFix='RepairPage'; ExplorerFix='RepairPage'
    DiskInfo='InfoPage'; SystemReport='InfoPage'; WindowsActivation='InfoPage'; OfficeActivation='InfoPage'
    DisableDefender='SecurityPage'; RestoreDefender='SecurityPage'; BitLockerManager='SecurityPage'; SmartAppControl='SecurityPage'
    EssentialTweaks='AdvancedPage'; GamingTweaks='AdvancedPage'; RuntimeCompatibility='AdvancedPage'; GpuDriverManager='AdvancedPage'; Debloat='AdvancedPage'; MsiModeUtility='AdvancedPage'
    LegacyWindowsPanels='HomePage'
}
foreach ($route in $routes.GetEnumerator()) {
    $matches = @($buttons | Where-Object { $_.GetAttribute('Click') -ceq ($route.Key + 'Button_Click') })
    Assert ($matches.Count -eq 1) ("one reference button: " + $route.Key)
    Assert ($matches[0].GetAttribute('Content') -ceq $route.Value) ("reference label: " + $route.Key)
    $ancestor = $matches[0].ParentNode
    while ($ancestor -is [Xml.XmlElement] -and $ancestor.GetAttribute('Name', 'http://schemas.microsoft.com/winfx/2006/xaml') -ne $pageForRoute[$route.Key]) { $ancestor = $ancestor.ParentNode }
    Assert ($ancestor -is [Xml.XmlElement]) ("correct page: " + $route.Key)
}
$ns = [Xml.XmlNamespaceManager]::new($app.NameTable)
$ns.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$base = $app.SelectSingleNode('//*[local-name()="Style" and @x:Key="ReferenceButtonStyle"]', $ns)
Assert ($null -ne $base) 'shared button style exists'
foreach ($property in @('Foreground', 'Background', 'BorderBrush')) {
    $setter = $base.SelectSingleNode('*[local-name()="Setter" and @Property="' + $property + '"]')
    $key = if ($property -eq 'BorderBrush') { 'NeutralButtonBorderBrush' } else { 'NeutralButton' + $property + 'Brush' }
    Assert ($null -ne $setter -and $setter.GetAttribute('Value') -ceq ('{ThemeResource ' + $key + '}')) ("shared button native theme resource: " + $property)
    foreach ($theme in @('Light', 'Dark', 'Default')) {
        $dictionary = $app.SelectSingleNode('//*[local-name()="ResourceDictionary" and @x:Key="' + $theme + '"]', $ns)
        Assert ($null -ne $dictionary.SelectSingleNode('*[local-name()="SolidColorBrush" and @x:Key="' + $key + '"]', $ns)) ("button resource exists: $theme/$key")
    }
}
foreach ($pair in @{ MinHeight='34'; Padding='10,4'; CornerRadius='0'; BorderThickness='1'; HorizontalContentAlignment='Center'; VerticalContentAlignment='Center' }.GetEnumerator()) {
    $setter = $base.SelectSingleNode('*[local-name()="Setter" and @Property="' + $pair.Key + '"]')
    Assert ($null -ne $setter -and $setter.GetAttribute('Value') -ceq $pair.Value) ("shared button metric: " + $pair.Key)
}
Assert ($null -ne $app.SelectSingleNode('//*[local-name()="Style" and @TargetType="Button" and not(@x:Key) and @BasedOn="{StaticResource ReferenceButtonStyle}"]', $ns)) 'dynamic buttons inherit the shared style'
Assert ($window.OuterXml.Contains('BasedOn="{StaticResource ReferenceButtonStyle}"')) 'Main UI inherits the same button base'
Assert ([regex]::Matches($main, 'await RunIndividualActionAsync\(async \(\) =>').Count -eq 2) 'both action directions use pre-confirmation guard'
Assert ($main.Contains('window.IsBusy = () => applyInProgress || stateLoadInProgress || individualActionGate.IsBusy;')) 'catalog cannot close during individual confirmation/queue/work'
Assert ([regex]::Matches($main, 'window.IsClosed \|\| (applyInProgress \|\| stateLoadInProgress|stateLoadInProgress \|\| applyInProgress) \|\| individualActionGate.IsBusy').Count -eq 3) 'analyze/apply/restore exclude individual work'
Assert ($main.Contains('foreach (var item in individualButtons)')) 'action buttons participate in interaction locking'
Assert ($main.Contains('actionService, action, restore: false, progressWindow.CreateReporter(action.Id)')) 'Apply reporter reaches shared worker'
Assert ($main.Contains('actionService, action, restore: true, progressWindow.CreateReporter(action.Id)')) 'Restore reporter reaches shared worker'
Assert (-not $main.Contains('await Task.Run(() => actionService.RestoreAsync(action))')) 'old reporter-less Restore route removed'
Assert ($progress.Contains('_overallBar.IsIndeterminate = _progress.IsOverallIndeterminate;')) 'overall consumes honest activity state'
Assert ($progress.Contains('UpdateOverall(_progress.Settled, update.Detail);')) 'live detail also updates export summary'
Assert ($progress.Contains('_progress.HasFailures ? FailureBrush() : WorkingBrush()')) 'overall failures remain red while tasks continue'
Assert ($progress.Contains('_overallBar.IsIndeterminate = false;')) 'terminal overall stops animating'
$invalidation = [regex]::Match($main, '(?s)void InvalidateCatalogView\(\)(.*?)void OnCatalogStateChanged').Groups[1].Value
Assert ($invalidation.Contains('_ = LoadCatalogStatesAsync();')) 'stale catalogs automatically rescan after a change'
Assert ($invalidation.Contains('lastScanAttempt == CatalogStateEpoch.Version')) 'failed scans do not trigger an endless retry'
Assert ([regex]::Matches($main, '(?s)applyInProgress = false;\s+SetCatalogInteraction\(true\);\s+UpdateSelectionSummary\(\);\s+analyzeButton.IsEnabled = true;\s+InvalidateCatalogView\(\);').Count -eq 2) 'both apply and restore release their lock before refreshing'
Assert ($main.Contains('if (!state.IsAvailable) selector.IsChecked = false;')) 'still-available selections survive automatic refresh'
Write-Output "PASS: $count static button/routing/progress assertions. Native layout and clicks are not certified."
