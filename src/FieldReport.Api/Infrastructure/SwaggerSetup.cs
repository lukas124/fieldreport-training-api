using System.Reflection;
using FieldReport.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FieldReport.Api.Infrastructure;

public static class SwaggerSetup
{
    public const string BearerScheme = "Bearer";

    public static IServiceCollection AddFieldReportSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "FieldReport API (szkoleniowe)",
                Version = "v1",
                Description =
                    "API szkoleniowe do aplikacji FieldReport (.NET MAUI).\n\n" +
                    "**Jak zacząć:** `POST /api/auth/login` z dowolnym poprawnym e-mailem i PIN-em `1234` → skopiuj " +
                    "`accessToken` → przycisk **Authorize** → wklej token.\n\n" +
                    "Każdy e-mail to osobny technik z osobnymi danymi. Endpointy **Dyspozytor** symulują dyspozytora: " +
                    "tworzą zgłoszenia i wysyłają push (Firebase Cloud Messaging, data: `{ type, reportId }`, kanał Android `reports`).\n\n" +
                    "Wyliczenia w JSON są tekstowe: priorytet `Low`/`Normal`/`High`, status `New`/`InProgress`/`Done`. " +
                    "Daty w ISO 8601."
            });

            var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xml)) c.IncludeXmlComments(xml, includeControllerXmlComments: true);

            c.AddSecurityDefinition(BearerScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Wklej `accessToken` z `POST /api/auth/login` (bez słowa Bearer)."
            });
            c.OperationFilter<AuthorizeOperationFilter>();
            c.SchemaFilter<EnumDescriptionSchemaFilter>();
            c.DocumentFilter<TagDescriptionsDocumentFilter>();
            c.SupportNonNullableReferenceTypes();
        });
        return services;
    }

    public static WebApplication UseFieldReportSwagger(this WebApplication app)
    {
        app.UseSwagger(o => o.PreSerializeFilters.Add((doc, request) =>
        {
            // Za reverse proxy z PathBase (/maui/podstawowe) „Try it out” musi trafiać pod prefiks.
            doc.Servers = [new OpenApiServer { Url = request.PathBase.HasValue ? request.PathBase.Value : "/" }];
        }));
        app.UseSwaggerUI(o =>
        {
            o.SwaggerEndpoint("v1/swagger.json", "FieldReport API v1"); // względnie → działa z każdym PathBase
            o.DocumentTitle = "FieldReport API — Swagger";
            o.EnableTryItOutByDefault();
            o.DisplayRequestDuration();
            o.EnablePersistAuthorization();
        });
        return app;
    }

    private sealed class AuthorizeOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
            if (!metadata.OfType<IAuthorizeData>().Any() || metadata.OfType<IAllowAnonymous>().Any()) return;

            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = BearerScheme } }] = []
                }
            ];
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Brak lub wygasły access token (odśwież przez /api/auth/refresh)." });
        }
    }

    private sealed class EnumDescriptionSchemaFilter : ISchemaFilter
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (context.Type == typeof(ReportPriority))
            {
                schema.Description = "Priorytet: `Low` = Niski, `Normal` = Normalny, `High` = Wysoki.";
                schema.Example = new OpenApiString("Normal");
            }
            else if (context.Type == typeof(ReportStatus))
            {
                schema.Description = "Status: `New` = Nowe, `InProgress` = W trakcie, `Done` = Zakończone.";
                schema.Example = new OpenApiString("New");
            }
        }
    }

    private sealed class TagDescriptionsDocumentFilter : IDocumentFilter
    {
        public void Apply(OpenApiDocument doc, DocumentFilterContext context)
        {
            doc.Tags =
            [
                new OpenApiTag { Name = "Auth", Description = "Logowanie technika i odświeżanie tokenu (F2)." },
                new OpenApiTag { Name = "Reports", Description = "Zgłoszenia zalogowanego technika: synchronizacja (F6) i zdjęcia." },
                new OpenApiTag { Name = "Devices", Description = "Rejestracja urządzeń do powiadomień push (F7)." },
                new OpenApiTag { Name = "Dispatcher", Description = "Symulacja dyspozytora: nowe zgłoszenia i zmiany statusu z wysyłką push (F7)." },
                new OpenApiTag { Name = "Admin", Description = "Administracja: reset danych (nagłówek X-Admin-Key)." },
                new OpenApiTag { Name = "Health", Description = "Stan usługi." }
            ];
        }
    }
}
