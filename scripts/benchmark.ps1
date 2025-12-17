#!/usr/bin/env pwsh
# Cross-platform benchmark comparison script
# Compares benchmark results between current branch and a base branch
#
# Usage:
#   pwsh scripts/benchmark.ps1                       # Run all benchmarks
#   pwsh scripts/benchmark.ps1 -Filter "*Source*"   # Filter by pattern
#   pwsh scripts/benchmark.ps1 -Stash               # Auto-stash uncommitted changes
#   pwsh scripts/benchmark.ps1 -Filter "*Write*" -Stash
#
# Output: benchmark_YYYY-MM-DD_HHmmss.md in current directory

param(
    [string]$Filter = "*",
    [switch]$Stash
)

# ============ CONFIGURATION ============
$BenchmarkProject = "src/Namotion.Interceptor.Benchmark/Namotion.Interceptor.Benchmark.csproj"
$BaseBranch = "master"
# =======================================

$ErrorActionPreference = "Stop"

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
if ($GitStatus) {
    if ($Stash) {
        Write-Host "Stashing uncommitted changes..."
        git stash push -m "benchmark-script-auto-stash" --quiet
        $script:DidStash = $true
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
        Write-Host "Restoring stashed changes..."
        git stash pop --quiet
    }
}

# Helper function to find the latest benchmark markdown file
function Get-LatestBenchmarkResult {
    $artifactsPath = "BenchmarkDotNet.Artifacts/results"
    if (-not (Test-Path $artifactsPath)) {
        return $null
    }
    $latestFile = Get-ChildItem -Path $artifactsPath -Filter "*.md" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    return $latestFile
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
dotnet run --project $BenchmarkProject -c Release -- --filter "$Filter" --exporters markdown
if ($LASTEXITCODE -ne 0) {
    Write-Error "Benchmark failed on $BaseBranch"
    Restore-OriginalBranch
    Restore-Stash
    exit 1
}

# Copy base branch results to temp
$baseResult = Get-LatestBenchmarkResult
if (-not $baseResult) {
    Write-Error "No benchmark results found for $BaseBranch"
    Restore-OriginalBranch
    Restore-Stash
    exit 1
}
Copy-Item $baseResult.FullName $BaseBranchResult

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

Write-Host "Running benchmark on $OriginalBranch (filter: $Filter)..."
dotnet run --project $BenchmarkProject -c Release -- --filter "$Filter" --exporters markdown
if ($LASTEXITCODE -ne 0) {
    Write-Error "Benchmark failed on $OriginalBranch"
    Remove-Item $BaseBranchResult -ErrorAction SilentlyContinue
    Restore-Stash
    exit 1
}

# Copy current branch results to temp
$currentResult = Get-LatestBenchmarkResult
if (-not $currentResult) {
    Write-Error "No benchmark results found for $OriginalBranch"
    Remove-Item $BaseBranchResult -ErrorAction SilentlyContinue
    Restore-Stash
    exit 1
}
Copy-Item $currentResult.FullName $CurrentBranchResult

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

# Restore stashed changes
Restore-Stash

Write-Host ""
Write-Host "Benchmark comparison saved to: $OutputFile" -ForegroundColor Green
