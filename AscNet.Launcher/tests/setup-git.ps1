param([string]$SetupScript = (Join-Path $PSScriptRoot '..\setup-local.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($SetupScript, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join "`n") }
$names = @('Fail', 'Invoke-Checked', 'Git-Output', 'Update-Checkout', 'Assert-FreePort')
foreach ($fn in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    if ($names -contains $fn.Name) { . ([scriptblock]::Create($fn.Extent.Text)) }
}
$git = (Get-Command git).Source
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ascnet-git-check-' + [guid]::NewGuid())
[IO.Directory]::CreateDirectory($temp) | Out-Null
$Repository = Join-Path $temp 'origin'
$checkout = Join-Path $temp 'checkout'
$Branch = 'master'
try {
    Invoke-Checked $git @('init', '--initial-branch=master', $Repository) 'fixture init'
    Invoke-Checked $git @('-C', $Repository, 'config', 'user.email', 'fixture@example.invalid') 'fixture config'
    Invoke-Checked $git @('-C', $Repository, 'config', 'user.name', 'Fixture') 'fixture config'
    [IO.File]::WriteAllText((Join-Path $Repository 'value.txt'), 'one')
    Invoke-Checked $git @('-C', $Repository, 'add', 'value.txt') 'fixture add'
    Invoke-Checked $git @('-C', $Repository, 'commit', '-m', 'one') 'fixture commit'
    Update-Checkout $git $checkout
    if ([IO.File]::ReadAllText((Join-Path $checkout 'value.txt')) -ne 'one') { throw 'clone contents wrong' }

    # An obsolete checkout script must not take control from the bundled setup.
    $checkoutLauncher = Join-Path $Repository 'AscNet.Launcher'
    [IO.Directory]::CreateDirectory($checkoutLauncher) | Out-Null
    [IO.File]::WriteAllText((Join-Path $checkoutLauncher 'setup-local.ps1'), "throw 'obsolete checkout setup ran'")
    Invoke-Checked $git @('-C', $Repository, 'add', 'AscNet.Launcher/setup-local.ps1') 'fixture obsolete setup'
    Invoke-Checked $git @('-C', $Repository, 'commit', '-m', 'obsolete setup') 'fixture obsolete setup commit'
    $Root = $temp
    $lock = $null
    function Ensure-Git { return $git }
    function Ensure-DotNet { throw 'bundled setup reached SDK stage' }
    $orchestration = $ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.TryStatementAst] } | Select-Object -Last 1
    $reachedSdk = $false
    try { . ([scriptblock]::Create($orchestration.Extent.Text)) }
    catch { $reachedSdk = $_.Exception.Message -eq 'bundled setup reached SDK stage' }
    if (-not $reachedSdk) { throw 'bundled setup delegated to the obsolete checkout instead of preparing the build itself' }

    [IO.File]::WriteAllText((Join-Path $Repository 'value.txt'), 'two')
    Invoke-Checked $git @('-C', $Repository, 'commit', '-am', 'two') 'fixture update'
    Update-Checkout $git $checkout
    if ([IO.File]::ReadAllText((Join-Path $checkout 'value.txt')) -ne 'two') { throw 'fast-forward failed' }

    [IO.File]::WriteAllText((Join-Path $checkout 'value.txt'), 'user changes')
    $refused = $false
    try { Update-Checkout $git $checkout } catch { $refused = $_.Exception.Message -match 'local changes' }
    if (-not $refused -or [IO.File]::ReadAllText((Join-Path $checkout 'value.txt')) -ne 'user changes') { throw 'dirty checkout was not preserved' }

    [IO.File]::WriteAllText((Join-Path $checkout 'value.txt'), 'two')
    Invoke-Checked $git @('-C', $checkout, 'config', 'user.email', 'fixture@example.invalid') 'fixture config'
    Invoke-Checked $git @('-C', $checkout, 'config', 'user.name', 'Fixture') 'fixture config'
    [IO.File]::WriteAllText((Join-Path $checkout 'local.txt'), 'local commit')
    Invoke-Checked $git @('-C', $checkout, 'add', 'local.txt') 'fixture add'
    Invoke-Checked $git @('-C', $checkout, 'commit', '-m', 'local') 'fixture commit'
    [IO.File]::WriteAllText((Join-Path $Repository 'remote.txt'), 'remote commit')
    Invoke-Checked $git @('-C', $Repository, 'add', 'remote.txt') 'fixture add'
    Invoke-Checked $git @('-C', $Repository, 'commit', '-m', 'remote') 'fixture commit'
    $before = Git-Output $git $checkout @('rev-parse', 'HEAD')
    $refused = $false
    try { Update-Checkout $git $checkout } catch { $refused = $_.Exception.Message -match 'commits that are not on' -and $_.Exception.Message -match 'reset --hard FETCH_HEAD' }
    if (-not $refused -or (Git-Output $git $checkout @('rev-parse', 'HEAD')) -ne $before) { throw 'divergent history was modified' }

    # Unreachable remote (GitHub blocked): a clean checkout on the branch builds its current revision.
    $hidden = "$Repository.offline"
    Move-Item -LiteralPath $Repository -Destination $hidden
    try {
        Update-Checkout $git $checkout
        if ((Git-Output $git $checkout @('rev-parse', 'HEAD')) -ne $before) { throw 'offline update changed the checkout' }
    } finally { Move-Item -LiteralPath $hidden -Destination $Repository }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        $refused = $false
        try { Assert-FreePort $listener.LocalEndpoint.Port 'fixture' } catch { $refused = $true }
        if (-not $refused) { throw 'occupied port accepted' }
    } finally { $listener.Stop() }
    Write-Output 'PASS: clone, fast-forward, bundled orchestration, dirty preservation, divergence refusal, occupied-port refusal'
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
