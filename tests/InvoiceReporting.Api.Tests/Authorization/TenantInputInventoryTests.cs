using System.Reflection;
using InvoiceReporting.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReporting.Api.Tests.Authorization;

/// <summary>
/// Every controller action that lets the caller name a tenant must enforce the
/// TenantAccess policy. A new action fails this test until a reviewer has
/// checked it and added it to <see cref="ReviewedActions"/>.
/// </summary>
public sealed class TenantInputInventoryTests
{
    private static readonly string[] ReviewedActions =
    [
        "InvoiceReportController.GetMonthly",
    ];

    private static readonly string[] TenantParameterNames = ["dealer", "dealerid", "tenant", "tenantid", "tenantkey"];

    [Fact]
    public void OnlyReviewedActions_AcceptAClientSuppliedTenantIdentifier()
    {
        var actions = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(m => m.GetParameters().Any(AcceptsTenantIdentifier))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Order()
            .ToArray();

        Assert.Equal(ReviewedActions.Order(), actions);
    }

    private static bool AcceptsTenantIdentifier(ParameterInfo parameter) =>
        parameter.GetCustomAttribute<FromHeaderAttribute>()?.Name == TenantResolver.TenantHeaderName
        || TenantParameterNames.Contains(parameter.Name?.ToLowerInvariant());
}
