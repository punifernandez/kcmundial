using System.IO;
using KCMundial.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace KCMundial.ShareServer;

public static class ShareServerExtensions
{
    public static IEndpointRouteBuilder MapShareRoutes(this IEndpointRouteBuilder builder, IPathResolver pathResolver)
    {
        builder.MapGet("/f/{id}", (string id, HttpContext ctx) =>
        {
            try
            {
                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                var imageUrl = $"{baseUrl}/img/{id}.jpg";
                var title = "KCMundial - Tu figurita";
                var help = "Mismo WiFi. Descargar: elegí «Guardar en Fotos» o «Fotos» en el menú. Compartir: elegí WhatsApp, Instagram, etc. La primera vez puede pedir aceptar la conexión (HTTPS).";
                var html = HtmlTemplates.FiguritaPage
                    .Replace("{0}", title)
                    .Replace("{1}", imageUrl)
                    .Replace("{2}", imageUrl)
                    .Replace("{3}", help);
                
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.Headers.Append("Cache-Control", "no-cache");
                return Results.Content(html, "text/html; charset=utf-8");
            }
            catch (Exception ex)
            {
                return Results.Text($"Error: {ex.Message}", "text/plain");
            }
        });

        builder.MapGet("/img/{id}.jpg", (string id, HttpContext ctx) =>
        {
            var path = Path.Combine(pathResolver.FiguritasFolder, id + ".jpg");
            if (!File.Exists(path))
                return Results.NotFound();
            
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0)
                    return Results.NotFound();
                ctx.Response.Headers["Content-Disposition"] = "inline";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                return Results.File(bytes, "image/jpeg");
            }
            catch
            {
                return Results.NotFound();
            }
        });

        return builder;
    }
}
