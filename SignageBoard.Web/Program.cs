global using Dapper;
global using Microsoft.Data.Sqlite;
using SignageBoard.Web.Components;
using SignageBoard.Web.Data;
using SignageBoard.Web.Services;

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

            builder.Services.AddOptions<BoardOptions>()
                .Bind(builder.Configuration.GetSection(BoardOptions.Section))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            builder.Services.AddSingleton(new SqliteConnectionFactory(
                builder.Configuration.GetConnectionString("Board")!));
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
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Body TEXT NOT NULL,
                        Priority INTEGER NOT NULL DEFAULT 0,
                        SortOrder INTEGER NOT NULL DEFAULT 0,
                        ExpiresAt TEXT NULL,
                        CreatedBy TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS BoardState (Id INTEGER PRIMARY KEY CHECK (Id = 1), Version INTEGER NOT NULL);
                    INSERT OR IGNORE INTO BoardState (Id, Version) VALUES (1, 0);
                    """);
            }

            app.Run();
        }
    }
}
