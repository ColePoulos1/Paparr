param(
    [ValidateRange(1024, 65535)][int]$HttpPort = 5190,
    [ValidateRange(1024, 65535)][int]$TlsPort = 5191
)

$ErrorActionPreference = 'Stop'
if ($HttpPort -eq $TlsPort) { throw 'HTTP and TLS ports must be different.' }

$repoRoot = Split-Path $PSScriptRoot -Parent
$project = 'paparr-tls-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$network = "$project-net"
$testRoot = Join-Path $repoRoot ".tools/$project"
$ingest = Join-Path $testRoot 'ingest'
$library = Join-Path $testRoot 'library'
$certDirectory = Join-Path $testRoot 'certs'
$envPath = Join-Path $testRoot 'compose.env'
$overlayPath = Join-Path $testRoot 'compose.tls.yml'
$proxyConfigPath = Join-Path $testRoot 'tls-proxy.conf'
$certificatePath = Join-Path $certDirectory 'tls.crt'
$privateKeyPath = Join-Path $certDirectory 'tls.key'
$ownerPassword = [Guid]::NewGuid().ToString('N')
$ownerHash = ''
$compose = @('compose', '--file', (Join-Path $repoRoot 'docker-compose.yml'), '--file', $overlayPath,
    '--env-file', $envPath, '--project-name', $project)
$composeCreated = $false
$networkCreated = $false
$checks = [Collections.Generic.List[string]]::new()
$baseHttp = "http://127.0.0.1:$HttpPort"
$baseTls = "https://localhost:$TlsPort"

New-Item -ItemType Directory -Path $certDirectory, $ingest, $library -Force | Out-Null

function Invoke-Compose([string[]]$Arguments) {
    & docker @compose @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Compose command failed: $($Arguments -join ' ')" }
}

function Invoke-Docker([string[]]$Arguments) {
    $output = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker command failed: $($Arguments -join ' ')" }
    return $output
}

function Assert-Equal($Expected, $Actual, [string]$Description) {
    if ($Expected -ne $Actual) { throw "$Description failed (expected $Expected, received $Actual)." }
}

function Get-CookieHeaders($Response) {
    $values = [string[]]@()
    if ($Response.Headers.TryGetValues('Set-Cookie', [ref]$values)) { return $values }
    return @()
}

function New-TestCertificate {
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=localhost', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $subjectNames = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $subjectNames.AddDnsName('localhost')
        $subjectNames.AddIpAddress([Net.IPAddress]::Loopback)
        $request.CertificateExtensions.Add($subjectNames.Build())
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-2), [DateTimeOffset]::UtcNow.AddDays(2))
        try {
            [IO.File]::WriteAllText($certificatePath, $certificate.ExportCertificatePem())
            [IO.File]::WriteAllText($privateKeyPath, $rsa.ExportPkcs8PrivateKeyPem())
        } finally { $certificate.Dispose() }
    } finally { $rsa.Dispose() }
}

function New-ComposeOverlay([string]$UiIp, [string]$TlsIp) {
    @"
services:
  postgres:
    networks:
      - benchmark
  paparr-api:
    environment:
      ReverseProxy__TrustedNetworks: "$UiIp/32"
    networks:
      - benchmark
  paparr-ui:
    ports:
      - "127.0.0.1:$($HttpPort):8080"
    environment:
      UI_TRUSTED_PROXY_NETWORKS: "$TlsIp/32"
    networks:
      benchmark:
        ipv4_address: $UiIp
  tls-proxy:
    image: nginxinc/nginx-unprivileged:stable-alpine
    ports:
      - "127.0.0.1:$($TlsPort):8443"
    volumes:
      - "$($proxyConfigPath.Replace('\', '/')):/etc/nginx/conf.d/default.conf:ro"
      - "$($certificatePath.Replace('\', '/')):/etc/nginx/certs/tls.crt:ro"
      - "$($privateKeyPath.Replace('\', '/')):/etc/nginx/certs/tls.key:ro"
    networks:
      benchmark:
        ipv4_address: $TlsIp
    depends_on:
      - paparr-ui
networks:
  benchmark:
    name: $network
    external: true
"@ | Set-Content -LiteralPath $overlayPath -Encoding utf8NoBOM
}

function Write-ProxyConfiguration {
    @'
server {
    listen 8443 ssl;
    server_name localhost;
    ssl_certificate /etc/nginx/certs/tls.crt;
    ssl_certificate_key /etc/nginx/certs/tls.key;
    ssl_protocols TLSv1.2 TLSv1.3;

    location / {
        proxy_pass http://paparr-ui:8080;
        proxy_set_header Host $http_host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
'@ | Set-Content -LiteralPath $proxyConfigPath -Encoding utf8NoBOM
}

function Test-CookieSecurity([string[]]$CookieHeaders) {
    $setCookies = @($CookieHeaders | Where-Object { $_ })
    if ($setCookies.Count -eq 0) { throw 'Authentication responses did not set cookies.' }
    foreach ($cookie in $setCookies) {
        if ($cookie -notmatch '(?i)(^|;\s*)Secure(?:;|$)') { throw 'An authentication cookie was missing Secure.' }
        if ($cookie -notmatch '(?i)(^|;\s*)SameSite=Strict(?:;|$)') { throw 'An authentication cookie was missing SameSite=Strict.' }
    }
    $sessionCookie = $setCookies | Where-Object { $_ -match '^Paparr\.Session=' } | Select-Object -Last 1
    if (!$sessionCookie -or $sessionCookie -notmatch '(?i)(^|;\s*)HttpOnly(?:;|$)') {
        throw 'The owner session cookie was missing HttpOnly.'
    }
}

try {
    New-TestCertificate
    $null = Invoke-Docker @('network', 'create', '--label', "paparr.tls.smoke=$project", $network)
    $networkCreated = $true
    $networkInfo = (Invoke-Docker @('network', 'inspect', $network) | ConvertFrom-Json)[0]
    $subnet = $networkInfo.IPAM.Config | Where-Object { $_.Subnet -match '^\d+\.\d+\.\d+\.\d+/\d+$' } | Select-Object -First 1 -ExpandProperty Subnet
    if (!$subnet) { throw 'The isolated Docker network has no IPv4 subnet.' }
    $parts = $subnet.Split('/')
    $prefix = [int]$parts[1]
    if ($prefix -gt 24) { throw "Refusing the Docker-assigned IPv4 prefix /$prefix; expected /24 or broader." }
    $networkOctets = $parts[0].Split('.')
    $uiIp = "$($networkOctets[0]).$($networkOctets[1]).$($networkOctets[2]).240"
    $tlsIp = "$($networkOctets[0]).$($networkOctets[1]).$($networkOctets[2]).241"
    foreach ($address in @($uiIp, $tlsIp)) {
        if (!([Net.IPNetwork]::Parse($subnet).Contains([Net.IPAddress]::Parse($address)))) {
            throw "Chosen proxy address is outside the isolated subnet ($subnet)."
        }
    }

    Write-ProxyConfiguration
    New-ComposeOverlay $uiIp $tlsIp
    @(
        "POSTGRES_PASSWORD=$([Guid]::NewGuid().ToString('N'))"
        'POSTGRES_DB=paparr'
        'AUTHENTICATION_ENABLED=true'
        'SECURE_COOKIES=true'
        'PAPARR_PASSWORD_HASH='
        "HOST_INGEST_PATH=$($ingest.Replace('\', '/'))"
        "HOST_LIBRARY_PATH=$($library.Replace('\', '/'))"
        "PAPARR_PORT=$HttpPort"
        'BIND_ADDRESS=127.0.0.1'
        "TRUSTED_PROXY_NETWORKS=$uiIp/32"
        "UI_TRUSTED_PROXY_NETWORKS=$tlsIp/32"
        'DELETE_SOURCE_AFTER_IMPORT=false'
        'METADATA_PROVIDERS_ENABLED=false'
        "TEST_NETWORK=$network"
    ) | Set-Content -LiteralPath $envPath -Encoding utf8NoBOM

    Invoke-Compose -Arguments @('build', 'paparr-api', 'paparr-ui')
    $hashOutput = $ownerPassword | & docker @compose run --rm --no-deps -T paparr-api --hash-password
    if ($LASTEXITCODE -ne 0) { throw 'Owner password hash generation failed.' }
    $ownerHash = [string]($hashOutput | Where-Object { $_ -match '^AQ[A-Za-z0-9+/]+=*$' } | Select-Object -Last 1)
    if (!$ownerHash) { throw 'Owner password hash generation returned no valid hash.' }
    Add-Content -LiteralPath $envPath -Value "PAPARR_PASSWORD_HASH=$ownerHash" -Encoding utf8NoBOM

    $composeCreated = $true
    Invoke-Compose -Arguments @('up', '-d')
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(180)
    $ready = $false
    do {
        try {
            $readyResponse = Invoke-WebRequest -Uri "$baseTls/health/ready" -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 5
            if ($readyResponse.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $readyDeadline)
    if (!$ready) {
        & docker @compose ps
        & docker @compose logs --no-color --tail 80 paparr-ui tls-proxy paparr-api
        throw 'The isolated TLS deployment did not become ready.'
    }

    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.ServerCertificateCustomValidationCallback = [Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
    $handler.CookieContainer = [Net.CookieContainer]::new()
    $client = [Net.Http.HttpClient]::new($handler)
    try {
        $tlsSessionResponse = $client.GetAsync("$baseTls/api/auth/session").GetAwaiter().GetResult()
        Assert-Equal 200 ([int]$tlsSessionResponse.StatusCode) 'HTTPS session endpoint'
        $sessionJson = $tlsSessionResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        Assert-Equal $true $sessionJson.enabled 'Authentication enabled over TLS'
        Assert-Equal $false $sessionJson.authenticated 'Anonymous initial session'
        if (!$sessionJson.csrfToken) { throw 'Anonymous TLS session did not return a CSRF token.' }
        $tlsSessionResponse.Dispose()
        $checks.Add('TLS endpoint returns the anonymous session and CSRF token')

        $anonymousApi = $client.GetAsync("$baseTls/api/imports").GetAwaiter().GetResult()
        Assert-Equal 401 ([int]$anonymousApi.StatusCode) 'Anonymous protected API access'
        $anonymousApi.Dispose()
        $checks.Add('Anonymous API access is rejected')

        $directRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "$baseHttp/api/auth/session")
        $directRequest.Headers.TryAddWithoutValidation('X-Forwarded-Proto', 'https') | Out-Null
        $directResponse = $client.SendAsync($directRequest).GetAwaiter().GetResult()
        Assert-Equal 400 ([int]$directResponse.StatusCode) 'Direct HTTP ignores spoofed forwarded-proto'
        $directResponse.Dispose()
        $directRequest.Dispose()
        $checks.Add('Direct HTTP rejects spoofed X-Forwarded-Proto')

        $spoofRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "$baseTls/api/auth/session")
        $spoofRequest.Headers.TryAddWithoutValidation('X-Forwarded-Proto', 'http') | Out-Null
        $spoofResponse = $client.SendAsync($spoofRequest).GetAwaiter().GetResult()
        Assert-Equal 200 ([int]$spoofResponse.StatusCode) 'TLS proxy overwrites a client-supplied forwarded-proto'
        $spoofResponse.Dispose()
        $spoofRequest.Dispose()
        $checks.Add('TLS proxy overwrites a client-supplied X-Forwarded-Proto value')

        $unauthorizedRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$baseTls/api/auth/login")
        $unauthorizedRequest.Headers.TryAddWithoutValidation('Origin', 'https://attacker.invalid') | Out-Null
        $unauthorizedRequest.Headers.TryAddWithoutValidation('X-CSRF-TOKEN', $sessionJson.csrfToken) | Out-Null
        $unauthorizedRequest.Content = [Net.Http.StringContent]::new((@{ password = $ownerPassword } | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
        $unauthorizedResponse = $client.SendAsync($unauthorizedRequest).GetAwaiter().GetResult()
        Assert-Equal 403 ([int]$unauthorizedResponse.StatusCode) 'Cross-origin mutation rejection'
        $unauthorizedResponse.Dispose()
        $unauthorizedRequest.Dispose()
        $checks.Add('Cross-origin login mutation is rejected')

        $loginRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$baseTls/api/auth/login")
        $loginRequest.Headers.TryAddWithoutValidation('X-CSRF-TOKEN', $sessionJson.csrfToken) | Out-Null
        $loginRequest.Content = [Net.Http.StringContent]::new((@{ password = $ownerPassword } | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
        $loginResponse = $client.SendAsync($loginRequest).GetAwaiter().GetResult()
        Assert-Equal 200 ([int]$loginResponse.StatusCode) 'Owner login over TLS'
        $loginCookies = @(Get-CookieHeaders $tlsSessionResponse) + @(Get-CookieHeaders $loginResponse)
        Test-CookieSecurity $loginCookies
        $loginJson = $loginResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        Assert-Equal $true $loginJson.authenticated 'Authenticated login response'
        if (!$loginJson.csrfToken) { throw 'Login response did not return a replacement CSRF token.' }
        $loginResponse.Dispose()
        $loginRequest.Dispose()
        $checks.Add('Owner login issues Secure, HttpOnly, SameSite=Strict cookies')

        $protected = $client.GetAsync("$baseTls/api/imports").GetAwaiter().GetResult()
        Assert-Equal 200 ([int]$protected.StatusCode) 'Authenticated API request'
        $protected.Dispose()
        $checks.Add('Authenticated API access succeeds')

        $logoutRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$baseTls/api/auth/logout")
        $logoutRequest.Headers.TryAddWithoutValidation('X-CSRF-TOKEN', $loginJson.csrfToken) | Out-Null
        $logoutResponse = $client.SendAsync($logoutRequest).GetAwaiter().GetResult()
        Assert-Equal 200 ([int]$logoutResponse.StatusCode) 'Owner logout'
        $logoutJson = $logoutResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        Assert-Equal $false $logoutJson.authenticated 'Logged-out session response'
        $logoutResponse.Dispose()
        $logoutRequest.Dispose()
        $afterLogout = $client.GetAsync("$baseTls/api/imports").GetAwaiter().GetResult()
        Assert-Equal 401 ([int]$afterLogout.StatusCode) 'Logout revokes API access'
        $afterLogout.Dispose()
        $checks.Add('Logout revokes the owner session')

        $health = $client.GetAsync("$baseTls/health/ready").GetAwaiter().GetResult()
        Assert-Equal 200 ([int]$health.StatusCode) 'TLS health endpoint'
        $health.Dispose()
        $checks.Add('Health endpoint is available through TLS')
    } finally {
        $client.Dispose()
        $handler.Dispose()
    }

    $resultPath = Join-Path $testRoot 'results.json'
    @{ project = $project; completedAt = [DateTime]::UtcNow.ToString('o'); checks = @($checks); tlsValidation = 'Self-signed local certificate; not a public CA validation.' } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding utf8NoBOM
    Write-Output "TLS deployment checks passed. Evidence: $resultPath"
} finally {
    if ($composeCreated) { Invoke-Compose -Arguments @('down', '--volumes', '--remove-orphans', '--rmi', 'local') }
    if ($networkCreated) { $null = Invoke-Docker @('network', 'rm', $network) }
}
