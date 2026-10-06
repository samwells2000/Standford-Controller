<#
.SYNOPSIS
    Downloads the official Stanford Research Systems DG645 documents into docs\manuals.

.EXAMPLE
    PS> .\scripts\Download-Manuals.ps1
#>
[CmdletBinding()]
param(
    [string] $Destination = (Join-Path $PSScriptRoot '..\docs\manuals')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$documents = [ordered]@{
    'DG645m.pdf'      = 'https://www.thinksrs.com/downloads/pdfs/manuals/DG645m.pdf'
    'DG645c.pdf'      = 'https://www.thinksrs.com/downloads/pdfs/catalog/DG645c.pdf'
    'DG645_TCPIP.pdf' = 'https://www.thinksrs.com/downloads/pdfs/other%20stuff/DG645_TCPIP.pdf'
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

foreach ($name in $documents.Keys) {
    $target = Join-Path $Destination $name
    $temp = "$target.tmp"
    try {
        Invoke-WebRequest -Uri $documents[$name] -OutFile $temp -UseBasicParsing
        $header = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($temp), 0, 4)
        if ($header -ne '%PDF') { throw 'The server did not return a PDF.' }
        Move-Item -Force $temp $target
        Write-Host "Downloaded $name" -ForegroundColor Green
    }
    catch {
        Remove-Item -Force -ErrorAction SilentlyContinue $temp
        Write-Warning "Could not download $name from $($documents[$name]): $($_.Exception.Message)"
    }
}
