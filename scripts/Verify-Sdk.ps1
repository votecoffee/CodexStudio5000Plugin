param([int]$Revision = 38)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$connection = Get-Content -LiteralPath (Join-Path $repo '.local/connection.json') -Raw | ConvertFrom-Json
$headers = @{Authorization='Bearer '+(Get-Content -LiteralPath $connection.tokenFile -Raw).Trim()}
$evidence = Join-Path $repo ('artifacts/test-results/sdk-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$reports = [System.Collections.Generic.List[object]]::new()
$success = $false
function Api([string]$Path, $Body=$null) {
    if ($null -eq $Body) { return Invoke-RestMethod -Uri ($connection.url+$Path) -Headers $headers -TimeoutSec 240 }
    Invoke-RestMethod -Uri ($connection.url+$Path) -Method Post -Headers $headers -ContentType 'application/json' -Body ($Body|ConvertTo-Json -Depth 50) -TimeoutSec 240
}
function Call([string]$Method, $Arguments=@{}) { @{method=$Method;arguments=$Arguments} }
function Job([string]$Name, $Body, [switch]$AllowFailure) {
    $job=Api '/api/sdk/jobs' $Body
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(230)
    do {
        Start-Sleep -Milliseconds 700
        $job=Api ('/api/sdk/jobs/'+$job.id)
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "SDK verification poll timeout: $($job.id)" }
    } while ($job.status -in @('queued','running'))
    $job|ConvertTo-Json -Depth 60|Set-Content -LiteralPath (Join-Path $evidence ($Name+'.json')) -Encoding utf8
    $reports.Add(@{name=$Name;id=$job.id;status=$job.status;expectedFailure=[bool]$AllowFailure;steps=$job.steps;error=$job.error})
    Write-Output "${Name}: $($job.status)" | Out-Host
    if ($job.status -ne 'succeeded' -and !$AllowFailure) { throw "SDK $Name failed: $($job.error)" }
    return $job
}
function Acd($Job) { @($Job.artifacts | Where-Object {$_.validation -match 'output final.acd;'})[0] }
function FromArtifact($Artifact, $Calls) { @{sourceArtifactId=$Artifact.id;expectedSha256=$Artifact.sha256;calls=@($Calls)} }
try {
    $coverage=Api '/api/sdk/coverage'
    if (!$coverage.matchesFrozenSurface -or !$coverage.allOperationsBound -or $coverage.signatureCount -ne 67) { throw 'Installed SDK surface/bindings drifted.' }
    $coverage|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $evidence 'coverage.json') -Encoding utf8
    $null=Job 'processors' @{calls=@((Call 'GetProcessorTypesAsync' @{majorRev=$Revision}))}
    $create=Job 'create-build-save' @{calls=@(
        (Call 'CreateNewProjectAsync' @{majorRevision=$Revision;processorTypeName='1756-L81E';controllerName='SdkFixture'}),
        (Call 'ReadConnectedStateAsync'),(Call 'GetCommunicationsPathAsync'),(Call 'BuildAsync'),(Call 'SaveAsync'),
        (Call 'SaveAsAsync' @{savePath='detailed.l5x';detailedL5x=$true}),(Call 'SaveAsAsync' @{savePath='text.l5k'}))}
    $project=$create.resultProject
    $values=[ordered]@{BOOL=$true;SINT=-12;INT=-1234;DINT=123456;LINT=[long]-1234567890123;USINT=210;UINT=61000;UDINT=[uint32]4000000000;ULINT=[uint64]18000000000000000000;REAL=1.25;LREAL=1.234567890123;STRING='SDK round trip'}
    $edits=@($values.Keys | ForEach-Object { @{operation='add_element';xpath='/RSLogix5000Content/Controller/Tags';value=('<Tag Name="Test_'+$_+'" TagType="Base" DataType="'+$_+'" Constant="false" ExternalAccess="Read/Write"/>')} })
    $edits+=@{operation='add_element';xpath='/RSLogix5000Content/Controller/Programs';value='<Program Name="SdkProgram" MainRoutineName="MainRoutine" Disabled="false" Class="Standard"><Tags/><Routines><Routine Name="MainRoutine" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine></Routines></Program>'}
    $draft=Api "/api/projects/$($project.id)/previews" @{expectedSha256=$project.currentSha256;edits=$edits}
    $project=Api "/api/projects/$($project.id)/previews/$($draft.id)/apply" @{expectedSha256=$project.currentSha256}
    $native=Api "/api/projects/$($project.id)/exports" @{expectedSha256=$project.currentSha256;format='acd'}
    $calls=@((Call 'OpenLogixProjectAsync'))
    foreach ($type in $values.Keys) {
        $tagPath="Controller/Tags/Tag[@Name='Test_$type']"
        $calls+=Call ('SetTagValue'+$type+'Async') @{tagPath=$tagPath;mode='Offline';tagValue=$values[$type]}
        $calls+=Call ('GetTagValue'+$type+'Async') @{tagPath=$tagPath;mode='Offline'}
    }
    $calls+=Call 'SetTagValueAsync' @{tagPath="Controller/Tags/Tag[@Name='Test_DINT']";mode='Offline';tagValue='KgAAAA==';type='DINT'}
    $calls+=Call 'GetTagValueAsync' @{tagPath="Controller/Tags/Tag[@Name='Test_DINT']";mode='Offline';type='DINT'}
    $calls+=Call 'GetTagValueDINTAsync' @{tagPath="Controller/Tags/Tag[@Name='Test_DINT']";mode='Offline'}
    $calls+=Call 'PartialExportToXmlFileAsync' @{xPath="Controller/Programs/Program[@Name='SdkProgram']";filePath='program.l5x'}
    $calls+=Call 'PartialExportToXmlFileAsync' @{xPath="Controller/Programs/Program[@Name='SdkProgram']/Routines/Routine[@Name='MainRoutine']";filePath='routine.l5x'}
    $calls+=Call 'PartialExportToXmlFileAsync' @{xPath="Controller/Programs/Program[@Name='SdkProgram']/Routines/Routine[@Name='MainRoutine']/RLLContent/Rung[@Number='0']";filePath='rungs.l5x'}
    $typed=Job 'typed-and-raw-tags-export' (FromArtifact $native $calls)
    foreach ($type in $values.Keys) {
        $value=@($typed.steps|Where-Object method -eq ('GetTagValue'+$type+'Async'))[0].value
        if ($type -eq 'LREAL') { if ([math]::Abs($value-$values[$type]) -gt 0.000000000001) {throw 'LREAL round trip mismatch'} }
        elseif ($value -ne $values[$type]) {throw "Typed value mismatch: $type"}
    }
    if (@($typed.steps|Where-Object method -eq 'GetTagValueDINTAsync')[-1].value -ne 42) {throw 'Raw DINT write did not decode to 42'}
    $inputs=@{}
    foreach ($name in @('program','routine','rungs')) {
        $artifact=@($typed.artifacts|Where-Object {$_.validation -match ('output '+$name+'.l5x;')})[0]
        $inputs[$name]=@{artifactId=$artifact.id;sha256=$artifact.sha256}
    }
    $body=FromArtifact (Acd $typed) @(
        (Call 'PartialImportFromXmlFileAsync' @{xPath="Controller/Programs/Program[@Name='SdkProgram']";xmlFileToImport='program';collisionOption='OverwriteOnColl'}),
        (Call 'PartialImportWithTargetFromXmlFileAsync' @{xPath="Controller/Programs/Program[@Name='SdkProgram']";targetName='MainRoutine';xmlFileToImport='routine';onlineImportOption='FinalizeEdits'}),
        (Call 'PartialImportRungsFromXmlFileAsync' @{xPath="Controller/Programs/Program[@Name='SdkProgram']/Routines/Routine[@Name='MainRoutine']";insertPosition=0;replaceCount=1;xmlFileToImport='rungs';onlineImportOption='FinalizeEdits'}),
        (Call 'BuildAsync'))
    $body.inputFiles=$inputs
    $partial=Job 'partial-imports-build' $body
    $partialXml=@($partial.artifacts|Where-Object {$_.validation -match 'output final.l5x;'})[0]
    $partialPath=Join-Path $evidence 'partial-imports-final.l5x'
    Invoke-WebRequest -Uri ($connection.url+'/api/artifacts/'+$partialXml.id) -Headers $headers -OutFile $partialPath
    [xml]$partialDoc=Get-Content -LiteralPath $partialPath -Raw -Encoding utf8
    $partialRungs=$partialDoc.SelectNodes("/RSLogix5000Content/Controller/Programs/Program[@Name='SdkProgram']/Routines/Routine[@Name='MainRoutine']/RLLContent/Rung")
    if($partialRungs.Count -ne 1 -or $partialRungs[0].SelectSingleNode('Text').InnerText.Trim() -ne 'NOP();'){throw 'Partial import appended or changed rungs instead of retaining the expected single rung'}
    $base=Job 'create-v35' @{calls=@((Call 'CreateNewProjectAsync' @{majorRevision=35;processorTypeName='1756-L81E';controllerName='SdkV35'}))}
    $null=Job 'convert-controller-type' (FromArtifact (Acd $base) @((Call 'ConvertAsync' @{destinationRevision=$Revision}),
        (Call 'ChangeControllerTypeAsync' @{processorTypeName='1756-L82E'}),(Call 'BuildAsync')))
    $password='OfflineFixture'+[guid]::NewGuid().ToString('N').Substring(0,8)
    $safety=Job 'offline-safety' @{calls=@(
        (Call 'CreateNewProjectAsync' @{majorRevision=$Revision;processorTypeName='1756-L81ES';controllerName='SdkSafetyFixture'}),
        (Call 'IsSafetyLockedAsync'),(Call 'GetSafetyNetworkNumberAsync' @{moduleName='Local'}),
        (Call 'SetSafetyLockPasswordAsync' @{newPassword=$password}),(Call 'SetSafetyUnlockPasswordAsync' @{newPassword=$password}),
        (Call 'SafetyLockAsync' @{password=$password}),(Call 'IsSafetyLockedAsync'),
        (Call 'SafetyUnlockAsync' @{password=$password}),(Call 'IsSafetyLockedAsync'),
        (Call 'SetSafetyLockPasswordAsync' @{newPassword='';oldPassword=$password}),
        (Call 'SetSafetyUnlockPasswordAsync' @{newPassword='';oldPassword=$password}))}
    $persisted=Get-Content -Raw -LiteralPath (Join-Path $evidence 'offline-safety.json')
    if ($persisted.Contains($password)) {throw 'Password leaked into job result'}
    $noSignature=Job 'safety-no-signature' (FromArtifact (Acd $safety) @((Call 'GetSafetySignatureAsync'))) -AllowFailure
    if ($noSignature.status -ne 'failed' -or $noSignature.error -notmatch 'XMLSrv_E_NO_VALUE_TO_GET') {throw 'Expected missing-signature diagnostic was not preserved'}
    $saved=Api "/api/artifacts/$((Acd $create).id)/save" @{path=(Join-Path $repo ('artifacts/exports/sdk-offline-path-'+[guid]::NewGuid().ToString('N')+'.acd'))}
    dotnet run --project (Join-Path $repo 'tests/Studio5000.Integration/Studio5000.Integration.csproj') -c Release -- --sdk-offline-path $saved.path
    if ($LASTEXITCODE -ne 0) {throw 'Offline communications-path fixture failed'}
    $offlinePath=Get-Content -Raw -LiteralPath (Join-Path $repo 'artifacts/test-results/sdk-offline-path.json')|ConvertFrom-Json
    $reports.Add(@{name='offline-path';id=$offlinePath.job.id;status=$offlinePath.job.status;steps=$offlinePath.job.steps;error=$offlinePath.job.error})
    $success = $true
} finally {
    $methods=@($reports.steps|Where-Object status -eq 'succeeded'|Select-Object -ExpandProperty method -Unique|Sort-Object)
    @{status=$(if ($success) {'passed'} else {'failed'});reports=$reports;successfulOperationNames=$methods;successfulOperationCount=$methods.Count;
      controllerValidation='Not performed. Only generated offline fixtures were used.';evidenceDirectory=$evidence}|
      ConvertTo-Json -Depth 60|Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding utf8
    Write-Output "SDK evidence: $evidence"
}
