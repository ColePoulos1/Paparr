$ErrorActionPreference = 'Stop'

function Invoke-Docker([string[]]$Arguments) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$images = @("paparr-proxy-trust-current:$suffix", "paparr-proxy-trust-legacy:$suffix")
$network = "paparr-proxy-trust-$suffix"
$containers = @("paparr-proxy-legacy-$suffix", "paparr-proxy-untrusted-$suffix", "paparr-proxy-trusted-$suffix", "paparr-proxy-trusted-http-$suffix")
$context = Join-Path ([IO.Path]::GetTempPath()) "paparr-proxy-test-$suffix"

try {
    New-Item -ItemType Directory -Path $context | Out-Null
    $nginxConfig = Get-Content -Raw (Join-Path $PSScriptRoot '..\docker\nginx.conf')
    $nginxConfig = $nginxConfig.Replace('http://paparr-api:8080', 'http://127.0.0.1:18080')
    $currentContext = Join-Path $context 'current'
    $legacyContext = Join-Path $context 'legacy'
    New-Item -ItemType Directory -Path $currentContext, $legacyContext | Out-Null
    Set-Content -Path (Join-Path $currentContext 'default.conf') -Value $nginxConfig -NoNewline

    $legacyConfig = $nginxConfig.Replace('map "$trusted_proxy:$http_x_forwarded_proto" $public_scheme {', 'map $http_x_forwarded_proto $public_scheme {')
    $legacyConfig = $legacyConfig.Replace('"1:https" https;', 'https https;')
    if ($legacyConfig -eq $nginxConfig) { throw 'Could not construct the vulnerable legacy proxy config for the regression.' }
    Set-Content -Path (Join-Path $legacyContext 'default.conf') -Value $legacyConfig -NoNewline
    foreach ($buildContext in @($currentContext, $legacyContext)) {
        Copy-Item (Join-Path $PSScriptRoot '..\docker\entrypoint.sh') (Join-Path $buildContext 'entrypoint.sh')
    }

    $upstream = @'
from http.server import BaseHTTPRequestHandler, HTTPServer

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        value = self.headers.get("X-Forwarded-Proto", "<missing>").encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(value)))
        self.end_headers()
        self.wfile.write(value)

HTTPServer(("127.0.0.1", 18080), Handler).serve_forever()
'@
    foreach ($buildContext in @($currentContext, $legacyContext)) { Set-Content -Path (Join-Path $buildContext 'upstream.py') -Value $upstream }

    $dockerfile = @'
FROM nginxinc/nginx-unprivileged:stable-alpine
USER root
RUN apk add --no-cache python3
COPY default.conf /etc/nginx/conf.d/default.conf
COPY entrypoint.sh /usr/local/bin/paparr-entrypoint.sh
COPY upstream.py /tmp/upstream.py
RUN chmod 755 /usr/local/bin/paparr-entrypoint.sh
    ENTRYPOINT ["/usr/local/bin/paparr-entrypoint.sh"]
    CMD ["nginx", "-g", "daemon off;"]
USER 101
'@
    foreach ($buildContext in @($currentContext, $legacyContext)) { Set-Content -Path (Join-Path $buildContext 'Dockerfile') -Value $dockerfile }

    Invoke-Docker @('build', '--quiet', '--tag', $images[0], $currentContext)
    Invoke-Docker @('build', '--quiet', '--tag', $images[1], $legacyContext)
    Invoke-Docker @('network', 'create', $network)

    $modes = @(
        @{ Name = $containers[0]; TestImage = $images[1]; TrustedCidrs = ''; RequestProto = 'https'; Expected = 'https'; Description = 'legacy config reproduces the spoofed HTTPS scheme' },
        @{ Name = $containers[1]; TestImage = $images[0]; TrustedCidrs = ''; RequestProto = 'https'; Expected = 'http'; Description = 'untrusted client cannot spoof HTTPS' },
        @{ Name = $containers[2]; TestImage = $images[0]; TrustedCidrs = '127.0.0.1/32;::1/128'; RequestProto = 'https'; Expected = 'https'; Description = 'explicitly trusted IPv4 and IPv6 peers may assert HTTPS' },
        @{ Name = $containers[3]; TestImage = $images[0]; TrustedCidrs = '127.0.0.1/32'; RequestProto = 'http'; Expected = 'http'; Description = 'trusted peer forwarding HTTP stays HTTP' }
    )

    foreach ($mode in $modes) {
        Invoke-Docker @('run', '--detach', '--name', $mode.Name, '--network', $network,
            '--env', "UI_TRUSTED_PROXY_NETWORKS=$($mode.TrustedCidrs)", '--entrypoint', '/bin/sh', $mode.TestImage,
            '-c', "python3 /tmp/upstream.py & exec /usr/local/bin/paparr-entrypoint.sh nginx -g 'daemon off;'" ) | Out-Null

        $ready = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            & docker exec $mode.Name wget -qO- http://127.0.0.1:8080/health/ready *> $null
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }
            Start-Sleep -Seconds 1
        }
        if (!$ready) {
            & docker logs $mode.Name
            throw "Nginx did not start for test case '$($mode.Description)'."
        }

        $actual = & docker exec $mode.Name wget -qO- --header="X-Forwarded-Proto: $($mode.RequestProto)" http://127.0.0.1:8080/api/proxy-trust
        if ($LASTEXITCODE -ne 0) { throw "Proxy request failed for test case '$($mode.Description)'." }
        if ($actual -ne $mode.Expected) {
            throw "Expected upstream X-Forwarded-Proto '$($mode.Expected)' for '$($mode.Description)', got '$actual'."
        }
        Write-Host "PASS: $($mode.Description)"
        Invoke-Docker @('rm', '--force', $mode.Name)
    }

    foreach ($invalidCidrs in @('127.0.0.1/32;include /etc/passwd', '127.0.0.1/999')) {
        & docker run --rm --name "paparr-proxy-invalid-$suffix" --env "UI_TRUSTED_PROXY_NETWORKS=$invalidCidrs" `
            --entrypoint /usr/local/bin/paparr-entrypoint.sh $images[0] nginx -t *> $null
        if ($LASTEXITCODE -eq 0) { throw "Invalid trusted proxy CIDRs unexpectedly started Nginx: $invalidCidrs" }
        Write-Host "PASS: invalid trusted proxy value rejected before startup: $invalidCidrs"
    }
}
finally {
    foreach ($container in $containers) { & docker rm --force $container *> $null }
    & docker rm --force "paparr-proxy-invalid-$suffix" *> $null
    & docker network rm $network *> $null
    foreach ($testImage in $images) { & docker image rm $testImage *> $null }
    $resolvedContext = [IO.Path]::GetFullPath($context)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedContext.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedContext) -notmatch '^paparr-proxy-test-[0-9a-f]{12}$') {
        throw 'Refusing to delete an unexpected proxy test directory.'
    }
    if (Test-Path -LiteralPath $resolvedContext) { Remove-Item -LiteralPath $resolvedContext -Recurse -Force }
}
