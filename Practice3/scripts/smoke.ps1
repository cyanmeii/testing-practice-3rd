# Смоук-тесты (Windows PowerShell 5.1+ / PowerShell 7).  Запуск: .\scripts\smoke.ps1 [-Base http://localhost:5080]
param([string]$Base = "http://localhost:5080")
$ErrorActionPreference = "Continue"
$stamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$email = "smoke_$stamp@example.com"; $email2 = "smoke2_$stamp@example.com"; $pass = "Passw0rd!123"
$script:ok = 0; $script:bad = 0; $script:body = $null

function Req($method, $path, [int]$expected, $json = $null, $token = $null) {
    $h = @{}; if ($token) { $h["Authorization"] = "Bearer $token" }
    $p = @{ Uri = "$Base$path"; Method = $method; Headers = $h; UseBasicParsing = $true }
    if ($json) { $p.Body = $json; $p.ContentType = "application/json" }
    try { $r = Invoke-WebRequest @p; $code = [int]$r.StatusCode; $script:body = $r.Content }
    catch {
        $code = [int]$_.Exception.Response.StatusCode
        try { $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $script:body = $sr.ReadToEnd() } catch { $script:body = "" }
    }
    if ($code -eq $expected) { Write-Host "PASS  $method $path -> $code" -ForegroundColor Green; $script:ok++ }
    else { Write-Host "FAIL  $method $path -> $code (ожидали $expected): $($script:body)" -ForegroundColor Red; $script:bad++ }
}
function Has($text) {
    if ($script:body -like "*$text*") { Write-Host "PASS  тело содержит $text" -ForegroundColor Green; $script:ok++ }
    else { Write-Host "FAIL  тело не содержит $text : $($script:body)" -ForegroundColor Red; $script:bad++ }
}
function J($o) { $o | ConvertTo-Json -Compress }

Req POST /api/auth/register 201 (J @{email=$email; password=$pass})
Req POST /api/auth/register 409 (J @{email=$email; password=$pass})
Req POST /api/auth/register 422 (J @{email="bad"; password="x"})
Req POST /api/auth/login 401 (J @{email=$email; password="wrong"})
Req POST /api/auth/login 200 (J @{email=$email; password=$pass})
$token = ($script:body | ConvertFrom-Json).accessToken
Req GET /api/users/me 401
Req GET /api/users/me 200 $null $token; Has $email
Req POST /api/subscriptions 422 '{"name":"X","amount":-1,"currency":"USD","period":"Monthly","startDate":"2026-01-15"}' $token
Req POST /api/subscriptions 201 '{"name":"Netflix","amount":9.99,"currency":"USD","period":"Monthly","startDate":"2026-01-15"}' $token
$sub = ($script:body | ConvertFrom-Json).id
Req GET /api/subscriptions 200 $null $token
Req GET "/api/subscriptions/$sub" 200 $null $token
Req PATCH "/api/subscriptions/$sub" 200 '{"amount":12.5}' $token
Req POST /api/auth/register 201 (J @{email=$email2; password=$pass})
Req POST /api/auth/login 200 (J @{email=$email2; password=$pass})
$token2 = ($script:body | ConvertFrom-Json).accessToken
Req GET "/api/subscriptions/$sub" 403 $null $token2
$pay = '{"amount":12.5,"currency":"USD","paymentDate":"2026-02-15","externalId":"ext-smoke-1"}'
Req POST "/api/subscriptions/$sub/payments" 201 $pay $token
Req POST "/api/subscriptions/$sub/payments" 200 $pay $token
Req GET /api/payments 200 $null $token; Has "ext-smoke-1"
Req GET "/api/analytics/expenses?dateFrom=2026-01-01&dateTo=2026-12-31" 200 $null $token; Has '"total":12.5'
Req GET /api/analytics/expenses 422 $null $token
Req DELETE "/api/subscriptions/$sub" 200 $null $token
Req GET "/api/subscriptions/$sub" 404 $null $token
Req GET "/api/payments?subscriptionId=$sub" 200 $null $token; Has "ext-smoke-1"

Write-Host "`nИтого: PASS=$($script:ok) FAIL=$($script:bad)"
if ($script:bad -gt 0) { exit 1 }
