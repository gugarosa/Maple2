Add-Type -AssemblyName System.Windows.Forms

Set-Location -LiteralPath $PSScriptRoot

Write-Host "====================================" -ForegroundColor Cyan
Write-Host "======= Maple2 Setup Script ========" -ForegroundColor Cyan
Write-Host "====================================" -ForegroundColor Cyan

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    throw ".NET SDK 8.0 or newer is required, but the dotnet command was not found."
}

$dotnetVersionOutput = & dotnet --version 2>$null
if ($LASTEXITCODE -ne 0 -or -not $dotnetVersionOutput) {
    throw "Unable to determine the installed .NET SDK version."
}
$dotnetVersionText = ($dotnetVersionOutput | Select-Object -First 1).Trim()

[version]$dotnetVersion = $null
if (-not [version]::TryParse(($dotnetVersionText -split '-', 2)[0], [ref]$dotnetVersion)) {
    throw "Unable to parse .NET SDK version '$dotnetVersionText'."
}
if ($dotnetVersion -lt [version]"8.0") {
    throw ".NET SDK 8.0 or newer is required; found $dotnetVersionText."
}

dotnet tool restore
if ($LASTEXITCODE -ne 0) {
    throw "Failed to restore the repository's pinned .NET tools."
}

# Create a copy of .env.example and rename it to .env
if (Test-Path .env) {
    Write-Host ".env file already exists. Skipping." -ForegroundColor Blue
} else {
    Write-Host "Creating .env file" -ForegroundColor Green
    Copy-Item .env.example .env
}

# Get the path to the client
$FileBrowser = New-Object System.Windows.Forms.OpenFileDialog -Property @{
    InitialDirectory = [Environment]::GetFolderPath('Desktop')
    Filter = "MapleStory2.exe|MapleStory2.exe"
    Title = "Maplestory2 Executable"
}
$null = $FileBrowser.ShowDialog()

$exePath = $FileBrowser.FileName
if ($exePath) {
    Write-Host "Using client path: $exePath" -ForegroundColor Green
} else {
    Write-Warning -Message "No client specified, exiting."
    exit
}

#Get the parent directory of the client path
$parentPath = Split-Path $exePath -Parent

# Get the data directory
$dataPath = Join-Path $parentPath "Data"
if (-not (Test-Path $dataPath)) {
    Write-Host "MapleStory2 data directory not found. Did you select the correct client?" -ForegroundColor Red
    exit
}

Write-Host "MapleStory2 data directory: $dataPath" -ForegroundColor Green
Write-Host "====================================" -ForegroundColor Cyan
Write-Host "Saving .env data"

# Remove existing key in the .env file
(Get-Content .env) | Where-Object { $_ -notmatch "MapleStory2 data directory" } | Set-Content .env
(Get-Content .env) | Where-Object { $_ -notmatch "MS2_DATA_FOLDER" } | Set-Content .env

# Write to the .env file under the MS2_DATA_FOLDER variable
Add-Content -Path .env -Value "# MapleStory2 data directory"
Add-Content -Path .env -Value "MS2_DATA_FOLDER=$dataPath"

# Database setup, prompt if they have MySQL setup
$answer = Read-Host "Do you have MySQL 8.0 installed? (y/n)"

# if starts with y, prompt for connection details
if ($answer -eq "y") {
    $ip = Read-Host "MySQL host (leave blank for localhost)"
    $port = Read-Host "MySQL port (leave blank for 3306)"
    $user = Read-Host "MySQL user (leave blank for root)"
    $pass = Read-Host "MySQL password" -AsSecureString

    if ($ip -eq "") {
        $ip = "localhost"
    }

    if ($port -eq "") {
        $port = "3306"
    }

    if ($user -eq "") {
        $user = "root"
    }

    if ($pass -eq "") {
        Write-Host "No password provided, using empty password." -ForegroundColor Yellow
        $pass = ""
    }

    $pass_d = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($pass))

    (Get-Content .env) | Where-Object { $_ -notmatch "Database connection" } | Set-Content .env
    (Get-Content .env) | Where-Object { $_ -notmatch "DB_IP" } | Set-Content .env
    (Get-Content .env) | Where-Object { $_ -notmatch "DB_PORT" } | Set-Content .env
    (Get-Content .env) | Where-Object { $_ -notmatch "DB_USER" } | Set-Content .env
    (Get-Content .env) | Where-Object { $_ -notmatch "DB_PASSWORD" } | Set-Content .env

    # Write to the .env file under the DB_* variables
    Add-Content -Path .env -Value "# Database connection"
    Add-Content -Path .env -Value "DB_IP=$ip"
    Add-Content -Path .env -Value "DB_PORT=$port"
    Add-Content -Path .env -Value "DB_USER=$user"
    Add-Content -Path .env -Value "DB_PASSWORD=$pass_d"
} else {
    Write-Host "Please install MySQL 8.0 and run this script again." -ForegroundColor Red
    Start-Process "https://dev.mysql.com/downloads/installer/"
    exit
}

Write-Host "====================================" -ForegroundColor Cyan
Write-Host "Downloading customized server files..."

Invoke-WebRequest -Uri "https://github.com/Zintixx/MapleStory2-XML/releases/latest/download/Server.m2d" -OutFile (Join-Path $dataPath "Server.m2d")
Invoke-WebRequest -Uri "https://github.com/Zintixx/MapleStory2-XML/releases/latest/download/Server.m2h" -OutFile (Join-Path $dataPath "Server.m2h")
Invoke-WebRequest -Uri "https://github.com/Zintixx/MapleStory2-XML/releases/latest/download/Xml.m2d" -OutFile (Join-Path $dataPath "Xml.m2d")
Invoke-WebRequest -Uri "https://github.com/Zintixx/MapleStory2-XML/releases/latest/download/Xml.m2h" -OutFile (Join-Path $dataPath "Xml.m2h")

Write-Host "====================================" -ForegroundColor Cyan
Write-Host "Initializing project..." -ForegroundColor Blue

# Set the working directory to the Maple2.File.Ingest project
Set-Location -Path "Maple2.File.Ingest"

# Run ingest
dotnet run

Set-Location -Path ".."

Write-Host "====================================" -ForegroundColor Cyan
Write-Host "Done! Happy Mapling!" -ForegroundColor Green
