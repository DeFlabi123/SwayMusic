#requires -Version 7.0

function Import-SwayUpdateSigningKey {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PrivatePem,
        [Parameter(Mandatory)][string]$PublicPem
    )

    $signingKey = [Security.Cryptography.ECDsa]::Create()
    $expectedKey = [Security.Cryptography.ECDsa]::Create()
    try {
        try {
            $signingKey.ImportFromPem($PrivatePem)
            # Reject a public-only key before starting a release build.
            [void]$signingKey.ExportParameters($true)
        }
        catch {
            throw 'UPDATE_SIGNING_PRIVATE_KEY must contain the complete PEM private-key text, including its BEGIN/END lines, rather than a file path.'
        }
        $expectedKey.ImportFromPem($PublicPem)
        # Git's Windows checkout can turn LF PEM lines into CRLF. Compare the
        # decoded SubjectPublicKeyInfo instead of the file's text formatting.
        $actual = [Convert]::ToBase64String($signingKey.ExportSubjectPublicKeyInfo())
        $expected = [Convert]::ToBase64String($expectedKey.ExportSubjectPublicKeyInfo())
        if ($actual -cne $expected) {
            throw 'The release signing key does not match the public key embedded in this source.'
        }
        return $signingKey
    }
    catch { $signingKey.Dispose(); throw }
    finally { $expectedKey.Dispose() }
}
