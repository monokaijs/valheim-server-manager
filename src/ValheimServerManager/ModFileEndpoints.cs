using ValheimServerManager.Services;
namespace ValheimServerManager;

public static class ModFileEndpoints
{
    public static void MapModFiles(this RouteGroupBuilder api)
    {
        api.MapGet("/mods/{id:guid}/files", async (Guid id, ModConfigService configs, CancellationToken ct) => Results.Ok(await configs.FileTree(id, ct)));
        api.MapGet("/mods/{id:guid}/files/content", async (Guid id, string path, ModConfigService configs, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await configs.ReadFile(id, path, ct));
        });
        api.MapPost("/mods/{id:guid}/files", async (Guid id, ConfigFileMutation request, ModConfigService configs, CancellationToken ct) =>
        {
            await configs.MutateFile(id, request, ct);
            return Results.NoContent();
        }).RequireAntiforgery();
    }
}
