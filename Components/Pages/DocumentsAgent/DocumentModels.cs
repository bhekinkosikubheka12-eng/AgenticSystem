using System;
using System.Collections.Generic;

namespace AgenticSystem.Components.Pages.DocumentsAgent;

public class DocumentTemplate
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = ""; // "PDF", "Presentation", "Invoice", "Txt Note", "Excel"
    public string Content { get; set; } = ""; // Markdown/Text layout template with placeholders
    public string Description { get; set; } = "";
    public string Variables { get; set; } = ""; // comma separated variable names, e.g. "BusinessName, ClientName, TotalAmount"
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}

public class GeneratedDocument
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = ""; // "PDF", "Presentation", "Invoice", "Txt Note", "Excel"
    public string RawContent { get; set; } = ""; // Complete filled-in layout or narrative
    public string StructuredDataJson { get; set; } = ""; // Deserialized structure JSON (InvoiceData, PresentationData, ExcelData)
    public string Status { get; set; } = "Draft"; // "Draft", "Finalized"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}

public class DocumentAgentMessage
{
    public string Id { get; set; } = "";
    public string Sender { get; set; } = ""; // "User", "Agent"
    public string Text { get; set; } = "";
    public string Thought { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// Structured models for parsing / displaying rich documents

public class InvoiceData
{
    public string BusinessName { get; set; } = "";
    public string BusinessAddress { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientEmail { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public string Date { get; set; } = "";
    public string DueDate { get; set; } = "";
    public List<InvoiceLineItem> LineItems { get; set; } = new();
    public decimal TaxRate { get; set; } = 0.08m; // Default 8%
    public string Notes { get; set; } = "";
}

public class InvoiceLineItem
{
    public string Description { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public decimal Rate { get; set; } = 0;
}

public class PresentationData
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string ThemeColor { get; set; } = "cyan"; // "cyan", "purple", "emerald", "rose"
    public List<PresentationSlide> Slides { get; set; } = new();
}

public class PresentationSlide
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<string> Bullets { get; set; } = new();
    public string LayoutType { get; set; } = "TitleContent"; // "TitleContent", "TwoColumns", "BigStat", "Quote"
    public string VisualNotes { get; set; } = "";
}

public class ExcelData
{
    public string SheetName { get; set; } = "Sheet1";
    public List<string> Headers { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new(); // Outer list: row, inner list: cell string content
}
