using System;

namespace AgenticSystem.Data;

public record LeadMessage(
    string Id,
    string LeadName,
    string ContactInfo,
    string Channel, // "Email", "WhatsApp", "Facebook", "Website Form"
    string MessageBody,
    string DraftResponse,
    string Status, // "Pending Review", "Approved & Sent"
    DateTime ReceivedAt,
    DateTime? HandledAt,
    string AgentLogs
);

public class LeadsSettings
{
    public string UserId { get; set; } = string.Empty;
    public bool IsAutonomous { get; set; } = false;
    public bool IsEmailEnabled { get; set; } = true;
    public bool IsWhatsappEnabled { get; set; } = true;
    public bool IsFacebookEnabled { get; set; } = true;
    public bool IsWebsiteEnabled { get; set; } = true;
}

public class EmailConfig
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;
    public string IncomingServer { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public int Pop3Port { get; set; } = 995;
    public string OutgoingServer { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 465;
    public string IncomingProtocol { get; set; } = "IMAP"; // "IMAP" or "POP3"
    public bool RequiresAuthentication { get; set; } = true;
}

