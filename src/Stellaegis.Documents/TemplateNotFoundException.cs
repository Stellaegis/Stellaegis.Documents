namespace Stellaegis.Documents;

/// <summary>
/// Thrown when a template cannot be resolved for the requested code (no tenant override and no
/// system default).
/// </summary>
public sealed class TemplateNotFoundException : Exception
{
    /// <summary>Creates the exception for the given code and tenant.</summary>
    public TemplateNotFoundException(string templateCode, string tenantSlug)
        : base($"No template resolves for code '{templateCode}' (tenant '{(tenantSlug.Length == 0 ? "system" : tenantSlug)}').")
    {
        TemplateCode = templateCode;
        TenantSlug = tenantSlug;
    }

    /// <summary>The requested template code.</summary>
    public string TemplateCode { get; }

    /// <summary>The tenant context the resolution was attempted in.</summary>
    public string TenantSlug { get; }
}
