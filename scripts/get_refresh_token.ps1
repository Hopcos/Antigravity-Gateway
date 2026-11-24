param(
    [string]$Target = "gemini:antigravity",
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

function Extract-EmailFromIdToken {
    param([string]$IdToken)
    if ([string]::IsNullOrWhiteSpace($IdToken)) { return $null }
    try {
        $parts = $IdToken.Split('.')
        if ($parts.Length -lt 2) { return $null }
        $payload = $parts[1].Replace('-', '+').Replace('_', '/')
        switch ($payload.Length % 4) {
            2 { $payload += "==" }
            3 { $payload += "=" }
        }
        $bytes = [Convert]::FromBase64String($payload)
        $jsonText = [System.Text.Encoding]::UTF8.GetString($bytes)
        $obj = $jsonText | ConvertFrom-Json
        return $obj.email
    }
    catch {
        return $null
    }
}

function Read-WindowsCredential {
    param([string]$TargetName)

    if (-not ("AntigravityCredReader" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class AntigravityCredReader {
    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPtr);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    public static string[] Read(string target) {
        IntPtr ptr;
        if (!CredRead(target, 1, 0, out ptr)) {
            return null;
        }
        try {
            CREDENTIAL cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) {
                return null;
            }
            byte[] bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
            return new string[] { Encoding.UTF8.GetString(bytes), cred.UserName };
        }
        finally {
            CredFree(ptr);
        }
    }
}
"@
    }

    $res = [AntigravityCredReader]::Read($TargetName)
    if ($null -eq $res) { return $null }

    $rawJson = $res[0]
    $userName = $res[1]
    $obj = $rawJson | ConvertFrom-Json

    $rt = $null
    if ($obj.PSObject.Properties["token"] -and $obj.token -and $obj.token.PSObject.Properties["refresh_token"]) {
        $rt = $obj.token.refresh_token
    }
    if ([string]::IsNullOrWhiteSpace($rt) -and $obj.PSObject.Properties["refresh_token"]) {
        $rt = $obj.refresh_token
    }
    if ([string]::IsNullOrWhiteSpace($rt)) { return $null }

    $idToken = if ($obj.PSObject.Properties["id_token"]) { $obj.id_token } else { $null }
    $email = Extract-EmailFromIdToken -IdToken $idToken
    if ([string]::IsNullOrWhiteSpace($email)) { $email = $userName }
    if ([string]::IsNullOrWhiteSpace($email)) { $email = "unknown" }

    return [PSCustomObject]@{
        Source       = "Windows Credential Manager ($TargetName)"
        Email        = $email
        RefreshToken = $rt
    }
}

function Read-OAuthCredsFile {
    $path = Join-Path $env:USERPROFILE ".gemini\oauth_creds.json"
    if (-not (Test-Path $path)) { return $null }

    $obj = Get-Content -Path $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $rt = if ($obj.PSObject.Properties["refresh_token"]) { $obj.refresh_token } else { $null }
    if ([string]::IsNullOrWhiteSpace($rt)) { return $null }

    $idToken = if ($obj.PSObject.Properties["id_token"]) { $obj.id_token } else { $null }
    $email = Extract-EmailFromIdToken -IdToken $idToken
    if ([string]::IsNullOrWhiteSpace($email)) { $email = "local-gemini-user" }

    return [PSCustomObject]@{
        Source       = "File ($path)"
        Email        = $email
        RefreshToken = $rt
    }
}

function Read-AdcFile {
    $path = Join-Path $env:APPDATA "gcloud\application_default_credentials.json"
    if (-not (Test-Path $path)) { return $null }

    $obj = Get-Content -Path $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $rt = if ($obj.PSObject.Properties["refresh_token"]) { $obj.refresh_token } else { $null }
    if ([string]::IsNullOrWhiteSpace($rt)) { return $null }

    $email = if ($obj.PSObject.Properties["account"] -and -not [string]::IsNullOrWhiteSpace($obj.account)) { $obj.account } else { "adc-user" }

    return [PSCustomObject]@{
        Source       = "ADC File ($path)"
        Email        = $email
        RefreshToken = $rt
    }
}

try {
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host "   AntigravityGateway - Refresh Token Extractor           " -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host ""

    $results = @()

    try {
        $winCred = Read-WindowsCredential -TargetName $Target
        if ($winCred) { $results += $winCred }
    }
    catch {
        Write-Warning "Failed to read Windows Credential Manager: $($_.Exception.Message)"
    }

    try {
        $fileCred = Read-OAuthCredsFile
        if ($fileCred) { $results += $fileCred }
    }
    catch {
        Write-Warning "Failed to read oauth_creds.json: $($_.Exception.Message)"
    }

    try {
        $adcCred = Read-AdcFile
        if ($adcCred) { $results += $adcCred }
    }
    catch {
        Write-Warning "Failed to read ADC file: $($_.Exception.Message)"
    }

    if ($results.Count -eq 0) {
        Write-Host "[!] No Antigravity / Gemini refresh_token found on this machine." -ForegroundColor Red
        Write-Host "    Checked:" -ForegroundColor Yellow
        Write-Host "    1. Windows Credential Manager: $Target" -ForegroundColor Gray
        Write-Host "    2. File: $env:USERPROFILE\.gemini\oauth_creds.json" -ForegroundColor Gray
        Write-Host "    3. ADC:  $env:APPDATA\gcloud\application_default_credentials.json" -ForegroundColor Gray
    }
    else {
        $primary = $results[0]
        foreach ($item in $results) {
            Write-Host "[+] Source : $($item.Source)" -ForegroundColor Cyan
            Write-Host "    Email  : $($item.Email)" -ForegroundColor Gray
            Write-Host "    Token  : $($item.RefreshToken)" -ForegroundColor Green
            Write-Host ""
        }

        Write-Host "----------------------------------------------------------" -ForegroundColor DarkGray
        Write-Host "Docker / .env configuration:" -ForegroundColor Yellow
        Write-Host "ANTIGRAVITY_AUTH_SOURCE=Env" -ForegroundColor White
        Write-Host "ANTIGRAVITY_REFRESH_TOKEN=$($primary.RefreshToken)" -ForegroundColor White
        Write-Host "----------------------------------------------------------" -ForegroundColor DarkGray

        try {
            Set-Clipboard -Value $primary.RefreshToken
            Write-Host "[i] Primary refresh_token has been copied to clipboard." -ForegroundColor Green
        }
        catch {
            Write-Host "[i] Could not copy to clipboard automatically; please copy from above." -ForegroundColor Yellow
        }
    }
}
catch {
    Write-Host ""
    Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if (-not $NoPause) {
        Write-Host ""
        try {
            Read-Host "Press Enter to exit" | Out-Null
        }
        catch {
            # Fallback if stdin is unavailable
            Start-Sleep -Seconds 10
        }
    }
}
