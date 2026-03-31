$keys = 'R2_ACCOUNT_ID','R2_ACCESS_KEY_ID','R2_SECRET_ACCESS_KEY','ADMIN_API_KEY','YELP_API_KEY'
foreach ($k in $keys) {
    $v = [Environment]::GetEnvironmentVariable($k, 'User')
    if ($v) { Write-Output "$k : SET ($($v.Length) chars)" } else { Write-Output "$k : MISSING" }
}
