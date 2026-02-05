#!/usr/bin/env pwsh
# Cross-platform benchmark comparison script
# Compares benchmark results between current branch and a base branch
#
# Usage:
#   pwsh scripts/benchmark.ps1                       # Run all benchmarks
#   pwsh scripts/benchmark.ps1 -Filter "*Source*"   # Filter by pattern
#   pwsh scripts/benchmark.ps1 -Stash               # Auto-stash uncommitted changes
#   pwsh scripts/benchmark.ps1 -Short               # Quick benchmark (fewer iterations)
#   pwsh scripts/benchmark.ps1 -Filter "*Write*" -Stash -Short
#
# Output: benchmark_YYYY-MM-DD_HHmmss.md in current directory

param(
    [string]$Filter = "*",
    [switch]$Stash,
    [switch]$Short
)

# ============ CONFIGURATION ============
$BenchmarkProject = "src/Namotion.Interceptor.Benchmark/Namotion.Interceptor.Benchmark.csproj"
$BaseBranch = "master"
# =======================================

$ErrorActionPreference = "Stop"
$JobArg = if ($Short) { "--job short" } else { "" }

# Save original branch
$OriginalBranch = git rev-parse --abbrev-ref HEAD
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to get current branch. Are you in a git repository?"
    exit 1
}

Write-Host "Current branch: $OriginalBranch"
Write-Host "Base branch: $BaseBranch"
Write-Host "Filter: $Filter"
Write-Host ""

# Check for uncommitted changes
$GitStatus = git status --porcelain
$script:DidStash = $false
$script:StashedFileCount = 0
if ($GitStatus) {
    if ($Stash) {
        $script:StashedFileCount = ($GitStatus -split "`n").Count
        Write-Host "Stashing $($script:StashedFileCount) uncommitted file(s)..."
        git stash push -u -m "benchmark-script-auto-stash" --quiet
        # Verify stash was actually created
        $stashList = git stash list
        if ($stashList -match "benchmark-script-auto-stash") {
            $script:DidStash = $true
        } else {
            Write-Host "No changes were stashed (files may already be committed or ignored)."
            $script:DidStash = $false
        }
    } else {
        Write-Error "Uncommitted changes detected. Please commit or stash before running benchmarks, or use -Stash option."
        exit 1
    }
}

# Helper function to safely return to original branch
function Restore-OriginalBranch {
    Write-Host "Restoring original branch ($OriginalBranch)..."
    git checkout $OriginalBranch --quiet
}

# Helper function to restore stashed changes
function Restore-Stash {
    if ($script:DidStash) {
        # Find our specific stash by message
        $stashList = git stash list
        $stashIndex = $null
        $index = 0
        foreach ($line in $stashList -split "`n") {
            if ($line -match "benchmark-script-auto-stash") {
                $stashIndex = $index
                break
            }
            $index++
        }

        if ($null -ne $stashIndex) {
            Write-Host "Restoring $($script:StashedFileCount) stashed file(s) from stash@{$stashIndex}..."
            git stash pop "stash@{$stashIndex}" --quiet
        } else {
            Write-Warning "Could not find benchmark stash to restore. Manual recovery may be needed."
        }
    }
}

# Helper function to get benchmark results (--join creates a single combined file)
function Get-BenchmarkResults {
    $artifactsPath = "BenchmarkDotNet.Artifacts/results"
    if (-not (Test-Path $artifactsPath)) {
        return $null
    }
    # BenchmarkDotNet creates *-report-github.md for GitHub-flavored markdown
    $file = Get-ChildItem -Path $artifactsPath -Filter "*-github.md" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $file) {
        return $null
    }
    return (Get-Content $file.FullName -Raw)
}

# Helper function to clean BenchmarkDotNet artifacts
function Clear-BenchmarkArtifacts {
    if (Test-Path "BenchmarkDotNet.Artifacts") {
        Remove-Item -Recurse -Force "BenchmarkDotNet.Artifacts" -ErrorAction SilentlyContinue
    }
}

# Temp files
$Timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$TempBase = [System.IO.Path]::GetTempPath()
$BaseBranchResult = Join-Path $TempBase "benchmark_base_$Timestamp.md"
$CurrentBranchResult = Join-Path $TempBase "benchmark_current_$Timestamp.md"

# Clean any existing artifacts
Clear-BenchmarkArtifacts

# Run benchmark on base branch
Write-Host "Checking out $BaseBranch..."
git checkout $BaseBranch --quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to checkout $BaseBranch"
    Restore-OriginalBranch
    Restore-Stash
    exit 1
}

Write-Host "Running benchmark on $BaseBranch (filter: $Filter)..."
dotnet run --project $BenchmarkProject -c Release -- --filter "$Filter" --exporters markdown --join $JobArg
if ($LASTEXITCODE -ne 0) {
    Write-Error "Benchmark failed on $BaseBranch"
    Restore-OriginalBranch
    Restore-Stash
    exit 1
}

# Save base branch results to temp
$baseResultContent = Get-BenchmarkResults
if (-not $baseResultContent) {
    Write-Error "No benchmark results found for $BaseBranch"
    Restore-OriginalBranch
    Restore-Stash
    exit 1
}
$baseResultContent | Out-File -FilePath $BaseBranchResult -Encoding utf8

# Clean artifacts before next run
Clear-BenchmarkArtifacts

# Run benchmark on original branch
Write-Host ""
Write-Host "Checking out $OriginalBranch..."
git checkout $OriginalBranch --quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to checkout $OriginalBranch"
    Restore-Stash
    exit 1
}

# Restore stash before running benchmark (so uncommitted changes are included)
Restore-Stash

Write-Host "Waiting 5 seconds before running benchmark..."
Start-Sleep -Seconds 5

Write-Host "Running benchmark on $OriginalBranch (filter: $Filter)..."
dotnet run --project $BenchmarkProject -c Release -- --filter "$Filter" --exporters markdown --join $JobArg
if ($LASTEXITCODE -ne 0) {
    Write-Error "Benchmark failed on $OriginalBranch"
    Remove-Item $BaseBranchResult -ErrorAction SilentlyContinue
    exit 1
}

# Save current branch results to temp
$currentResultContent = Get-BenchmarkResults
if (-not $currentResultContent) {
    Write-Error "No benchmark results found for $OriginalBranch"
    Remove-Item $BaseBranchResult -ErrorAction SilentlyContinue
    exit 1
}
$currentResultContent | Out-File -FilePath $CurrentBranchResult -Encoding utf8

# Clean artifacts
Clear-BenchmarkArtifacts

# Build merged report
$OutputFile = "benchmark_$Timestamp.md"

$CurrentBranchContent = Get-Content $CurrentBranchResult -Raw
$BaseBranchContent = Get-Content $BaseBranchResult -Raw

$Report = @"
# Benchmark Comparison

**Date:** $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
**Filter:** $Filter
**Base branch:** $BaseBranch
**Compared branch:** $OriginalBranch

---

## $OriginalBranch (current):

$CurrentBranchContent

---

## $BaseBranch branch:

$BaseBranchContent
"@

# Write final report
$Report | Out-File -FilePath $OutputFile -Encoding utf8

# Cleanup temp files
Remove-Item $BaseBranchResult -ErrorAction SilentlyContinue
Remove-Item $CurrentBranchResult -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Benchmark comparison saved to: $OutputFile" -ForegroundColor Green
