#!/bin/bash
# Import Miami curated places AND plans via admin bulk endpoints.
#
# Usage:
#   ./import-curated.sh <API_BASE_URL> <JWT_TOKEN>
#
# Examples:
#   ./import-curated.sh http://localhost:5000 eyJhbGciOi...
#   ./import-curated.sh https://locallist-api.up.railway.app eyJhbGciOi...
#
# Steps:
#   1. Imports 40 curated places as "published"
#   2. Creates 6 showcase plans referencing those places by name

set -euo pipefail

API_URL="${1:?Usage: $0 <API_BASE_URL> <JWT_TOKEN>}"
TOKEN="${2:?Usage: $0 <API_BASE_URL> <JWT_TOKEN>}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "=== Step 1: Import places ==="
curl -s -X POST "$API_URL/admin/places/bulk" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d @"$SCRIPT_DIR/miami_curated_import.json" | python3 -m json.tool 2>/dev/null || cat

echo ""
echo "=== Step 2: Create curated plans ==="
curl -s -X POST "$API_URL/admin/plans/bulk" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d @"$SCRIPT_DIR/miami_curated_plans.json" | python3 -m json.tool 2>/dev/null || cat

echo ""
echo "Done. Places and plans imported."
