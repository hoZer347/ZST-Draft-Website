$probeTimeoutMs = 2000

function Test-Listening([int]$port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        return $client.ConnectAsync('127.0.0.1', $port).Wait($probeTimeoutMs)
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}
