namespace Stellaegis.Documents;

/// <summary>
/// Declares a system template (<c>tenant_slug = ''</c>) to be seeded at startup. Registered in DI;
/// the framework ships none by default — the integrator (or a framework module such as the security
/// emails) contributes its own definitions.
/// </summary>
public sealed record SystemTemplateDefinition
{
    /// <summary>Canonical template code (e.g. <c>standard_invoice</c>).</summary>
    public required string TemplateCode { get; init; }

    /// <summary>Logical blob key of the content within the system templates space.</summary>
    public required string StoragePath { get; init; }

    /// <summary>Whether tenants may override this template (default <see langword="true"/>).</summary>
    public bool IsCustomizable { get; init; } = true;

    /// <summary>Render engine discriminant (default <c>scriban</c>).</summary>
    public string RenderEngineCode { get; init; } = "scriban";
}
