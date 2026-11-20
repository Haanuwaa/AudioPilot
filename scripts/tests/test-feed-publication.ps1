Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = Join-Path $repo "artifacts/testresults/feed-publication/$([guid]::NewGuid().ToString('N'))"
$feed = Join-Path $root 'feed'
[IO.Directory]::CreateDirectory((Join-Path $feed 'metadata')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $feed 'targets')) | Out-Null
Set-Content -LiteralPath (Join-Path $feed 'bootstrap.root.json') -Value 'synthetic root'
Set-Content -LiteralPath (Join-Path $feed 'metadata/2.snapshot.json') -Value 'synthetic snapshot'
Set-Content -LiteralPath (Join-Path $feed 'metadata/2.targets.json') -Value 'synthetic targets'
@{signed=@{version=2}} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $feed 'metadata/timestamp.json')
$temporaryPackage = Join-Path $feed 'targets/package.zip'
Set-Content -LiteralPath $temporaryPackage -Value 'synthetic package'
$packageHash = (Get-FileHash -LiteralPath $temporaryPackage).Hash.ToLowerInvariant()
$packageName = "$packageHash.package.zip"
Move-Item -LiteralPath $temporaryPackage -Destination (Join-Path $feed "targets/$packageName")
$stampHash = (Get-FileHash -LiteralPath (Join-Path $feed 'metadata/timestamp.json')).Hash.ToLowerInvariant()
@{version=2;rootVersion=1;files=@('bootstrap.root.json','metadata/2.snapshot.json','metadata/2.targets.json',"targets/$packageName",'metadata/timestamp.json');artifacts=@(@{name=$packageName;length=(Get-Item -LiteralPath (Join-Path $feed "targets/$packageName")).Length;sha256=$packageHash})} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'verification.json')
@'
$request = ($input | Out-String) | ConvertFrom-Json
if ($request.operation -cne 'check' -or $request.channel -cnotin @('stable','staging')) { throw 'The fixture verifier cannot sign or publish.' }
$result = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'verification.json') -Raw | ConvertFrom-Json
$result | Add-Member -NotePropertyName channel -NotePropertyValue $request.channel
if ($request.metadataOnly) { $result.files = @($result.files | Where-Object { -not $_.StartsWith('targets/') }) }
$global:LASTEXITCODE = 0
$result | ConvertTo-Json -Depth 8
'@ | Set-Content -LiteralPath (Join-Path $root 'verifier.ps1')
function Add-FixtureAsset([string]$Name, [string]$Source) {
    $destination = Join-Path $global:feedFixture.remote $Name
    Copy-Item -LiteralPath $Source -Destination $destination
    $global:feedFixture.nextId++
    $asset = @{id=$global:feedFixture.nextId;name=$Name;state='uploaded';size=(Get-Item -LiteralPath $destination).Length;digest=('sha256:'+(Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant())}
    $global:feedFixture.assets.Add($asset)
    return $asset
}
function gh {
    [CmdletBinding(PositionalBinding=$false)]
    param([Parameter(ValueFromPipeline)]$InputValue, [Parameter(ValueFromRemainingArguments)]$Arguments)
    $a = @($Arguments)
    $global:LASTEXITCODE = 0
    $f = $global:feedFixture
    $f.calls.Add(($a -join ' '))
    if ($a[0] -ceq 'api') {
        $methodIndex = [Array]::IndexOf($a,'--method')
        $method = if ($methodIndex -ge 0) { $a[$methodIndex+1] } else { 'GET' }
        $path = if ($methodIndex -ge 0) { $a[$methodIndex+2] } else { $a[1] }
        if (-not $path.StartsWith("repos/$($f.repository)/", [StringComparison]::Ordinal)) { throw 'Publication crossed its destination repository.' }
        if ($path -clike '*/git/ref/tags/*') {
            if (-not $path.EndsWith("/_updater-$($f.channel)-publication-lock",[StringComparison]::Ordinal)) { throw 'A publication crossed its channel lease.' }
            return ('a'*40)
        }
        if ($path -ceq "repos/$($f.repository)/git/ref/heads/main") { return ('a'*40) }
        if ($method -ceq 'POST' -and $path -ceq "repos/$($f.repository)/releases") {
            $creation=$InputValue | ConvertFrom-Json
            if ($f.created -or -not $creation.draft -or -not $creation.prerelease -or $creation.make_latest -cne 'false' -or $creation.tag_name -cne "updater-$($f.channel)-feed") { throw 'Invalid initial feed creation.' }
            $f.created=$true; $f.draft=$true
            return (@{id=42;tag_name=$creation.tag_name;draft=$true;prerelease=$true;name=$creation.name;body=$creation.body;assets=@()} | ConvertTo-Json -Depth 8)
        }
        if ($path -cmatch '/releases/assets/([0-9]+)$') {
            $asset = @($f.assets.ToArray() | Where-Object id -eq ([long]$Matches[1]))
            if ($asset.Count -ne 1) { $global:LASTEXITCODE=1; return }
            $asset = $asset[0]
            if ($method -ceq 'DELETE') {
                $f.deleted.Add($asset.name)
                $f.assets.Remove($asset) | Out-Null
                $file = Join-Path $f.remote $asset.name
                if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
                return
            }
            if ($method -ceq 'PATCH') {
                $name = $a[[Array]::IndexOf($a,'-f')+1].Substring(5)
                if (@($f.assets.ToArray() | Where-Object name -CEQ $name).Count) { $global:LASTEXITCODE=1; return }
                $promote = $name -ceq 'timestamp.json' -and $asset.name.EndsWith('.pending',[StringComparison]::Ordinal)
                if ($promote -and $f.mode -ceq 'fail-promote') { $global:LASTEXITCODE=1; return }
                Move-Item -LiteralPath (Join-Path $f.remote $asset.name) -Destination (Join-Path $f.remote $name)
                $f.renames.Add(($asset.name+' -> '+$name))
                $asset.name=$name
                if ($promote -and $f.mode -ceq 'drop-promote-ack') { $global:LASTEXITCODE=1 }
                return
            }
            throw 'Unexpected fixture asset method.'
        }
        if ($method -ceq 'GET' -and $path -clike '*/releases*') {
            if (-not $f.created -or ($f.mode -ceq 'new-release' -and $path -cnotmatch '/releases/[0-9]+$')) { return }
            return (@{id=42;tag_name="updater-$($f.channel)-feed";draft=$f.draft;prerelease=$true;name=$f.title;body=$f.description;html_url='https://fixture.invalid/feed';assets=@($f.assets.ToArray())} | ConvertTo-Json -Depth 8)
        }
        throw 'Unexpected fixture GitHub API call; no real network is allowed.'
    }
    if ($a[0] -cne 'release' -or $a[2] -cne "updater-$($f.channel)-feed") { throw 'A publication crossed its feed channel.' }
    if ($a[[Array]::IndexOf($a,'--repo')+1] -cne $f.repository) { throw 'Release operation crossed its destination repository.' }
    if ($a[1] -ceq 'edit') {
        $title=$a[[Array]::IndexOf($a,'--title')+1]
        $description=[IO.File]::ReadAllText($a[[Array]::IndexOf($a,'--notes-file')+1])
        $expectedTitle=if ($f.channel -ceq 'stable') { 'Updater data - Stable' } else { 'Updater data - Testing' }
        if ($title -cne $expectedTitle -or $a -cnotcontains '--prerelease' -or $a -cnotcontains '--latest=false' -or
            -not $description.Contains('https://github.com/Haanuwaa/AudioPilot/releases/latest') -or
            $description.Contains('Public live updates remain disabled') -or $description.Contains('separate user approvals')) {
            throw 'Feed presentation lost its channel, safe release flags, or application download guidance.'
        }
        $f.title=$title; $f.description=$description; $f.draft=$false
        return
    }
    if ($a[1] -ceq 'download') {
        $name=$a[[Array]::IndexOf($a,'--pattern')+1]
        Copy-Item -LiteralPath (Join-Path $f.remote $name) -Destination $a[[Array]::IndexOf($a,'--output')+1]
        return
    }
    if ($a[1] -ceq 'upload') {
        if ($a -ccontains '--clobber') { throw 'Publication must not delete the timestamp before uploading its replacement.' }
        $path=$a[5]; $name=[IO.Path]::GetFileName($path)
        if ($f.mode -ceq 'source-mutation' -and -not $f.sourceMutationBlocked) {
            try { [IO.File]::WriteAllText((Join-Path $feed 'metadata/2.targets.json'), 'changed after verification') }
            catch [IO.IOException] { $f.sourceMutationBlocked=$true }
            if (-not $f.sourceMutationBlocked) { throw 'The source export was not held stable during publication.' }
        }
        if (@($f.assets.ToArray() | Where-Object name -CEQ $name).Count) { throw 'Duplicate fixture asset upload.' }
        $asset=Add-FixtureAsset $name $path
        if ($name -ceq $f.packageName -and $f.mode -ceq 'corrupt-server') {
            Set-Content -LiteralPath (Join-Path $f.remote $name) -Value 'corrupt server bytes'
            $asset.size=(Get-Item -LiteralPath (Join-Path $f.remote $name)).Length
            $asset.digest='sha256:'+(Get-FileHash -LiteralPath (Join-Path $f.remote $name)).Hash.ToLowerInvariant()
        }
        if ($name -ceq $f.packageName -and $f.mode -ceq 'drop-upload-ack') { $global:LASTEXITCODE=1 }
        return
    }
    throw 'Unexpected fixture GitHub command; no real network is allowed.'
}
function Start-Sleep { param([int]$Milliseconds) $global:feedFixture.waits.Add($Milliseconds) }
$results = @()
foreach ($case in @(
    @{name='empty-starter';failed=$false;version=2},
    @{name='fail-promote';failed=$true;version=1},
    @{name='drop-promote-ack';failed=$false;version=2},
    @{name='drop-upload-ack';failed=$false;version=2},
    @{name='interrupted-switch';failed=$false;version=2},
    @{name='corrupt-server';failed=$true;version=1},
    @{name='stale-export';failed=$true;version=3},
    @{name='newer-root';failed=$true;version=1},
    @{name='source-mutation';failed=$false;version=2},
    @{name='separate-staging';failed=$false;version=2},
    @{name='new-release';failed=$false;version=2},
    @{name='missing-package-renewal';failed=$true;version=1}
)) {
    $remote=Join-Path $root $case.name
    [IO.Directory]::CreateDirectory($remote) | Out-Null
    $global:feedFixture=@{remote=$remote;mode=$case.name;created=($case.name -cne 'new-release');draft=$false;repository='Haanuwaa/AudioPilot';channel=$(if($case.name -ceq 'separate-staging'){'staging'}else{'stable'});title='old title';description='old description';nextId=100;packageName=$packageName;sourceMutationBlocked=$false;
        assets=[Collections.Generic.List[object]]::new();calls=[Collections.Generic.List[string]]::new();deleted=[Collections.Generic.List[string]]::new();renames=[Collections.Generic.List[string]]::new();waits=[Collections.Generic.List[int]]::new()}
    $old=Join-Path $remote 'initial'
    @{signed=@{version=$(if($case.name -ceq 'stale-export'){3}else{1})}} | ConvertTo-Json | Set-Content -LiteralPath $old
    $oldAsset=Add-FixtureAsset 'timestamp.json' $old
    if ($case.name -ceq 'new-release') { $global:feedFixture.assets.Clear(); Remove-Item -LiteralPath (Join-Path $remote 'timestamp.json') }
    if ($case.name -ceq 'empty-starter') { $global:feedFixture.nextId++;$global:feedFixture.assets.Add(@{id=$global:feedFixture.nextId;name=$packageName;state='starter';size=0}) }
    if ($case.name -ceq 'newer-root') { $null=Add-FixtureAsset '2.root.json' (Join-Path $feed 'bootstrap.root.json') }
    if ($case.name -ceq 'interrupted-switch') {
        foreach($relative in @('bootstrap.root.json','metadata/2.snapshot.json','metadata/2.targets.json',"targets/$packageName")) { $null=Add-FixtureAsset ([IO.Path]::GetFileName($relative)) (Join-Path $feed $relative) }
        $backup="timestamp-$stampHash-$($oldAsset.digest.Substring(7)).previous"
        Move-Item -LiteralPath (Join-Path $remote 'timestamp.json') -Destination (Join-Path $remote $backup)
        $oldAsset.name=$backup
        $null=Add-FixtureAsset "timestamp-$stampHash.pending" (Join-Path $feed 'metadata/timestamp.json')
    }
    $global:LASTEXITCODE=0
    $failed=$false;$failure=$null
    $parameters = @{MetadataOnly=($case.name -ceq 'missing-package-renewal')}
    try { & (Join-Path $repo 'scripts/publish-github-update-staging.ps1') -FeedDirectory $feed -BootstrapHash ('A'*64) -Publisher (Join-Path $root 'verifier.ps1') -ReportPath (Join-Path $root ($case.name+'-report.json')) -Channel $global:feedFixture.channel -ExistingLeaseSha ('a'*40) @parameters | Out-Null }
    catch { $failed=$true;$failure=$_.Exception.Message }
    $timestamp=Join-Path $remote 'timestamp.json'
    if ($failed -ne $case.failed -or -not (Test-Path -LiteralPath $timestamp) -or (Get-Content -LiteralPath $timestamp -Raw | ConvertFrom-Json).signed.version -ne $case.version) { throw "Publication scenario failed: $($case.name): $failure" }
    if ($case.name -ceq 'corrupt-server' -and ($global:feedFixture.renames.Count -ne 0 -or $global:feedFixture.deleted.Contains($packageName))) { throw 'Publication advanced or deleted a completed mismatched asset.' }
    if ($case.name -ceq 'fail-promote' -and $global:feedFixture.waits.Count -ne 2) { throw 'Timestamp retries were not bounded to three attempts.' }
    if ($case.name -ceq 'source-mutation' -and -not $global:feedFixture.sourceMutationBlocked) { throw 'Source mutation protection was not exercised.' }
    if (-not $failed -and @($global:feedFixture.assets.ToArray() | Where-Object {$_.name.EndsWith('.pending') -or $_.name.EndsWith('.previous')}).Count) { throw 'Completed publication left its temporary timestamp assets behind.' }
    $results += @{case=$case.name;passed=$true;timestampVersion=$case.version}
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'results.json')
Write-Output "Feed publication recovery checks passed: $($results.Count) scenarios; no real network or signing keys."
