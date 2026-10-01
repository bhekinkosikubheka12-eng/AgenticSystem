using System;

namespace AgenticSystem.Services;

public class WhatsappConfig
{
    public string UserId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty; // Encrypted in database, decrypted in memory
    public string PhoneNumberId { get; set; } = string.Empty;
    public string WabaId { get; set; } = string.Empty;
    public string VerifyToken { get; set; } = string.Empty; // Verification token configured on Meta Developer site

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessToken) &&
                                !string.IsNullOrWhiteSpace(PhoneNumberId) &&
                                !string.IsNullOrWhiteSpace(WabaId);
}
