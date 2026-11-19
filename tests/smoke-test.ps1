<#
.SYNOPSIS
    Quick check that the MCP for Unity server answers (initialize, tools/list, get_editor_state).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests\smoke-test.ps1
    powershell -ExecutionPolicy Bypass -File tests\smoke-test.ps1 -Port 8891
#>
param(
    [int]$Port = 8890
)

$ErrorActionPreference = 'Stop'
$url = "http://127.0.0.1:$Port/mcp"
$script:nextId = 1

function Invoke-Mcp {
    param([string]$Method, $Params = @{})
    $body = @{ jsonrpc = '2.0'; id = $script:nextId; method = $Method; params = $Params } | ConvertTo-Json -Depth 20 -Compress
    $script:nextId++
    $response = Invoke-WebRequest -Uri $url -Method Post -UseBasicParsing `
        -ContentType 'application/json' `
        -Headers @{ Accept = 'application/json, text/event-stream' } `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
    $text = [System.Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray())
    $json = $text | ConvertFrom-Json
    if ($json.error) { throw "JSON-RPC error $($json.error.code): $($json.error.message)" }
    return $json.result
}

try {
    Write-Host "Connecting to $url ..." -ForegroundColor Cyan
    $init = Invoke-Mcp 'initialize' @{
        protocolVersion = '2025-06-18'
        capabilities    = @{}
        clientInfo      = @{ name = 'smoke-test'; version = '1.0' }
    }
    Write-Host ("OK  initialize -> {0} {1} (protocol {2})" -f $init.serverInfo.name, $init.serverInfo.version, $init.protocolVersion) -ForegroundColor Green

    $tools = Invoke-Mcp 'tools/list'
    Write-Host ("OK  tools/list -> {0} tools" -f $tools.tools.Count) -ForegroundColor Green
    $tools.tools | ForEach-Object { Write-Host ("      - {0}" -f $_.name) }

    $state = Invoke-Mcp 'tools/call' @{ name = 'get_editor_state'; arguments = @{} }
    if ($state.isError) { throw "get_editor_state failed: $($state.content[0].text)" }
    $info = $state.content[0].text | ConvertFrom-Json
    Write-Host ("OK  get_editor_state -> Unity {0}, project '{1}', active scene '{2}'" -f `
        $info.unityVersion, $info.project.name, ($info.scenes | Where-Object { $_.isActive } | Select-Object -First 1).name) -ForegroundColor Green

    Write-Host "`nThe server is working." -ForegroundColor Green
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Is Unity open with the package installed? Check Window > MCP for Unity for the status and port." -ForegroundColor Yellow
    exit 1
}
