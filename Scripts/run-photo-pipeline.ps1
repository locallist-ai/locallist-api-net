# Load env vars from User environment and run the photo pipeline
$env:R2_ACCOUNT_ID = 'da270186da32e41c7443ad387683733f'
$env:R2_ACCESS_KEY_ID = [Environment]::GetEnvironmentVariable('R2_ACCESS_KEY_ID', 'User')
$env:R2_SECRET_ACCESS_KEY = [Environment]::GetEnvironmentVariable('R2_SECRET_ACCESS_KEY', 'User')
$env:ADMIN_API_KEY = [Environment]::GetEnvironmentVariable('ADMIN_API_KEY', 'User')
$env:YELP_API_KEY = [Environment]::GetEnvironmentVariable('YELP_API_KEY', 'User')

# Test R2 first
Write-Output "=== Testing R2 connection ==="
node test-r2.js

if ($LASTEXITCODE -ne 0) {
    Write-Output "R2 test failed, aborting."
    exit 1
}

Write-Output ""
Write-Output "=== Running photo pipeline ==="
node fetch-yelp-photos-candidates.js candidates-2026-03-29.json
