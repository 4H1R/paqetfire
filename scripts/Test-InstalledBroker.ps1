param(
    [int]$TimeoutMilliseconds = 3000
)

$ErrorActionPreference = 'Stop'
$pipeName = 'PaqetFire.Broker.v9'
$protocolVersion = 9
$requestId = [guid]::NewGuid()
$deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)

try {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        $pipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    $pipe.Connect($TimeoutMilliseconds)

    $request = @{
        requestId = $requestId
        protocolVersion = $protocolVersion
        command = 'getSnapshot'
        settings = $null
    } | ConvertTo-Json -Compress
    $payload = [System.Text.Encoding]::UTF8.GetBytes($request)
    $prefix = [System.BitConverter]::GetBytes([int]$payload.Length)
    $pipe.Write($prefix, 0, $prefix.Length)
    $pipe.Write($payload, 0, $payload.Length)
    $pipe.Flush()

    while ([DateTime]::UtcNow -lt $deadline) {
        $prefix = [byte[]]::new(4)
        $read = 0
        while ($read -lt $prefix.Length) {
            $count = $pipe.Read($prefix, $read, $prefix.Length - $read)
            if ($count -eq 0) {
                throw 'The broker closed the pipe before returning a response.'
            }
            $read += $count
        }

        $length = [System.BitConverter]::ToInt32($prefix, 0)
        if ($length -le 0 -or $length -gt 65536) {
            throw "The broker returned an invalid message length: $length."
        }

        $buffer = [byte[]]::new($length)
        $read = 0
        while ($read -lt $buffer.Length) {
            $count = $pipe.Read($buffer, $read, $buffer.Length - $read)
            if ($count -eq 0) {
                throw 'The broker closed the pipe during a response.'
            }
            $read += $count
        }

        $message = [System.Text.Encoding]::UTF8.GetString($buffer) | ConvertFrom-Json
        if ($message.messageType -ne 'response' -or
            $message.response.requestId -ne $requestId.ToString()) {
            continue
        }

        if (-not $message.response.success) {
            throw "Broker request failed: $($message.response.error.code): $($message.response.error.message)"
        }

        if ($null -eq $message.response.snapshot) {
            throw 'Broker request succeeded without a snapshot.'
        }

        $engineSummary = @($message.response.snapshot.engines | ForEach-Object {
            "$($_.engine)=$($_.state)"
        }) -join ', '
        [pscustomobject]@{
            Result = 'PASS'
            Pipe = $pipeName
            Protocol = $message.response.protocolVersion
            IsConfigured = $message.response.snapshot.isConfigured
            IsRouting = $message.response.snapshot.isRouting
            Engines = $engineSummary
            Status = $message.response.snapshot.statusMessage
        } | Format-List
        exit 0
    }

    throw 'Timed out waiting for the matching broker response.'
}
catch {
    [pscustomobject]@{
        Result = 'FAIL'
        Pipe = $pipeName
        Error = $_.Exception.Message
    } | Format-List
    exit 1
}
finally {
    if ($null -ne $pipe) {
        $pipe.Dispose()
    }
}
