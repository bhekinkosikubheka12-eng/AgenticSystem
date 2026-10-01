using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Firebase.Database;
using Firebase.Database.Query;
using AgenticSystem.Data;
using AgenticSystem.Services;

namespace AgenticSystem.Components.Pages.DocumentsAgent;

public class DocumentsAgentService
{
    private readonly FirebaseClient? _firebaseClient;
    private readonly bool _useLocalFallback = false;
    private readonly IConfiguration _config;
    private readonly Kernel? _kernel;

    // Local fallbacks in memory
    private static readonly ConcurrentDictionary<string, List<DocumentTemplate>> _localTemplates = new();
    private static readonly ConcurrentDictionary<string, List<GeneratedDocument>> _localDocuments = new();

    public DocumentsAgentService(IConfiguration config)
    {
        _config = config;
        
        // 1. Firebase client initialization
        var url = config["AiConfig:FirebaseUrl"];
        if (string.IsNullOrEmpty(url) || url.Contains("your-project-id") || url.Contains("your-firebase-url"))
        {
            Console.WriteLine("Firebase URL is empty or generic. Using local in-memory fallback for Documents Agent.");
            _useLocalFallback = true;
        }
        else
        {
            try
            {
                _firebaseClient = new FirebaseClient(url);
                Console.WriteLine($"Firebase Documents database initialized with URL: {url}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize Documents FirebaseClient: {ex.Message}. Using in-memory fallback.");
                _useLocalFallback = true;
            }
        }

        // 2. Semantic Kernel initialization for Gemini
        var apiKey = config["AiConfig:GeminiApiKey"];
        var modelId = config["AiConfig:GeminiModelId"] ?? "gemini-1.5-flash";
        if (!string.IsNullOrEmpty(apiKey) && !apiKey.Contains("your-gemini-api-key"))
        {
            try
            {
                var builder = Kernel.CreateBuilder();
                builder.AddGoogleAIGeminiChatCompletion(
                    modelId: modelId,
                    apiKey: apiKey
                );
                _kernel = builder.Build();
                Console.WriteLine($"Semantic Kernel initialized with model {modelId} for Documents Agent.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize Semantic Kernel for Documents Agent: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("Gemini API Key is missing or default. Agent will run in simulated response mode.");
        }
    }

    #region Template Management

    public async Task<List<DocumentTemplate>> GetTemplatesAsync(string userId)
    {
        List<DocumentTemplate>? templates = null;

        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                var items = await _firebaseClient
                    .Child("DocumentTemplates")
                    .Child(userId)
                    .OnceAsync<DocumentTemplate>();
                
                templates = items.Select(x => x.Object).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to retrieve templates from Firebase: {ex.Message}. Falling back to memory.");
            }
        }

        if (templates == null)
        {
            if (!_localTemplates.TryGetValue(userId, out templates))
            {
                templates = new List<DocumentTemplate>();
                _localTemplates[userId] = templates;
            }
        }

        // Seed default templates if empty
        if (!templates.Any())
        {
            var defaults = GetDefaultTemplates(userId);
            foreach (var t in defaults)
            {
                await SaveTemplateAsync(userId, t);
            }
            templates.AddRange(defaults);
        }

        return templates;
    }

    public async Task SaveTemplateAsync(string userId, DocumentTemplate template)
    {
        // Set ID if empty
        if (string.IsNullOrEmpty(template.Id))
        {
            template.Id = Guid.NewGuid().ToString("N").Substring(0, 10);
        }
        template.UserId = userId;
        template.LastModified = DateTime.UtcNow;

        // Local copy update
        if (!_localTemplates.TryGetValue(userId, out var list))
        {
            list = new List<DocumentTemplate>();
            _localTemplates[userId] = list;
        }
        var index = list.FindIndex(x => x.Id == template.Id);
        if (index >= 0) list[index] = template;
        else list.Add(template);

        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                await _firebaseClient
                    .Child("DocumentTemplates")
                    .Child(userId)
                    .Child(template.Id)
                    .PutAsync(template);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save template to Firebase: {ex.Message}");
            }
        }
    }

    public async Task DeleteTemplateAsync(string userId, string templateId)
    {
        if (_localTemplates.TryGetValue(userId, out var list))
        {
            list.RemoveAll(x => x.Id == templateId);
        }

        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                await _firebaseClient
                    .Child("DocumentTemplates")
                    .Child(userId)
                    .Child(templateId)
                    .DeleteAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete template from Firebase: {ex.Message}");
            }
        }
    }

    private List<DocumentTemplate> GetDefaultTemplates(string userId)
    {
        return new List<DocumentTemplate>
        {
            new DocumentTemplate
            {
                Id = "t-pdf-proposal",
                UserId = userId,
                Name = "Service Proposal Agreement",
                Type = "PDF",
                Description = "A clean professional contract layout with sections for Scope, Terms, and Confidentiality.",
                Variables = "BusinessName, ClientName, TotalAmount, ProjectName, Date",
                Content = @"# PROFESSIONAL SERVICES AGREEMENT

**Effective Date:** {{Date}}
**Project Name:** {{ProjectName}}

### Parties
This Professional Services Agreement (the ""Agreement"") is entered into by and between:
- **Service Provider:** {{BusinessName}}
- **Client:** {{ClientName}}

---

### 1. Scope of Work
{{BusinessName}} agrees to perform consulting and development services for the Client in connection with {{ProjectName}}. The services include but are not limited to:
- Systems architecture consulting and integration services.
- Verification and automation checks.
- Handover of technical logs and files.

### 2. Payment and Milestones
The Client shall pay {{BusinessName}} a total fee of **{{TotalAmount}}** as full compensation for the services. 
Payments shall be processed within 14 business days of invoice receipt.

### 3. Intellectual Property & Confidentiality
All materials, designs, and source codes developed under this agreement shall belong to the Client upon final payment. Both parties agree to protect proprietary information and not disclose it to third parties.

---

**Signed by representatives:**

_______________________________________
For {{BusinessName}}

_______________________________________
For {{ClientName}}",
                LastModified = DateTime.UtcNow
            },
            new DocumentTemplate
            {
                Id = "t-ppt-pitch",
                UserId = userId,
                Name = "Quarterly Operations Deck",
                Type = "Presentation",
                Description = "Outline and slides schema for presenting key business updates to directors.",
                Variables = "BusinessName, Date, CurrentQ",
                Content = @"Slide Deck Template: Q3 Strategy Review
Color Palette: Cyan / Dark Slate

Slide 1: Title Slide
- Title: Q3 Strategic Performance Overview
- Subtitle: Prepared by {{BusinessName}} | {{Date}}

Slide 2: Execution Results
- Bullet 1: Completed core integrations with leads automation pipeline
- Bullet 2: Scaled operations across target audience segments
- Bullet 3: Reached all key performance targets

Slide 3: Financial Summary
- Bullet 1: 22% growth in net margins
- Bullet 2: Total budget spent: $45,000
- Bullet 3: Projected ROI: 180%",
                LastModified = DateTime.UtcNow
            },
            new DocumentTemplate
            {
                Id = "t-inv-standard",
                UserId = userId,
                Name = "Professional Services Invoice",
                Type = "Invoice",
                Description = "Clean invoicing layout with line items table, subtotal, tax rate, and totals.",
                Variables = "BusinessName, ClientName, ClientEmail, InvoiceNumber, Date, DueDate",
                Content = @"Invoice Template for {{ClientName}}
Invoice ID: {{InvoiceNumber}}
Date: {{Date}}
Due Date: {{DueDate}}

Line Items:
- Professional Consulting Services: 10 hrs @ $150/hr
- Technical Integration Work: 20 hrs @ $100/hr",
                LastModified = DateTime.UtcNow
            },
            new DocumentTemplate
            {
                Id = "t-txt-meeting",
                UserId = userId,
                Name = "Meeting Minutes & Actions",
                Type = "Txt Note",
                Description = "Standard quick summary note format with discussion points and checklist tracker.",
                Variables = "BusinessName, ClientName, Date",
                Content = @"# Meeting Action Items
**Date:** {{Date}}
**Attendees:** {{BusinessName}} Team, {{ClientName}} Executives

### 1. Discussion Summary
We aligned on the next milestones for the automation features. The client expressed excitement about document drafting pipelines.

### 2. Immediate Priorities
- [ ] Deploy Documents Agent dashboard UI
- [ ] Configure templates default database
- [ ] Finalize exports format verification",
                LastModified = DateTime.UtcNow
            },
            new DocumentTemplate
            {
                Id = "t-xls-budget",
                UserId = userId,
                Name = "Monthly Operations Budget",
                Type = "Excel",
                Description = "Basic financial budget spreadsheet layout with totals and remaining column.",
                Variables = "BusinessName, Date",
                Content = @"Excel Template: Operations Budget for {{BusinessName}}
Headers: Category, Allocated ($), Spent ($), Remaining ($)
Rows:
- Software Licenses, 1200, 950, =B2-C2
- Marketing Ops, 2500, 1800, =B3-C3
- Travel Expense, 800, 200, =B4-C4
- Contractor Fees, 4000, 4000, =B5-C5",
                LastModified = DateTime.UtcNow
            }
        };
    }

    #endregion

    #region Generated Document Management

    public async Task<List<GeneratedDocument>> GetGeneratedDocumentsAsync(string userId)
    {
        List<GeneratedDocument>? docs = null;

        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                var items = await _firebaseClient
                    .Child("GeneratedDocuments")
                    .Child(userId)
                    .OnceAsync<GeneratedDocument>();
                
                docs = items.Select(x => x.Object).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to retrieve generated documents from Firebase: {ex.Message}. Falling back to memory.");
            }
        }

        if (docs == null)
        {
            if (!_localDocuments.TryGetValue(userId, out docs))
            {
                docs = new List<GeneratedDocument>();
                _localDocuments[userId] = docs;
            }
        }

        // Seed some sample documents if empty
        if (!docs.Any())
        {
            var samples = GetSampleGeneratedDocuments(userId);
            foreach (var d in samples)
            {
                await SaveGeneratedDocumentAsync(userId, d);
            }
            docs.AddRange(samples);
        }

        return docs.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public async Task SaveGeneratedDocumentAsync(string userId, GeneratedDocument doc)
    {
        if (string.IsNullOrEmpty(doc.Id))
        {
            doc.Id = Guid.NewGuid().ToString("N").Substring(0, 10);
        }
        doc.UserId = userId;
        doc.LastModified = DateTime.UtcNow;

        if (!_localDocuments.TryGetValue(userId, out var list))
        {
            list = new List<GeneratedDocument>();
            _localDocuments[userId] = list;
        }
        var index = list.FindIndex(x => x.Id == doc.Id);
        if (index >= 0) list[index] = doc;
        else list.Add(doc);

        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                await _firebaseClient
                    .Child("GeneratedDocuments")
                    .Child(userId)
                    .Child(doc.Id)
                    .PutAsync(doc);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save generated document to Firebase: {ex.Message}");
            }
        }
    }

    public async Task DeleteGeneratedDocumentAsync(string userId, string docId)
    {
        if (_localDocuments.TryGetValue(userId, out var list))
        {
            list.RemoveAll(x => x.Id == docId);
        }

        if (!_useLocalFallback && _firebaseClient != null)
        {
            try
            {
                await _firebaseClient
                    .Child("GeneratedDocuments")
                    .Child(userId)
                    .Child(docId)
                    .DeleteAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete generated document from Firebase: {ex.Message}");
            }
        }
    }

    private List<GeneratedDocument> GetSampleGeneratedDocuments(string userId)
    {
        var invoiceData = new InvoiceData
        {
            BusinessName = "NextGen AI Services Ltd",
            BusinessAddress = "78 Quantum Road, Innovation Park",
            ClientName = "CloudStream Systems LLC",
            ClientEmail = "accounts@cloudstream.io",
            InvoiceNumber = "INV-2026-8801",
            Date = "2026-06-28",
            DueDate = "2026-07-28",
            LineItems = new List<InvoiceLineItem>
            {
                new() { Description = "Cloud Operations Agent Setup & Testing", Quantity = 1, Rate = 3500.00m },
                new() { Description = "System Architecture Verification Consult", Quantity = 8, Rate = 150.00m },
                new() { Description = "Custom Connectors and Database Webhook Dev", Quantity = 1, Rate = 1200.00m }
            },
            TaxRate = 0.08m,
            Notes = "Payment terms net 30 days. Thank you for choosing NextGen AI Services!"
        };

        var pptData = new PresentationData
        {
            Title = "Product Pitch: Agentic OS Suite",
            Subtitle = "Revolutionizing localized operations",
            ThemeColor = "purple",
            Slides = new List<PresentationSlide>
            {
                new()
                {
                    Title = "The Agentic OS Mission",
                    Subtitle = "Empowering users with private agents",
                    Bullets = new List<string>
                    {
                        "Keep data 100% sovereign via local vector databases",
                        "Automate cross-platform pipelines without external dependency locks",
                        "Integrate voice, text, and documents in one workspace UI"
                    },
                    LayoutType = "TitleContent",
                    VisualNotes = "Display schematic drawing of workspace nodes."
                },
                new()
                {
                    Title = "Massive Operational Improvements",
                    Subtitle = "Before and after statistics",
                    Bullets = new List<string>
                    {
                        "Average process completion times reduced from 4 hours to 90 seconds",
                        "Manual error rates in document generation decreased by 94%",
                        "Employee productivity scores increased by an average of 40%"
                    },
                    LayoutType = "TwoColumns",
                    VisualNotes = "Show performance comparison chart side-by-side."
                },
                new()
                {
                    Title = "94% Cost Efficiency Gain",
                    Subtitle = "Calculated compared to hiring agency services",
                    Bullets = new List<string>(),
                    LayoutType = "BigStat",
                    VisualNotes = "Make '94%' massive and gold in the center"
                }
            }
        };

        var xlsData = new ExcelData
        {
            SheetName = "Operational Costs",
            Headers = new List<string> { "Item Name", "Allocated ($)", "Spent ($)", "Variance ($)" },
            Rows = new List<List<string>>
            {
                new() { "Cloud VPS Infrastructure", "1800", "1450", "=B2-C2" },
                new() { "WhatsApp Business API Limits", "500", "220", "=B3-C3" },
                new() { "Internal Security Audit", "2500", "2500", "=B4-C4" },
                new() { "SaaS Subscriptions & Tokens", "800", "920", "=B5-C5" },
                new() { "Total Costs Summary", "5600", "5090", "510" }
            }
        };

        return new List<GeneratedDocument>
        {
            new GeneratedDocument
            {
                Id = "d-sample-inv",
                UserId = userId,
                TemplateId = "t-inv-standard",
                Title = "CloudStream Systems Invoice",
                Type = "Invoice",
                RawContent = "Invoice INV-2026-8801 to CloudStream Systems LLC. Total amount: $5,280.00 plus tax.",
                StructuredDataJson = JsonSerializer.Serialize(invoiceData),
                Status = "Draft",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                LastModified = DateTime.UtcNow.AddHours(-2)
            },
            new GeneratedDocument
            {
                Id = "d-sample-ppt",
                UserId = userId,
                TemplateId = "t-ppt-pitch",
                Title = "Agentic OS Pitch Deck",
                Type = "Presentation",
                RawContent = "Presentation slides deck titled Product Pitch: Agentic OS Suite",
                StructuredDataJson = JsonSerializer.Serialize(pptData),
                Status = "Draft",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                LastModified = DateTime.UtcNow.AddDays(-1)
            },
            new GeneratedDocument
            {
                Id = "d-sample-xls",
                UserId = userId,
                TemplateId = "t-xls-budget",
                Title = "Q2 Operational Expense Grid",
                Type = "Excel",
                RawContent = "Excel spreadsheet containing operations budget and variances.",
                StructuredDataJson = JsonSerializer.Serialize(xlsData),
                Status = "Finalized",
                CreatedAt = DateTime.UtcNow.AddDays(-3),
                LastModified = DateTime.UtcNow.AddDays(-3)
            }
        };
    }

    #endregion

    #region Document Generation Agent Logic

    public async Task<(string response, string agentThought, GeneratedDocument? document)> GenerateDocumentAsync(
        string userId,
        string userPrompt,
        string documentType,
        DocumentTemplate? template,
        UserBusinessProfile? profile
    )
    {
        try
        {
            if (BillingService.Instance != null)
            {
                await BillingService.Instance.EnforceLimitsAsync("doc-generation", userPrompt);
            }
        }
        catch (Exception ex)
        {
            return ($"Error: {ex.Message}", $"Agent execution blocked by billing policy: {ex.Message}", null);
        }

        string promptType = documentType;
        if (template != null)
        {
            promptType = template.Type;
        }

        string systemPrompt = $@"You are an autonomous Document Generation Agent. Your job is to generate structured document contents based on the user's instructions and business profile.
User Business Context:
- Business Name: {profile?.BusinessName ?? "My Enterprise"}
- Industry: {profile?.BusinessIndustry ?? "Technology"}
- Description: {profile?.BusinessDescription ?? "Advanced Solutions"}
- Audience: {profile?.TargetAudience ?? "Clients"}

You generate documents of type: {promptType}.
{(template != null ? $"The user is basing this document on the template '{template.Name}' which has the layout:\n{template.Content}\n" : "")}

You MUST reply with a JSON object. Ensure that your output consists ONLY of valid JSON, inside a markdown code block starting with ```json and ending with ```.

The JSON schema you must strictly follow:
{{
  ""thought"": ""Your step-by-step thinking process explaining how you designed the document and what formulas or parameters you chose."",
  ""title"": ""A professional filename or title for the generated document"",
  ""rawContent"": ""A detailed, fully-realized Markdown or text version of the document, resolving all template placeholders."",
  ""structuredDataJson"": ""A JSON string (escaped correctly inside the main JSON string, or as a JSON object) matching the format corresponding to the document type:
     - If the type is 'Invoice': Matches InvoiceData schema: {{ ""BusinessName"": """", ""BusinessAddress"": """", ""ClientName"": """", ""ClientEmail"": """", ""InvoiceNumber"": """", ""Date"": """", ""DueDate"": """", ""LineItems"": [{{ ""Description"": """", ""Quantity"": 1, ""Rate"": 100.0 }}], ""TaxRate"": 0.08, ""Notes"": """" }}
     - If the type is 'Presentation': Matches PresentationData schema: {{ ""Title"": """", ""Subtitle"": """", ""ThemeColor"": ""cyan|purple|amber|emerald|rose"", ""Slides"": [{{ ""Title"": """", ""Subtitle"": """", ""Bullets"": [""""], ""LayoutType"": ""TitleContent|TwoColumns|BigStat|Quote"", ""VisualNotes"": """" }}] }}
     - If the type is 'Excel': Matches ExcelData schema: {{ ""SheetName"": """", ""Headers"": ["""", """"], ""Rows"": [["""", """"]] }}
     - If the type is 'PDF' or 'Txt Note': Can be empty string or general custom fields.""
}}";

        string userMessage = $"Generate a document based on: {userPrompt}";

        string rawResponse = "";
        string agentThoughts = "";
        string docTitle = $"Generated {promptType}";
        string rawContent = "";
        string structuredData = "";

        bool apiSucceeded = false;

        if (_kernel != null)
        {
            try
            {
                var chatService = _kernel.GetRequiredService<IChatCompletionService>();
                var chat = new ChatHistory(systemPrompt);
                chat.AddUserMessage(userMessage);

                var executionSettings = new GeminiPromptExecutionSettings
                {
                    Temperature = 0.4
                };

                var response = await chatService.GetChatMessageContentAsync(chat, executionSettings, _kernel);
                rawResponse = response.Content ?? "";
                
                if (!string.IsNullOrEmpty(rawResponse))
                {
                    // Clean markdown markers if any
                    var cleanJson = rawResponse;
                    if (cleanJson.Contains("```json"))
                    {
                        cleanJson = cleanJson.Split(new[] { "```json" }, StringSplitOptions.None)[1];
                        cleanJson = cleanJson.Split(new[] { "```" }, StringSplitOptions.None)[0];
                    }
                    else if (cleanJson.Contains("```"))
                    {
                        cleanJson = cleanJson.Split(new[] { "```" }, StringSplitOptions.None)[1];
                        cleanJson = cleanJson.Split(new[] { "```" }, StringSplitOptions.None)[0];
                    }
                    cleanJson = cleanJson.Trim();

                    using var doc = JsonDocument.Parse(cleanJson);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("thought", out var thoughtProp))
                        agentThoughts = thoughtProp.GetString() ?? "";
                    if (root.TryGetProperty("title", out var titleProp))
                        docTitle = titleProp.GetString() ?? docTitle;
                    if (root.TryGetProperty("rawContent", out var rawContentProp))
                        rawContent = rawContentProp.GetString() ?? "";
                    if (root.TryGetProperty("structuredDataJson", out var structProp))
                    {
                        if (structProp.ValueKind == JsonValueKind.String)
                        {
                            structuredData = structProp.GetString() ?? "";
                        }
                        else
                        {
                            structuredData = JsonSerializer.Serialize(structProp);
                        }
                    }

                    apiSucceeded = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gemini API execution failed: {ex.Message}. Falling back to simulator.");
            }
        }

        if (!apiSucceeded)
        {
            // Run Simulation Fallback
            var simResult = GenerateSimulatedDocument(userPrompt, promptType, template, profile);
            agentThoughts = simResult.thought;
            docTitle = simResult.title;
            rawContent = simResult.rawContent;
            structuredData = simResult.structuredDataJson;
        }

        var newDoc = new GeneratedDocument
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 10),
            UserId = userId,
            TemplateId = template?.Id ?? "custom",
            Title = docTitle,
            Type = promptType,
            RawContent = rawContent,
            StructuredDataJson = structuredData,
            Status = "Draft",
            CreatedAt = DateTime.UtcNow,
            LastModified = DateTime.UtcNow
        };

        await SaveGeneratedDocumentAsync(userId, newDoc);

        return ("Document generated successfully!", agentThoughts, newDoc);
    }

    private (string thought, string title, string rawContent, string structuredDataJson) GenerateSimulatedDocument(
        string userPrompt,
        string type,
        DocumentTemplate? template,
        UserBusinessProfile? profile
    )
    {
        string dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string bizName = profile?.BusinessName ?? "Horizon Tech Systems";
        string bizIndustry = profile?.BusinessIndustry ?? "Technology Services";
        
        // Basic keywords checks
        string lowerPrompt = userPrompt.ToLower();
        string clientName = "Acme Corp";
        if (lowerPrompt.Contains("for "))
        {
            var parts = userPrompt.Split(new[] { "for " }, StringSplitOptions.None);
            if (parts.Length > 1)
            {
                clientName = parts[1].Split(',')[0].Split('.')[0].Trim();
            }
        }

        if (type.Equals("Invoice", StringComparison.OrdinalIgnoreCase))
        {
            var invoiceNum = "INV-" + DateTime.UtcNow.Year + "-" + new Random().Next(1000, 9999);
            decimal rate = 125m;
            int hours = 40;
            if (lowerPrompt.Contains("rate"))
            {
                rate = 150m; // simple guess
            }
            
            var invoiceData = new InvoiceData
            {
                BusinessName = bizName,
                BusinessAddress = "12 Enterprise Boulevard, Suite A",
                ClientName = clientName,
                ClientEmail = $"billing@{clientName.Replace(" ", "").ToLower()}.com",
                InvoiceNumber = invoiceNum,
                Date = dateStr,
                DueDate = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"),
                LineItems = new List<InvoiceLineItem>
                {
                    new() { Description = $"Custom Consulting Service for {bizIndustry}", Quantity = hours, Rate = rate },
                    new() { Description = "Initial Onboarding & Environment Audit", Quantity = 1, Rate = 500m }
                },
                TaxRate = 0.08m,
                Notes = "Thank you for partnering with us. Please settle within net 30 days."
            };

            var total = invoiceData.LineItems.Sum(x => x.Quantity * x.Rate) * 1.08m;
            string invoiceMarkdown = $@"# INVOICE {invoiceNum}

**Date:** {dateStr}
**Due Date:** {invoiceData.DueDate}

**From:**
{invoiceData.BusinessName}
{invoiceData.BusinessAddress}

**To:**
{invoiceData.ClientName}
({invoiceData.ClientEmail})

---

### Billing Details
- Consulting hours: {hours} hrs @ ${rate}/hr - **${hours*rate}**
- Onboarding & Audit: 1 unit @ $500 - **$500**

**Subtotal:** ${hours*rate + 500}
**Tax (8.0%):** ${(hours*rate + 500)*0.08m}
**Total Amount Due:** ${total}

*Notes:* {invoiceData.Notes}";

            return (
                "Simulated Agent: Synthesized custom invoice by binding user business context details and calculating line totals.",
                $"Invoice for {clientName}",
                invoiceMarkdown,
                JsonSerializer.Serialize(invoiceData)
            );
        }
        else if (type.Equals("Presentation", StringComparison.OrdinalIgnoreCase))
        {
            var pptData = new PresentationData
            {
                Title = $"Business Pitch: {bizName}",
                Subtitle = $"Tailored strategy for {clientName}",
                ThemeColor = "emerald",
                Slides = new List<PresentationSlide>
                {
                    new()
                    {
                        Title = "Introduction",
                        Subtitle = $"Partnering {bizName} with {clientName}",
                        Bullets = new List<string>
                        {
                            $"Expertise in the {bizIndustry} sector.",
                            "Focusing on automating workflow bottlenecks.",
                            "Creating seamless custom experiences."
                        },
                        LayoutType = "TitleContent",
                        VisualNotes = "Display overlapping logo visuals."
                    },
                    new()
                    {
                        Title = "Key Problem Area",
                        Subtitle = "Why automation matters",
                        Bullets = new List<string>
                        {
                            "Manual drafting of invoices and PDFs takes average of 15 mins per file.",
                            "Scaling operations requires automated generation capability.",
                            "Standardizing templates prevents styling errors."
                        },
                        LayoutType = "TitleContent",
                        VisualNotes = "Display red arrows or charts showing time lost."
                    },
                    new()
                    {
                        Title = "Proposed Solution & ROI",
                        Subtitle = "Document Agent pipeline",
                        Bullets = new List<string>
                        {
                            "Autonomous agent processes incoming prompts in seconds.",
                            "Interactive viewers let managers tweak values before dispatch.",
                            "Saves 95% of staff administrative hours."
                        },
                        LayoutType = "TwoColumns",
                        VisualNotes = "Compare 'Before: 15 min' vs 'After: 2 sec'."
                    },
                    new()
                    {
                        Title = "95% Automation Efficiency",
                        Subtitle = "Standardized document turnaround improvement",
                        Bullets = new List<string>(),
                        LayoutType = "BigStat",
                        VisualNotes = "Huge text block in center"
                    }
                }
            };

            return (
                "Simulated Agent: Formulated PowerPoint slides structure targeting client onboarding goals and automation advantages.",
                $"{clientName} Pitch Slides",
                $"Presentation Title: {pptData.Title}\nSlides count: {pptData.Slides.Count}",
                JsonSerializer.Serialize(pptData)
            );
        }
        else if (type.Equals("Excel", StringComparison.OrdinalIgnoreCase))
        {
            var xlsData = new ExcelData
            {
                SheetName = "Operational Roadmap",
                Headers = new List<string> { "Task Description", "Owner", "Estimated Hours", "Status" },
                Rows = new List<List<string>>
                {
                    new() { "Initial Design Brainstorming", "Consultant", "10", "Completed" },
                    new() { "System Implementation Phase 1", "Engineering Team", "40", "In Progress" },
                    new() { "Custom Template Settings Setup", "Quality Assurance", "15", "Pending" },
                    new() { "Client Handover and Walkthrough", "Operations", "5", "Pending" },
                    new() { "Total Estimated Hours", "Summary Total", "70", "Active" }
                }
            };

            return (
                "Simulated Agent: Engineered spreadsheet structure detailing task allocations and sum formulas for hours.",
                $"{clientName} Planning Sheet",
                "Excel Grid: Task Roadmap and Hour Estimations",
                JsonSerializer.Serialize(xlsData)
            );
        }
        else if (type.Equals("PDF", StringComparison.OrdinalIgnoreCase))
        {
            string pdfContent = $@"# SERVICE AGREEMENT & WORK STATEMENT

**Date:** {dateStr}
**Service Provider:** {bizName}
**Client:** {clientName}

---

### Scope of Deliverables
Pursuant to the user's prompt request, {bizName} shall deliver the following items:
1. Customized dashboard and orchestration models.
2. Integration checks for local databases.
3. System logs and execution verification pipelines.

### Compensation
The total contract value is estimated to be calculated on standard rates. Work shall commence immediately.

Signed by representatives of both parties on {dateStr}.";

            return (
                "Simulated Agent: Rendered customized service agreement contract based on default PDF template mapping.",
                $"{clientName} Service Agreement.pdf",
                pdfContent,
                ""
            );
        }
        else // Txt Note
        {
            string noteContent = $@"# Discussion Notes: {clientName} Project
**Date:** {dateStr}
**Created by:** {bizName} Agent

### Summary
The client requests standard operational automation. 

### Action Items
- [x] Configure profile databases
- [ ] Implement templates editor
- [ ] Connect agent prompt endpoints";

            return (
                "Simulated Agent: Drafted quick meeting action notes based on the request.",
                $"{clientName} Discussion Notes.txt",
                noteContent,
                ""
            );
        }
    }

    #endregion
}
