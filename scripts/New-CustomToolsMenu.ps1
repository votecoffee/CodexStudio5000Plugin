param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputPath) { $OutputPath = Join-Path $repo 'artifacts/CustomToolsMenu.xml' }
$doc = New-Object System.Xml.XmlDocument
$rootElement = $doc.CreateElement('CustomToolsContent')
$rootElement.SetAttribute('SchemaRevision', '1.0')
[void]$doc.AppendChild($rootElement)
$tool = $doc.CreateElement('CustomTool')
[void]$rootElement.AppendChild($tool)
$values = [ordered]@{
    Command = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
    WorkingDirectory = $repo
    Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $repo 'scripts/Start-Gui.ps1') + '" -ProjectPath "%project_path%"'
    ToolName = 'Codex Studio 5000 Workspace'
    StatusBarText = 'Open the saved project in the shared Codex operations service'
}
foreach ($name in $values.Keys) { $element=$doc.CreateElement($name); $element.InnerText=$values[$name]; [void]$tool.AppendChild($element) }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))) | Out-Null
$doc.Save([IO.Path]::GetFullPath($OutputPath))
Write-Output "Created $OutputPath. Merge this CustomTool into the existing Studio 5000 menu; preserve other entries."
