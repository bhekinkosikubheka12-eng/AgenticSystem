using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using MimeKit;
using MailKit.Security;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class LeadsCenterOrchestrator
{
    private readonly Kernel _kernel;
    private readonly FirebaseLoggerService _firebaseLogger;
    private readonly FirebaseLeadsService _leadsService;
    private readonly WhatsappService _whatsappService;

    public LeadsCenterOrchestrator(
        IConfiguration config, 
        FirebaseLoggerService firebaseLogger, 
        FirebaseLeadsService leadsService,
        WhatsappService? whatsappService = null)
    {
        _firebaseLogger = firebaseLogger;
        _leadsService = leadsService;
        _whatsappService = whatsappService ?? new WhatsappService(new HttpClient(), config);

        var builder = Kernel.CreateBuilder();
        builder.AddGoogleAIGeminiChatCompletion(
            modelId: config["AiConfig:GeminiModelId"]!,
            apiKey: config["AiConfig:GeminiApiKey"]!
        );
        _kernel = builder.Build();
    }

    public async Task ProcessIncomingLeadAsync(
        string userId,
        string channel,
        string leadName,
        string contactInfo,
        string messageBody,
        UserBusinessProfile businessProfile
    )
    {
        var taskId = $"lead-{Guid.NewGuid().ToString().Substring(0, 8)}";

        // 1. Initial execution logging
        var startLog = new AgentTaskLog(
            taskId,
            $"[LeadsCenterAgent] Processing incoming {channel} lead from '{leadName}'",
            "Running",
            $"Reading lead inquiry and invoking the customized channel persona agent...",
            "",
            DateTime.UtcNow
        );
        await _firebaseLogger.LogStateAsync(startLog);

        // 2. Load settings
        var settings = await _leadsService.GetLeadsSettingsAsync(userId);
        
        // 3. Select Persona details
        string personaPrompt = GetPersonaPrompt(channel, businessProfile, leadName, contactInfo);
        
        string draftedResponse = "";
        string agentThoughts = "";

        try
        {
            var chatService = _kernel.GetRequiredService<IChatCompletionService>();
            var chat = new ChatHistory(personaPrompt);
            chat.AddUserMessage(messageBody);

            var response = await chatService.GetChatMessageContentAsync(chat, new GeminiPromptExecutionSettings(), _kernel);
            draftedResponse = response.Content ?? "";

            agentThoughts = $"Inquiry processed successfully. Selected '{channel}' communication rules. Drafted a conversion-focused response.";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Gemini failed for Lead Agent: {ex.Message}");
            draftedResponse = GetFallbackDraftResponse(channel, businessProfile, leadName);
            agentThoughts = $"Gemini connection failed: {ex.Message}. Falling back to default system reply.";
        }

        // 4. Determine Status & Log Details based on Autonomous mode
        string status = settings.IsAutonomous ? "Approved & Sent" : "Pending Review";
        DateTime? handledAt = settings.IsAutonomous ? DateTime.UtcNow : null;
        
        string finalLogs = settings.IsAutonomous 
            ? $"[Autonomous Dispatch] Mode: Autonomous. Response automatically delivered to user contact details.\nAgent thoughts: {agentThoughts}"
            : $"[Human in the Loop] Mode: Guarded. Response saved as draft. Awaiting manual review and approval.\nAgent thoughts: {agentThoughts}";

        if (settings.IsAutonomous && channel.Equals("Email", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await SendSmtpEmailAsync(userId, contactInfo, draftedResponse);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Autonomous email dispatch failed: {ex.Message}");
                status = "Pending Review";
                handledAt = null;
                finalLogs = $"[Autonomous Dispatch Failed] Attempted automatic email dispatch to {contactInfo} but failed: {ex.Message}. Changed status to Pending Review for manual intervention.\nAgent thoughts: {agentThoughts}";
            }
        }

        if (settings.IsAutonomous && channel.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var success = await _whatsappService.SendWhatsappMessageAsync(userId, contactInfo, draftedResponse);
                if (!success)
                {
                    throw new Exception("WhatsApp message failed to send via API. Please check WhatsApp settings.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Autonomous WhatsApp dispatch failed: {ex.Message}");
                status = "Pending Review";
                handledAt = null;
                finalLogs = $"[Autonomous Dispatch Failed] Attempted automatic WhatsApp dispatch to {contactInfo} but failed: {ex.Message}. Changed status to Pending Review for manual intervention.\nAgent thoughts: {agentThoughts}";
            }
        }

        var leadMessage = new LeadMessage(
            taskId,
            leadName,
            contactInfo,
            channel,
            messageBody,
            draftedResponse,
            status,
            DateTime.UtcNow,
            handledAt,
            finalLogs
        );

        // 5. Save message
        await _leadsService.SaveLeadMessageAsync(userId, leadMessage);

        // 6. Complete task logging
        var endLog = startLog with {
            Status = "Completed",
            AgentThought = agentThoughts,
            ToolOutput = $"Processed lead via {channel} and committed to history table.",
            Timestamp = DateTime.UtcNow
        };
        await _firebaseLogger.LogStateAsync(endLog);
    }

    public async Task SendApprovedResponseAsync(string userId, LeadMessage message, string finalResponse)
    {
        if (message.Channel.Equals("Email", StringComparison.OrdinalIgnoreCase))
        {
            await SendSmtpEmailAsync(userId, message.ContactInfo, finalResponse);
        }

        if (message.Channel.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
        {
            var success = await _whatsappService.SendWhatsappMessageAsync(userId, message.ContactInfo, finalResponse);
            if (!success)
            {
                throw new Exception("WhatsApp service failed to send message. Please verify your configuration credentials.");
            }
        }

        var updatedMessage = message with {
            DraftResponse = finalResponse,
            Status = "Approved & Sent",
            HandledAt = DateTime.UtcNow,
            AgentLogs = message.AgentLogs + $"\n[Manual Dispatch] Response approved and sent by user at {DateTime.UtcNow.ToLocalTime():g}."
        };
        
        await _leadsService.SaveLeadMessageAsync(userId, updatedMessage);

        // Log the manual dispatch action
        var logId = $"lead-manual-{Guid.NewGuid().ToString().Substring(0, 8)}";
        var manualLog = new AgentTaskLog(
            logId,
            $"[LeadsCenterAgent] Manually approved & sent response to {message.LeadName} ({message.Channel})",
            "Completed",
            $"User approved draft. Delivered response: \"{finalResponse}\"",
            "Sent successfully.",
            DateTime.UtcNow
        );
        await _firebaseLogger.LogStateAsync(manualLog);
    }

    private async Task SendSmtpEmailAsync(string userId, string recipientEmail, string finalResponse)
    {
        var config = await _leadsService.GetEmailConfigAsync(userId);
        if (config == null || string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.OutgoingServer))
        {
            throw new Exception("Email configuration is not complete. Please configure SMTP settings.");
        }

        var emailMessage = new MimeMessage();
        emailMessage.From.Add(new MailboxAddress("Agentic OS", config.Username));
        emailMessage.To.Add(new MailboxAddress("", recipientEmail));

        // Subject extraction or fallback
        string subject = "Re: Lead Inquiry Response";
        string body = finalResponse;

        if (finalResponse.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
        {
            var lines = finalResponse.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                subject = lines[0].Substring("Subject:".Length).Trim();
                body = string.Join("\n", lines.Skip(1)).TrimStart();
            }
        }

        emailMessage.Subject = subject;
        emailMessage.Body = new TextPart("plain") { Text = body };

        using (var client = new MailKit.Net.Smtp.SmtpClient())
        {
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;

            var socketOptions = SecureSocketOptions.Auto;
            if (config.SmtpPort == 465) socketOptions = SecureSocketOptions.SslOnConnect;
            else if (config.SmtpPort == 587) socketOptions = SecureSocketOptions.StartTls;

            client.Timeout = 10000; // 10s timeout
            await client.ConnectAsync(config.OutgoingServer, config.SmtpPort, socketOptions);

            if (config.RequiresAuthentication)
            {
                await client.AuthenticateAsync(config.Username, config.EncryptedPassword);
            }

            await client.SendAsync(emailMessage);
            await client.DisconnectAsync(true);
        }
    }

    private string GetPersonaPrompt(string channel, UserBusinessProfile business, string leadName, string contactInfo)
    {
        string basePrompt = $@"You are the Leads Center Agent acting on behalf of {business.BusinessName} (Industry: {business.BusinessIndustry}).
Company Core Vision: {business.BusinessDescription}
Target Audience: {business.TargetAudience}

You are replying to a lead named '{leadName}' (Contact Info: '{contactInfo}').
Your reply must represent the business accurately, keep a warm and professional tone, and match the target audience's profile.

Specific Channel Rules:
";

        return channel switch
        {
            "Email" => basePrompt + @"- This is a formal email.
- Include a clear, professional greeting and subject header suggestion at the top of the body (e.g. 'Subject: Re: ...').
- Format the response as a complete professional email body with spacing, introducing how we can help, and a polite sign-off.
- Do not write placeholder text. Do not include template tags like '[Your Name]'; use the company name or 'Leads Success Team' as the sender signature.",

            "WhatsApp" => basePrompt + @"- This is a chat message on WhatsApp.
- Keep the tone friendly, quick, and conversational.
- Use emojis naturally to keep it engaging.
- Use bold text for key terms or dates (e.g. *bold text*), and keep paragraphs short.
- Avoid formal email signatures or headers.",

            "Facebook" => basePrompt + @"- This is a message on a Facebook page.
- Keep it highly helpful and positive.
- Highlight our customer care attitude.
- Suggest next steps or invite them to share more details about their requirements.
- Keep it concise (1-2 short paragraphs).",

            "Website Form" => basePrompt + @"- This is a submission from the corporate website form.
- The user submitted their details asking for information.
- Address their queries directly and propose a call-to-action such as booking a free consultation, a demo, or visiting a specific landing page.
- Maintain a highly consultative, solutions-driven tone.",

            _ => basePrompt + "- Be polite, professional, and address the customer inquiry."
        };
    }

    private string GetFallbackDraftResponse(string channel, UserBusinessProfile business, string leadName)
    {
        return channel switch
        {
            "Email" => $"Subject: Re: Your Inquiry - {business.BusinessName}\n\nDear {leadName},\n\nThank you for reaching out to us. We have received your inquiry regarding our services at {business.BusinessName}. Our team is currently reviewing your details and will get back to you with a personalized solution shortly.\n\nBest regards,\nThe {business.BusinessName} Team",
            "WhatsApp" => $"Hi {leadName}! 👋 Thank you for messaging *{business.BusinessName}*. We received your query and one of our experts will help you out shortly! In the meantime, let us know if you have any other details to share. 😊",
            "Facebook" => $"Hello {leadName}, thank you for contacting the {business.BusinessName} team! We appreciate your message and would love to help. An agent has been notified and will reply to your question as soon as possible.",
            _ => $"Dear {leadName},\n\nThank you for submitting your request on the {business.BusinessName} form. We are revieweing your request and look forward to discussing how we can support you."
        };
    }

    public async Task<string> RunCommanderAgentAsync(
        string userId,
        string userPrompt,
        List<LeadMessage> activeLeads,
        UserBusinessProfile businessProfile,
        bool isAutonomous
    )
    {
        var chatService = _kernel.GetRequiredService<IChatCompletionService>();

        var leadsContextText = activeLeads.Any()
            ? string.Join("\n\n", activeLeads.Select(l => 
                $"- ID: {l.Id}\n  Lead Name: {l.LeadName}\n  Contact: {l.ContactInfo}\n  Channel: {l.Channel}\n  Status: {l.Status}\n  Message: {l.MessageBody}\n  Draft: {l.DraftResponse}"))
            : "No active leads in database.";

        var systemPrompt = $@"You are the Leads Commander Agent for {businessProfile.BusinessName} (Industry: {businessProfile.BusinessIndustry}).
Your capabilities:
1. Answer the user's questions about their active leads database.
2. Draft or simulate sending messages (inbound queries + drafts) on any of the 4 channels: Email, WhatsApp, Facebook, Website Form.
3. Respond in a conversational tone, but also execute structured actions.

Active Database Context:
{leadsContextText}

User Company Identity:
{businessProfile.BusinessDescription}

Your output MUST be a valid JSON object. Do not wrap in markdown code blocks, do not output anything else.
If you are performing a message command (e.g. user says: 'draft a WhatsApp to John saying Hello', or 'tell Sarah via Email we will call her'):
{{
   ""replyText"": ""Conversational response confirming you drafted/sent the message."",
   ""command"": {{
       ""action"": ""CreateMessage"",
       ""channel"": ""Email"" or ""WhatsApp"" or ""Facebook"" or ""Website Form"",
       ""leadName"": ""John (lead name)"",
       ""contactInfo"": ""john@example.com (invent a suitable address or phone number if not in DB)"",
       ""messageBody"": ""Inbound query content (e.g. User requested commander message)"",
       ""draftResponse"": ""The response you draft matching the selected channel's persona""
   }}
}}

If the user is just asking a question (e.g., 'who is John?' or 'what is our business?'):
{{
   ""replyText"": ""Your conversational reply here answering the query.""
}}";

        var chat = new ChatHistory(systemPrompt);
        chat.AddUserMessage(userPrompt);

        try
        {
            var response = await chatService.GetChatMessageContentAsync(chat, new GeminiPromptExecutionSettings(), _kernel);
            var content = CleanJson(response.Content ?? "");

            var result = JsonSerializer.Deserialize<CommanderResponse>(content, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });

            if (result != null)
            {
                if (result.Command != null && result.Command.Action.Equals("CreateMessage", StringComparison.OrdinalIgnoreCase))
                {
                    var cmd = result.Command;
                    var existing = activeLeads.FirstOrDefault(l => 
                        l.LeadName.Equals(cmd.LeadName, StringComparison.OrdinalIgnoreCase) || 
                        l.ContactInfo.Equals(cmd.ContactInfo, StringComparison.OrdinalIgnoreCase)
                    );

                    var leadId = existing != null ? existing.Id : $"lead-{Guid.NewGuid().ToString().Substring(0, 8)}";
                    
                    var newMsg = new LeadMessage(
                        leadId,
                        cmd.LeadName,
                        cmd.ContactInfo,
                        cmd.Channel,
                        cmd.MessageBody,
                        cmd.DraftResponse,
                        isAutonomous ? "Approved & Sent" : "Pending Review",
                        DateTime.UtcNow,
                        isAutonomous ? DateTime.UtcNow : null,
                        isAutonomous 
                            ? $"[Commander Auto Dispatch] Mode: Autonomous. Response sent automatically via AI Commander."
                            : $"[Commander Draft] Mode: Guarded. Response saved to draft via AI Commander."
                    );

                    await _leadsService.SaveLeadMessageAsync(userId, newMsg);

                    // Create log execution trace
                    var logId = $"lead-cmd-{Guid.NewGuid().ToString().Substring(0, 8)}";
                    var startLog = new AgentTaskLog(
                        logId,
                        $"[LeadsCommanderAgent] Executing message command for {cmd.Channel} lead '{cmd.LeadName}'",
                        "Completed",
                        $"AI Commander processed instructions. Status: {(isAutonomous ? "Autosent" : "Draft Saved")}.",
                        $"DraftResponse: {cmd.DraftResponse}",
                        DateTime.UtcNow
                    );
                    await _firebaseLogger.LogStateAsync(startLog);
                }

                return result.ReplyText;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Commander execution failed: {ex.Message}");
            return $"Commander encountered error parsing instruction. Ensure commands clearly mention a lead's name and channel. Error: {ex.Message}";
        }

        return "Commander completed with no conversational response.";
    }

    private string CleanJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "{}";
        var cleaned = content.Trim();
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(7);
        }
        else if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(3);
        }
        if (cleaned.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 3);
        }
        return cleaned.Trim();
    }
}

public class CommanderResponse
{
    public string ReplyText { get; set; } = string.Empty;
    public CommanderCommand? Command { get; set; }
}

public class CommanderCommand
{
    public string Action { get; set; } = string.Empty; // "CreateMessage"
    public string Channel { get; set; } = string.Empty;
    public string LeadName { get; set; } = string.Empty;
    public string ContactInfo { get; set; } = string.Empty;
    public string MessageBody { get; set; } = string.Empty;
    public string DraftResponse { get; set; } = string.Empty;
}
