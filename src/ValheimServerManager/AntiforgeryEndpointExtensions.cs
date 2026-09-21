namespace Microsoft.AspNetCore.Builder;

// JSON minimal APIs do not receive antiforgery metadata automatically. Program.cs performs
// validation for every unsafe /api/v1 request; this fluent marker keeps endpoint intent visible.
internal static class AntiforgeryEndpointExtensions
{
    internal static TBuilder RequireAntiforgery<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder => builder;
}
