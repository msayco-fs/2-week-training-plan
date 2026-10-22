using Microsoft.AspNetCore.Authorization;

namespace InvoiceReporting.Api.Security;

/// <summary>The caller may operate on the requested tenant.</summary>
public sealed class TenantAccessRequirement : IAuthorizationRequirement;
