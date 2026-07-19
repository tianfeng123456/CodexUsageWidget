[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [DateTimeOffset]$ReferenceTime = [DateTimeOffset]::Now
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) {
    $existing = Get-ChildItem -LiteralPath $OutputPath -Force |
        Select-Object -First 1
    if ($null -ne $existing) {
        throw "Demo Codex Home must be empty: $OutputPath"
    }
}
else {
    [IO.Directory]::CreateDirectory($OutputPath) | Out-Null
}

$sessionDirectory = Join-Path $OutputPath (
    'sessions\{0:yyyy}\{0:MM}\{0:dd}' -f $ReferenceTime)
[IO.Directory]::CreateDirectory($sessionDirectory) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)

function ConvertTo-JsonLine {
    param([Parameter(Mandatory = $true)]$Value)

    return $Value | ConvertTo-Json -Compress -Depth 12
}

function New-SessionMetaLine {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][DateTimeOffset]$Timestamp
    )

    return ConvertTo-JsonLine ([ordered]@{
        timestamp = $Timestamp.ToString('O')
        type = 'session_meta'
        payload = [ordered]@{
            session_id = $Id
            id = $Id
            parent_thread_id = $null
        }
    })
}

function New-RateLimit {
    param(
        [Parameter(Mandatory = $true)][DateTimeOffset]$Timestamp,
        [Parameter(Mandatory = $true)][double]$UsedPercent,
        [Parameter(Mandatory = $true)][DateTimeOffset]$ResetsAt
    )

    return [ordered]@{
        limit_id = 'codex'
        limit_name = 'Codex'
        plan_type = 'pro'
        primary = [ordered]@{
            used_percent = $UsedPercent
            window_minutes = 10080
            resets_at = $ResetsAt.ToUnixTimeSeconds()
        }
        secondary = $null
    }
}

function New-WeeklyObservationLine {
    param(
        [Parameter(Mandatory = $true)][DateTimeOffset]$Timestamp,
        [Parameter(Mandatory = $true)][double]$UsedPercent,
        [Parameter(Mandatory = $true)][DateTimeOffset]$ResetsAt
    )

    return ConvertTo-JsonLine ([ordered]@{
        timestamp = $Timestamp.ToString('O')
        type = 'event_msg'
        payload = [ordered]@{
            type = 'token_count'
            info = $null
            rate_limits = New-RateLimit $Timestamp $UsedPercent $ResetsAt
        }
    })
}

function New-TokenCountLine {
    param(
        [Parameter(Mandatory = $true)][DateTimeOffset]$Timestamp,
        [Parameter(Mandatory = $true)][long]$TotalTokens,
        [double]$UsedPercent = -1,
        [DateTimeOffset]$ResetsAt = [DateTimeOffset]::MinValue
    )

    $outputTokens = [Math]::Max(
        1L,
        [Convert]::ToInt64([Math]::Round($TotalTokens * 0.008)))
    $inputTokens = $TotalTokens - $outputTokens
    $cachedInputTokens = [Convert]::ToInt64(
        [Math]::Round($inputTokens * 0.94))
    $reasoningOutputTokens = [Convert]::ToInt64(
        [Math]::Round($outputTokens * 0.35))
    $rateLimits = if ($UsedPercent -ge 0) {
        New-RateLimit $Timestamp $UsedPercent $ResetsAt
    }
    else {
        $null
    }

    return ConvertTo-JsonLine ([ordered]@{
        timestamp = $Timestamp.ToString('O')
        type = 'event_msg'
        payload = [ordered]@{
            type = 'token_count'
            info = [ordered]@{
                total_token_usage = [ordered]@{
                    input_tokens = $inputTokens
                    cached_input_tokens = $cachedInputTokens
                    cache_write_input_tokens = 0
                    output_tokens = $outputTokens
                    reasoning_output_tokens = $reasoningOutputTokens
                    total_tokens = $TotalTokens
                }
            }
            rate_limits = $rateLimits
        }
    })
}

$tasks = @(
    [pscustomobject]@{ Id = '11111111-1111-1111-1111-111111111111'; Title = 'Code review'; Total = 13110000000L }
    [pscustomobject]@{ Id = '22222222-2222-2222-2222-222222222222'; Title = 'Data analysis'; Total = 11850000000L }
    [pscustomobject]@{ Id = '33333333-3333-3333-3333-333333333333'; Title = 'Documentation'; Total = 293801000L }
    [pscustomobject]@{ Id = '44444444-4444-4444-4444-444444444444'; Title = 'Test automation'; Total = 182400000L }
    [pscustomobject]@{ Id = '55555555-5555-5555-5555-555555555555'; Title = 'UI polish'; Total = 94500000L }
    [pscustomobject]@{ Id = '66666666-6666-6666-6666-666666666666'; Title = 'Performance audit'; Total = 67200000L }
    [pscustomobject]@{ Id = '77777777-7777-7777-7777-777777777777'; Title = 'Localization'; Total = 42300000L }
    [pscustomobject]@{ Id = '88888888-8888-8888-8888-888888888888'; Title = 'Release packaging'; Total = 31600000L }
    [pscustomobject]@{ Id = '99999999-9999-9999-9999-999999999999'; Title = 'Bug triage'; Total = 23100000L }
    [pscustomobject]@{ Id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'; Title = 'API integration'; Total = 18400000L }
    [pscustomobject]@{ Id = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'; Title = 'Refactoring'; Total = 12800000L }
    [pscustomobject]@{ Id = 'cccccccc-cccc-cccc-cccc-cccccccccccc'; Title = 'Research'; Total = 7700000L }
)

$dayStart = [DateTimeOffset]::new(
    $ReferenceTime.Year,
    $ReferenceTime.Month,
    $ReferenceTime.Day,
    0,
    0,
    0,
    $ReferenceTime.Offset)
$weeklyUsed = @(12.0, 18.0, 23.0, 29.0, 34.0, 40.0, 46.0)
$resetsAt = $ReferenceTime.AddDays(6).AddHours(8)
$sessionIndexLines = [Collections.Generic.List[string]]::new()

for ($index = 0; $index -lt $tasks.Count; $index++) {
    $task = $tasks[$index]
    $tokenTimestamp = $ReferenceTime.AddMinutes(-1 - $index)
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add((New-SessionMetaLine $task.Id $dayStart.AddDays(-6))) | Out-Null

    if ($index -eq 0) {
        for ($day = 0; $day -lt $weeklyUsed.Count; $day++) {
            $observation = $dayStart.AddDays($day - 6).AddHours(22).AddMinutes(40)
            if ($observation -gt $ReferenceTime.AddMinutes(-5)) {
                $observation = $ReferenceTime.AddMinutes(-5)
            }

            $lines.Add((
                New-WeeklyObservationLine `
                    $observation `
                    $weeklyUsed[$day] `
                    $resetsAt)) | Out-Null
        }

        $lines.Add((
            New-TokenCountLine `
                $tokenTimestamp `
                $task.Total `
                $weeklyUsed[-1] `
                $resetsAt)) | Out-Null
    }
    else {
        $lines.Add((New-TokenCountLine $tokenTimestamp $task.Total)) | Out-Null
    }

    $logPath = Join-Path $sessionDirectory ("rollout-{0}.jsonl" -f $task.Id)
    [IO.File]::WriteAllText(
        $logPath,
        ([string]::Join("`n", $lines) + "`n"),
        $utf8)

    $sessionIndexLines.Add((ConvertTo-JsonLine ([ordered]@{
        id = $task.Id
        thread_name = $task.Title
        updated_at = $tokenTimestamp.ToString('O')
    }))) | Out-Null
}

[IO.File]::WriteAllText(
    (Join-Path $OutputPath 'session_index.jsonl'),
    ([string]::Join("`n", $sessionIndexLines) + "`n"),
    $utf8)

[pscustomobject]@{
    CodexHome = $OutputPath
    TaskCount = $tasks.Count
    WeeklyObservationCount = $weeklyUsed.Count
    RemainingPercent = 54
    ReferenceTime = $ReferenceTime.ToString('O')
}
