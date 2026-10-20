global using Dapper;
global using Microsoft.Data.Sqlite;
using SignageBoard.Web.Components;
using SignageBoard.Web.Data;
using SignageBoard.Web.Services;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace SignageBoard.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddValidation();
            builder.Services.AddOpenApi();

            builder.Services.AddOptions<BoardOptions>()
                .Bind(builder.Configuration.GetSection(BoardOptions.Section))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            builder.Services.AddSingleton(new SqliteConnectionFactory(
                builder.Configuration.GetConnectionString("Board")!));
            builder.Services.AddSingleton<DisplayGateFilter>();
            builder.Services.AddScoped<NoteService>();

            //builder.Services
            //    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            //    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

            var connString = builder.Configuration.GetConnectionString("Board");

            //builder.Services.Configure<ForwardedHeadersOptions>(o =>
            //{
            //    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            //    foreach (var ip in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
            //        o.KnownProxies.Add(IPAddress.Parse(ip));
            //});

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
                app.MapOpenApi(); // serves /openapi/v1.json
                app.UseSwaggerUI(o =>
                {
                    o.SwaggerEndpoint("/openapi/v1.json", "SignageBoard v1");
                    o.RoutePrefix = "swagger"; // UI at /swagger
                });
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseHttpsRedirection();

            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            using (var conn = app.Services.GetRequiredService<SqliteConnectionFactory>().Create())
            {
                conn.Execute("PRAGMA journal_mode=WAL;");
                conn.Execute("""
                    CREATE TABLE IF NOT EXISTS Notes (
                        Id TEXT PRIMARY KEY,
                        Body TEXT NOT NULL,
                        Priority INTEGER NOT NULL DEFAULT 0,
                        SortOrder INTEGER NOT NULL DEFAULT 0,
                        ExpiresAt TEXT NULL,
                        CreatedBy TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        Status INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE IF NOT EXISTS BoardState (Id INTEGER PRIMARY KEY CHECK (Id = 1), Version INTEGER NOT NULL, SingleNoteMode INTEGER NOT NULL DEFAULT 0, FontScale INTEGER NOT NULL DEFAULT 100);
                    INSERT OR IGNORE INTO BoardState (Id, Version) VALUES (1, 0);
                    """);

                (string Name, string Definition)[] boardStateColumns =
                [
                    ("SingleNoteMode", "INTEGER NOT NULL DEFAULT 0"),
                    ("FontScale", "INTEGER NOT NULL DEFAULT 100")
                ];

                foreach (var column in boardStateColumns)
                {
                    var exists = conn.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM pragma_table_info('BoardState') WHERE name = @Name", new { column.Name }) > 0;
                    if (!exists)
                        conn.Execute($"ALTER TABLE BoardState ADD COLUMN {column.Name} {column.Definition}");
                }
            }

            var board = app.MapGroup("/api/board");

            board.MapGet("", async (HttpContext http, NoteService svc) =>
            {
                var snapshot = await svc.GetBoardAsync();
                var etag = $"\"{snapshot.Version}\"";

                http.Response.Headers.ETag = etag;
                http.Response.Headers.CacheControl = "no-cache";

                var notModified = http.Request.Headers.IfNoneMatch
                    .SelectMany(value => (value ?? "").Split(','))
                    .Any(tag => tag.Trim() == etag);

                return notModified
                    ? Results.StatusCode(StatusCodes.Status304NotModified)
                    : Results.Ok(snapshot);
            })
            .WithName("GetBoard")
            .WithSummary("Current board snapshot for the TV display")
            .Produces<BoardSnapshot>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .AddEndpointFilter<DisplayGateFilter>();

            board.MapGet("/notes", (NoteService svc) => svc.GetActiveAsync());

            board.MapPost("/notes", async (CreateNoteDto dto, NoteService svc, ClaimsPrincipal user) =>
            {
                var createdBy = user.FindFirst("preferred_username")?.Value ?? "dev";
                try
                {
                    var note = await svc.CreateNoteAsync(dto, createdBy);
                    return Results.Created($"/api/board/notes/{note.Id}", note);
                }
                catch (ValidationException ex)
                {
                    return NoteValidationProblem(ex);
                }
            })
            .WithName("CreateNote")
            .Produces<Note>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

            board.MapPost("/notes/{id}/publish", async (string id, NoteService svc) =>
            {
                try
                {
                    var note = await svc.PublishNoteAsync(id);
                    return Results.Ok(note);
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound();
                }
                catch (ValidationException ex)
                {
                    return NoteValidationProblem(ex);
                }
            })
            .WithName("PublishNote")
            .WithSummary("Publish a note to the board")
            .Produces<Note>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status404NotFound);

            board.MapPost("/notes/{id}/unpublish", async (string id, NoteService svc) =>
            {
                try
                {
                    var note = await svc.UnpublishNoteAsync(id);
                    return Results.Ok(note);
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound();
                }
                catch (ValidationException ex)
                {
                    return NoteValidationProblem(ex);
                }
            })
            .WithName("UnpublishNote")
            .WithSummary("Return a note to draft")
            .Produces<Note>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status404NotFound);

            app.Run();
        }

        private static IResult NoteValidationProblem(ValidationException ex) =>
            Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["note"] = [ex.Message]
            });
    }
}
