param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$CodexArgs
)

$ErrorActionPreference = 'Stop'
$nodePath = (Get-Command node -ErrorAction Stop).Source
$serverPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'mcp-server.mjs'))

$mcpOverrides = @(
  '-c', "mcp_servers.memos_local.command='$nodePath'",
  '-c', "mcp_servers.memos_local.args=['$serverPath']",
  '-c', 'mcp_servers.memos_local.startup_timeout_sec=180',
  '-c', 'mcp_servers.memos_local.tool_timeout_sec=600',
  '-c', "mcp_servers.memos_local.enabled_tools=['memos_recall','memos_remember','memos_health','memos_list_recent']"
)

& codex @mcpOverrides @CodexArgs
exit $LASTEXITCODE

