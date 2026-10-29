# .NET 10 SDK の dotnet.exe を探す（他のスクリプトから読み込んで使う）
$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd -and ((& $cmd.Source --list-sdks) -match '^10\.')) { $dotnet = $cmd.Source }
if (-not $dotnet) {
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if ((Test-Path $local) -and ((& $local --list-sdks) -match '^10\.')) { $dotnet = $local }
}
if (-not $dotnet) {
    throw '.NET 10 SDK が見つかりません。winget install Microsoft.DotNet.SDK.10 でインストールしてください。'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
