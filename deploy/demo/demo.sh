#!/bin/sh
# The compose demo, end to end, with nothing but BusyBox (wget, sed, grep, dd, base64). One synthetic person (seed 2026,
# person #95: a patient file, invoices, an online-portal account, and their phone in someone else's appointment note)
# asks for access and erasure through the web portal:
#   1. intake registers the application; the 30-day deadline is computed
#   2. a one-time code goes to the phone ON FILE (read from the demo inbox) and is confirmed; with the matching birth
#      date that reaches the "high" assurance erasure needs
#   3. discovery over the inventory and foreign keys; a plan per row under the retention policy
#   4. the planner tries to approve their own plan: refused (four eyes)
#   5. the access export is produced before anything is erased
#   6. a second person approves exactly that plan hash; the executor applies it; the post-check is clean
#   7. the answer letter is written and the identifiers are purged
#   8. the signed evidence pack verifies; a copy with one flipped byte does not
# Exits 0 only if every check holds. Re-runnable: `docker compose run --rm seed` restores the person first.
set -eu

API="${API_URL:-http://api:8080}"
WORK=$(mktemp -d)
FAILURES=""

fail() { FAILURES="${FAILURES}${FAILURES:+, }\"$1\""; echo "CHECK FAILED: $1" >&2; }
json() { sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p" "$2" | head -n 1; }
status_of() { sed -n 's/^ *HTTP\/[0-9.]* \([0-9]*\).*/\1/p' "$1" | tail -n 1; }

# call NAME KEY METHOD PATH [JSON]: body to $WORK/NAME, HTTP status echoed. BusyBox wget exits non-zero on 4xx/5xx.
call() {
    name=$1; key=$2; method=$3; path=$4; data=${5:-}
    if [ "$method" = POST ]; then
        wget -q -S -O "$WORK/$name" --header "X-Api-Key: $key" --header "Content-Type: application/json" \
            --post-data "$data" "$API$path" 2>"$WORK/$name.h" || true
    else
        wget -q -S -O "$WORK/$name" --header "X-Api-Key: $key" "$API$path" 2>"$WORK/$name.h" || true
    fi
    status_of "$WORK/$name.h"
}

echo "waiting for $API/health/ready"
i=0
until wget -q -O /dev/null "$API/health/ready" 2>/dev/null; do
    i=$((i + 1)); [ "$i" -le 90 ] || { echo "API not ready after 90 s" >&2; exit 1; }; sleep 1
done

SUBJECT='{"nationalId":"59320200034","email":"arik.eronat6868@outlook.com","phone":"0532 935 05 13","firstName":"Arık","lastName":"Eronat","birthDate":"1998-06-11"}'

# 1. Intake.
[ "$(call create demo-intake-key POST /api/v1/requests "{\"types\":[\"Access\",\"Erasure\"],\"channel\":\"WebPortal\",\"subject\":$SUBJECT}")" = 201 ] || fail "request created"
ID=$(json id "$WORK/create"); REF=$(json reference "$WORK/create"); DUE=$(json dueOn "$WORK/create")
echo "registered $REF ($ID), due $DUE, required assurance $(json requiredAssurance "$WORK/create")"

# 2. OTP to the phone on file.
[ "$(call challenge demo-verifier-key POST "/api/v1/requests/$ID/verification/challenge" '')" = 200 ] || fail "otp sent"
echo "one-time code sent to $(json sentTo "$WORK/challenge")"
call inbox demo-verifier-key GET "/demo/inbox/$ID" >/dev/null
CODE=$(json code "$WORK/inbox")
call confirm demo-verifier-key POST "/api/v1/requests/$ID/verification/confirm" "{\"code\":\"$CODE\"}" >/dev/null
grep -q '"status":"Verified"' "$WORK/confirm" || fail "identity verified"
grep -q '"assurance":"High"' "$WORK/confirm" || fail "high assurance (otp + birth date)"

# 3. Discovery and plan.
[ "$(call discovery demo-dpo-key POST "/api/v1/requests/$ID/discovery" '')" = 200 ] || fail "discovery"
echo "discovery: $(sed -n 's/.*"owned":\({[^}]*}\).*/\1/p' "$WORK/discovery") mentions=$(sed -n 's/.*"mentions":\([0-9]*\).*/\1/p' "$WORK/discovery")"
[ "$(call plan demo-dpo2-key POST "/api/v1/requests/$ID/plan" '')" = 200 ] || fail "plan"
HASH=$(json planHash "$WORK/plan")
echo "plan $HASH: $(sed -n 's/.*"counts":\({[^}]*}\).*/\1/p' "$WORK/plan")"

# 4. Four eyes: the planner (mert.dpo also holds the approver role) cannot approve their own plan.
SELF=$(call selfapprove demo-dpo2-key POST "/api/v1/requests/$ID/approve" "{\"planHash\":\"$HASH\"}")
[ "$SELF" = 409 ] || fail "self-approval refused"
echo "self-approval by the planner: HTTP $SELF (four eyes)"

# 5. Access export before erasure.
[ "$(call export demo-dpo-key GET "/api/v1/requests/$ID/export")" = 200 ] || fail "access export"
grep -q 'Eronat' "$WORK/export" || fail "export holds the subject's data"
EXPORT_SHA=$(sed -n 's/^ *X-Export-Sha256: *//Ip' "$WORK/export.h" | tr -d '\r' | tail -n 1)
[ "$(sha256sum "$WORK/export" | cut -d' ' -f1)" = "$EXPORT_SHA" ] || fail "export hash header"
echo "access export: $(wc -c < "$WORK/export") bytes, sha256 $EXPORT_SHA (only the hash is stored)"

# 6. Approve, execute.
[ "$(call approve demo-legal-key POST "/api/v1/requests/$ID/approve" "{\"planHash\":\"$HASH\"}")" = 200 ] || fail "approval"
[ "$(call execute demo-ops-key POST "/api/v1/requests/$ID/execute" '')" = 200 ] || fail "execution"
grep -q '"status":"Executed"' "$WORK/execute" || fail "executed with a clean post-check"
echo "executed: deleted $(sed -n 's/.*"rowsDeleted":\([0-9]*\).*/\1/p' "$WORK/execute"), updated $(sed -n 's/.*"rowsUpdated":\([0-9]*\).*/\1/p' "$WORK/execute"), unexpected residual $(grep -o '"unexpected":\[[^]]*\]' "$WORK/execute")"

# 7. Answer.
[ "$(call respond demo-dpo-key POST "/api/v1/requests/$ID/respond" '')" = 200 ] || fail "response"
grep -q '"identifiersPurged":true' "$WORK/respond" || fail "identifiers purged"
grep -q 'customers (1)' "$WORK/respond" || fail "letter lists the erased portal account"
grep -q "$EXPORT_SHA" "$WORK/respond" || fail "letter cites the export hash"

# 8. Evidence.
[ "$(call evidence demo-audit-key GET "/api/v1/requests/$ID/evidence")" = 200 ] || fail "evidence pack"
base64 "$WORK/evidence" > "$WORK/evidence.b64"   # BusyBox wget cannot post binary; the API accepts base64
wget -q -O "$WORK/verify" --header "X-Api-Key: demo-audit-key" --header "Content-Type: application/base64" --post-file "$WORK/evidence.b64" "$API/api/v1/evidence/verify" || true
grep -q '"valid":true' "$WORK/verify" || fail "evidence verifies"
cp "$WORK/evidence" "$WORK/tampered"
SIZE=$(wc -c < "$WORK/tampered"); OFFSET=$((SIZE / 2))
BYTE=$(dd if="$WORK/tampered" bs=1 skip="$OFFSET" count=1 2>/dev/null | od -An -tu1 | tr -d ' ')
printf "$(printf '\\%03o' $(( (BYTE + 1) % 256 )))" | dd of="$WORK/tampered" bs=1 seek="$OFFSET" conv=notrunc 2>/dev/null
base64 "$WORK/tampered" > "$WORK/tampered.b64"
wget -q -O "$WORK/verify2" --header "X-Api-Key: demo-audit-key" --header "Content-Type: application/base64" --post-file "$WORK/tampered.b64" "$API/api/v1/evidence/verify" || true
grep -q '"valid":false' "$WORK/verify2" || fail "tampered evidence rejected"
echo "evidence: $(wc -c < "$WORK/evidence") bytes, $(sed -n 's/.*"files":\([0-9]*\).*/\1/p' "$WORK/verify") files, key $(json keyId "$WORK/verify"); tampered copy: $(sed -n 's/.*"problems":\["\([^"]*\)".*/\1/p' "$WORK/verify2")"

echo
sed -n 's/.*"letterEn":"\(.*\)"}$/\1/p' "$WORK/respond" | sed 's/\\n/\n/g; s/\\u0022/"/g' | head -n 40
echo

echo "{\"demo\":\"kvkk-dsar\",\"reference\":\"$REF\",\"status\":\"$(json status "$WORK/respond")\",\"selfApproval\":$SELF,\"evidenceValid\":$(grep -q '"valid":true' "$WORK/verify" && echo true || echo false),\"tamperDetected\":$(grep -q '"valid":false' "$WORK/verify2" && echo true || echo false),\"failures\":[$FAILURES]}"
[ -z "$FAILURES" ]
