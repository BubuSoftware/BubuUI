param([Parameter(Mandatory = $true)][string]$ServerPath, [switch]$MaterializeDecompiled)

# Exercise the actual published process and stdio framing, not an in-process test double.
$ErrorActionPreference = 'Stop'
$server = [IO.Path]::GetFullPath($ServerPath)
$workspace = Join-Path ([IO.Path]::GetTempPath()) ("eclipseui-visualstudio-smoke-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workspace) | Out-Null
$document = Join-Path $workspace 'Smoke.ui'
$text = "@namespace Smoke`n@using EclipseUI.Elements`n`n<But"
[IO.File]::WriteAllText($document, $text)
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = 'dotnet'
$start.Arguments = '"' + $server + '" --stdio --workspace "' + $workspace + '" --log "' + (Join-Path $workspace 'server.log') + '"'
if ($MaterializeDecompiled) { $start.Arguments += ' --materialize-decompiled' }
$start.WorkingDirectory = $workspace
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = New-Object Diagnostics.Process
$process.StartInfo = $start
$started = $false

function Send-Message($value) {
    $body = [Text.Encoding]::UTF8.GetBytes(($value | ConvertTo-Json -Depth 40 -Compress))
    $header = [Text.Encoding]::ASCII.GetBytes("Content-Length: $($body.Length)`r`n`r`n")
    $process.StandardInput.BaseStream.Write($header, 0, $header.Length)
    $process.StandardInput.BaseStream.Write($body, 0, $body.Length)
    $process.StandardInput.BaseStream.Flush()
}

function Read-Bytes([byte[]]$buffer, [int]$offset, [int]$length, [DateTime]$deadline) {
    $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
    $read = $process.StandardOutput.BaseStream.ReadAsync($buffer, $offset, $length)
    if (-not $read.Wait($remaining)) { throw 'Published server did not respond within 30 seconds.' }
    if ($read.Result -eq 0) { throw 'Published server closed stdout unexpectedly.' }
    return $read.Result
}

function Read-Response([int]$id) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $header = ''
        $one = New-Object byte[] 1
        while (-not $header.EndsWith("`r`n`r`n")) {
            $null = Read-Bytes $one 0 1 $deadline
            $header += [char]$one[0]
            if ($header.Length -gt 8192) { throw 'Invalid LSP header.' }
        }
        if ($header -notmatch '(?i)Content-Length:\s*(\d+)') { throw 'Missing Content-Length.' }
        $length = [int]$Matches[1]
        $body = New-Object byte[] $length
        $offset = 0
        while ($offset -lt $length) { $offset += Read-Bytes $body $offset ($length - $offset) $deadline }
        $message = [Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json
        # Acknowledge the server's watcher registration while consuming notifications.
        if ($message.method -eq 'client/registerCapability') {
            Send-Message @{ jsonrpc = '2.0'; id = $message.id; result = $null }
        }
        elseif ($null -ne $message.id -and $message.id -eq $id -and -not $message.method) {
            if ($message.error) { throw ($message.error | ConvertTo-Json -Compress) }
            return $message.result
        }
    }
    throw "No response for request $id."
}

try {
    if (-not $process.Start()) { throw 'Could not start the published server.' }
    $started = $true
    $stderr = $process.StandardError.ReadToEndAsync()
    Send-Message @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{
        rootUri = ([Uri]($workspace + '\')).AbsoluteUri; capabilities = @{}
    } }
    $initialized = Read-Response 1
    if (-not $initialized.capabilities.completionProvider) { throw 'Server did not advertise completion.' }
    Send-Message @{ jsonrpc = '2.0'; method = 'initialized'; params = @{} }
    $uri = ([Uri]$document).AbsoluteUri
    Send-Message @{ jsonrpc = '2.0'; method = 'textDocument/didOpen'; params = @{
        textDocument = @{ uri = $uri; languageId = 'eclipseui'; version = 1; text = $text }
    } }
    Send-Message @{ jsonrpc = '2.0'; id = 2; method = 'textDocument/completion'; params = @{
        textDocument = @{ uri = $uri }; position = @{ line = 3; character = 4 }
    } }
    $completion = Read-Response 2
    if (-not ($completion.items | Where-Object { $_.label -eq 'Button' })) { throw 'Bundled server did not complete Button from its bundled Runtime metadata.' }
    # VS Ctrl+hover sends the first character of the attribute, not its middle.
    $updatedText = "@namespace Smoke`n@using EclipseUI.Elements`n`n<Button Label=""demo"" />"
    Send-Message @{ jsonrpc = '2.0'; method = 'textDocument/didChange'; params = @{
        textDocument = @{ uri = $uri; version = 2 }; contentChanges = @(@{ text = $updatedText })
    } }
    $attributePosition = @{ textDocument = @{ uri = $uri }; position = @{ line = 3; character = 8 } }
    Send-Message @{ jsonrpc = '2.0'; id = 3; method = 'textDocument/hover'; params = $attributePosition }
    $hover = Read-Response 3
    if (-not $hover -or $hover.contents.value -notmatch 'Label' -or $hover.range.start.character -ne 8 -or $hover.range.end.character -ne 13) {
        throw 'Attribute-start hover did not return the complete Label symbol range.'
    }
    Send-Message @{ jsonrpc = '2.0'; id = 4; method = 'textDocument/definition'; params = $attributePosition }
    $definition = Read-Response 4
    if ($MaterializeDecompiled) {
        if (-not $definition -or $definition.uri -notlike 'file:*') { throw 'Rider metadata navigation did not return a file URI.' }
        $cachedSource = ([Uri]$definition.uri).LocalPath
        if (-not (Test-Path -LiteralPath $cachedSource) -or [IO.File]::ReadAllText($cachedSource) -notmatch 'Label') { throw 'Rider metadata source is not readable.' }
    }
    else {
        if (-not $definition -or $definition.uri -notlike 'eui-decompiled:*') { throw 'Attribute-start definition did not resolve the Label member.' }
        Send-Message @{ jsonrpc = '2.0'; id = 5; method = 'eclipseui/decompiledContent'; params = @{ uri = $definition.uri } }
        $decompiled = Read-Response 5
        if (-not $decompiled.content -or $decompiled.content -notmatch 'Label') { throw 'Attribute definition did not provide decompiled source.' }
    }
    $skinUri = ([Uri](Join-Path $workspace 'Skin.ui')).AbsoluteUri
    $skinText = "@namespace Smoke`n@using EclipseUI.Elements`n@template Button`n<Border Background={Owner.Background} />"
    Send-Message @{ jsonrpc = '2.0'; method = 'textDocument/didOpen'; params = @{
        textDocument = @{ uri = $skinUri; languageId = 'eclipseui'; version = 1; text = $skinText }
    } }
    $styleUri = ([Uri](Join-Path $workspace 'controls.eustyle')).AbsoluteUri
    $styleText = "@using EclipseUI.Elements;`n@using Smoke;`n@theme Rounded for Button { Template: template(Skin); }`nButton { Theme: resource(Rounded); }"
    Send-Message @{ jsonrpc = '2.0'; method = 'textDocument/didOpen'; params = @{
        textDocument = @{ uri = $styleUri; languageId = 'eustyle'; version = 1; text = $styleText }
    } }
    $resourcePosition = @{ textDocument = @{ uri = $styleUri }; position = @{ line = 3; character = 27 } }
    Send-Message @{ jsonrpc = '2.0'; id = 6; method = 'textDocument/completion'; params = $resourcePosition }
    $resources = Read-Response 6
    if (-not ($resources.items | Where-Object { $_.label -eq 'Rounded' })) { throw 'Control theme resource completion failed.' }
    Send-Message @{ jsonrpc = '2.0'; id = 7; method = 'textDocument/definition'; params = $resourcePosition }
    $resourceDefinition = Read-Response 7
    if (-not $resourceDefinition -or $resourceDefinition.uri -ne $styleUri -or $resourceDefinition.range.start.line -ne 2) { throw 'Theme definition did not map to its stylesheet declaration.' }
    $embeddedText = '@namespace Smoke' + "`n" + '@using EclipseUI.Elements' + "`n" + '@style {' + "`n" + '$accent: #112233;' + "`n" + '.primary { Background: $accent; }' + "`n" + '}' + "`n" + '<Border Style="Background: th" />'
    Send-Message @{ jsonrpc = '2.0'; method = 'textDocument/didChange'; params = @{
        textDocument = @{ uri = $uri; version = 3 }; contentChanges = @(@{ text = $embeddedText })
    } }
    Send-Message @{ jsonrpc = '2.0'; id = 9; method = 'textDocument/definition'; params = @{
        textDocument = @{ uri = $uri }; position = @{ line = 4; character = 26 }
    } }
    $variableDefinition = Read-Response 9
    if (-not $variableDefinition -or $variableDefinition.uri -ne $uri -or $variableDefinition.range.start.line -ne 3) { throw 'Embedded variable navigation failed.' }
    Send-Message @{ jsonrpc = '2.0'; id = 10; method = 'textDocument/completion'; params = @{
        textDocument = @{ uri = $uri }; position = @{ line = 6; character = 28 }
    } }
    $inlineCompletion = Read-Response 10
    if (-not ($inlineCompletion.items | Where-Object { $_.label -eq 'theme' })) { throw 'Inline Style function completion failed.' }
    Send-Message @{ jsonrpc = '2.0'; id = 11; method = 'textDocument/rename'; params = @{
        textDocument = @{ uri = $uri }; position = @{ line = 4; character = 26 }; newName = 'ink'
    } }
    $renamedVariable = Read-Response 11
    if (-not $renamedVariable -or ($renamedVariable | ConvertTo-Json -Depth 30 -Compress) -notmatch 'ink') { throw 'Embedded variable rename failed.' }
    Send-Message @{ jsonrpc = '2.0'; id = 8; method = 'shutdown'; params = $null }
    $null = Read-Response 8
    Send-Message @{ jsonrpc = '2.0'; method = 'exit'; params = $null }
    if (-not $process.WaitForExit(5000)) { throw 'Server did not exit after shutdown.' }
    if ($process.ExitCode -ne 0) { throw "Server exited with code $($process.ExitCode)." }
    Write-Host "Published server verified: completion, navigation, decompilation, control themes, embedded styles, variable rename, shutdown. Log: $workspace\server.log" -ForegroundColor Green
}
finally {
    if ($started) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        if ($stderr -and $stderr.IsCompleted -and $stderr.Result) { Write-Host $stderr.Result }
    }
    $process.Dispose()
}
