$ErrorActionPreference = 'Stop'
$session = $null
$invitation = $null
$viewer = $null
$opened = $false
try {
    $sessionType = [type]::GetTypeFromCLSID([guid]'9B78F0E6-3E05-4A5B-B2E8-E743A8956B65', $true)
    $viewerType = [type]::GetTypeFromCLSID([guid]'32be5ed2-5c86-480f-a914-0ff8885a1b3f', $true)
    $session = [Activator]::CreateInstance($sessionType)
    $viewer = [Activator]::CreateInstance($viewerType)
    Write-Output 'SHARER_AND_VIEWER_ACTIVATION=PASS'
    $session.Open()
    $opened = $true
    Write-Output 'SHARER_OPEN=PASS'
    $password = [guid]::NewGuid().ToString('N')
    $invitation = $session.Invitations.CreateInvitation('EduStream-Smoke', 'Smoke', $password, 2)
    if ([string]::IsNullOrWhiteSpace($invitation.ConnectionString)) { throw 'Empty invitation' }
    if ($invitation.AttendeeLimit -ne 2) { throw 'Unexpected attendee limit' }
    Write-Output 'INVITATION_CREATE_LIMIT_2=PASS'
    $invitation.Revoked = $true
    Write-Output 'INVITATION_REVOKE=PASS'
} finally {
    if ($opened) { $session.Close(); Write-Output 'SHARER_CLOSE=PASS' }
    foreach ($comObject in @($invitation, $viewer, $session)) {
        if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject)
        }
    }
}
