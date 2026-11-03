#!/usr/bin/env bash
# Смоук-тесты по основным FR через curl.  Запуск: ./scripts/smoke.sh [http://localhost:5080]
BASE="${1:-http://localhost:5080}"
EMAIL="smoke_$(date +%s)@example.com"; EMAIL2="smoke2_$(date +%s)@example.com"; PASS="Passw0rd!123"
pass=0; fail=0; TMP="$(mktemp)"

req() { # метод путь ожидаемый_код [json] [token]  -> тело в $BODY
  local m=$1 p=$2 exp=$3 d=${4:-} t=${5:-} args
  args=(-s -o "$TMP" -w '%{http_code}' -X "$m" "$BASE$p")
  [ -n "$d" ] && args+=(-H 'Content-Type: application/json' -d "$d")
  [ -n "$t" ] && args+=(-H "Authorization: Bearer $t")
  local code; code=$(curl "${args[@]}"); BODY=$(cat "$TMP")
  if [ "$code" = "$exp" ]; then echo "PASS  $m $p -> $code"; pass=$((pass+1))
  else echo "FAIL  $m $p -> $code (ожидали $exp): $BODY"; fail=$((fail+1)); fi
}
field() { echo "$BODY" | sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p"; }
has()   { if echo "$BODY" | grep -q "$1"; then echo "PASS  тело содержит $1"; pass=$((pass+1)); else echo "FAIL  тело не содержит $1: $BODY"; fail=$((fail+1)); fi; }

req POST /api/auth/register 201 "{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}"          # FR-01
req POST /api/auth/register 409 "{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}"          # email уникален
req POST /api/auth/register 422 '{"email":"bad","password":"x"}'                         # FR-08
req POST /api/auth/login    401 "{\"email\":\"$EMAIL\",\"password\":\"wrong\"}"
req POST /api/auth/login    200 "{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}"          # FR-02
TOKEN=$(field accessToken)
req GET  /api/users/me      401                                                          # без токена
req GET  /api/users/me      200 "" "$TOKEN"                                              # FR-03; has $EMAIL
req POST /api/subscriptions 422 '{"name":"X","amount":-1,"currency":"USD","period":"Monthly","startDate":"2026-01-15"}' "$TOKEN"
req POST /api/subscriptions 201 '{"name":"Netflix","amount":9.99,"currency":"USD","period":"Monthly","startDate":"2026-01-15"}' "$TOKEN"   # FR-04
SUB=$(field id)
req GET  "/api/subscriptions" 200 "" "$TOKEN"
req GET  "/api/subscriptions/$SUB" 200 "" "$TOKEN"
req PATCH "/api/subscriptions/$SUB" 200 '{"amount":12.5}' "$TOKEN"
req POST /api/auth/register 201 "{\"email\":\"$EMAIL2\",\"password\":\"$PASS\"}"
req POST /api/auth/login    200 "{\"email\":\"$EMAIL2\",\"password\":\"$PASS\"}"
TOKEN2=$(field accessToken)
req GET  "/api/subscriptions/$SUB" 403 "" "$TOKEN2"                                      # FR-07
req POST "/api/subscriptions/$SUB/payments" 201 '{"amount":12.5,"currency":"USD","paymentDate":"2026-02-15","externalId":"ext-smoke-1"}' "$TOKEN"  # FR-05
req POST "/api/subscriptions/$SUB/payments" 200 '{"amount":12.5,"currency":"USD","paymentDate":"2026-02-15","externalId":"ext-smoke-1"}' "$TOKEN"  # NFR-03: без дубликата
req GET  "/api/payments" 200 "" "$TOKEN"; has ext-smoke-1
req GET  "/api/analytics/expenses?dateFrom=2026-01-01&dateTo=2026-12-31" 200 "" "$TOKEN"; has '"total":12.5'   # FR-06
req GET  "/api/analytics/expenses" 422 "" "$TOKEN"
req DELETE "/api/subscriptions/$SUB" 200 "" "$TOKEN"
req GET  "/api/subscriptions/$SUB" 404 "" "$TOKEN"
req GET  "/api/payments?subscriptionId=$SUB" 200 "" "$TOKEN"; has ext-smoke-1            # история сохранена

rm -f "$TMP"; echo; echo "Итого: PASS=$pass FAIL=$fail"; [ "$fail" -eq 0 ]
