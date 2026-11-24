# Panda Pocket: clear the security evidence before a demonstration.
#
#   .\infra\reset-for-demo.ps1
#
# Why this exists rather than reset-demo.ps1
# ------------------------------------------
# reset-demo.ps1 runs "docker compose down -v", which destroys every volume. That
# is right for starting the whole system from nothing, and wrong before this
# demonstration, because it would also destroy the settlement ledger.
#
# The ledger carries a R247.50 gap between the stored balance and the sum of its
# entries: two settlements that landed in the same second and both computed from
# the same starting figure. It is the strongest thing in the project and it
# cannot be recreated, because the row lock added in Phase 2 prevents it
# happening again. Wipe that volume and the opening of the presentation is gone.
#
# So this script clears exactly two things:
#
#   1. The SOC service's collected events, alerts, incidents and links, so the
#      console starts empty and every alert the marker sees was raised in front
#      of her.
#   2. The graph, which is then reloaded with its structure only. The security
#      knowledge is deliberately NOT loaded here: it joins alerts to threats
#      through the rules that detect them, so it has to run after the attacks
#      have produced rules and alerts to join. Loading it early looks like it
#      works, reports its usual counts, and quietly creates no DETECTS or
#      INDICATES edges at all, which is the join between the two halves of the
#      graph and the thing the hybrid retrieval route walks. Found by comparing
#      the live relationship types against the ones the report lists: 21 where
#      there should have been 23.
#
# Detection rules are not cleared because they do not need to be: the engine
# seeds them at startup and seeds them again if they are missing.
#
# Nothing touches invoice_db, merchant_db or settlement_db.

$ErrorActionPreference = 'Continue'

$docker = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
$root   = Split-Path $PSScriptRoot -Parent

function Step($text) { Write-Host "  $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "  $text" -ForegroundColor Green }
function Warn($text) { Write-Host "  $text" -ForegroundColor Yellow }

Push-Location $root
try {
    Write-Host ""
    Write-Host "Clearing the security evidence, leaving the payment data alone" -ForegroundColor Cyan
    Write-Host ""

    # ---------------------------------------------------------------- SOC
    Step "soc_db: removing events, alerts, incidents"

    # TRUNCATE rather than DELETE: it is one statement, it resets nothing we
    # depend on, and CASCADE follows the link table rather than needing the
    # tables named in dependency order.
    $sql = "TRUNCATE alert_events, alerts, incidents, soc_events CASCADE;"
    & $docker exec -e PGPASSWORD=soc_pw_dev pp-postgres psql -U soc_svc -d soc_db -c $sql | Out-Null

    if ($LASTEXITCODE -eq 0) { Ok "soc_db cleared" } else { Warn "soc_db clear failed with exit $LASTEXITCODE" }

    # ---------------------------------------------------------------- Graph
    Step "neo4j: removing every node and relationship"

    # DETACH DELETE removes a node and its relationships together. Constraints
    # and indexes are schema rather than data and survive, which is why the
    # service does not need to reapply them before loading.
    & $docker exec pp-neo4j cypher-shell -u neo4j -p panda_graph_dev `
        "MATCH (n) DETACH DELETE n" | Out-Null

    if ($LASTEXITCODE -eq 0) { Ok "graph emptied" } else { Warn "graph clear failed with exit $LASTEXITCODE" }

    # ---------------------------------------------------------------- Reload
    Step "reloading the structure, which does not depend on the attacks"

    try {
        Invoke-RestMethod -Method Post -TimeoutSec 300 `
            -Uri "http://localhost:5006/api/graph/load/topology" | Out-Null
        Ok "topology loaded"
    }
    catch {
        Warn "topology failed: $($_.Exception.Message)"
    }

    # ---------------------------------------------------------------- Report
    Write-Host ""
    try {
        $stats = Invoke-RestMethod -Uri "http://localhost:5006/api/graph/stats" -TimeoutSec 60
        Ok ("graph now holds {0} nodes and {1} relationships" -f $stats.totalNodes, $stats.totalRelationships)
    } catch { Warn "could not read the graph size: $($_.Exception.Message)" }

    Write-Host ""
    Write-Host "  The SOC console is now empty. Next:" -ForegroundColor Green
    Write-Host ""
    Write-Host "    1.  .\infra\simulate-attacks.ps1" -ForegroundColor Green
    Write-Host "    2.  Invoke-RestMethod -Method Post http://localhost:5006/api/graph/load/all" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Use load/all, not load/soc. It loads knowledge last, which is the" -ForegroundColor Green
    Write-Host "  only order in which alerts can be joined to the threats they indicate." -ForegroundColor Green
    Write-Host ""
}
finally {
    Pop-Location
}
