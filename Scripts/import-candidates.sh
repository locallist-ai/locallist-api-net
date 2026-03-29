#!/bin/bash
# Import candidate places into LocalList via the admin bulk API endpoint.
#
# Usage:
#   ./import-candidates.sh <candidates-json-file> <api-url> <auth-token>
#
# Auth: The script auto-detects the token type:
#   - If the token starts with "ey" (JWT), it uses: Authorization: Bearer <token>
#   - Otherwise, it uses: X-Admin-Key: <token>
#
# Examples:
#   # With API Key (recommended for scripts):
#   ./import-candidates.sh candidates-2026-03-28.json https://api.locallist.app abc123-admin-key
#
#   # With JWT token:
#   ./import-candidates.sh candidates-2026-03-28.json https://api.locallist.app eyJhbGciOi...
#
#   # Local development:
#   ./import-candidates.sh candidates-2026-03-28.json http://localhost:5000 my-dev-key
#
# The endpoint POST /admin/places/bulk accepts a JSON array of CreatePlaceRequest objects
# and returns: { created, skipped, errors, items: [...] }

set -euo pipefail

# --- Validate arguments ---
if [ $# -lt 3 ]; then
    echo "Usage: $0 <candidates-json-file> <api-url> <auth-token>"
    echo ""
    echo "  candidates-json-file  Path to the JSON file output by SeedData.js"
    echo "  api-url               Base URL of the API (e.g. https://api.locallist.app)"
    echo "  auth-token            JWT token (starts with 'ey') or Admin API Key"
    exit 1
fi

JSON_FILE="$1"
API_URL="${2%/}"  # Strip trailing slash if present
AUTH_TOKEN="$3"

# --- Validate JSON file ---
if [ ! -f "$JSON_FILE" ]; then
    echo "ERROR: File not found: $JSON_FILE"
    exit 1
fi

CANDIDATE_COUNT=$(cat "$JSON_FILE" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))" 2>/dev/null || echo "?")
echo "=== LocalList Candidate Import ==="
echo "File:       $JSON_FILE"
echo "API:        $API_URL/admin/places/bulk"
echo "Candidates: $CANDIDATE_COUNT"
echo ""

# --- Determine auth header ---
AUTH_HEADER=""
if [[ "$AUTH_TOKEN" == ey* ]]; then
    AUTH_HEADER="Authorization: Bearer $AUTH_TOKEN"
    echo "Auth mode:  JWT Bearer token"
else
    AUTH_HEADER="X-Admin-Key: $AUTH_TOKEN"
    echo "Auth mode:  Admin API Key"
fi

echo ""
echo "Importing..."

# --- POST to bulk endpoint ---
HTTP_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$API_URL/admin/places/bulk" \
    -H "Content-Type: application/json" \
    -H "$AUTH_HEADER" \
    -d @"$JSON_FILE")

# Split response body and HTTP status code
HTTP_BODY=$(echo "$HTTP_RESPONSE" | head -n -1)
HTTP_STATUS=$(echo "$HTTP_RESPONSE" | tail -n 1)

echo ""
echo "HTTP Status: $HTTP_STATUS"

if [ "$HTTP_STATUS" -ge 200 ] && [ "$HTTP_STATUS" -lt 300 ]; then
    echo ""
    echo "=== Import Result ==="
    # Pretty-print if python3 is available, otherwise raw output
    echo "$HTTP_BODY" | python3 -m json.tool 2>/dev/null || echo "$HTTP_BODY"
else
    echo ""
    echo "ERROR: Import failed (HTTP $HTTP_STATUS)"
    echo "$HTTP_BODY" | python3 -m json.tool 2>/dev/null || echo "$HTTP_BODY"
    exit 1
fi
