using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using System;
using System.Collections.Generic;

namespace I3X4Kusto
{
    public class Program
    {
        private const string CorsPolicyName = "i3xCors";

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();

            builder.Services.AddOpenApi();

            builder.Services.AddSwaggerGen(options =>
            {
                // Enable HTTP Basic auth in the Swagger UI so the "Authorize" button lets users
                // supply credentials that are sent as the "Authorization: Basic" header on API calls.
                options.AddSecurityDefinition("basic", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "basic",
                    Description = "Enter the I3X_BASIC_AUTH_USERNAME and I3X_BASIC_AUTH_PASSWORD credentials."
                });

                options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecuritySchemeReference("basic", document),
                        new List<string>()
                    }
                });
            });

            builder.Services.AddSingleton<ADXDataService>();

            builder.Services.AddSingleton<SubscriptionStore>();

            // CORS: the browser-based CESMII i3X client calls this API cross-origin, so the API must
            // return the appropriate Access-Control-* headers (including for preflight OPTIONS) or the
            // browser blocks the requests. Allow any origin/header/method by default; restrict the
            // origins via the I3X_CORS_ORIGINS env var (comma-separated) in production if needed.
            string[] allowedOrigins = (Environment.GetEnvironmentVariable("I3X_CORS_ORIGINS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            builder.Services.AddCors(options =>
            {
                options.AddPolicy(CorsPolicyName, policy =>
                {
                    if (allowedOrigins.Length > 0)
                    {
                        policy.WithOrigins(allowedOrigins)
                              .AllowAnyHeader()
                              .AllowAnyMethod();
                    }
                    else
                    {
                        policy.AllowAnyOrigin()
                              .AllowAnyHeader()
                              .AllowAnyMethod();
                    }
                });
            });

            var app = builder.Build();

            // Configure middleware pipeline
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "i3X Kusto Adapter v1");
            });

            app.UseHttpsRedirection();

            // CORS must run before the endpoints so preflight requests are handled.
            app.UseCors(CorsPolicyName);

            // HTTP Basic authentication
            // Runs after CORS so preflight OPTIONS requests are not challenged.
            app.UseMiddleware<BasicAuthMiddleware>();

            app.MapControllers();

            app.Run();
        }
    }
}
