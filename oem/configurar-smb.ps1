# Publica C:\TarjetaAdicional como \\<servidor>\TarjetaAdicional con las reglas de seguridad
# de la solución: SMB 1 apagado, firma obligatoria, cifrado obligatorio y una sola cuenta
# con permiso de modificación. El registro queda en C:\configurar-smb.log.
$ErrorActionPreference = 'Stop'
Start-Transcript -Path 'C:\configurar-smb.log' -Append

$usuario = 'svc-pdf-demo'
$clave   = ConvertTo-SecureString 'Prueba.Smb.2026' -AsPlainText -Force
$recurso = 'TarjetaAdicional'
$ruta    = 'C:\TarjetaAdicional'

# Cuenta de servicio local. En un entorno real sería una cuenta de servicio del dominio.
if (-not (Get-LocalUser -Name $usuario -ErrorAction SilentlyContinue)) {
    New-LocalUser -Name $usuario -Password $clave -PasswordNeverExpires -AccountNeverExpires `
        -UserMayNotChangePassword -Description 'Cuenta de servicio de la carpeta compartida'
}

# Carpeta: sin herencia, solo Administradores, SYSTEM y la cuenta de servicio (Modificar).
New-Item -Path $ruta -ItemType Directory -Force | Out-Null
icacls $ruta /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' "${usuario}:(OI)(CI)M"

# Recurso compartido con cifrado SMB 3 obligatorio y acceso de cambio solo para la cuenta.
if (-not (Get-SmbShare -Name $recurso -ErrorAction SilentlyContinue)) {
    New-SmbShare -Name $recurso -Path $ruta -ChangeAccess $usuario -EncryptData $true | Out-Null
}

# Servidor SMB: sin SMB 1, firma obligatoria y rechazo de clientes que no cifran.
Set-SmbServerConfiguration -EnableSMB1Protocol $false -RequireSecuritySignature $true `
    -RejectUnencryptedAccess $true -Confirm:$false

# Firewall: solo el puerto 445 entrante.
if (-not (Get-NetFirewallRule -DisplayName 'SMB 445 TarjetaAdicional' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'SMB 445 TarjetaAdicional' -Direction Inbound -Protocol TCP `
        -LocalPort 445 -Action Allow -Profile Any | Out-Null
}

Get-SmbShare -Name $recurso | Format-List Name, Path, EncryptData
Get-SmbServerConfiguration | Format-List EnableSMB1Protocol, EnableSMB2Protocol, RequireSecuritySignature, EncryptData, RejectUnencryptedAccess
Stop-Transcript
