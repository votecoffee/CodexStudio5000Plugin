param([Parameter(Mandatory=$true)][string]$ProjectPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$connection = Get-Content -LiteralPath (Join-Path $repo '.local/connection.json') -Raw | ConvertFrom-Json
$headers = @{ Authorization = 'Bearer ' + (Get-Content -LiteralPath $connection.tokenFile -Raw).Trim() }
function Get-Api([string]$Path) { Invoke-RestMethod -Uri ($connection.url + $Path) -Headers $headers -TimeoutSec 240 }
function Post-Api([string]$Path, $Body) { Invoke-RestMethod -Uri ($connection.url + $Path) -Method Post -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 30) -TimeoutSec 240 }
$source = (Resolve-Path -LiteralPath $ProjectPath).Path
$before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$project = Post-Api '/api/projects' @{ path = $source }
Write-Output 'PASS: Native source snapshot exported to L5X through the service.'
$inspection = Get-Api "/api/projects/$($project.id)/elements?kind=Rung&limit=1"
$marker = 'CodexStudio5000Plugin verification ' + [Guid]::NewGuid().ToString('N')
if ($inspection.elements.Count -eq 0) {
    # A freshly created native project is empty. Add an unscheduled program to its working copy.
    $component = '<Program Name="CodexVerification" MainRoutineName="MainRoutine" Disabled="false" Class="Standard"><Tags/><Routines><Routine Name="MainRoutine" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Comment><![CDATA[' + $marker + ']]></Comment><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine></Routines></Program>'
    $edit = @{operation='add_element'; xpath='/RSLogix5000Content/Controller/Programs'; value=$component}
} else {
    $selector = $inspection.elements[0].xPath
    $element = Get-Api ("/api/projects/$($project.id)/element?xpath=" + [Uri]::EscapeDataString($selector))
    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.LoadXml($element.xml)
    $comment = $xml.DocumentElement.SelectSingleNode('Comment')
    if (!$comment) { $comment=$xml.CreateElement('Comment'); [void]$xml.DocumentElement.PrependChild($comment) }
    $comment.RemoveAll()
    [void]$comment.AppendChild($xml.CreateCDataSection($marker))
    $edit = @{operation='replace_element'; xpath=$selector; value=$xml.DocumentElement.OuterXml}
}
$draft = Post-Api "/api/projects/$($project.id)/previews" @{ expectedSha256=$project.currentSha256; edits=@($edit) }
$updated = Post-Api "/api/projects/$($project.id)/previews/$($draft.id)/apply" @{ expectedSha256=$draft.baseSha256 }
$artifact = Post-Api "/api/projects/$($project.id)/exports" @{ expectedSha256=$updated.currentSha256; format='acd' }
$output = Join-Path $repo ("artifacts/exports/native-verified-" + $project.id + '.acd')
$saved = Post-Api "/api/artifacts/$($artifact.id)/save" @{ path=$output }
Write-Output 'PASS: Edited working revision saved as a new native ACD.'
$evidence = Join-Path $repo 'artifacts/test-results'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$reopened = Join-Path $evidence ("native-reopened-" + $project.id + '.l5x')
$bridge = Join-Path $repo 'artifacts/app/NativeBridge/Studio5000.NativeBridge.exe'
$process = Start-Process -FilePath $bridge -ArgumentList @(('"'+$saved.path+'"'),('"'+$reopened+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $evidence 'native.stdout.log') -RedirectStandardError (Join-Path $evidence 'native.stderr.log')
if (!$process.WaitForExit(180000)) { $process.Kill(); throw 'Reopen verification timed out.' }
if ($process.ExitCode -ne 0) { throw 'Native reopen failed; inspect native.stderr.log.' }
$roundtrip = New-Object System.Xml.XmlDocument
$roundtrip.Load($reopened)
$found = @($roundtrip.SelectNodes('//Rung/Comment') | Where-Object { $_.InnerText -eq $marker })
if ($found.Count -ne 1) { throw 'Edited comment was not found exactly once in the re-exported ACD.' }
$after = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
if ($before -ne $after) { throw 'Source project changed during verification.' }
$report = [ordered]@{
    status='passed'; source=$source; sourceSha256=$before; sourcePreserved=$true
    projectId=$project.id; editedL5xSha256=$updated.currentSha256; output=$saved.path
    outputSha256=$artifact.sha256; reopenedL5xSha256=(Get-FileHash -LiteralPath $reopened -Algorithm SHA256).Hash.ToLowerInvariant()
    marker=$marker; markerCount=$found.Count; validation='Native SDK Open/SaveAs/reopen succeeded; native BuildAsync, controller and runtime acceptance not tested.'
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'native.json') -Encoding utf8
Write-Output 'PASS: Native ACD reopened and re-exported with the edited comment; original source hash preserved.'
